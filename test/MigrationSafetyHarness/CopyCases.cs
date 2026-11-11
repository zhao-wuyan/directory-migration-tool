using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MigrationCore.Models;
using MigrationCore.Services;
using Identity = DiskLabPath.Identity;

internal static partial class Program
{
    private static readonly CopyPhase[] CancelCopyPhases = [CopyPhase.Preparing, CopyPhase.ProcessStarted,
        CopyPhase.ReadingOutput, CopyPhase.Sampling, CopyPhase.WaitingForExit, CopyPhase.Verifying, CopyPhase.Promoting];
    private static readonly string[] CopyCaseNames = new[] { "copy-metadata-roundtrip", "A03-wrong-path-equal-total",
        "A04-file-lengths-equal-total", "A05-zero-and-empty-missing", "A06-unsafe-enumeration",
        "A07-basic-versus-hash", "A08-source-changed", "A09-nonempty-target", "A09-target-appears",
        "A09-staging-file-preserved", "A09-staging-identity-changed", "basic-with-data-read-denied" }
        .Concat(CancelCopyPhases.Select(p => "A13-cancel-" + p)).ToArray();

    private sealed class CopyObserver(Action<MigrationProgress> report) : IProgress<MigrationProgress>
    { public void Report(MigrationProgress value) => report(value); }

    private sealed record CopyFixture(string Source, string Target, DirectoryIdentity SourceIdentity,
        Dictionary<string, (Identity Identity, byte[] Bytes)> Files);

