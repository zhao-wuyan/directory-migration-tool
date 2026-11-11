using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MigrationCore.Models;
using MigrationCore.Services;

internal static partial class Program
{
    private static readonly string[] IdentityCaseNames =
    ["persistent-identity", "atomic-revision-restart", "stale-revision", "active-overlap",
     "record-write-failure", "corrupt-no-fallback", "same-path-replacement", "test-ui-state-injection"];

    private static async Task RunIdentityCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        string sourceScope = Path.Combine(lab.Source.Root, @"Data\Sources\Sample");
        string source = Path.Combine(sourceScope, $"Identity-{attempt:D}");
        string targetParent = Path.Combine(lab.Target.Root, @"Data\Targets");
        string target = Path.Combine(targetParent, $"Identity-{attempt:D}");
        string stateRoot = Path.Combine(MigrationRuntime.TransactionDirectory, attempt.ToString("D"));
        NewDirectory(lab, lab.Source, sourceScope, source);
        byte[] payload = Encoding.UTF8.GetBytes($"Identity fixture {attempt:D}");
        string payloadPath = Path.Combine(source, PayloadName);
        var originalFile = WriteFile(lab, lab.Source, source, payloadPath, payload);
        var identity = new PathIdentityService();
        var store = new MigrationTransactionStore(stateRoot);
        MigrationTransactionRecord record = new()
        {
            SourcePath = source, TargetPath = target,
            SourceIdentity = identity.ReadDirectory(source, false),
            TargetParentIdentity = identity.ReadDirectory(targetParent, false)
        };
        await RunCase(lab, cases[0], o =>
        {
            var boundary = lab.Source.Existing(source, source);
            Require(record.SourceIdentity.FileId == boundary.FileId && record.SourceIdentity.VolumeSerial == boundary.Serial,
                "Production identity disagrees with the independent lab reader.");
            Require(record.SourceIdentity.VolumeGuid != record.TargetParentIdentity.VolumeGuid,
                "Fixture must cross the two registered volumes.");
            o["source"] = source; o["target"] = target; o["identity"] = record.SourceIdentity;
            return Task.CompletedTask;
        });
        await RunCase(lab, cases[1], async o =>
        {
            lab.BeforeWrite();
            record = await store.CreateAsync(record);
            record = await store.SaveAsync(record with { ProbePath = source + "_lock_test_" + record.TransactionId.ToString("D") }, record.Revision);
            MigrationTransactionRecord saved = await ReadSnapshotInChild(lab, stateRoot, record.TransactionId, o);
            Require(saved.Revision == 2 && saved.SourceIdentity == record.SourceIdentity && saved.ProbePath == record.ProbePath
                && saved.SourcePath == record.SourcePath && saved.TargetPath == record.TargetPath
                && saved.Stage == MigrationStage.Prepared, "New process did not read the exact persisted transaction state.");
            o["transactionId"] = record.TransactionId; o["revision"] = saved.Revision; o["stateDirectory"] = stateRoot;
        });
        await RunCase(lab, cases[2], async o =>
        {
            o["rejection"] = await RejectAsync<IOException>(() => store.SaveAsync(record with { Stage = MigrationStage.Copying }, 1));
            Require((await store.LoadAsync(record.TransactionId))?.Revision == 2, "Rejected stale save changed the snapshot.");
            Require(record.Stage == MigrationStage.Prepared, "Rejected save mutated the caller's immutable record.");
        });
        await RunCase(lab, cases[3], async o =>
        {
            var overlap = record with { TransactionId = Guid.NewGuid(), Revision = 0, TargetPath = target + "-other" };
            o["rejection"] = await RejectAsync<IOException>(() => store.CreateAsync(overlap));
            Require(await store.LoadAsync(overlap.TransactionId) is null, "Overlapping transaction was recorded.");
            o["preservedDigest"] = VerifyFile(lab.Source, source, payloadPath, originalFile, payload);
        });
        await RunCase(lab, cases[4], async o =>
        {
            string path = Path.Combine(stateRoot, record.TransactionId.ToString("D") + ".json");
            byte[] before = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                o["rejection"] = await RejectAsync<IOException>(() => store.SaveAsync(record with { Stage = MigrationStage.Copying }, record.Revision));
            Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Failed atomic replace modified the live snapshot.");
            Require((await store.LoadAsync(record.TransactionId))?.Stage == MigrationStage.Prepared, "Failed write advanced durable state.");
            o["preservedDigest"] = VerifyFile(lab.Source, source, payloadPath, originalFile, payload);
            Require(Directory.EnumerateFiles(stateRoot, "*.tmp").Any(), "Interrupted snapshot evidence was silently removed.");
        });
        await RunCase(lab, cases[5], async o =>
        {
            string corruptRoot = Path.Combine(stateRoot, "corrupt");
            var corruptStore = new MigrationTransactionStore(corruptRoot);
            MigrationTransactionRecord copy = await corruptStore.CreateAsync(record with { TransactionId = Guid.NewGuid(), Revision = 0 });
            string path = Path.Combine(corruptRoot, copy.TransactionId.ToString("D") + ".json");
            lab.BeforeWrite();
            File.Copy(path, path + ".bak", false);
            File.WriteAllText(path, "{broken", new UTF8Encoding(false));
            o["rejection"] = await RejectAsync<JsonException>(() => corruptStore.LoadAsync(copy.TransactionId));
            Require(File.ReadAllText(path) == "{broken" && File.Exists(path + ".bak"), "Corrupt record was silently repaired or backup consumed.");
            string incomplete = JsonSerializer.Serialize(new { schemaVersion = 1, transactionId = copy.TransactionId, revision = 1,
                sourcePath = copy.SourcePath, targetPath = copy.TargetPath, sourceIdentity = copy.SourceIdentity,
                targetParentIdentity = copy.TargetParentIdentity });
            File.WriteAllText(path, incomplete, new UTF8Encoding(false));
            o["missingFieldsRejection"] = await RejectAsync<InvalidDataException>(() => corruptStore.LoadAsync(copy.TransactionId));
            Require(File.ReadAllText(path) == incomplete, "Incomplete record was silently repaired.");
        });
        await RunCase(lab, cases[6], o =>
        {
            string preserved = source + "-preserved";
            lab.BeforeWrite(); lab.Source.Missing(preserved, sourceScope);
            Directory.Move(source, preserved);
            NewDirectory(lab, lab.Source, sourceScope, source);
            byte[] replacement = Encoding.UTF8.GetBytes("Replacement must remain untouched.");
            var newFile = WriteFile(lab, lab.Source, source, payloadPath, replacement);
            o["rejection"] = Reject(() => identity.RequireSame(source, record.SourceIdentity), "路径身份不符：");
            identity.RequireSame(preserved, record.SourceIdentity);
            o["preservedDigest"] = VerifyFile(lab.Source, preserved, Path.Combine(preserved, PayloadName), originalFile, payload, false);
            o["replacementDigest"] = VerifyFile(lab.Source, source, payloadPath, newFile, replacement);
            var task = new QuickMigrateTask { SourcePath = source, TargetPath = target, Transaction = record };
            MigrationStateDetector.DetectMigrationState(task, store);
            Require(task.MigrationState == MigrationState.Inconsistent && !task.IsResumable, "Replaced source was advertised as resumable.");
            return Task.CompletedTask;
        });
        await RunCase(lab, cases[7], async o =>
        {
            ValidateConfigurationMutation(lab, o);
            await VerifyTestUi(lab, o);
        });
    }

    private static async Task<int> ReadTransactionChild(string[] args)
    {
        try
        {
            Lab lab = LoadLab(args[..6]);
            MigrationRuntime.InitializeTestLab(args[1], lab.RunId);
            Guid id = ParseGuid(args[7], "child transaction ID");
            string state = PathIdentityService.Normalize(args[9]);
            MigrationRuntime.AssertStatePath(state);
            if (new PathIdentityService().ReadDirectory(state, false).IsReparsePoint)
                throw new IOException("Child state directory is a reparse point.");
            var store = new MigrationTransactionStore(state);
            MigrationTransactionRecord record = await store.LoadAsync(id) ?? throw new IOException("Child transaction missing.");
            Console.WriteLine(JsonSerializer.Serialize(new { processId = Environment.ProcessId, record },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Describe(ex));
            return 2;
        }
    }

    private static async Task<MigrationTransactionRecord> ReadSnapshotInChild(Lab lab, string state, Guid id,
        Dictionary<string, object?> observations)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "--lab", Path.Combine(lab.WorkRoot, "lab.json"), "--run-id", lab.RunId.ToString("D"),
            "--suite", "Identity", "--read-transaction", id.ToString("D"), "--state-directory", state })
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new IOException("Cannot start snapshot reader.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(process.ExitCode == 0, "Snapshot child failed: " + await error);
            using JsonDocument document = JsonDocument.Parse(await output);
            int pid = document.RootElement.GetProperty("processId").GetInt32();
            Require(pid == process.Id && pid != Environment.ProcessId, "Snapshot was not read in a fresh process.");
            observations["readerProcessId"] = pid;
            return document.RootElement.GetProperty("record").Deserialize<MigrationTransactionRecord>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new IOException("Empty child snapshot.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void ValidateConfigurationMutation(Lab lab, Dictionary<string, object?> observations)
    {
        string path = MigrationRuntime.TestConfigPath ?? throw new IOException("Harness configuration missing.");
        DiskLabPath.Identity before = OrdinarySingleFile(path);
        byte[] original = File.ReadAllBytes(path);
        try
        {
            var changed = System.Text.Json.Nodes.JsonNode.Parse(original) ?? throw new IOException("Invalid fixture configuration.");
            changed["standaloneSources"]![0]!["absolutePath"] = @"C:\Windows\System32";
            lab.BeforeWrite();
            File.WriteAllText(path, changed.ToJsonString(), new UTF8Encoding(false));
            observations["configurationChangeRejected"] = Reject(() => QuickMigrateConfigLoader.LoadConfig(), "路径越出测试数据区：");
        }
        finally
        {
            SameObject(before, OrdinarySingleFile(path), "Harness configuration object changed.");
            File.WriteAllBytes(path, original);
        }
        QuickMigrateConfig config = QuickMigrateConfigLoader.LoadConfig() ?? throw new IOException("Restored configuration missing.");
        Require(config.Profiles.Count == 0 && config.StandaloneSources.Single().AbsolutePath == Path.Combine(lab.Source.Root, @"Data\Sources\Sample")
            && config.Defaults.UnifiedTargetRoot == Path.Combine(lab.Target.Root, @"Data\Targets"),
            "Restored test configuration did not retain its exact fake-data sources and target.");
    }

    private static async Task<string> RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return Describe(ex); }
        throw new InvalidOperationException($"Expected {typeof(T).Name}, operation was accepted.");
    }

    private static async Task VerifyTestUi(Lab lab, Dictionary<string, object?> o)
    {
        string app = Environment.GetEnvironmentVariable("MIGRATION_TEST_LAB_APP") ?? throw new IOException("App package not supplied.");
        Require(Path.GetDirectoryName(app) == Path.Combine(lab.WorkRoot, "App"), "App package is outside registered WorkRoot.");
        ParseGuid(Path.GetFileName(app), "app attempt ID");
        DiskLabPath.AssertOrdinaryPath(app, false);
        string runtime = Directory.EnumerateFiles(app, "*.runtimeconfig.json").Single();
        string executable = runtime[..^".runtimeconfig.json".Length] + ".exe";
        OrdinarySingleFile(executable);
        ProcessStartInfo Launch(string manifest)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--test-lab"); start.ArgumentList.Add(manifest);
            start.ArgumentList.Add("--run-id"); start.ArgumentList.Add(lab.RunId.ToString("D"));
            return start;
        }
        using (Process invalid = Process.Start(Launch(Path.Combine(lab.WorkRoot, "unregistered.json"))) ?? throw new IOException("Could not start invalid UI probe."))
        {
            Task<string> error = invalid.StandardError.ReadToEndAsync();
            await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(invalid.ExitCode == 2, "Invalid test configuration fell back to production startup.");
            o["invalidLaunchExitCode"] = invalid.ExitCode; o["invalidLaunchError"] = await error;
        }
        using Process process = Process.Start(Launch(Path.Combine(lab.WorkRoot, "lab.json"))) ?? throw new IOException("Could not start test UI.");
        try
        {
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Require(process.WaitForInputIdle(10000), "Test UI did not become input idle.");
            var watch = Stopwatch.StartNew();
            do
            {
                process.Refresh();
                if (process.HasExited || process.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(50);
            } while (watch.Elapsed < TimeSpan.FromSeconds(10));
            Require(!process.HasExited && process.MainWindowHandle != IntPtr.Zero,
                "Test UI did not expose a real window: " + (process.HasExited ? await error : "still running"));
            Require(process.MainWindowTitle.Contains("Test Lab", StringComparison.Ordinal), "Window is not the dedicated test instance.");
            o["windowTitle"] = process.MainWindowTitle; o["stateDirectory"] = MigrationRuntime.TransactionDirectory;
            string script = Path.Combine(AppContext.BaseDirectory, "BackupUi.ps1"); OrdinarySingleFile(script);
            string screenshot = Path.Combine(lab.WorkRoot, "Results", $"{Guid.NewGuid():D}-startup.png");
            lab.BeforeWrite(); DiskLabPath.AssertOrdinaryPath(screenshot, true);
            var readyStart = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-AppProcessId", process.Id.ToString(),
                "-Mode", "Startup", "-Screenshot", screenshot }) readyStart.ArgumentList.Add(arg);
            using (var ready = Process.Start(readyStart) ?? throw new IOException("Cannot observe actual UI readiness."))
            {
                Task<string> readyOutput = ready.StandardOutput.ReadToEndAsync(), readyError = ready.StandardError.ReadToEndAsync();
                try { await ready.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
                finally { if (!ready.HasExited) { ready.Kill(true); await ready.WaitForExitAsync(); } }
                Require(ready.ExitCode == 0, "Initialization did not finish: " + await readyError);
                o["initialization"] = await readyOutput;
            }
            Require(process.CloseMainWindow(), "Test UI did not accept normal close.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(process.ExitCode == 0, "Test UI failed: " + await error);
            o["startupOutput"] = await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
