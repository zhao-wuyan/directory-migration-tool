using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using MigrationCore.Models;
using MigrationCore.Services;

internal static partial class Program
{
    private static readonly string[] BoundaryCaseNames = ["roots", "unknown-drive", "wrong-serial", "wrong-guid", "component-boundary",
        "system-path-parser", "untrusted-reparse", "cross-volume-copy", "source-rename", "actual-link-target", "fixture-only-deletion"];
    private static readonly string[] RegressionCaseNames = ["A06-native-enumeration-denied", "A06-acl-change-rejected", "A09-native-robocopy-failure",
        "A09-target-disk-full", "A10-same-volume", "A20-native-cmd-link", "A21-fixed-dataset"];
    private static string[] CaseNames(string suite) => suite switch
    {
        "Identity" => IdentityCaseNames, "Probe" => ProbeCaseNames, "Copy" => CopyCaseNames,
        "Switch" => SwitchCaseNames, "Backup" => BackupCaseNames, "Regression" => RegressionCaseNames, _ => BoundaryCaseNames
    };

    private sealed record AcceptanceEvidence(string Id, string Status, string[] CaseRefs, Dictionary<string, object?>[] Inputs, string[] Errors);
    private static AcceptanceEvidence[] Acceptance(CaseEvidence[] cases) => Enumerable.Range(1, 21).Select(number =>
    {
        string id = $"A{number:D2}";
        var selected = cases.Where(c => c.Name.StartsWith(id + "-", StringComparison.Ordinal)).ToArray();
        return new AcceptanceEvidence(id, selected.Length == 0 ? "NotCovered" : selected.Any(c => c.Status == "Failed") ? "Failed"
            : selected.All(c => c.Status == "Passed") ? "Passed" : "NotCovered",
            selected.Select(c => c.Suite + "/" + c.Name).ToArray(), selected.Select(c => c.Observations).ToArray(),
            selected.Where(c => c.Error is not null).Select(c => c.Error!).ToArray());
    }).ToArray();

