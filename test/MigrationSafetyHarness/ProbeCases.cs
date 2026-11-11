using System.Text;
using MigrationCore.Models;
using MigrationCore.Services;
using Identity = DiskLabPath.Identity;

internal static partial class Program
{
    private static readonly string[] ProbeCaseNames =
    ["normal-roundtrip", "A01-original-name-conflict", "A02-deep-file-lock",
     "cancel-before-rename", "record-failure-before-rename", "registered-restore-roundtrip",
     "readonly-path-admission", "unknown-reparse-ancestor"];

    private sealed class ProbeObserver(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static (string Source, string Target, string File, byte[] Bytes, Identity FileIdentity) ProbeFixture(
        Lab lab, Guid attempt, string name, bool deep = false)
    {
        string scope = Path.Combine(lab.Source.Root, @"Data\Sources\Sample");
        string source = Path.Combine(scope, $"Probe-{attempt:D}-{name}");
        string target = Path.Combine(lab.Target.Root, @"Data\Targets", $"Probe-{attempt:D}-{name}");
        NewDirectory(lab, lab.Source, scope, source);
        string parent = source;
        if (deep)
        {
            parent = Path.Combine(source, "深层 空格");
            NewDirectory(lab, lab.Source, source, parent);
        }
        string file = Path.Combine(parent, PayloadName);
        byte[] bytes = Encoding.UTF8.GetBytes($"Probe fixture {attempt:D} {name}：原始字节");
        Identity fileIdentity = WriteFile(lab, lab.Source, source, file, bytes);
        return (source, target, file, bytes, fileIdentity);
    }

    private static async Task RunProbeCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        var identity = new PathIdentityService();
        var store = new MigrationTransactionStore();
        await RunCase(lab, cases[0], async o =>
        {
            var f = ProbeFixture(lab, attempt, "正常 空格");
            lab.BeforeWrite();
            var record = await MigrationWorkflowHelper.PrepareTransactionAsync(
                new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }, MigrationMode.Migrate, CancellationToken.None);
            var result = await SimpleFileLockDetector.CheckMigrationAsync(record, null, CancellationToken.None);
            Require(result.CanProceed && !result.RecoveryRequired && result.ActualDataPath == f.Source,
                "Successful probe did not restore the original data location.");
            identity.RequireSame(f.Source, record.SourceIdentity);
            Require(!Directory.Exists(record.ProbePath) && !Directory.Exists(f.Target), "Probe left a renamed directory or started a copy.");
            var saved = await store.LoadAsync(record.TransactionId);
            Require(saved?.Stage == MigrationStage.Prepared && saved.Revision == record.Revision + 2,
                "Successful roundtrip was not durably recorded.");
            o["source"] = f.Source; o["target"] = f.Target; o["transactionId"] = record.TransactionId;
            o["sourceIdentity"] = record.SourceIdentity; o["stage"] = saved.Stage.ToString();
            o["sha256"] = VerifyFile(lab.Source, f.Source, f.File, f.FileIdentity, f.Bytes);
        });
        await RunCase(lab, cases[1], async o =>
        {
            var f = ProbeFixture(lab, attempt, "conflict");
            lab.BeforeWrite();
            var record = await MigrationWorkflowHelper.PrepareTransactionAsync(
                new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }, MigrationMode.Migrate, CancellationToken.None);
            byte[] conflict = Encoding.UTF8.GetBytes("The newly created original-name directory must survive.");
            Identity? conflictFile = null;
            bool observed = false;
            var observer = new ProbeObserver(_ =>
            {
                var saved = store.LoadAsync(record.TransactionId).GetAwaiter().GetResult();
                if (saved?.Stage != MigrationStage.ProbeRenamed || observed) return;
                observed = true;
                identity.RequireSame(record.ProbePath!, record.SourceIdentity);
                string scope = Path.GetDirectoryName(f.Source)!;
                NewDirectory(lab, lab.Source, scope, f.Source);
                conflictFile = WriteFile(lab, lab.Source, f.Source, f.File, conflict);
            });
            var result = await SimpleFileLockDetector.CheckMigrationAsync(record, observer, CancellationToken.None);
            Require(observed && !result.CanProceed && result.RecoveryRequired && result.ActualDataPath == record.ProbePath,
                "Restore conflict did not report the preserved original entity's actual location.");
            var saved = await store.LoadAsync(record.TransactionId);
            Require(saved?.Stage == MigrationStage.RecoveryRequired, "Restore conflict was not durably marked for recovery.");
            identity.RequireSame(result.ActualDataPath, record.SourceIdentity);
            Require(conflictFile is not null && !Directory.Exists(f.Target), "Conflict fixture is missing or copy started after failed probe.");
            o["source"] = f.Source; o["target"] = f.Target; o["actualDataPath"] = result.ActualDataPath;
            o["transactionId"] = record.TransactionId; o["stage"] = saved.Stage.ToString(); o["error"] = result.Error;
            o["originalSha256"] = VerifyFile(lab.Source, result.ActualDataPath, Path.Combine(result.ActualDataPath, PayloadName), f.FileIdentity, f.Bytes, false);
            o["conflictSha256"] = VerifyFile(lab.Source, f.Source, f.File, conflictFile!, conflict);
        });
        await RunCase(lab, cases[2], async o =>
        {
            var f = ProbeFixture(lab, attempt, "deep-lock", true);
            lab.BeforeWrite();
            var record = await MigrationWorkflowHelper.PrepareTransactionAsync(
                new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }, MigrationMode.Migrate, CancellationToken.None);
            using var held = new FileStream(f.File, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await SimpleFileLockDetector.CheckMigrationAsync(record, null, CancellationToken.None);
            Require(!result.CanProceed && !result.RecoveryRequired && result.ActualDataPath == f.Source,
                "Deep file lock was not rejected with the original entity intact.");
            identity.RequireSame(f.Source, record.SourceIdentity);
            Require(!Directory.Exists(record.ProbePath) && !Directory.Exists(f.Target), "Locked source was renamed or copied.");
            Require(result.Diagnostics.Count != 0 && result.Diagnostics.All(d => d.ProcessExited && d.Status != HandleDiagnosticStatus.NotFound),
                "A known held handle was misreported as unoccupied, or a diagnostic process remains alive.");
            Require((await store.LoadAsync(record.TransactionId))?.Stage == MigrationStage.Failed,
                "Rejected lock probe did not persist failure.");
            o["source"] = f.Source; o["target"] = f.Target; o["heldFile"] = f.File;
            o["diagnostics"] = result.Diagnostics; o["error"] = result.Error;
            o["sha256"] = VerifyFile(lab.Source, f.Source, f.File, f.FileIdentity, f.Bytes);
        });
        await RunCase(lab, cases[3], async o =>
        {
            var f = ProbeFixture(lab, attempt, "cancel");
            lab.BeforeWrite();
            var record = await MigrationWorkflowHelper.PrepareTransactionAsync(
                new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }, MigrationMode.Migrate, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = await SimpleFileLockDetector.CheckMigrationAsync(record, null, cancellation.Token);
            Require(!result.CanProceed && !result.RecoveryRequired && result.Diagnostics.Count == 0,
                "Pre-cancelled probe attempted a rename diagnostic or lost source certainty.");
            identity.RequireSame(f.Source, record.SourceIdentity);
            Require(!Directory.Exists(record.ProbePath) && !Directory.Exists(f.Target), "Pre-cancelled probe changed data paths.");
            Require((await store.LoadAsync(record.TransactionId))?.Stage == MigrationStage.Cancelled, "Cancellation was not persisted.");
            o["source"] = f.Source; o["actualDataPath"] = result.ActualDataPath;
            o["sha256"] = VerifyFile(lab.Source, f.Source, f.File, f.FileIdentity, f.Bytes);
        });
        await RunCase(lab, cases[4], async o =>
        {
            var f = ProbeFixture(lab, attempt, "write-failure");
            DirectoryIdentity original = identity.ReadDirectory(f.Source, false);
            int count = (await store.ReadAllAsync()).Length;
            DirectoryProbeResult result;
            lab.BeforeWrite();
            using (var held = new FileStream(Path.Combine(store.DirectoryPath, ".transactions.lock"), FileMode.Open, FileAccess.Read, FileShare.None))
                result = await MigrationWorkflowHelper.ProbeAsync(new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target },
                    MigrationMode.Migrate, null, CancellationToken.None);
            Require(!result.CanProceed && result.Transaction is null && !result.RecoveryRequired,
                "Failed initial record write was not rejected before rename.");
            identity.RequireSame(f.Source, original);
            Require(!Directory.Exists(f.Target) && (await store.ReadAllAsync()).Length == count, "Failed record creation changed target or transactions.");
            o["source"] = f.Source; o["target"] = f.Target; o["error"] = result.Error;
            o["sha256"] = VerifyFile(lab.Source, f.Source, f.File, f.FileIdentity, f.Bytes);
        });
        await RunCase(lab, cases[5], async o =>
        {
            var f = ProbeFixture(lab, attempt, "restore");
            lab.BeforeWrite();
            var record = await MigrationWorkflowHelper.PrepareTransactionAsync(
                new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }, MigrationMode.Migrate, CancellationToken.None);
            NewDirectory(lab, lab.Target, Path.GetDirectoryName(f.Target)!, f.Target);
            Identity targetFile = WriteFile(lab, lab.Target, f.Target, Path.Combine(f.Target, PayloadName), f.Bytes);
            string backup = f.Source + ".bak." + record.TransactionId.ToString("D");
            record = await store.SaveAsync(record with { BackupPath = backup, TargetIdentity = identity.ReadDirectory(f.Target, false) }, record.Revision);
            lab.BeforeWrite(); lab.Source.Missing(backup, Path.GetDirectoryName(f.Source)!);
            Directory.Move(f.Source, backup);
            lab.BeforeWrite();
            Directory.CreateSymbolicLink(f.Source, f.Target);
            record = await store.SaveAsync(record with
            {
                BackupIdentity = record.SourceIdentity, LinkIdentity = identity.ReadDirectory(f.Source, false),
                Stage = MigrationStage.AwaitingUserVerification
            }, record.Revision);
            var prepared = await MigrationWorkflowHelper.PrepareTransactionAsync(
                new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }, MigrationMode.Restore, CancellationToken.None);
            var result = await SimpleFileLockDetector.CheckRestoreAsync(prepared, null, CancellationToken.None);
            Require(result.CanProceed && !result.RecoveryRequired && result.ActualDataPath == f.Target,
                "Registered restore probe did not restore the real target.");
            identity.RequireSame(f.Source, record.LinkIdentity);
            identity.RequireSame(f.Source, record.TargetIdentity, true);
            identity.RequireSame(backup, record.BackupIdentity);
            Require(!Directory.Exists(prepared.ProbePath), "Restore probe left a temporary data location.");
            o["sourceLink"] = f.Source; o["target"] = f.Target; o["backup"] = backup;
            o["transactionId"] = record.TransactionId;
            o["targetSha256"] = VerifyFile(lab.Target, f.Target, Path.Combine(f.Target, PayloadName), targetFile, f.Bytes);
            o["backupSha256"] = VerifyFile(lab.Source, backup, Path.Combine(backup, PayloadName), f.FileIdentity, f.Bytes, false);
        });
        await RunCase(lab, cases[6], o =>
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Require(!PathValidator.ValidateSourcePath(system).IsValid && !PathValidator.ValidateTargetPath(system).IsValid,
                "System directory was admitted for data operations.");
            string ancestor = Path.Combine(lab.Source.Root, @"Data\Sources");
            string child = Path.Combine(ancestor, "Sample");
            Require(!PathValidator.ValidatePathRelation(ancestor, child).IsValid
                && !PathValidator.ValidatePathRelation(child, ancestor).IsValid
                && !PathValidator.ValidateTargetPath(lab.Source.Root).IsValid,
                "Root or bidirectional path overlap was admitted.");
            o["systemDirectoryInputOnly"] = system; o["ancestor"] = ancestor; o["child"] = child;
            return Task.CompletedTask;
        });
        await RunCase(lab, cases[7], async o =>
        {
            string scope = Path.Combine(lab.Source.Root, @"Data\Sources\Sample");
            string protectedRoot = Path.Combine(lab.Source.Root, @"Data\Protected");
            string sentinel = Path.Combine(protectedRoot, $"Probe-{attempt:D}.txt");
            byte[] bytes = Encoding.UTF8.GetBytes("Protected fake data must not be traversed through an unregistered link.");
            Identity file = WriteFile(lab, lab.Source, protectedRoot, sentinel, bytes);
            string link = Path.Combine(scope, $"Probe-{attempt:D}-reparse");
            lab.BeforeWrite(); lab.Source.Missing(link, scope);
            Directory.CreateSymbolicLink(link, protectedRoot);
            Identity linkIdentity = lab.Source.Existing(link, scope, true);
            string input = Path.Combine(link, Path.GetFileName(sentinel));
            o["rejection"] = await RejectAsync<IOException>(() =>
            {
                MigrationRuntime.AssertDataPath(input);
                return Task.CompletedTask;
            });
            SameObject(linkIdentity, lab.Source.Existing(link, scope, true), "Rejected ancestor link changed.");
            o["link"] = link; o["input"] = input;
            o["protectedSha256"] = VerifyFile(lab.Source, protectedRoot, sentinel, file, bytes);
        });
    }
}
