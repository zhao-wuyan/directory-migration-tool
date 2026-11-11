using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MigrationCore.Models;
using MigrationCore.Services;

internal static partial class Program
{
    private static readonly string[] BackupCaseNames = ["A18-unverified", "A18-no-confirmations", "A18-first-only",
        "A18-order-and-name", "A18-cancel-First", "A18-cancel-Second", "A17-stale-revision",
        "A17-replaced-backup", "A17-target-changed", "A18-backup-changed", "A18-new-reparse",
        "A18-other-transaction", "A18-unknown-candidate", "A19-recycle-unsupported", "A19-partial-delete",
        "A19-permanent-delete", "A20-restored-source-changed", "A21-real-wpf"];

    private static async Task RunBackupCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        var service = new BackupManagementService();
        var store = new MigrationTransactionStore();
        var identity = new PathIdentityService();
        async Task Case(string name, Func<Dictionary<string, object?>, Task> action) =>
            await RunCase(lab, cases.Single(c => c.Name == name), action);
        async Task<CopyFixture> Fixture(string name, Dictionary<string, object?> o)
        {
            var f = NewSwitchFixture(lab, attempt, "backup-" + name);
            AssertSwitched(f, await MigrateSwitch(f), o);
            return f;
        }
        async Task<BackupDeletionRequest> Request(CopyFixture f, bool confirmed = false, BackupDeletionMode mode = BackupDeletionMode.Permanent)
        {
            var record = CopyRecord(f);
            await service.MarkUserVerifiedAsync(record.TransactionId);
            var request = await service.CreateDeletionRequestAsync(record.TransactionId, mode);
            if (confirmed)
            {
                await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null);
                request = await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, Path.GetFileName(record.BackupPath));
            }
            return request;
        }
        void Intact(CopyFixture f)
        {
            var record = CopyRecord(f);
            identity.RequireSame(record.BackupPath!, f.SourceIdentity);
            AssertSwitchBytes(f, record.BackupPath!);
            AssertSwitchBytes(f, f.Target);
        }
        await Case("A18-unverified", async o =>
        {
            var f = await Fixture("unverified", o);
            o["rejection"] = await RejectAsync<IOException>(() => service.CreateDeletionRequestAsync(CopyRecord(f).TransactionId, BackupDeletionMode.Permanent));
            Intact(f);
            Require(CopyRecord(f).DeletionRequest is null, "Unverified data acquired a deletion request.");
        });
        await Case("A18-no-confirmations", async o =>
        {
            var f = await Fixture("no-confirmations", o); var request = await Request(f);
            o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            Intact(f);
            Require(CopyRecord(f).DeletionRequest?.Consumed == false, "Rejected incomplete authorization was consumed.");
        });
        await Case("A18-first-only", async o =>
        {
            var f = await Fixture("first-only", o); var request = await Request(f);
            await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null);
            o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            Intact(f);
        });
        await Case("A18-order-and-name", async o =>
        {
            var f = await Fixture("order-name", o); var request = await Request(f);
            o["order"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, Path.GetFileName(CopyRecord(f).BackupPath)));
            await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null);
            o["repeatFirst"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null));
            o["wrongName"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, "not-the-backup"));
            Require(CopyRecord(f).DeletionRequest?.SecondConfirmedAtUtc is null, "Wrong name acquired second confirmation.");
            Intact(f);
        });
        foreach (var step in new[] { ConfirmationStep.First, ConfirmationStep.Second })
            await Case($"A18-cancel-{step}", async o =>
            {
                var f = await Fixture("cancel-" + step, o); var request = await Request(f);
                if (step == ConfirmationStep.Second) await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                o["cancellation"] = await RejectAsync<OperationCanceledException>(() => service.ConfirmDeletionAsync(request.RequestId, step, Path.GetFileName(CopyRecord(f).BackupPath), cancelled.Token));
                o["deleteRejected"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
                Intact(f);
            });
        await Case("A17-stale-revision", async o =>
        {
            var f = await Fixture("stale", o); var request = await Request(f, true);
            var record = CopyRecord(f);
            await store.SaveAsync(record with { LastError = "Concurrent state change" }, record.Revision);
            o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            Require(CopyRecord(f).DeletionRequest?.Consumed == true && CopyRecord(f).UserVerifiedAtUtc is null, "Stale authorization was not invalidated.");
            Intact(f);
        });
        await Case("A17-replaced-backup", async o =>
        {
            var f = await Fixture("replacement", o); var request = await Request(f, true);
            var record = CopyRecord(f); string backup = record.BackupPath!; string retained = backup + "-preserved";
            lab.BeforeWrite(); Directory.Move(backup, retained);
            NewDirectory(lab, lab.Source, Path.GetDirectoryName(backup)!, backup);
            byte[] bytes = Encoding.UTF8.GetBytes("replacement must survive");
            var file = WriteFile(lab, lab.Source, backup, Path.Combine(backup, "replacement.txt"), bytes);
            o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            o["preservedOriginal"] = retained;
            identity.RequireSame(retained, f.SourceIdentity); AssertSwitchBytes(f, retained); AssertSwitchBytes(f, f.Target);
            VerifyFile(lab.Source, backup, Path.Combine(backup, "replacement.txt"), file, bytes);
        });
        foreach (bool target in new[] { true, false })
            await Case(target ? "A17-target-changed" : "A18-backup-changed", async o =>
            {
                var f = await Fixture(target ? "target-change" : "backup-change", o); var request = await Request(f, true);
                string root = target ? f.Target : CopyRecord(f).BackupPath!;
                byte[] bytes = Encoding.UTF8.GetBytes("unknown new content");
                var file = WriteFile(lab, target ? lab.Target : lab.Source, root, Path.Combine(root, "new.txt"), bytes);
                o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
                VerifyFile(target ? lab.Target : lab.Source, root, Path.Combine(root, "new.txt"), file, bytes); Intact(f);
            });
        await Case("A18-new-reparse", async o =>
        {
            var f = await Fixture("reparse", o); var request = await Request(f, true);
            var protectedData = NewSwitchFixture(lab, attempt, "backup-protected");
            string link = Path.Combine(CopyRecord(f).BackupPath!, "unexpected-link");
            lab.BeforeWrite(); Require(SymbolicLinkHelper.CreateDirectorySymbolicLink(link, protectedData.Source), "Cannot create lab reparse fixture.");
            var linkIdentity = identity.ReadDirectory(link, false);
            o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            identity.RequireSame(link, linkIdentity); AssertSwitchBytes(protectedData, protectedData.Source); Intact(f);
        });
        await Case("A18-other-transaction", async o =>
        {
            var f = await Fixture("referenced", o); var request = await Request(f, true); var record = CopyRecord(f);
            var other = await store.CreateAsync(new MigrationTransactionRecord
            {
                SourcePath = record.BackupPath!, SourceIdentity = record.BackupIdentity,
                TargetPath = f.Target + "-other", TargetParentIdentity = identity.ReadDirectory(Path.GetDirectoryName(f.Target)!, false)
            });
            o["otherTransaction"] = other.TransactionId;
            o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            Intact(f);
            await store.SaveAsync(other with { Stage = MigrationStage.Cancelled }, other.Revision);
        });
        await Case("A18-unknown-candidate", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "unknown-backup");
            string candidate = f.Source + ".bak_unknown";
            NewDirectory(lab, lab.Source, Path.GetDirectoryName(f.Source)!, candidate);
            byte[] bytes = Encoding.UTF8.GetBytes("not owned");
            var file = WriteFile(lab, lab.Source, candidate, Path.Combine(candidate, "unknown.txt"), bytes);
            var task = MigrationStateDetector.DetectMigrationState(new QuickMigrateTask { SourcePath = f.Source, TargetPath = f.Target });
            Require(task.Transaction is null && task.MigrationState == MigrationState.Inconsistent && !task.CanManageBackup && !task.CanVerifyData,
                "Unknown backup was claimed as owned or deletable.");
            o["candidate"] = task.BackupPath; o["status"] = task.StatusMessage;
            o["rejection"] = await RejectAsync<IOException>(() => service.MarkUserVerifiedAsync(Guid.NewGuid()));
            VerifyFile(lab.Source, candidate, Path.Combine(candidate, "unknown.txt"), file, bytes); AssertSwitchBytes(f, f.Source);
        });
        await Case("A19-recycle-unsupported", async o =>
        {
            var f = await Fixture("recycle", o); var request = await Request(f, true, BackupDeletionMode.Recycle);
            var result = await service.DeleteAsync(request.RequestId);
            Require(result.Status == BackupDeletionStatus.Unsupported && result.Items.All(i => i.Outcome == BackupItemOutcome.Unsupported),
                "Unproven recycling did not fail closed.");
            Require(CopyRecord(f).LastDeletionResult?.Status == BackupDeletionStatus.Unsupported && CopyRecord(f).DeletionRequest?.Consumed == true,
                "Unsupported result or consumed authorization was not persisted.");
            o["result"] = result; Intact(f);
            var expectedItems = (await new CopyVerificationService().CaptureAsync(CopyRecord(f).BackupPath!, VerificationMode.Basic, CancellationToken.None))
                .Entries.Keys.Select(p => Path.Combine(CopyRecord(f).BackupPath!, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            o["repeatRejected"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            Require(expectedItems.SetEquals(result.Items.Select(i => i.Path)), "Recycle result omitted or invented an item.");
        });
        await Case("A19-partial-delete", async o =>
        {
            var f = await Fixture("partial", o); var request = await Request(f, true); string backup = CopyRecord(f).BackupPath!;
            BackupDeletionResult result;
            using (var held = new FileStream(Path.Combine(backup, "零字节"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                result = await service.DeleteAsync(request.RequestId);
            Require(result.Status == BackupDeletionStatus.Partial && result.Items.Any(i => i.Outcome == BackupItemOutcome.Deleted)
                && result.Items.Any(i => i.Outcome == BackupItemOutcome.Failed), "Native sharing conflict did not produce auditable partial deletion.");
            Require(!File.Exists(Path.Combine(backup, "正文.txt")) && File.Exists(Path.Combine(backup, "零字节")), "Partial deletion did not match recorded per-item results.");
            var record = CopyRecord(f);
            Require(record.BackupState == BackupState.PartiallyDeleted && !record.BackupDeletionInProgress && record.DeletionRequest?.Consumed == true,
                "Incomplete backup is being advertised as complete or authorized.");
            record = await service.MarkUserVerifiedAsync(record.TransactionId);
            Require(record.BackupState == BackupState.PartiallyDeleted, "User verification resurrected an incomplete backup.");
            o["newRequestRejected"] = await RejectAsync<IOException>(() => service.CreateDeletionRequestAsync(record.TransactionId, BackupDeletionMode.Permanent));
            var task = MigrationStateDetector.DetectMigrationState(new QuickMigrateTask { SourcePath = f.Source, TargetPath = f.Target });
            Require(task.BackupState == BackupState.PartiallyDeleted && !task.CanManageBackup,
                "Refreshed task lost partial state or re-enabled deletion.");
            AssertSwitchBytes(f, f.Target); AssertSwitchBytes(f, f.Source); o["result"] = result;
        });
        await Case("A19-permanent-delete", async o =>
        {
            var f = await Fixture("permanent", o); var request = await Request(f, true); string backup = CopyRecord(f).BackupPath!;
            var expectedItems = (await new CopyVerificationService().CaptureAsync(backup, VerificationMode.Basic, CancellationToken.None))
                .Entries.Keys.Select(p => Path.Combine(backup, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await service.DeleteAsync(request.RequestId);
            Require(result.Status == BackupDeletionStatus.Deleted && !Directory.Exists(backup) && result.Items.All(i => i.Outcome == BackupItemOutcome.Deleted),
                "Valid authorization did not delete exactly the original backup: " + result.Error);
            Require(expectedItems.SetEquals(result.Items.Select(i => i.Path)), "Permanent deletion omitted or invented an item result.");
            AssertSwitchBytes(f, f.Target); AssertSwitchBytes(f, f.Source);
            var task = MigrationStateDetector.DetectMigrationState(new QuickMigrateTask { SourcePath = f.Source, TargetPath = f.Target });
            Require(task.BackupState == BackupState.Deleted && !task.CanManageBackup, "Refresh lost completed deletion state.");
            o["repeatRejected"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId)); o["result"] = result;
        });
        await Case("A20-restored-source-changed", async o =>
        {
            var f = await Fixture("restored", o);
            var restored = await new ReversibleMigrationService(SwitchConfig(f), MigrationMode.Restore).ExecuteAsync();
            Require(restored.Success, "Restore fixture failed: " + restored.ErrorMessage);
            var request = await Request(f, true);
            var bytes = Encoding.UTF8.GetBytes("new source after confirmation");
            var file = WriteFile(lab, lab.Source, f.Source, Path.Combine(f.Source, "new.txt"), bytes);
            o["rejection"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
            VerifyFile(lab.Source, f.Source, Path.Combine(f.Source, "new.txt"), file, bytes); Intact(f);
        });
        await Case("A21-real-wpf", async o =>
        {
            var initial = NewSwitchFixture(lab, attempt, "ui");
            NewDirectory(lab, lab.Target, Path.GetDirectoryName(initial.Target)!, initial.Target);
            var f = initial with { Target = Path.Combine(initial.Target, Path.GetFileName(initial.Source)) };
            AssertSwitched(f, await MigrateSwitch(f), o);
            await VerifyBackupUi(lab, f, attempt, o);
            Require(CopyRecord(f).BackupState == BackupState.Deleted && !Directory.Exists(CopyRecord(f).BackupPath), "UI did not persist the authorized outcome.");
            AssertSwitchBytes(f, f.Target); AssertSwitchBytes(f, f.Source);
        });
    }

    private static async Task VerifyBackupUi(Lab lab, CopyFixture f, Guid attempt, Dictionary<string, object?> o)
    {
        string app = Environment.GetEnvironmentVariable("MIGRATION_TEST_LAB_APP") ?? throw new IOException("App package not supplied.");
        Require(Path.GetDirectoryName(app) == Path.Combine(lab.WorkRoot, "App"), "UI is outside the registered package.");
        ParseGuid(Path.GetFileName(app), "app attempt ID"); DiskLabPath.AssertOrdinaryPath(app, false);
        string configPath = Path.Combine(app, "quick-migrate.json"); OrdinarySingleFile(configPath);
        string original = await File.ReadAllTextAsync(configPath);
        string script = Path.Combine(AppContext.BaseDirectory, "BackupUi.ps1"); OrdinarySingleFile(script);
        string runtime = Directory.EnumerateFiles(app, "*.runtimeconfig.json").Single();
        string executable = runtime[..^".runtimeconfig.json".Length] + ".exe"; OrdinarySingleFile(executable);
        try
        {
            lab.BeforeWrite();
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                defaults = new { targetStrategy = "unified", unifiedTargetRoot = Path.GetDirectoryName(f.Target), robocopyThreads = 2 },
                profiles = Array.Empty<object>(), standaloneSources = new[] { new { displayName = "Backup UI", absolutePath = f.Source, enabled = true } }
            }));
            foreach (string mode in new[] { "Exercise", "Restart" })
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--test-lab"); start.ArgumentList.Add(Path.Combine(lab.WorkRoot, "lab.json"));
                start.ArgumentList.Add("--run-id"); start.ArgumentList.Add(lab.RunId.ToString("D"));
                using var process = Process.Start(start) ?? throw new IOException("Cannot start dedicated UI.");
                var appOutput = process.StandardOutput.ReadToEndAsync(); var appError = process.StandardError.ReadToEndAsync();
                try
                {
                    string screenshot = Path.Combine(lab.WorkRoot, "Results", $"{attempt:D}-backup-ui-{mode}.png");
                    lab.BeforeWrite(); lab.CheckRoots(); DiskLabPath.AssertOrdinaryPath(screenshot, true);
                    var automationStart = new ProcessStartInfo("powershell.exe")
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-AppProcessId", process.Id.ToString(),
                        "-BackupPath", CopyRecord(f).BackupPath!, "-Mode", mode, "-Screenshot", screenshot }) automationStart.ArgumentList.Add(arg);
                    using var automation = Process.Start(automationStart) ?? throw new IOException("Cannot start UI automation.");
                    var stdout = automation.StandardOutput.ReadToEndAsync(); var stderr = automation.StandardError.ReadToEndAsync();
                    try { await automation.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                    finally { if (!automation.HasExited) { automation.Kill(true); await automation.WaitForExitAsync(); } }
                    string result = await stdout, error = await stderr;
                    o[mode + "Ui"] = result; o[mode + "Screenshot"] = screenshot;
                    Require(automation.ExitCode == 0, "Real UI verification failed: " + result + error);
                    Require(process.CloseMainWindow(), "UI did not accept safe close.");
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Require(process.ExitCode == 0, "Dedicated UI exited with failure.");
                }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                o[mode + "AppOutput"] = await appOutput;
                o[mode + "AppError"] = await appError;
            }
        }
        finally
        {
            lab.BeforeWrite(); OrdinarySingleFile(configPath);
            await File.WriteAllTextAsync(configPath, original);
        }
    }
}