    private static void WriteLargeFixture(Lab lab, CopyFixture f, string name, int bytes)
    {
        Require(bytes is > 0 and <= 32 * 1024 * 1024, "Large fixture exceeds the agreed 32 MiB bound.");
        lab.BeforeWrite(); string path = Path.Combine(f.Source, name); lab.Source.Missing(path, f.Source);
        byte[] buffer = new byte[65536];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i * 31 + 7);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        for (int written = 0; written < bytes; written += buffer.Length)
        {
            lab.BeforeWrite(); file.Write(buffer.AsSpan(0, Math.Min(buffer.Length, bytes - written)));
        }
    }
    private static string FileDigest(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static async Task RunRegressionCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        var identity = new PathIdentityService();
        async Task Case(string name, Func<Dictionary<string, object?>, Task> action) => await RunCase(lab, cases.Single(c => c.Name == name), action);
        await Case("A06-native-enumeration-denied", async o =>
        {
            var f = NewCopyFixture(lab, attempt, "acl-denied");
            string original = new DirectoryInfo(f.Source).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                lab.BeforeWrite();
                var denied = new DirectoryInfo(f.Source).GetAccessControl(AccessControlSections.Access);
                denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
                new DirectoryInfo(f.Source).SetAccessControl(denied);
                o["enumerationError"] = await RejectAsync<UnauthorizedAccessException>(() => new CopyVerificationService().CaptureAsync(f.Source, VerificationMode.Basic, CancellationToken.None));
                var result = await new MigrationService(SwitchConfig(f)).ExecuteMigrationAsync();
                Require(!result.Success && !Directory.Exists(f.Target) && !identity.ReadDirectory(f.Source, false).IsReparsePoint,
                    "Unreadable source was switched or accepted as complete.");
                o["result"] = result; o["source"] = f.Source; o["target"] = f.Target;
            }
            finally
            {
                lab.BeforeWrite(); var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
                new DirectoryInfo(f.Source).SetAccessControl(restored);
            }
            AssertCopySource(lab, f);
        });
        await Case("A06-acl-change-rejected", async o =>
        {
            var f = NewCopyFixture(lab, attempt, "acl-change");
            var verifier = new CopyVerificationService();
            var before = await verifier.CaptureAsync(f.Source, VerificationMode.Basic, CancellationToken.None);
            await CopyFixtureData(lab, f);
            string file = Path.Combine(f.Target, "A.bin");
            string original = new FileInfo(file).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                lab.BeforeWrite();
                var changed = new FileInfo(file).GetAccessControl(AccessControlSections.Access);
                changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Deny));
                new FileInfo(file).SetAccessControl(changed);
                var summary = await verifier.VerifyAsync(before, f.Source, f.Target, VerificationMode.Basic, CancellationToken.None);
                Require(!summary.IsMatch, "Real access-rule changes were erased by ACL normalization.");
                o["source"] = f.Source; o["target"] = f.Target; o["verification"] = summary;
            }
            finally
            {
                lab.BeforeWrite(); var restored = new FileSecurity();
                restored.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
                new FileInfo(file).SetAccessControl(restored);
            }
            AssertCopySource(lab, f);
        });
        await Case("A09-native-robocopy-failure", async o =>
        {
            var f = NewCopyFixture(lab, attempt, "native-failure");
            var record = await MigrationWorkflowHelper.PrepareTransactionAsync(SwitchConfig(f), MigrationMode.Migrate, CancellationToken.None);
            var stats = await FileStatsService.ScanDirectoryAsync(f.Source, 32L << 20);
            FileStream? held = null;
            var progress = new CopyObserver(p =>
            {
                if (p.CopyPhase == CopyPhase.ProcessStarted && held is null)
                {
                    lab.BeforeWrite(); held = new FileStream(Path.Combine(f.Source, "A.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
                }
            });
            CopyExecutionResult result;
            try { result = await new CopyOperationExecutor(record, stats, 2, 20).ExecuteAsync(progress, null, CancellationToken.None); }
            finally { held?.Dispose(); }
            Require(held is not null && !result.Success && result.ExitCode >= 8 && result.ProcessExited && Directory.Exists(result.StagingPath)
                && !Directory.Exists(f.Target), "Native Robocopy failure did not stop promotion: " + result.Error);
            AssertCopySource(lab, f);
            o["source"] = f.Source; o["target"] = f.Target; o["staging"] = result.StagingPath; o["exitCode"] = result.ExitCode;
            o["processExited"] = result.ProcessExited; o["error"] = result.Error;
        });
        await Case("A09-target-disk-full", async o =>
        {
            var f = NewCopyFixture(lab, attempt, "disk-full"); WriteLargeFixture(lab, f, "eight-MiB.bin", 8 * 1024 * 1024);
            string digest = FileDigest(Path.Combine(f.Source, "eight-MiB.bin"));
            string scope = Path.Combine(lab.Target.Root, @"Data\Targets"), filler = Path.Combine(scope, $"filler-{attempt:D}.bin");
            DiskLabPath.Identity? fillerIdentity = null;
            var drive = new DriveInfo(lab.Target.Root);
            try
            {
                bool filled = false;
                var progress = new CopyObserver(p =>
                {
                    if (p.CopyPhase != CopyPhase.ProcessStarted || filled) return;
                    filled = true;
                    lab.BeforeWrite(); lab.Target.Missing(filler, scope);
                    using (var file = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        fillerIdentity = lab.Target.Existing(filler, scope);
                        byte[] buffer = new byte[1024 * 1024];
                        while (drive.AvailableFreeSpace > 1024 * 1024)
                        {
                            lab.BeforeWrite(); file.Write(buffer.AsSpan(0, (int)Math.Min(buffer.Length, drive.AvailableFreeSpace - 512 * 1024)));
                        }
                        file.Flush(true);
                    }
                    Require(drive.AvailableFreeSpace < 8 * 1024 * 1024, "Target volume was not actually space constrained.");
                    o["targetFreeBytesBeforeCopy"] = drive.AvailableFreeSpace;
                });
                var record = await MigrationWorkflowHelper.PrepareTransactionAsync(SwitchConfig(f), MigrationMode.Migrate, CancellationToken.None);
                var stats = await FileStatsService.ScanDirectoryAsync(f.Source, 32L << 20);
                var result = await new CopyOperationExecutor(record, stats, 2, 20).ExecuteAsync(progress, null, CancellationToken.None);
                Require(filled && !result.Success && result.ExitCode >= 8 && result.ProcessExited && Directory.Exists(result.StagingPath) && !Directory.Exists(f.Target),
                    "Native disk-full failure did not retain source/staging or stop promotion: " + result.Error);
                AssertCopySource(lab, f); Require(FileDigest(Path.Combine(f.Source, "eight-MiB.bin")) == digest, "Disk-full failure changed source bytes.");
                o["source"] = f.Source; o["target"] = f.Target; o["staging"] = result.StagingPath; o["exitCode"] = result.ExitCode; o["error"] = result.Error;
            }
            finally
            {
                if (fillerIdentity is not null)
                {
                    lab.BeforeWrite(); SameObject(fillerIdentity, lab.Target.Existing(filler, scope), "Filler identity changed");
                    File.Delete(filler); lab.Target.Missing(filler, scope); o["onlyFillerRemoved"] = filler; o["targetFreeBytesAfterFillerRemoval"] = drive.AvailableFreeSpace;
                }
            }
        });
        await Case("A10-same-volume", async o =>
        {
            var initial = NewSwitchFixture(lab, attempt, "same-volume");
            var f = initial with { Target = Path.Combine(Path.GetDirectoryName(initial.Source)!, "同卷目标") };
            var result = await MigrateSwitch(f); AssertSwitched(f, result, o);
            var record = CopyRecord(f);
            Require(record.SourceIdentity!.VolumeGuid == record.TargetIdentity!.VolumeGuid, "Fixture did not migrate within one registered volume.");
        });
        await Case("A20-native-cmd-link", async o =>
        {
            var f = NewSwitchFixture(lab, attempt, "cmd 空格");
            string link = Path.Combine(Path.GetDirectoryName(f.Source)!, "cmd 目录链接");
            lab.BeforeWrite(); lab.Source.Missing(link, Path.GetDirectoryName(f.Source)!);
            var result = await SymbolicLinkHelper.CreateSymbolicLinkViaCmdAsync(link, f.Source);
            Require(result.Success, "Real cmd mklink fallback failed: " + result.Error);
            var linkIdentity = identity.ReadDirectory(link, false);
            Require(linkIdentity.IsReparsePoint, "cmd result is not a real link."); identity.RequireSame(link, f.SourceIdentity, true);
            AssertSwitchBytes(f, link); AssertSwitchBytes(f, f.Source);
            o["source"] = f.Source; o["link"] = link; o["linkIdentity"] = linkIdentity; o["targetIdentity"] = f.SourceIdentity;
        });
        await Case("A21-fixed-dataset", async o =>
        {
            Require(new DriveInfo(lab.Source.Root).AvailableFreeSpace >= 128L << 20 && new DriveInfo(lab.Target.Root).AvailableFreeSpace >= 128L << 20,
                "Fixed dataset requires at least 128 MiB free per registered volume; old evidence is not deleted to make room.");
            var f = NewCopyFixture(lab, attempt, "固定数据 Unicode 空格");
            string smallRoot = Path.Combine(f.Source, "小文件 空格"); NewDirectory(lab, lab.Source, f.Source, smallRoot);
            byte[] small = new byte[4096]; for (int i = 0; i < small.Length; i++) small[i] = (byte)(i * 13 + 5);
            for (int i = 0; i < 1000; i++) WriteFile(lab, lab.Source, smallRoot, Path.Combine(smallRoot, $"file-{i:D4}.bin"), small);
            WriteFile(lab, lab.Source, f.Source, Path.Combine(f.Source, "零字节"), []);
            NewDirectory(lab, lab.Source, f.Source, Path.Combine(f.Source, "空目录"));
            WriteLargeFixture(lab, f, "32 MiB 文件.bin", 32 * 1024 * 1024);
            string expected = FileDigest(Path.Combine(f.Source, "32 MiB 文件.bin"));
            var result = await CopyFixtureData(lab, f);
            Require(result.Verification is { IsMatch: true, Mode: VerificationMode.Basic }, "Fixed dataset silently required hash mode.");
            Require(FileDigest(Path.Combine(f.Target, "32 MiB 文件.bin")) == expected && FileDigest(Path.Combine(f.Source, "32 MiB 文件.bin")) == expected,
                "32 MiB fixture bytes changed across the real copy.");
            byte[] actual = new byte[4096];
            foreach (string root in new[] { f.Source, f.Target })
            {
                for (int i = 0; i < 1000; i++)
                {
                    using var file = File.OpenRead(Path.Combine(root, "小文件 空格", $"file-{i:D4}.bin"));
                    Require(file.Length == small.Length, "Small file size changed."); file.ReadExactly(actual);
                    Require(actual.AsSpan().SequenceEqual(small), "Small file bytes changed.");
                }
                Require(new FileInfo(Path.Combine(root, "零字节")).Length == 0 && Directory.Exists(Path.Combine(root, "空目录")), "Zero file or empty directory was lost.");
            }
            AssertCopySource(lab, f);
            o["source"] = f.Source; o["target"] = f.Target; o["largeFileBytes"] = 32 * 1024 * 1024;
            o["smallFiles"] = 1000; o["smallFileBytes"] = small.Length; o["sha256"] = expected; o["verification"] = result.Verification;
        });
    }
}