    private static CopyFixture NewCopyFixture(Lab lab, Guid attempt, string name)
    {
        string source = Path.Combine(lab.Source.Root, @"Data\Sources\Sample", $"Copy-{attempt:D}-{name}");
        string target = Path.Combine(lab.Target.Root, @"Data\Targets", $"Copy-{attempt:D}-{name}");
        NewDirectory(lab, lab.Source, Path.GetDirectoryName(source)!, source);
        var files = new Dictionary<string, (Identity, byte[])>();
        foreach (var item in new[] { ("A.bin", "ABCD"), ("B.bin", "EFGH") })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(item.Item2);
            files[item.Item1] = (WriteFile(lab, lab.Source, source, Path.Combine(source, item.Item1), bytes), bytes);
        }
        return new CopyFixture(source, target, new PathIdentityService().ReadDirectory(source, false), files);
    }

    private static void AssertCopySource(Lab lab, CopyFixture f)
    {
        new PathIdentityService().RequireSame(f.Source, f.SourceIdentity);
        foreach (var item in f.Files)
            VerifyFile(lab.Source, f.Source, Path.Combine(f.Source, item.Key), item.Value.Identity, item.Value.Bytes);
    }

    private static MigrationTransactionRecord CopyRecord(CopyFixture f) => new MigrationTransactionStore()
        .ReadAllAsync().GetAwaiter().GetResult().Single(r => r.SourcePath == f.Source && r.TargetPath == f.Target);

    private static async Task<MigrationResult> MutateCopy(Lab lab, CopyFixture f, CopyPhase phase,
        Action<MigrationTransactionRecord> mutation, Dictionary<string, object?> observations)
    {
        bool injected = false;
        var progress = new CopyObserver(p =>
        {
            lab.BeforeWrite();
            if (p.CopyPhase != phase || injected) return;
            injected = true;
            var record = CopyRecord(f);
            new PathIdentityService().RequireSame(record.StagingPath!, record.StagingIdentity);
            if (phase == CopyPhase.Verifying)
            {
                var verifier = new CopyVerificationService();
                var before = verifier.CaptureAsync(f.Source, VerificationMode.Basic, CancellationToken.None).GetAwaiter().GetResult();
                var baseline = verifier.VerifyAsync(before, f.Source, record.StagingPath!, VerificationMode.Basic, CancellationToken.None).GetAwaiter().GetResult();
                Require(baseline.IsMatch, "Fault injection requires a verified baseline: " + string.Join("; ", baseline.Mismatches));
            }
            mutation(record);
        });
        var result = await new MigrationService(new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target,
            RobocopyThreads = 2, SampleMilliseconds = 20 }).ExecuteMigrationAsync(progress);
        Require(injected && !result.Success && result.RollbackOutcome != RollbackOutcome.Restored,
            "Fault injection did not reject the actual core migration before link switching.");
        AssertCopySource(lab, f);
        var saved = CopyRecord(f);
        Require(!new PathIdentityService().ReadDirectory(f.Source, false).IsReparsePoint, "Rejected copy switched the source.");
        observations["source"] = f.Source; observations["target"] = f.Target;
        observations["staging"] = saved.StagingPath; observations["stage"] = result.Stage.ToString();
        observations["error"] = result.ErrorMessage; observations["verification"] = result.VerificationSummary;
        return result;
    }

    private static async Task<CopyExecutionResult> CopyFixtureData(Lab lab, CopyFixture f, IProgress<MigrationProgress>? progress = null)
    {
        lab.BeforeWrite();
        var config = new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target };
        var record = await MigrationWorkflowHelper.PrepareTransactionAsync(config, MigrationMode.Migrate, CancellationToken.None);
        var stats = await FileStatsService.ScanDirectoryAsync(f.Source, 32L << 20);
        var result = await new CopyOperationExecutor(record, stats, 2, 20).ExecuteAsync(progress, null, CancellationToken.None);
        Require(result.Success && result.ExitCode is >= 0 and < 8 && result.ProcessExited,
            $"Actual Robocopy/manifest failed: {result.Error}");
        return result;
    }

    private static async Task RunCopyCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        var identity = new PathIdentityService();
        var verifier = new CopyVerificationService();
        await RunCase(lab, cases[0], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "正常 空格");
            o["source"] = f.Source; o["target"] = f.Target;
            f.Files["零字节"] = (WriteFile(lab, lab.Source, f.Source, Path.Combine(f.Source, "零字节"), []), []);
            NewDirectory(lab, lab.Source, f.Source, Path.Combine(f.Source, "空目录"));
            lab.BeforeWrite();
            byte[] ads = Encoding.UTF8.GetBytes("ADS-content");
            File.WriteAllBytes(Path.Combine(f.Source, "A.bin") + ":lab-stream", ads);
            File.WriteAllBytes(f.Source + ":lab-directory-stream", ads);
            var acl = new DirectoryInfo(f.Source).GetAccessControl(AccessControlSections.All);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Read, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            acl.SetAccessRuleProtection(true, preserveInheritance: true);
            new DirectoryInfo(f.Source).SetAccessControl(acl);
            var result = await CopyFixtureData(lab, f, new CopyObserver(_ => lab.BeforeWrite()));
            AssertCopySource(lab, f);
            foreach (var file in f.Files) Require(File.ReadAllBytes(Path.Combine(f.Target, file.Key)).AsSpan().SequenceEqual(file.Value.Bytes), "Copied bytes differ.");
            Require(Directory.Exists(Path.Combine(f.Target, "空目录")), "Empty directory was omitted.");
            Require(File.ReadAllBytes(Path.Combine(f.Target, "A.bin") + ":lab-stream").AsSpan().SequenceEqual(ads)
                && File.ReadAllBytes(f.Target + ":lab-directory-stream").AsSpan().SequenceEqual(ads), "ADS bytes differ.");
            identity.RequireSame(f.Target, result.Transaction.TargetIdentity);
            Require(new DirectoryInfo(f.Target).GetAccessControl().AreAccessRulesProtected,
                "Root ACL protection was lost during copy.");
            Require(!Directory.Exists(result.StagingPath), "Verified staging was not promoted.");
            o["source"] = f.Source; o["target"] = f.Target; o["exitCode"] = result.ExitCode;
            o["verification"] = result.Verification; o["transactionId"] = result.Transaction.TransactionId;
        });
        await RunCase(lab, cases[1], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "wrong-path");
            string? staged = null;
            var result = await MutateCopy(lab, f, CopyPhase.Verifying, r =>
            {
                staged = r.StagingPath!;
                lab.BeforeWrite();
                File.Move(Path.Combine(staged, "A.bin"), Path.Combine(staged, "C.bin"), false);
                Require(FileStatsService.GetDirectorySize(staged) == 8 && Directory.GetFiles(staged).Length == 2,
                    "Equal-total wrong-path fixture was not established.");
            }, o);
            Require(result.VerificationSummary?.IsMatch == false && !Directory.Exists(f.Target)
                && File.ReadAllBytes(Path.Combine(staged!, "C.bin")).AsSpan().SequenceEqual(f.Files["A.bin"].Bytes),
                "Wrong path was accepted or partial data was removed.");
        });
        await RunCase(lab, cases[2], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "file-lengths");
            var result = await MutateCopy(lab, f, CopyPhase.Verifying, r =>
            {
                lab.BeforeWrite();
                File.WriteAllBytes(Path.Combine(r.StagingPath!, "A.bin"), Encoding.ASCII.GetBytes("ABCDE"));
                File.WriteAllBytes(Path.Combine(r.StagingPath!, "B.bin"), Encoding.ASCII.GetBytes("FGH"));
                foreach (string leaf in f.Files.Keys) File.SetLastWriteTimeUtc(Path.Combine(r.StagingPath!, leaf), File.GetLastWriteTimeUtc(Path.Combine(f.Source, leaf)));
                Require(FileStatsService.GetDirectorySize(r.StagingPath!) == 8, "Equal-total length fixture was not established.");
            }, o);
            Require(result.VerificationSummary?.IsMatch == false && !Directory.Exists(f.Target), "Per-file length mismatch passed.");
        });
        await RunCase(lab, cases[3], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "empty-missing");
            f.Files["zero"] = (WriteFile(lab, lab.Source, f.Source, Path.Combine(f.Source, "zero"), []), []);
            NewDirectory(lab, lab.Source, f.Source, Path.Combine(f.Source, "empty"));
            var result = await MutateCopy(lab, f, CopyPhase.Verifying, r =>
            {
                lab.BeforeWrite(); File.Delete(Path.Combine(r.StagingPath!, "zero"));
                lab.BeforeWrite(); Directory.Delete(Path.Combine(r.StagingPath!, "empty"), false);
            }, o);
            Require(result.VerificationSummary?.IsMatch == false && Directory.Exists(Path.Combine(f.Source, "empty"))
                && !Directory.Exists(f.Target), "Zero-byte or empty-directory omission passed.");
        });
        await RunCase(lab, cases[4], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "unsafe-enumeration");
            string protectedRoot = Path.Combine(lab.Source.Root, @"Data\Protected");
            string path = Path.Combine(f.Source, "untrusted-link");
            lab.BeforeWrite(); Directory.CreateSymbolicLink(path, protectedRoot);
            o["manifestError"] = await RejectAsync<IOException>(() => verifier.CaptureAsync(f.Source, VerificationMode.Basic, CancellationToken.None));
            o["statisticsError"] = await RejectAsync<IOException>(() => FileStatsService.ScanDirectoryAsync(f.Source, 1));
            AssertCopySource(lab, f);
            Require(!Directory.Exists(f.Target), "Enumeration failure wrote a destination.");
            o["source"] = f.Source; o["unknownLink"] = path;
        });
        await RunCase(lab, cases[5], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "equal-length-content");
            var copy = await CopyFixtureData(lab, f);
            var hashBefore = await verifier.CaptureAsync(f.Source, VerificationMode.ContentHash, CancellationToken.None);
            string path = Path.Combine(f.Target, "A.bin");
            DateTime time = File.GetLastWriteTimeUtc(path);
            lab.BeforeWrite(); File.WriteAllBytes(path, Encoding.ASCII.GetBytes("WXYZ")); File.SetLastWriteTimeUtc(path, time);
            var basic = await verifier.VerifyAsync(copy.SourceManifest!, f.Source, f.Target, VerificationMode.Basic, CancellationToken.None);
            var hash = await verifier.VerifyAsync(hashBefore, f.Source, f.Target, VerificationMode.ContentHash, CancellationToken.None);
            Require(basic.IsMatch && basic.Mode == VerificationMode.Basic && !hash.IsMatch && hash.Mode == VerificationMode.ContentHash,
                "Basic/ContentHash did not distinguish equal-length content with identical observable metadata.");
            AssertCopySource(lab, f);
            o["source"] = f.Source; o["target"] = f.Target; o["basic"] = basic; o["contentHash"] = hash;
        });
        await RunCase(lab, cases[6], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "source-changed");
            var result = await MutateCopy(lab, f, CopyPhase.Verifying, _ =>
            {
                lab.BeforeWrite();
                byte[] changed = Encoding.ASCII.GetBytes("ABCDE");
                File.WriteAllBytes(Path.Combine(f.Source, "A.bin"), changed);
                f.Files["A.bin"] = (f.Files["A.bin"].Identity, changed);
            }, o);
            Require(result.VerificationSummary?.IsMatch == false && !Directory.Exists(f.Target), "Observable source change passed.");
        });
        await RunCase(lab, cases[7], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "nonempty-target");
            NewDirectory(lab, lab.Target, Path.GetDirectoryName(f.Target)!, f.Target);
            byte[] foreign = Encoding.UTF8.GetBytes("Unowned target contents.");
            Identity file = WriteFile(lab, lab.Target, f.Target, Path.Combine(f.Target, "foreign"), foreign);
            var result = await new MigrationService(new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }).ExecuteMigrationAsync();
            Require(!result.Success && result.TransactionId is null, "Nonempty target reached a dangerous operation.");
            AssertCopySource(lab, f);
            o["source"] = f.Source; o["target"] = f.Target; o["error"] = result.ErrorMessage;
            o["foreignSha256"] = VerifyFile(lab.Target, f.Target, Path.Combine(f.Target, "foreign"), file, foreign);
        });
        await RunCase(lab, cases[8], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "target-appears");
            byte[] bytes = Encoding.ASCII.GetBytes("Concurrent target must remain.");
            Identity? foreign = null;
            await MutateCopy(lab, f, CopyPhase.Promoting, _ =>
            {
                NewDirectory(lab, lab.Target, Path.GetDirectoryName(f.Target)!, f.Target);
                foreign = WriteFile(lab, lab.Target, f.Target, Path.Combine(f.Target, "foreign"), bytes);
            }, o);
            o["foreignSha256"] = VerifyFile(lab.Target, f.Target, Path.Combine(f.Target, "foreign"), foreign!, bytes);
            Require(Directory.Exists(CopyRecord(f).StagingPath), "Promotion conflict deleted verified staging.");
        });
        await RunCase(lab, cases[9], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "staging-collision");
            byte[] bytes = Encoding.ASCII.GetBytes("Foreign staging bytes.");
            Identity? foreign = null;
            var result = await MutateCopy(lab, f, CopyPhase.ProcessStarted, r =>
            {
                string file = Path.Combine(r.StagingPath!, "A.bin");
                foreign = WriteFile(lab, lab.Target, r.StagingPath!, file, bytes);
                File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(Path.Combine(f.Source, "A.bin")));
            }, o);
            string staging = CopyRecord(f).StagingPath!;
            Require(result.VerificationSummary?.IsMatch == false && !Directory.Exists(f.Target), "Foreign staging file was accepted.");
            o["foreignSha256"] = VerifyFile(lab.Target, staging, Path.Combine(staging, "A.bin"), foreign!, bytes);
        });
        await RunCase(lab, cases[10], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "staging-replaced");
            string? preserved = null;
            DirectoryIdentity? original = null;
            await MutateCopy(lab, f, CopyPhase.ProcessStarted, r =>
            {
                preserved = r.StagingPath + "-preserved";
                original = r.StagingIdentity;
                lab.BeforeWrite(); lab.Target.Missing(preserved, Path.GetDirectoryName(r.StagingPath!)!);
                Directory.Move(r.StagingPath!, preserved);
                NewDirectory(lab, lab.Target, Path.GetDirectoryName(r.StagingPath!)!, r.StagingPath!);
            }, o);
            identity.RequireSame(preserved!, original);
            Require(CopyRecord(f).Stage == MigrationStage.RecoveryRequired && !Directory.Exists(f.Target),
                "Staging identity replacement was not quarantined.");
            Require(!Directory.EnumerateFileSystemEntries(CopyRecord(f).StagingPath!).Any(), "Robocopy wrote into a replaced staging object.");
            o["preservedStaging"] = preserved;
        });
        await RunCase(lab, cases[11], async o =>
        {
            var f = NewCopyFixture(lab, attempt, "basic-no-data-read");
            using (var held = new FileStream(Path.Combine(f.Source, "A.bin"), FileMode.Open, FileAccess.Write, FileShare.Write))
            {
                var basic = await verifier.CaptureAsync(f.Source, VerificationMode.Basic, CancellationToken.None);
                Require(basic.Entries["A.bin"].Length == 4, "Basic metadata capture failed while data-read access was denied.");
                o["hashReadError"] = await RejectAsync<IOException>(() => verifier.CaptureAsync(f.Source, VerificationMode.ContentHash, CancellationToken.None));
            }
            AssertCopySource(lab, f);
            o["source"] = f.Source; o["dataReadDeniedByKernelShareMode"] = true;
        });
        var cancel = NewCopyFixture(lab, attempt, "cancel-dataset");
        byte[] block = new byte[4096];
        for (int i = 0; i < block.Length; i++) block[i] = (byte)(i * 31 + 17);
        for (int i = 0; i < 1000; i++)
        {
            string leaf = $"small-{i:D4}.bin";
            cancel.Files[leaf] = (WriteFile(lab, lab.Source, cancel.Source, Path.Combine(cancel.Source, leaf), block), block);
        }
        for (int i = 0; i < CancelCopyPhases.Length; i++)
        {
            CopyPhase phase = CancelCopyPhases[i];
            await RunCase(lab, cases[12 + i], async o =>
            {
                var f = cancel with { Target = cancel.Target + "-" + phase };
                lab.BeforeWrite();
                var record = await MigrationWorkflowHelper.PrepareTransactionAsync(
                    new MigrationConfig { SourcePath = f.Source, TargetPath = f.Target }, MigrationMode.Migrate, CancellationToken.None);
                var stats = await FileStatsService.ScanDirectoryAsync(f.Source, 32L << 20);
                using var cancellation = new CancellationTokenSource();
                bool requested = false;
                int? pid = null;
                DateTime? processStart = null;
                bool? aliveAtCancel = null;
                var progress = new CopyObserver(p =>
                {
                    lab.BeforeWrite();
                    if (p.CopyPhase != phase || requested) return;
                    requested = true; pid = p.ProcessId;
                    if (pid is not null)
                    {
                        try
                        {
                            using Process process = Process.GetProcessById(pid.Value);
                            aliveAtCancel = !process.HasExited;
                            if (aliveAtCancel == true) processStart = process.StartTime.ToUniversalTime();
                        }
                        catch (ArgumentException) { aliveAtCancel = false; }
                    }
                    if (phase is CopyPhase.ProcessStarted or CopyPhase.ReadingOutput or CopyPhase.WaitingForExit)
                        Require(aliveAtCancel == true, "The actual process/read/exit wait must still be pending at cancellation.");
                    cancellation.Cancel();
                });
                var result = await new CopyOperationExecutor(record, stats, 2, 20).ExecuteAsync(progress, null, cancellation.Token);
                Require(requested && result.Cancelled && result.ProcessExited && !result.RecoveryRequired && !result.Success,
                    $"Cancellation was not safely completed at {phase}: {result.Error}");
                if (pid is not null)
                {
                    try
                    {
                        using Process process = Process.GetProcessById(pid.Value);
                        Require(process.HasExited || process.StartTime.ToUniversalTime() != processStart,
                            "The owned Robocopy process is still alive after cancellation returned.");
                    }
                    catch (ArgumentException) { }
                }
                AssertCopySource(lab, f);
                Require(!Directory.Exists(f.Target) && (await new MigrationTransactionStore().LoadAsync(record.TransactionId))?.Stage == MigrationStage.Cancelled,
                    "Cancelled copy promoted data or failed to persist cancellation.");
                if (result.Transaction.StagingIdentity is not null) identity.RequireSame(result.StagingPath, result.Transaction.StagingIdentity);
                o["source"] = f.Source; o["target"] = f.Target; o["staging"] = result.StagingPath;
                o["phase"] = phase.ToString(); o["processId"] = pid; o["aliveAtCancel"] = aliveAtCancel;
                o["processExitedBeforeReturn"] = result.ProcessExited; o["sourceFilesChecked"] = f.Files.Count;
            });
        }
    }
}
