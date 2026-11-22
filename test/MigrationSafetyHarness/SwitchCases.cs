using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MigrationCore.Models;
using MigrationCore.Services;

internal static partial class Program
{
    private static readonly string[] SwitchCaseNames = ["A10-manual-roundtrip", "A10-quick-roundtrip",
        "A20-empty-repair", "A20-data-repair", "A11-wrong-link", "A12-native-link-failure",
        "A12-source-conflict", "A12-target-new-write", "A12-matching-new-writes", "A13-cancel-before-create",
        "A13-cancel-SourceRenamed", "A13-cancel-LinkCreated", "A13-restore-cancel-vacated",
        "A14-restore-conflict-preserves", "A15-before-rename-save-failure", "A15-after-rename-save-failure",
        "A16-restart-SourceRenamed", "A16-restart-LinkCreated", "A16-corrupt-no-candidate", "A20-old-json-no-delete"];

    private static CopyFixture NewSwitchFixture(Lab lab, Guid attempt, string name)
    {
        string scope = Path.Combine(lab.Source.Root, @"Data\Sources\Sample");
        string parent = Path.Combine(scope, $"Switch-{attempt:D}-{name}");
        NewDirectory(lab, lab.Source, scope, parent);
        string source = Path.Combine(parent, "源 data");
        NewDirectory(lab, lab.Source, parent, source);
        var files = new Dictionary<string, (DiskLabPath.Identity, byte[])>();
        foreach (var pair in new[] { ("正文.txt", "switch fixture content"), ("零字节", "") })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(pair.Item2);
            files[pair.Item1] = (WriteFile(lab, lab.Source, source, Path.Combine(source, pair.Item1), bytes), bytes);
        }
        NewDirectory(lab, lab.Source, source, Path.Combine(source, "空目录"));
        return new CopyFixture(source, Path.Combine(lab.Target.Root, @"Data\Targets", $"Switch-{attempt:D}-{name}"),
            new PathIdentityService().ReadDirectory(source, false), files);
    }

    private static MigrationConfig SwitchConfig(CopyFixture f) => new()
    { SourcePath = f.Source, TargetPath = f.Target, RobocopyThreads = 2, SampleMilliseconds = 20 };

    private static void AssertSwitchBytes(CopyFixture f, string root)
    {
        foreach (var file in f.Files)
            Require(File.ReadAllBytes(Path.Combine(root, file.Key)).AsSpan().SequenceEqual(file.Value.Bytes), $"Data changed at {root}.");
        Require(Directory.Exists(Path.Combine(root, "空目录")), "Empty directory disappeared.");
    }

    private static async Task<MigrationResult> MigrateSwitch(CopyFixture f, bool manual = true,
        IProgress<MigrationProgress>? progress = null, CancellationToken ct = default)
    {
        var result = manual ? await new MigrationService(SwitchConfig(f)).ExecuteMigrationAsync(progress, cancellationToken: ct)
            : await new ReversibleMigrationService(SwitchConfig(f)).ExecuteAsync(progress, cancellationToken: ct);
        return result;
    }

    private static void AssertSwitched(CopyFixture f, MigrationResult result, Dictionary<string, object?> o)
    {
        Require(result.Success && result.Stage == MigrationStage.AwaitingUserVerification && result.TransactionId is not null,
            "Migration failed: " + result.ErrorMessage);
        var record = CopyRecord(f);
        var identity = new PathIdentityService();
        identity.RequireSame(f.Source, record.LinkIdentity);
        identity.RequireSame(f.Source, record.TargetIdentity, true);
        identity.RequireSame(record.BackupPath!, f.SourceIdentity);
        Require(record.BackupState == BackupState.RetainedUnverified && record.UserVerifiedAtUtc is null,
            "Technical completion incorrectly authorized backup deletion.");
        AssertSwitchBytes(f, record.BackupPath!); AssertSwitchBytes(f, f.Target); AssertSwitchBytes(f, f.Source);
        o["source"] = f.Source; o["target"] = f.Target; o["backup"] = record.BackupPath;
        o["transactionId"] = record.TransactionId; o["verification"] = result.VerificationSummary;
    }

    private static async Task RunSwitchCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        var identity = new PathIdentityService();
        async Task Case(string name, Func<Dictionary<string, object?>, Task> action) =>
            await RunCase(lab, cases.Single(c => c.Name == name), action);
        foreach (bool manual in new[] { true, false })
        {
            await Case(manual ? "A10-manual-roundtrip" : "A10-quick-roundtrip", async o =>
            {
                var f = NewSwitchFixture(lab, attempt, manual ? "manual" : "quick");
                var migrated = await MigrateSwitch(f, manual);
                AssertSwitched(f, migrated, o);
                var before = CopyRecord(f);
                var restored = await new ReversibleMigrationService(SwitchConfig(f), MigrationMode.Restore).ExecuteAsync();
                Require(restored.Success && restored.Stage == MigrationStage.AwaitingUserVerification,
                    "Restore failed: " + restored.ErrorMessage);
                var after = CopyRecord(f);
                Require(after.TransactionId != before.TransactionId && after.BackupPath == f.Target + ".bak_" + after.TransactionId.ToString("D"),
                    "Restore did not create an independent transaction and target backup.");
                identity.RequireSame(f.Source, after.StagingIdentity);
                identity.RequireSame(after.BackupPath!, before.TargetIdentity);
                identity.RequireSame(before.BackupPath!, before.BackupIdentity);
                Require(!Directory.Exists(f.Target), "Restore left old target unrenamed.");
                Require(!identity.ReadDirectory(f.Source, false).IsReparsePoint, "Restored source is still a link.");
                AssertSwitchBytes(f, f.Source); AssertSwitchBytes(f, after.BackupPath!); AssertSwitchBytes(f, before.BackupPath!);
                var task = MigrationStateDetector.DetectMigrationState(new QuickMigrateTask
                { SourcePath = f.Source, TargetPath = f.Target, Transaction = after });
                Require(task.MigrationState == MigrationState.Pending && task.Transaction?.Stage == MigrationStage.AwaitingUserVerification,
                    "Restore result did not survive a state refresh.");
                o["restoredIdentity"] = after.StagingIdentity; o["restoreBackup"] = after.BackupPath;
            });
        }
        await Case("A20-empty-repair", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "empty-repair");
            string empty = Path.Combine(Path.GetDirectoryName(f.Source)!, "空源");
            NewDirectory(lab, lab.Source, Path.GetDirectoryName(empty)!, empty);
            var repaired = await new RepairService(new MigrationConfig { SourcePath = empty, TargetPath = f.Source }).ExecuteAsync();
            Require(repaired.Success && repaired.BackupPath is null && repaired.BackupState is null,
                "Empty repair failed or invented a backup: " + repaired.ErrorMessage);
            identity.RequireSame(empty, f.SourceIdentity, true);
            AssertSwitchBytes(f, f.Source);
            var task = MigrationStateDetector.DetectMigrationState(new QuickMigrateTask { SourcePath = empty, TargetPath = f.Source });
            Require(task.MigrationState == MigrationState.Migrated && task.BackupPath is null,
                "Empty repair falsely advertises a retained backup.");
            o["source"] = empty; o["target"] = f.Source; o["backup"] = repaired.BackupPath; o["status"] = task.StatusMessage;
        });
        await Case("A20-data-repair", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "data-repair");
            var copy = await CopyFixtureData(lab, f);
            // 前置复制夹具会留下活动态 CopyVerified 事务；将其显式标记为非活动失败态，
            // 既保留生产代码对活动事务禁止修复的安全护栏，也覆盖"源/目标已有完整数据"的修复验收场景。
            await new MigrationTransactionStore().SaveAsync(
                copy.Transaction with { Stage = MigrationStage.Failed, LastError = "simulated prior stop" },
                copy.Transaction.Revision);
            var repaired = await new RepairService(SwitchConfig(f)).ExecuteAsync();
            AssertSwitched(f, repaired, o);
        });
        await Case("A11-wrong-link", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "wrong-link");
            AssertSwitched(f, await MigrateSwitch(f), o);
            var saved = CopyRecord(f);
            string preserved = f.Source + "-owned-link";
            lab.BeforeWrite(); Directory.Move(f.Source, preserved);
            var other = NewSwitchFixture(lab, attempt, "wrong-object");
            Require(SymbolicLinkHelper.CreateDirectorySymbolicLink(f.Source, other.Source), "Cannot inject wrong accessible link.");
            var wrong = identity.ReadDirectory(f.Source, false);
            var rejected = await new RepairService(SwitchConfig(f)).ExecuteAsync();
            Require(!rejected.Success, "Repair accepted an unknown link pointing to another object.");
            identity.RequireSame(f.Source, wrong); identity.RequireSame(f.Source, other.SourceIdentity, true);
            identity.RequireSame(preserved, saved.LinkIdentity); identity.RequireSame(saved.BackupPath!, saved.BackupIdentity);
            AssertSwitchBytes(other, other.Source); AssertSwitchBytes(f, f.Target); AssertSwitchBytes(f, saved.BackupPath!);
            o["rejection"] = rejected.ErrorMessage; o["wrongLinkPreserved"] = f.Source;
        });
        await Case("A12-native-link-failure", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "native-link-denied");
            string parent = Path.GetDirectoryName(f.Source)!;
            string original = new DirectoryInfo(parent).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            void RestoreAcl()
            {
                lab.BeforeWrite();
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
                new DirectoryInfo(parent).SetAccessControl(restored);
            }
            bool denied = false, restoredAcl = false;
            var observer = new CopyObserver(p =>
            {
                lab.BeforeWrite();
                if (p.Stage == MigrationStage.SourceRenamed && !denied)
                {
                    var acl = new DirectoryInfo(parent).GetAccessControl(AccessControlSections.All);
                    acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                        FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles, AccessControlType.Deny));
                    new DirectoryInfo(parent).SetAccessControl(acl);
                    denied = true;
                }
                if (p.Stage == MigrationStage.RecoveryRequired && denied && !restoredAcl)
                {
                    RestoreAcl();
                    restoredAcl = true;
                }
            });
            MigrationResult result;
            try { result = await MigrateSwitch(f, progress: observer); }
            finally { RestoreAcl(); }
            Require(denied && restoredAcl && !result.Success && result.RollbackOutcome == RollbackOutcome.Restored,
                "Native link failure was not safely restored: " + result.ErrorMessage);
            AssertCopySource(lab, f); AssertSwitchBytes(f, f.Target);
            o["source"] = f.Source; o["target"] = f.Target; o["error"] = result.ErrorMessage; o["rollback"] = result.RollbackOutcome.ToString();
        });
        foreach (bool conflict in new[] { true, false })
        {
            await Case(conflict ? "A12-source-conflict" : "A12-target-new-write", async o =>
            {
                var f = NewSwitchFixture(lab, attempt, conflict ? "conflict" : "target-new-write");
                bool injected = false;
                byte[] added = Encoding.UTF8.GetBytes("New user data must survive.");
                string? addedPath = null;
                var observer = new CopyObserver(p =>
                {
                    if (p.Stage != MigrationStage.SourceRenamed || injected) return;
                    injected = true; lab.BeforeWrite();
                    if (conflict) NewDirectory(lab, lab.Source, Path.GetDirectoryName(f.Source)!, f.Source);
                    addedPath = Path.Combine(conflict ? f.Source : f.Target, "new-user-data.txt");
                    WriteFile(lab, conflict ? lab.Source : lab.Target, conflict ? f.Source : f.Target, addedPath, added);
                });
                var result = await MigrateSwitch(f, progress: observer);
                Require(injected && !result.Success && result.RollbackOutcome == RollbackOutcome.RecoveryRequired
                    && result.Stage == MigrationStage.RecoveryRequired, "Conflicting entities were not retained.");
                var record = CopyRecord(f);
                identity.RequireSame(record.BackupPath!, f.SourceIdentity);
                AssertSwitchBytes(f, record.BackupPath!); AssertSwitchBytes(f, f.Target);
                Require(File.ReadAllBytes(addedPath!).AsSpan().SequenceEqual(added), "New user data was removed.");
                o["source"] = f.Source; o["target"] = f.Target; o["backup"] = record.BackupPath;
                o["recoveryPaths"] = result.RecoveryPaths; o["newData"] = addedPath;
            });
        }
        await Case("A12-matching-new-writes", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "matching-new-writes");
            byte[] bytes = Encoding.UTF8.GetBytes("New writes in both copies must remain separate.");
            bool injected = false;
            var observer = new CopyObserver(p =>
            {
                if (p.Stage != MigrationStage.SourceRenamed || injected) return;
                injected = true;
                var record = CopyRecord(f);
                var stamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                foreach (string root in new[] { record.BackupPath!, f.Target })
                {
                    WriteFile(lab, root == f.Target ? lab.Target : lab.Source, root, Path.Combine(root, "new.txt"), bytes);
                    File.SetLastWriteTimeUtc(Path.Combine(root, "new.txt"), stamp);
                    Directory.SetLastWriteTimeUtc(root, stamp);
                }
                var verifier = new CopyVerificationService();
                var current = verifier.CaptureAsync(record.BackupPath!, VerificationMode.Basic, CancellationToken.None).GetAwaiter().GetResult();
                Require(verifier.VerifyAsync(current, record.BackupPath!, f.Target, VerificationMode.Basic, CancellationToken.None).GetAwaiter().GetResult().IsMatch,
                    "Both changed collections must match before testing the persisted baseline.");
            });
            var result = await MigrateSwitch(f, progress: observer);
            Require(injected && !result.Success && result.RollbackOutcome == RollbackOutcome.RecoveryRequired,
                "Matching current collections replaced the original transaction baseline.");
            var saved = CopyRecord(f);
            identity.RequireSame(saved.BackupPath!, f.SourceIdentity);
            Require(!Directory.Exists(f.Source), "Changed copies were silently restored into the source name.");
            foreach (string root in new[] { saved.BackupPath!, f.Target })
                Require(File.ReadAllBytes(Path.Combine(root, "new.txt")).AsSpan().SequenceEqual(bytes), "New data disappeared.");
            o["backup"] = saved.BackupPath; o["target"] = f.Target; o["recoveryPaths"] = result.RecoveryPaths;
        });
        await Case("A13-cancel-before-create", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "cancel-before-create");
            using var ct = new CancellationTokenSource(); ct.Cancel();
            var result = await MigrateSwitch(f, ct: ct.Token);
            Require(!result.Success && result.Stage == MigrationStage.Cancelled && result.TransactionId is null
                && result.RollbackOutcome == RollbackOutcome.NotRequired, "Pre-transaction cancellation was not reported as cancelled.");
            AssertCopySource(lab, f); Require(!Directory.Exists(f.Target), "Cancelled preparation created a target.");
            o["source"] = f.Source; o["stage"] = result.Stage.ToString();
        });
        foreach (MigrationStage stage in new[] { MigrationStage.SourceRenamed, MigrationStage.LinkCreated })
        {
            await Case("A13-cancel-" + stage, async o =>
            {
                var f = NewSwitchFixture(lab, attempt, "cancel-" + stage);
                using var ct = new CancellationTokenSource();
                bool cancelled = false;
                var observer = new CopyObserver(p => { if (p.Stage == stage && !cancelled) { cancelled = true; ct.Cancel(); } });
                var result = await MigrateSwitch(f, progress: observer, ct: ct.Token);
                Require(cancelled && !result.Success && result.Stage == MigrationStage.Cancelled
                    && result.RollbackOutcome == RollbackOutcome.Restored, "Switch cancellation did not restore safely: " + result.ErrorMessage);
                AssertCopySource(lab, f); AssertSwitchBytes(f, f.Target);
                o["source"] = f.Source; o["target"] = f.Target; o["rollback"] = result.RollbackOutcome.ToString();
            });
        }
        await Case("A13-restore-cancel-vacated", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "restore-cancel");
            AssertSwitched(f, await MigrateSwitch(f), o);
            string migratedBackup = CopyRecord(f).BackupPath!;
            using var ct = new CancellationTokenSource();
            bool cancelled = false;
            var observer = new CopyObserver(p => { if (p.Stage == MigrationStage.SourceRenamed && !cancelled) { cancelled = true; ct.Cancel(); } });
            var result = await new ReversibleMigrationService(SwitchConfig(f), MigrationMode.Restore).ExecuteAsync(observer, cancellationToken: ct.Token);
            Require(cancelled && result.Stage == MigrationStage.Cancelled && result.RollbackOutcome == RollbackOutcome.Restored,
                "Restore cancellation did not restore the link: " + result.ErrorMessage);
            var record = CopyRecord(f);
            identity.RequireSame(f.Source, record.LinkIdentity); identity.RequireSame(f.Source, record.TargetIdentity, true);
            identity.RequireSame(record.StagingPath!, record.StagingIdentity);
            AssertSwitchBytes(f, record.StagingPath!); AssertSwitchBytes(f, f.Target); AssertSwitchBytes(f, migratedBackup);
            o["stagingRetained"] = record.StagingPath;
        });
        await Case("A14-restore-conflict-preserves", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "restore-conflict");
            AssertSwitched(f, await MigrateSwitch(f), o);
            string migratedBackup = CopyRecord(f).BackupPath!;
            var decoys = new[] { f.Source + ".restore_temp_foreign", f.Source + ".bak_foreign" };
            byte[] bytes = Encoding.UTF8.GetBytes("Other transaction payload");
            foreach (string dir in decoys)
            {
                NewDirectory(lab, lab.Source, Path.GetDirectoryName(dir)!, dir);
                WriteFile(lab, lab.Source, dir, Path.Combine(dir, "foreign.txt"), bytes);
            }
            bool injected = false;
            var observer = new CopyObserver(p =>
            {
                if (p.Stage != MigrationStage.SourceRenamed || injected) return;
                injected = true;
                NewDirectory(lab, lab.Source, Path.GetDirectoryName(f.Source)!, f.Source);
                WriteFile(lab, lab.Source, f.Source, Path.Combine(f.Source, "conflict.txt"), bytes);
            });
            var result = await new ReversibleMigrationService(SwitchConfig(f), MigrationMode.Restore).ExecuteAsync(observer);
            Require(injected && !result.Success && result.RollbackOutcome == RollbackOutcome.RecoveryRequired, "Restore replaced the conflicting source.");
            var record = CopyRecord(f);
            foreach (string dir in decoys.Append(f.Source))
                Require(File.ReadAllBytes(Path.Combine(dir, dir == f.Source ? "conflict.txt" : "foreign.txt")).AsSpan().SequenceEqual(bytes), "Foreign data was deleted.");
            AssertSwitchBytes(f, record.StagingPath!); AssertSwitchBytes(f, migratedBackup); AssertSwitchBytes(f, f.Target);
            o["decoys"] = decoys; o["staging"] = record.StagingPath; o["recoveryPaths"] = result.RecoveryPaths;
        });
        foreach (bool afterRename in new[] { false, true })
        {
            await Case(afterRename ? "A15-after-rename-save-failure" : "A15-before-rename-save-failure", async o =>
            {
                var f = NewSwitchFixture(lab, attempt, "write-failure-" + afterRename);
                FileStream? held = null;
                var observer = new CopyObserver(p =>
                {
                    bool reached = afterRename ? p.Stage == MigrationStage.SourceRenamed : p.CopyPhase == CopyPhase.Completed;
                    if (!reached || held is not null) return;
                    var record = CopyRecord(f);
                    string path = Path.Combine(MigrationRuntime.TransactionDirectory, record.TransactionId.ToString("D") + ".json");
                    held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                });
                MigrationResult result;
                try { result = await MigrateSwitch(f, progress: observer); }
                finally { held?.Dispose(); }
                Require(held is not null && !result.Success && result.Stage == MigrationStage.RecoveryRequired
                    && result.RollbackOutcome == RollbackOutcome.RecoveryRequired, "Record failure was hidden: " + result.ErrorMessage);
                var record = CopyRecord(f);
                if (afterRename)
                {
                    Require(record.Stage == MigrationStage.SourceRenamed, "Failed replace advanced the durable phase.");
                    identity.RequireSame(record.BackupPath!, f.SourceIdentity);
                    AssertSwitchBytes(f, record.BackupPath!); AssertSwitchBytes(f, f.Source);
                }
                else
                {
                    AssertCopySource(lab, f);
                    Require(record.BackupPath is null && record.Stage == MigrationStage.CopyVerified, "Source changed before backup intent was saved.");
                    Require(!Directory.Exists(f.Source + ".bak_" + record.TransactionId.ToString("D")), "Source renamed despite failed intent save.");
                }
                AssertSwitchBytes(f, f.Target);
                o["source"] = f.Source; o["target"] = f.Target; o["backup"] = record.BackupPath; o["durableStage"] = record.Stage.ToString();
                o["error"] = result.ErrorMessage; o["recoveryPaths"] = result.RecoveryPaths;
            });
        }
        foreach (MigrationStage stage in new[] { MigrationStage.SourceRenamed, MigrationStage.LinkCreated })
            await Case("A16-restart-" + stage, o => RestartSwitch(lab, NewSwitchFixture(lab, attempt, "restart-" + stage), stage, o));
        await Case("A16-corrupt-no-candidate", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "corrupt-record");
            string state = Path.Combine(MigrationRuntime.TransactionDirectory, attempt.ToString("D"), "corrupt-switch");
            var store = new MigrationTransactionStore(state);
            var record = await store.CreateAsync(new MigrationTransactionRecord
            {
                SourcePath = f.Source, TargetPath = f.Target, SourceIdentity = f.SourceIdentity,
                SourceParentIdentity = identity.ReadDirectory(Path.GetDirectoryName(f.Source)!, false),
                TargetParentIdentity = identity.ReadDirectory(Path.GetDirectoryName(f.Target)!, false)
            });
            string candidate = f.Source + ".bak_99999999";
            NewDirectory(lab, lab.Source, Path.GetDirectoryName(candidate)!, candidate);
            byte[] bytes = Encoding.UTF8.GetBytes("Unconfirmed candidate");
            WriteFile(lab, lab.Source, candidate, Path.Combine(candidate, "candidate.txt"), bytes);
            lab.BeforeWrite();
            File.WriteAllText(Path.Combine(state, record.TransactionId.ToString("D") + ".json"), "{broken", new UTF8Encoding(false));
            var task = MigrationStateDetector.DetectMigrationState(new QuickMigrateTask
            { SourcePath = f.Source, TargetPath = f.Target, Transaction = record }, store);
            Require(task.MigrationState == MigrationState.Inconsistent && !task.IsResumable && task.BackupPath is null,
                "Corrupt transaction fell back to a backup candidate.");
            AssertCopySource(lab, f);
            Require(File.ReadAllBytes(Path.Combine(candidate, "candidate.txt")).AsSpan().SequenceEqual(bytes), "Candidate was consumed.");
            o["state"] = task.StatusMessage; o["candidate"] = candidate;
        });
        await Case("A20-old-json-no-delete", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "legacy-json");
            AssertSwitched(f, await MigrateSwitch(f, manual: false), o);
            string migratedBackup = CopyRecord(f).BackupPath!;
            string path = MigrationRuntime.TestConfigPath!;
            byte[] original = File.ReadAllBytes(path);
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(original)!;
                node["defaults"]!["restoreKeepTarget"] = false;
                node["defaults"]!["autoCleanupBackup"] = true;
                lab.BeforeWrite(); File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(false));
                var loaded = QuickMigrateConfigLoader.LoadConfig() ?? throw new IOException("Legacy test config failed to load.");
                var config = SwitchConfig(f); config.RobocopyThreads = loaded.Defaults.RobocopyThreads;
                var result = await new ReversibleMigrationService(config, MigrationMode.Restore).ExecuteAsync();
                Require(result.Success, "Legacy JSON restore failed: " + result.ErrorMessage);
                AssertSwitchBytes(f, f.Source); AssertSwitchBytes(f, result.BackupPath!); AssertSwitchBytes(f, migratedBackup);
                o["restoreBackupRetained"] = result.BackupPath;
            }
            finally { lab.BeforeWrite(); File.WriteAllBytes(path, original); }
        });
    }

    private static ProcessStartInfo SwitchStart(Lab lab, params string[] extra)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--lab", Path.Combine(lab.WorkRoot, "lab.json"), "--run-id", lab.RunId.ToString("D"), "--suite", "Switch" }.Concat(extra))
            start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task RestartSwitch(Lab lab, CopyFixture f, MigrationStage stage, Dictionary<string, object?> o)
    {
        using Process child = Process.Start(SwitchStart(lab, "--switch-source", f.Source, "--switch-target", f.Target, "--stop-stage", stage.ToString()))
            ?? throw new IOException("Cannot launch controlled switch child.");
        Task<string> error = child.StandardError.ReadToEndAsync();
        MigrationTransactionRecord record;
        try
        {
            string line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new IOException("Child checkpoint missing: " + await error);
            using JsonDocument checkpoint = JsonDocument.Parse(line);
            Guid id = checkpoint.RootElement.GetProperty("transactionId").GetGuid();
            record = await new MigrationTransactionStore().LoadAsync(id) ?? throw new IOException("Durable child transaction missing.");
            Require(record.Stage == stage && !child.HasExited, "Child did not stop at a persisted checkpoint.");
            lab.BeforeWrite(); child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            o["terminatedProcessId"] = child.Id; o["persistedStage"] = stage.ToString(); o["backupBeforeRecovery"] = record.BackupPath;
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
        using Process reader = Process.Start(SwitchStart(lab, "--recover-transaction", record.TransactionId.ToString("D")))
            ?? throw new IOException("Cannot launch recovery child.");
        Task<string> output = reader.StandardOutput.ReadToEndAsync(), readerError = reader.StandardError.ReadToEndAsync();
        try
        {
            await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(reader.ExitCode == 0, "Recovery child failed: " + await readerError);
            using JsonDocument recovered = JsonDocument.Parse(await output);
            Require(recovered.RootElement.GetProperty("outcome").GetString() == "Restored", "New process could not restore the recorded entity.");
            Require(reader.Id != child.Id && reader.Id != Environment.ProcessId, "Recovery did not run in a fresh process.");
            AssertCopySource(lab, f); AssertSwitchBytes(f, f.Target);
            var saved = CopyRecord(f);
            Require(saved.Stage == MigrationStage.Failed && saved.BackupPath is null, "Restored transaction still claims a separate backup.");
            o["source"] = f.Source; o["target"] = f.Target; o["readerProcessId"] = reader.Id; o["transactionId"] = saved.TransactionId;
        }
        finally
        {
            if (!reader.HasExited) { reader.Kill(entireProcessTree: true); await reader.WaitForExitAsync(); }
        }
    }

    private static async Task<int> SwitchChild(string[] args)
    {
        try
        {
            Lab lab = LoadLab(args[..6]); MigrationRuntime.InitializeTestLab(args[1], lab.RunId);
            string source = PathIdentityService.Normalize(args[7]), target = PathIdentityService.Normalize(args[9]);
            MigrationRuntime.AssertDataPath(source); MigrationRuntime.AssertDataPath(target);
            MigrationStage stage = Enum.Parse<MigrationStage>(args[11]);
            var progress = new CopyObserver(p =>
            {
                if (p.Stage != stage) return;
                var record = new MigrationTransactionStore().ReadAllAsync().GetAwaiter().GetResult().Single(r => r.SourcePath == source && r.TargetPath == target);
                Require(record.Stage == stage, "Progress preceded its durable snapshot.");
                Console.WriteLine(JsonSerializer.Serialize(new { transactionId = record.TransactionId, stage = record.Stage.ToString() }));
                Console.Out.Flush();
                _ = Console.ReadLine() ?? throw new IOException("Checkpoint control closed unexpectedly.");
            });
            var result = await new MigrationService(new MigrationConfig { SourcePath = source, TargetPath = target, RobocopyThreads = 2, SampleMilliseconds = 20 })
                .ExecuteMigrationAsync(progress);
            throw new IOException("Child escaped the selected checkpoint: " + result.ErrorMessage);
        }
        catch (Exception ex) { Console.Error.WriteLine(Describe(ex)); return 2; }
    }

    private static async Task<int> RecoverSwitchChild(string[] args)
    {
        try
        {
            Lab lab = LoadLab(args[..6]); MigrationRuntime.InitializeTestLab(args[1], lab.RunId); lab.BeforeWrite();
            var record = await new MigrationTransactionStore().LoadAsync(Guid.Parse(args[7])) ?? throw new IOException("Recovery transaction missing.");
            var outcome = await MigrationWorkflowHelper.RollbackMigrationAsync(record);
            Console.WriteLine(JsonSerializer.Serialize(new { processId = Environment.ProcessId, outcome = outcome.ToString() }));
            return outcome == RollbackOutcome.Restored ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(Describe(ex)); return 2; }
    }
}
