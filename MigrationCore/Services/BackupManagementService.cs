using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>用户验证、冻结请求与两次确认都在后台核验；界面不能直接授予删除权限。</summary>
public sealed class BackupManagementService
{
    private readonly MigrationTransactionStore _store = new();
    private readonly PathIdentityService _identity = new();
    private readonly CopyVerificationService _verification = new();
    private sealed record Snapshot(FileManifest? Backup, FileManifest? Target, FileManifest? Source, DirectoryIdentity SourceIdentity);

    public static bool IsRestoreTargetBackup(MigrationTransactionRecord record) =>
        record.Mode == MigrationMode.Restore
        && record.BackupPath is not null
        && record.BackupPath == record.TargetPath + ".bak_" + record.TransactionId.ToString("D");

    public static string ExpectedConfirmationName(MigrationTransactionRecord record) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(
            IsRestoreTargetBackup(record) ? record.TargetPath : record.SourcePath));

    public async Task<MigrationTransactionRecord> MarkUserVerifiedAsync(Guid transactionId, CancellationToken ct = default)
    {
        var record = await LoadAsync(transactionId, ct).ConfigureAwait(false);
        await SnapshotAsync(record, false, ct).ConfigureAwait(false);
        return await _store.SaveAsync(record with
        {
            UserVerifiedAtUtc = DateTimeOffset.UtcNow,
            BackupState = record.BackupPath is not null && record.BackupState is (BackupState.RetainedUnverified or BackupState.UserVerified or BackupState.DeleteRequested or BackupState.DeleteFailed)
                ? BackupState.UserVerified : record.BackupState,
            DeletionRequestHistory = ArchiveRequest(record), DeletionRequest = null
        }, record.Revision, ct).ConfigureAwait(false);
    }

    public async Task<BackupDeletionRequest> CreateDeletionRequestAsync(Guid transactionId, BackupDeletionMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var record = await LoadAsync(transactionId, ct).ConfigureAwait(false);
        if (record.UserVerifiedAtUtc is null) throw new IOException("尚未确认用户验证，不能申请处理备份。");
        Snapshot snapshot = await SnapshotAsync(record, true, ct).ConfigureAwait(false);
        var request = new BackupDeletionRequest
        {
            RequestId = Guid.NewGuid(), TransactionId = transactionId, TransactionRevision = checked(record.Revision + 1), Mode = mode,
            BackupIdentity = record.BackupIdentity, TargetIdentity = record.TargetIdentity, SourceIdentity = snapshot.SourceIdentity,
            ManifestDigest = CopyVerificationService.Fingerprint(snapshot.Backup!, true),
            TargetManifestDigest = snapshot.Target is null ? null : CopyVerificationService.Fingerprint(snapshot.Target, true),
            LiveSourceManifestDigest = snapshot.Source is null ? null : CopyVerificationService.Fingerprint(snapshot.Source, true)
        };
        await _store.SaveAsync(record with
        {
            DeletionRequestHistory = ArchiveRequest(record), DeletionRequest = request, BackupState = BackupState.DeleteRequested
        }, record.Revision, ct).ConfigureAwait(false);
        return request;
    }

    public async Task<BackupDeletionRequest> ConfirmDeletionAsync(Guid requestId, ConfirmationStep step,
        string? confirmationText, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(step)) throw new ArgumentOutOfRangeException(nameof(step));
        var record = await FindRequestAsync(requestId, ct).ConfigureAwait(false);
        var request = record.DeletionRequest!;
        await ValidateOrInvalidateAsync(record, ct).ConfigureAwait(false);
        if (step == ConfirmationStep.First)
        {
            if (request.FirstConfirmedAtUtc is not null || request.SecondConfirmedAtUtc is not null)
                throw new IOException("第一次确认已记录，不能用重复调用代替第二次确认。");
            request = request with { FirstConfirmedAtUtc = DateTimeOffset.UtcNow };
        }
        else
        {
            if (request.FirstConfirmedAtUtc is null || request.SecondConfirmedAtUtc is not null)
                throw new IOException("必须先完成独立的第一次确认，且第二次确认不能重复。");
            if (request.Mode == BackupDeletionMode.Permanent && confirmationText != ExpectedConfirmationName(record))
                throw new IOException("永久删除第二次确认必须准确输入原目录名称（不含备份随机后缀）。");
            request = request with { SecondConfirmedAtUtc = DateTimeOffset.UtcNow, SecondConfirmationText = confirmationText };
        }
        request = request with { TransactionRevision = checked(record.Revision + 1) };
        await _store.SaveAsync(record with { DeletionRequest = request }, record.Revision, ct).ConfigureAwait(false);
        return request;
    }

    public Task<BackupDeletionResult> DeleteAsync(Guid requestId, CancellationToken ct = default) =>
        Task.Run(() => DeleteCoreAsync(requestId, ct), CancellationToken.None);

    private async Task<BackupDeletionResult> DeleteCoreAsync(Guid requestId, CancellationToken ct)
    {
        var record = await FindRequestAsync(requestId, ct).ConfigureAwait(false);
        var request = record.DeletionRequest!;
        if (request.FirstConfirmedAtUtc is null || request.SecondConfirmedAtUtc is null
            || (request.Mode == BackupDeletionMode.Permanent && request.SecondConfirmationText != ExpectedConfirmationName(record)))
            throw new IOException("缺少两次有效确认，不能处理备份。");
        Snapshot snapshot = await ValidateOrInvalidateAsync(record, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        BackupDeletionResult[] history = record.LastDeletionResult is null ? record.DeletionResultHistory : [.. record.DeletionResultHistory, record.LastDeletionResult];
        request = request with { Consumed = true, ConsumedAtUtc = DateTimeOffset.UtcNow, TransactionRevision = checked(record.Revision + 1) };
        record = await _store.SaveAsync(record with
        {
            DeletionRequest = request, BackupDeletionInProgress = true, BackupState = BackupState.PartiallyDeleted,
            DeletionResultHistory = history,
            LastDeletionResult = new BackupDeletionResult
            {
                RequestId = requestId, TransactionId = record.TransactionId, Status = BackupDeletionStatus.Partial,
                Items = [new BackupDeletionItem(record.BackupPath!, BackupItemOutcome.Retained, "授权已消费，处理中；中断时不得视为完整备份。")]
            }
        }, record.Revision, ct).ConfigureAwait(false);
        var results = new List<BackupDeletionItem>();
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        string? current = null;
        BackupDeletionStatus status = BackupDeletionStatus.Failed;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (request.Mode == BackupDeletionMode.Recycle)
            {
                var recycled = RecycleBinHelper.MoveDirectoryToRecycleBin(requestId, record.TransactionId, record.BackupPath!, snapshot.Backup!);
                results.AddRange(recycled.Items); error = recycled.Error; status = recycled.Status;
            }
            else
            {
                // 先锁住已冻结的普通目录，禁止改名/替换；未知新增内容会让非递归目录删除失败。
                foreach (var item in snapshot.Backup!.Entries.Values.Where(e => e.IsDirectory).OrderBy(e => Depth(e.RelativePath)))
                {
                    ct.ThrowIfCancellationRequested();
                    current = FullPath(record, item.RelativePath);
                    directories.Add(item.RelativePath, OpenGuard(current, item.Identity, delete: false));
                }
                var locked = await _verification.CaptureAsync(record.BackupPath!, record.VerificationMode, ct).ConfigureAwait(false);
                RequireDigest(locked, request.ManifestDigest);
                if (request.TargetManifestDigest is not null)
                    RequireDigest(await _verification.CaptureAsync(record.TargetPath, record.VerificationMode, ct).ConfigureAwait(false), request.TargetManifestDigest);
                _identity.RequireSame(record.SourcePath, request.SourceIdentity);
                if (record.Mode == MigrationMode.Restore)
                    RequireDigest(await _verification.CaptureAsync(record.SourcePath, record.VerificationMode, ct).ConfigureAwait(false), request.LiveSourceManifestDigest);
                else _identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
                foreach (var item in snapshot.Backup.Entries.Values.Where(e => !e.IsDirectory).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    current = FullPath(record, item.RelativePath);
                    await DeleteEntryAsync(current, record.BackupPath!, item, record.VerificationMode, ct).ConfigureAwait(false);
                    removed.Add(item.RelativePath);
                    results.Add(new BackupDeletionItem(current, BackupItemOutcome.Deleted));
                }
                foreach (var item in snapshot.Backup.Entries.Values.Where(e => e.IsDirectory).OrderByDescending(e => Depth(e.RelativePath)))
                {
                    ct.ThrowIfCancellationRequested();
                    current = FullPath(record, item.RelativePath);
                    directories.Remove(item.RelativePath, out SafeFileHandle? guard);
                    guard?.Dispose();
                    await DeleteEntryAsync(current, record.BackupPath!, item, record.VerificationMode, ct).ConfigureAwait(false);
                    removed.Add(item.RelativePath);
                    results.Add(new BackupDeletionItem(current, BackupItemOutcome.Deleted));
                }
                status = BackupDeletionStatus.Deleted;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (current is not null) results.Add(new BackupDeletionItem(current, BackupItemOutcome.Failed, ex.Message));
            status = BackupDeletionStatus.Partial;
            if (removed.Count == 0)
            {
                try
                {
                    var remaining = await _verification.CaptureAsync(record.BackupPath!, record.VerificationMode, CancellationToken.None).ConfigureAwait(false);
                    if (CopyVerificationService.Fingerprint(remaining, true) == request.ManifestDigest) status = BackupDeletionStatus.Failed;
                }
                catch (Exception inspectionError) { error += $"；无法确认副本完整性：{inspectionError.Message}"; }
            }
            foreach (var item in snapshot.Backup!.Entries)
            {
                string path = FullPath(record, item.Key);
                if (!removed.Contains(item.Key) && path != current) results.Add(new BackupDeletionItem(path, BackupItemOutcome.Retained, "已停止，不重试剩余对象。"));
            }
        }
        finally
        {
            foreach (var handle in directories.Values) handle.Dispose();
        }
        var result = new BackupDeletionResult
        {
            RequestId = requestId, TransactionId = record.TransactionId, Status = status,
            Items = results.ToArray(), Error = error, CompletedAtUtc = DateTimeOffset.UtcNow
        };
        BackupState state = status switch
        {
            BackupDeletionStatus.Deleted => BackupState.Deleted,
            BackupDeletionStatus.Recycled => BackupState.Recycled,
            BackupDeletionStatus.Partial => BackupState.PartiallyDeleted,
            _ => BackupState.DeleteFailed
        };
        try
        {
            await _store.SaveAsync(record with
            {
                BackupDeletionInProgress = false, BackupState = state, LastDeletionResult = result, LastError = error
            }, record.Revision, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception writeError)
        {
            result = result with { Status = BackupDeletionStatus.Partial, Error = $"{error}；结果未能持久化：{writeError.Message}；授权已消费，按处理中/不完整处理，禁止重试。" };
        }
        return result;
    }

    private async Task DeleteEntryAsync(string path, string root, FileManifestEntry expected, VerificationMode mode, CancellationToken ct)
    {
        using (SafeFileHandle handle = OpenGuard(path, expected.Identity, delete: true))
        {
            FileManifestEntry actual = await _verification.CaptureDeletionEntryAsync(path, root, expected.IsDirectory, mode, ct).ConfigureAwait(false);
            if (!CopyVerificationService.SameEntry(expected, actual, identity: true, writeTime: !expected.IsDirectory))
                throw new IOException($"对象元数据或内容已变化，停止删除：{path}");
            ct.ThrowIfCancellationRequested();
            var disposition = new Disposition { Delete = true };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"不能按已核对句柄删除：{path}");
        }
        if (_identity.ReadIfExists(path) is not null) throw new IOException($"删除尚未完成，停止后续操作：{path}");
    }

    private SafeFileHandle OpenGuard(string path, DirectoryIdentity expected, bool delete)
    {
        MigrationRuntime.AssertDataPath(path);
        SafeFileHandle handle = CreateFileW(@"\\?\" + path, 0x80U | (delete ? 0x10000U : 0U), 1,
            IntPtr.Zero, 3, 0x02200000U, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(code, $"不能锁定已登记对象：{path}");
        }
        try { _identity.RequireSame(path, expected); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private async Task<Snapshot> ValidateOrInvalidateAsync(MigrationTransactionRecord record, CancellationToken ct)
    {
        var request = record.DeletionRequest ?? throw new IOException("删除请求缺失。");
        try
        {
            if (request.Consumed || request.TransactionId != record.TransactionId || request.TransactionRevision != record.Revision
                || record.UserVerifiedAtUtc is null || !Enum.IsDefined(request.Mode))
                throw new IOException("删除请求已消费、状态已变化或尚未用户验证。");
            Snapshot snapshot = await SnapshotAsync(record, true, ct).ConfigureAwait(false);
            if (request.BackupIdentity != record.BackupIdentity || request.TargetIdentity != record.TargetIdentity
                || request.SourceIdentity != snapshot.SourceIdentity)
                throw new IOException("请求冻结的实体身份已经变化。");
            RequireDigest(snapshot.Backup!, request.ManifestDigest);
            if (snapshot.Target is not null) RequireDigest(snapshot.Target, request.TargetManifestDigest);
            else if (request.TargetManifestDigest is not null) throw new IOException("目标实体状态已经变化。");
            if (snapshot.Source is not null) RequireDigest(snapshot.Source, request.LiveSourceManifestDigest);
            else if (request.LiveSourceManifestDigest is not null) throw new IOException("源实体类型已经变化。");
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !request.Consumed)
        {
            var current = await LoadAsync(record.TransactionId, CancellationToken.None).ConfigureAwait(false);
            if (current.DeletionRequest?.RequestId == request.RequestId && !current.BackupDeletionInProgress)
                await _store.SaveAsync(current with
                {
                    DeletionRequest = current.DeletionRequest with { Consumed = true, ConsumedAtUtc = DateTimeOffset.UtcNow },
                    UserVerifiedAtUtc = null, BackupState = BackupState.DeleteFailed, LastError = $"删除请求已失效：{ex.Message}"
                }, current.Revision, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Snapshot> SnapshotAsync(MigrationTransactionRecord record, bool deletion, CancellationToken ct)
    {
        if (record.Stage != MigrationStage.AwaitingUserVerification || record.BackupDeletionInProgress || record.VerificationSummary?.IsMatch != true)
            throw new IOException("事务未技术完成或仍在处理中，不能授权备份操作。");
        bool restoreTargetBackup = IsRestoreTargetBackup(record);
        MigrationRuntime.AssertDataPath(record.SourcePath, allowLink: record.Mode != MigrationMode.Restore);
        MigrationRuntime.AssertDataPath(record.TargetPath);
        _identity.RequireSame(Path.GetDirectoryName(record.SourcePath)!, record.SourceParentIdentity);
        _identity.RequireSame(Path.GetDirectoryName(record.TargetPath)!, record.TargetParentIdentity);
        FileManifest? target = null;
        if (!restoreTargetBackup)
        {
            _identity.RequireSame(record.TargetPath, record.TargetIdentity);
            target = await _verification.CaptureAsync(record.TargetPath, record.VerificationMode, ct).ConfigureAwait(false);
        }
        FileManifest? source = null;
        DirectoryIdentity live;
        if (record.Mode == MigrationMode.Restore)
        {
            _identity.RequireSame(record.SourcePath, record.StagingIdentity);
            live = record.StagingIdentity!;
            source = await _verification.CaptureAsync(record.SourcePath, record.VerificationMode, ct).ConfigureAwait(false);
        }
        else
        {
            _identity.RequireSame(record.SourcePath, record.LinkIdentity);
            _identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
            live = record.LinkIdentity!;
        }
        FileManifest? backup = null;
        if (record.BackupPath is not null && record.BackupState is not (BackupState.Deleted or BackupState.Recycled))
        {
            MigrationRuntime.AssertDataPath(record.BackupPath);
            _identity.RequireSame(record.BackupPath, record.BackupIdentity);
            backup = await _verification.CaptureAsync(record.BackupPath, record.VerificationMode, ct).ConfigureAwait(false);
        }
        if (deletion)
        {
            if (record.BackupPath is null || backup is null || record.BackupIdentity is null
                || record.BackupState is BackupState.PartiallyDeleted or BackupState.Missing or BackupState.IdentityChanged)
                throw new IOException("没有可授权处理的完整原始备份。");
            string expectedBackupPath = (record.Mode == MigrationMode.Restore ? record.TargetPath : record.SourcePath)
                + ".bak_" + record.TransactionId.ToString("D");
            if (record.BackupPath != expectedBackupPath)
                throw new IOException("备份位置与本事务意图不符，不能按目录名认领。");
            RequireDigest(backup, record.BackupManifestDigest);
            foreach (var other in await _store.ReadAllAsync(ct).ConfigureAwait(false))
                if (MigrationTransactionStore.ReferencesBackup(record, other, _identity))
                    throw new IOException($"备份仍被其他事务使用：{MigrationTransactionStore.FormatConflictTarget(other)}");
        }
        return new Snapshot(backup, target, source, live);
    }

    private async Task<MigrationTransactionRecord> FindRequestAsync(Guid requestId, CancellationToken ct)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("请求 ID 不能为空。", nameof(requestId));
        return (await _store.ReadAllAsync(ct).ConfigureAwait(false)).SingleOrDefault(r => r.DeletionRequest?.RequestId == requestId)
            ?? throw new IOException("删除请求不存在或已被新请求取代。");
    }

    private async Task<MigrationTransactionRecord> LoadAsync(Guid id, CancellationToken ct) =>
        await _store.LoadAsync(id, ct).ConfigureAwait(false) ?? throw new IOException("事务记录缺失，不能认领备份。");

    private static BackupDeletionRequest[] ArchiveRequest(MigrationTransactionRecord record) => record.DeletionRequest is null
        ? record.DeletionRequestHistory : [.. record.DeletionRequestHistory, record.DeletionRequest];

    private static void RequireDigest(FileManifest manifest, string? digest)
    {
        if (digest is null || CopyVerificationService.Fingerprint(manifest, true) != digest)
            throw new IOException("集合与持久化基线或冻结请求不同；未授予删除权限。");
    }

    private static int Depth(string relative) => relative.Length == 0 ? -1 : relative.Count(c => c == '\\');
    private static string FullPath(MigrationTransactionRecord record, string relative) => relative.Length == 0
        ? record.BackupPath! : Path.Combine(record.BackupPath!, relative);

    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref Disposition information, uint size);
}
