using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;
using System.Text;
using System.Text.Json;
using MigrationCore.Models;
using MigrationCore.Services;

internal static partial class Program
{
    private static readonly string[] BackupCaseNames = ["A18-unverified", "A18-no-confirmations", "A18-first-only",
        "A18-order-and-name", "A18-cancel-First", "A18-cancel-Second", "A17-stale-revision",
        "A17-replaced-backup", "A17-target-changed", "A18-backup-changed", "A18-new-reparse",
        "A18-other-transaction", "A18-unknown-candidate", "A19-recycle-success", "A19-recycle-too-large", "A19-partial-delete",
        "A19-permanent-delete", "A20-restored-source-changed", "A21-real-wpf", "A21-recycle-wpf",
        "A21-exit-ExitComplete", "A21-exit-ExitButton", "A21-exit-ExitX"];
    private static readonly string[] RecycleCaseNames = ["A19-recycle-success", "A19-recycle-too-large", "A21-recycle-wpf"];
    private static readonly string[] VerificationCaseNames = ["A18-order-and-name", "A19-permanent-delete",
        "A21-real-wpf", "A21-recycle-wpf", "A21-exit-ExitComplete", "A21-exit-ExitButton", "A21-exit-ExitX"];

    private static async Task RunBackupCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        var service = new BackupManagementService();
        var store = new MigrationTransactionStore();
        var identity = new PathIdentityService();
        async Task Case(string name, Func<Dictionary<string, object?>, Task> action)
        {
            var selected = cases.SingleOrDefault(c => c.Name == name);
            if (selected is not null) await RunCase(lab, selected, action);
        }
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
                request = await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, BackupManagementService.ExpectedConfirmationName(record));
            }
            return request;
        }
        void Intact(CopyFixture f)
        {
            var record = CopyRecord(f);
            identity.RequireSame(record.BackupPath!, record.BackupIdentity);
            AssertSwitchBytes(f, record.BackupPath!);
            if (!BackupManagementService.IsRestoreTargetBackup(record))
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
            o["order"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, Path.GetFileName(Path.TrimEndingDirectorySeparator(CopyRecord(f).SourcePath))));
            await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null);
            o["repeatFirst"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null));
            o["wrongName"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, "not-the-backup"));
            o["emptyName"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, string.Empty));
            var record = CopyRecord(f);
            string sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(record.SourcePath));
            string backupName = Path.GetFileName(record.BackupPath)!;
            Require(backupName == sourceName + ".bak_" + record.TransactionId.ToString("D"), "测试备份未使用事务 UUID 随机后缀。");
            o["backupName"] = await RejectAsync<IOException>(() => service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.Second, backupName));
            Require(CopyRecord(f).DeletionRequest?.SecondConfirmedAtUtc is null, "错误、空白或 UUID 备份名称获得了第二次确认。");
            Intact(f);
        });
        foreach (var step in new[] { ConfirmationStep.First, ConfirmationStep.Second })
            await Case($"A18-cancel-{step}", async o =>
            {
                var f = await Fixture("cancel-" + step, o); var request = await Request(f);
                if (step == ConfirmationStep.Second) await service.ConfirmDeletionAsync(request.RequestId, ConfirmationStep.First, null);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                o["cancellation"] = await RejectAsync<OperationCanceledException>(() => service.ConfirmDeletionAsync(request.RequestId, step, Path.GetFileName(Path.TrimEndingDirectorySeparator(CopyRecord(f).SourcePath)), cancelled.Token));
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
        await Case("A19-recycle-success", async o =>
        {
            var f = await Fixture("recycle", o); var request = await Request(f, true, BackupDeletionMode.Recycle);
            string backup = CopyRecord(f).BackupPath!;
            var expectedItems = (await new CopyVerificationService().CaptureAsync(backup, VerificationMode.Basic, CancellationToken.None))
                .Entries.Keys.Select(p => Path.Combine(backup, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await service.DeleteAsync(request.RequestId);
            Require(result.Status == BackupDeletionStatus.Recycled && result.Items.All(i => i.Outcome == BackupItemOutcome.Recycled),
                "Shell 未确认完整回收：" + result.Error);
            Require(expectedItems.SetEquals(result.Items.Select(i => i.Path)), "回收结果遗漏或虚构了逐项记录。");
            var record = CopyRecord(f);
            Require(record.BackupState == BackupState.Recycled && record.LastDeletionResult?.Status == BackupDeletionStatus.Recycled
                && record.DeletionRequest?.Consumed == true && !record.BackupDeletionInProgress,
                "回收状态、结果或已消费授权没有持久化。");
            Require(!Directory.Exists(backup), "回收后原备份仍存在。");
            string recycled = await FindRecycledBackup(backup);
            Require(Path.GetPathRoot(recycled)!.Equals(Path.GetPathRoot(backup), StringComparison.OrdinalIgnoreCase)
                && recycled.StartsWith(Path.Combine(Path.GetPathRoot(backup)!, "$Recycle.Bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Shell 返回的备份不在原卷回收站内。");
            identity.RequireSame(recycled, f.SourceIdentity);
            AssertSwitchBytes(f, recycled); AssertSwitchBytes(f, f.Target); AssertSwitchBytes(f, f.Source);
            var task = MigrationStateDetector.DetectMigrationState(new QuickMigrateTask { SourcePath = f.Source, TargetPath = f.Target });
            Require(task.BackupState == BackupState.Recycled && !task.CanManageBackup, "刷新后丢失回收状态或重新允许处理备份。");
            o["recycledPath"] = recycled; o["result"] = result;
            o["repeatRejected"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
        });
        await Case("A19-recycle-too-large", async o =>
        {
            // 稀疏文件不在当前清单支持范围；普通假数据必须在迁移前生成，沿用真实冻结基线。
            var f = NewSwitchFixture(lab, attempt, "recycle-too-large");
            long length = RecycleOversizedLength(lab, o);
            string large = Path.Combine(f.Source, "超过回收容量.bin");
            lab.Source.Missing(large, f.Source);
            byte[] block = new byte[1024 * 1024];
            for (int i = 0; i < block.Length; i++) block[i] = (byte)(i % 251);
            using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                for (long written = 0; written < length; written += block.Length)
                {
                    lab.BeforeWrite();
                    stream.Write(block, 0, (int)Math.Min(block.Length, length - written));
                }
            var largeIdentity = lab.Source.Existing(large, f.Source);
            string hash;
            using (var stream = File.OpenRead(large)) hash = Convert.ToHexString(SHA256.HashData(stream));
            AssertSwitched(f, await MigrateSwitch(f), o);
            var request = await Request(f, true, BackupDeletionMode.Recycle);
            string backup = CopyRecord(f).BackupPath!;
            var verifier = new CopyVerificationService();
            var before = await verifier.CaptureAsync(backup, VerificationMode.Basic, CancellationToken.None);
            var expectedItems = before.Entries.Keys.Select(p => Path.Combine(backup, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = await service.DeleteAsync(request.RequestId);
            Require(result.Status == BackupDeletionStatus.Failed && result.Items.All(i => i.Outcome is BackupItemOutcome.Failed or BackupItemOutcome.Retained),
                "超过回收容量的备份没有完整保留，或出现永久删除/部分回收：" + result.Error);
            Require(expectedItems.SetEquals(result.Items.Select(i => i.Path)), "失败回收结果遗漏或虚构了逐项记录。");
            var record = CopyRecord(f);
            Require(record.BackupState == BackupState.DeleteFailed && record.LastDeletionResult?.Status == BackupDeletionStatus.Failed
                && record.DeletionRequest?.Consumed == true && !record.BackupDeletionInProgress,
                "失败结果或已消费授权没有持久化。");
            Intact(f); AssertSwitchBytes(f, f.Source);
            var after = await verifier.CaptureAsync(backup, VerificationMode.Basic, CancellationToken.None);
            Require(CopyVerificationService.Fingerprint(before, true) == CopyVerificationService.Fingerprint(after, true),
                "回收失败后备份完整清单身份发生变化。");
            SameObject(largeIdentity, lab.Source.Existing(Path.Combine(backup, Path.GetFileName(large)), backup),
                "回收失败后大文件身份变化。", comparePath: false);
            foreach (string root in new[] { backup, f.Target, f.Source })
            {
                using var stream = File.OpenRead(Path.Combine(root, Path.GetFileName(large)));
                Require(stream.Length == length && Convert.ToHexString(SHA256.HashData(stream)) == hash,
                    "回收失败后大文件长度或完整内容改变：" + root);
            }
            o["result"] = result; o["largeFileSha256"] = hash;
            o["repeatRejected"] = await RejectAsync<IOException>(() => service.DeleteAsync(request.RequestId));
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
            var f = NewSwitchFixture(lab, attempt, "backup-permanent");
            string parent = Path.Combine(f.Source, "父目录"), child = Path.Combine(parent, "子目录");
            NewDirectory(lab, lab.Source, f.Source, parent); NewDirectory(lab, lab.Source, parent, child);
            byte[] nested = Encoding.UTF8.GetBytes("nested backup must use the same manifest root");
            f.Files[Path.Combine("父目录", "子目录", "nested.txt")] = (WriteFile(lab, lab.Source, child, Path.Combine(child, "nested.txt"), nested), nested);
            AssertSwitched(f, await MigrateSwitch(f), o);
            var request = await Request(f, true); string backup = CopyRecord(f).BackupPath!;
            var confirmed = CopyRecord(f);
            string sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(confirmed.SourcePath));
            Require(Path.GetFileName(backup) == sourceName + ".bak_" + confirmed.TransactionId.ToString("D")
                && confirmed.DeletionRequest?.SecondConfirmationText == sourceName, "UUID 备份必须使用不含随机后缀的原目录名称完成确认。");
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
        await Case("A21-recycle-wpf", async o =>
        {
            var initial = NewSwitchFixture(lab, attempt, "recycle-ui");
            NewDirectory(lab, lab.Target, Path.GetDirectoryName(initial.Target)!, initial.Target);
            var f = initial with { Target = Path.Combine(initial.Target, Path.GetFileName(initial.Source)) };
            AssertSwitched(f, await MigrateSwitch(f), o);
            await VerifyBackupUi(lab, f, attempt, o, recycleOnly: true);
            Intact(f); AssertSwitchBytes(f, f.Source);
            Require(CopyRecord(f).LastDeletionResult is null && !CopyRecord(f).BackupDeletionInProgress,
                "取消实际回收确认后仍产生了后台删除结果。");
        });
        foreach (string exitMode in new[] { "ExitComplete", "ExitButton", "ExitX" })
        {
            await Case("A21-exit-" + exitMode, async o =>
            {
                var initial = NewSwitchFixture(lab, attempt, "ui-" + exitMode);
                NewDirectory(lab, lab.Target, Path.GetDirectoryName(initial.Target)!, initial.Target);
                var f = initial with { Target = Path.Combine(initial.Target, Path.GetFileName(initial.Source)) };
                AssertSwitched(f, await MigrateSwitch(f), o);
                await VerifyBackupUi(lab, f, attempt, o, exitMode: exitMode);
                var record = CopyRecord(f);
                Require(record.UserVerifiedAtUtc is null && record.DeletionRequest is null,
                    "忽略验证被错误记录为已验证或授予备份处理权限。");
                Intact(f); AssertSwitchBytes(f, f.Source);
            });
        }
    }

    private static long RecycleOversizedLength(Lab lab, Dictionary<string, object?> observations)
    {
        lab.BeforeWrite();
        var source = new DriveInfo(lab.Source.Root);
        var target = new DriveInfo(lab.Target.Root);
        // 仅读取该登记卷的用户配置；没有覆盖值时，取默认 10% 的保守上界。
        string volumeName = lab.Source.GuidRoot.TrimEnd('\\').Split('\\').Last();
        using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\" + volumeName, writable: false);
        object? configured = settings?.GetValue("MaxCapacity");
        long capacity = configured is int mib && mib >= 0 ? checked((long)mib * 1024 * 1024) : source.TotalSize / 10;
        Require(configured is null || configured is int and >= 0, "无法安全解释此测试卷的只读回收容量配置。");
        long length = Math.Max(128L * 1024 * 1024, checked(capacity + 1024 * 1024));
        Require(length <= 256L * 1024 * 1024, "测试卷回收容量过大，无法在 256MiB 假数据安全上限内构造拒绝场景；未改回收设置。");
        const long margin = 64L * 1024 * 1024;
        Require(source.AvailableFreeSpace >= length + margin && target.AvailableFreeSpace >= length + margin,
            "登记 VHD 的剩余空间不足以保留 64MiB 裕量；未填满卷，也未清理旧内容。");
        Require(lab.BeforeWrite() >= MinimumHostBytes + 2 * length + margin,
            "宿主空间不足以容纳两个 VHD 的假数据增长和 1GiB 安全余量。");
        observations["volumeTotalBytes"] = source.TotalSize; observations["recycleCapacityBytes"] = capacity;
        observations["capacitySource"] = configured is null ? "Windows 默认 10% 保守上界" : "只读 MaxCapacity";
        observations["largeFileBytes"] = length;
        return length;
    }

    // 只读枚举 Shell 回收站；仅匹配本次唯一事务的原始父路径和目录名，不清理任何项目。
    private static async Task<string> FindRecycledBackup(string backup)
    {
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string script = "$ErrorActionPreference='Stop'; $shell=New-Object -ComObject Shell.Application; "
            + "$bin=$shell.NameSpace(10); if($null -eq $bin){throw '无法枚举 Shell 回收站'}; "
            + "$found=@($bin.Items() | Where-Object { $_.Name -eq " + Quote(Path.GetFileName(backup))
            + " -and $_.ExtendedProperty('System.Recycle.DeletedFrom') -eq " + Quote(Path.GetDirectoryName(backup)!)
            + " }); if($found.Count -ne 1){throw ('唯一备份回收站匹配数：'+$found.Count)}; $found[0].Path";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动只读 Shell 枚举。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string path = (await output).Trim();
        Require(process.ExitCode == 0 && !string.IsNullOrWhiteSpace(path), "Shell 回收站枚举失败：" + await error);
        return path;
    }

    private static async Task VerifyBackupUi(Lab lab, CopyFixture f, Guid attempt, Dictionary<string, object?> o, bool recycleOnly = false, string? exitMode = null)
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
            foreach (string mode in exitMode is not null ? new[] { exitMode } : recycleOnly ? new[] { "Recycle" } : new[] { "Exercise", "Restart" })
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
