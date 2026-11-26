using MigrationCore.Models;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Cleanup;
using MigrationCore.Services.Scanning;

namespace MigrationCore.Services;

/// <summary>
/// 跨页面共享的只读磁盘卷态势项（磁盘扫描、垃圾清理、独立 AI 页与只读工具底座共用，避免反复枚举 DriveInfo）。
/// </summary>
public sealed record SharedDriveTelemetry(
    string DriveRoot,
    string DriveLetter,
    string VolumeLabel,
    string DriveType,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    double UsedPercent,
    bool IsSystemDrive,
    bool IsFixedDrive,
    bool SupportsRawMft,
    bool SupportsSymlinkMigrationTarget);

/// <summary>
/// 跨页面共享的迁移备份轻量汇总（绝不执行递归目录遍历；若进程内有新鲜 MFT 缓存则顺手 O(1) 估算体积）。
/// </summary>
public sealed record SharedBackupSummary(
    int UnverifiedBackupCount,
    int RecoveryRequiredCount,
    long EstimatedBackupBytes,
    bool HasEstimatedBytes,
    IReadOnlyDictionary<string, int> CountByDrive);

/// <summary>
/// 跨页面共享的清理隔离区轻量汇总（按卷汇总，跳过逐文件恢复冲突遍历）。
/// </summary>
public sealed record SharedQuarantineSummary(
    int TotalBatchCount,
    long TotalBytes,
    IReadOnlyDictionary<string, (int BatchCount, long TotalBytes)> ByDrive);

/// <summary>
/// 进程级共享态势快照。
/// </summary>
public sealed record WorkspaceStateSnapshot(
    DateTimeOffset RefreshedAtUtc,
    string SystemDrive,
    bool IsAdministrator,
    bool IsDeveloperModeEnabled,
    SharedDriveTelemetry[] Drives,
    SharedBackupSummary Backups,
    SharedQuarantineSummary Quarantine);

/// <summary>
/// 轻量跨页面共享态势缓存（短 TTL + 并发刷新合并 + 写操作主动失效），
/// 消除磁盘扫描页、垃圾清理页、独立 AI 页和 AI 工具底座重复枚举磁盘/隔离区/事务日志的开销。
/// </summary>
public sealed class SharedWorkspaceStateCache
{
    public static SharedWorkspaceStateCache Shared { get; } = new();

    private readonly TimeSpan _ttl;
    private readonly Func<CancellationToken, WorkspaceStateSnapshot> _collector;
    private readonly Lock _sync = new();

    private WorkspaceStateSnapshot? _cached;
    private bool _dirty = true;
    private Task<WorkspaceStateSnapshot>? _inFlightRefresh;

    public event EventHandler<WorkspaceStateSnapshot>? StateUpdated;

    public SharedWorkspaceStateCache(
        TimeSpan? ttl = null,
        Func<CancellationToken, WorkspaceStateSnapshot>? collector = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(20);
        _collector = collector ?? CollectLiveState;
    }

    /// <summary>
    /// 零等待读取当前缓存快照（若尚未初始化则返回 null）。
    /// </summary>
    public WorkspaceStateSnapshot? GetCachedOrNull()
    {
        lock (_sync)
        {
            return _cached;
        }
    }

    /// <summary>
    /// 当前缓存是否仍在有效期内且未被主动标记失效。
    /// </summary>
    public bool IsFresh()
    {
        lock (_sync)
        {
            return _cached is not null
                && !_dirty
                && (DateTimeOffset.UtcNow - _cached.RefreshedAtUtc) < _ttl;
        }
    }

    /// <summary>
    /// 标记全局态势缓存失效（完成迁移、删除备份、执行清理、释放/恢复隔离区或用户手动刷新时调用）。
    /// </summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// 同步获取态势快照：新鲜期内 0ms 返回内存缓存，过期或强制刷新时同步采集一次。
    /// </summary>
    public WorkspaceStateSnapshot GetOrRefreshSync(bool force = false, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!force && _cached is not null && !_dirty && (DateTimeOffset.UtcNow - _cached.RefreshedAtUtc) < _ttl)
            {
                return _cached;
            }
        }

        WorkspaceStateSnapshot fresh = _collector(ct);
        CommitSnapshot(fresh);
        return fresh;
    }

    /// <summary>
    /// 异步获取态势快照：新鲜期内 0ms 返回内存缓存；多页面并发请求时自动合并为单次后台采集。
    /// </summary>
    public Task<WorkspaceStateSnapshot> GetOrRefreshAsync(bool force = false, CancellationToken ct = default)
    {
        Task<WorkspaceStateSnapshot> task;
        lock (_sync)
        {
            if (!force && _cached is not null && !_dirty && (DateTimeOffset.UtcNow - _cached.RefreshedAtUtc) < _ttl)
            {
                return Task.FromResult(_cached);
            }

            if (_inFlightRefresh is not null && !force)
            {
                task = _inFlightRefresh;
            }
            else
            {
                task = RunBackgroundCollectAsync();
                _inFlightRefresh = task;
            }
        }

        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private Task<WorkspaceStateSnapshot> RunBackgroundCollectAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                WorkspaceStateSnapshot fresh = _collector(CancellationToken.None);
                CommitSnapshot(fresh);
                return fresh;
            }
            finally
            {
                lock (_sync)
                {
                    _inFlightRefresh = null;
                }
            }
        });
    }

    private void CommitSnapshot(WorkspaceStateSnapshot fresh)
    {
        lock (_sync)
        {
            _cached = fresh;
            _dirty = false;
        }

        try
        {
            StateUpdated?.Invoke(this, fresh);
        }
        catch
        {
        }
    }

    private static WorkspaceStateSnapshot CollectLiveState(CancellationToken ct)
    {
        string systemDir = Environment.SystemDirectory;
        string systemDriveRoot = VolumeSnapshotCache.NormalizeDriveRoot(Path.GetPathRoot(systemDir) ?? @"C:\");
        string systemDriveLetter = systemDriveRoot.TrimEnd('\\');
        bool isAdmin = PathValidator.IsAdministrator();
        bool isDevMode = IsDeveloperModeEnabled();

        var drives = new List<SharedDriveTelemetry>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();
                if (!drive.IsReady) continue;

                string root = VolumeSnapshotCache.NormalizeDriveRoot(drive.Name);
                string letter = root.TrimEnd('\\');
                bool isFixed = drive.DriveType == DriveType.Fixed;
                bool isSys = root.Equals(systemDriveRoot, StringComparison.OrdinalIgnoreCase);

                string fs = "Unknown";
                string label = string.Empty;
                long total = 0;
                long free = 0;
                try
                {
                    fs = string.IsNullOrWhiteSpace(drive.DriveFormat) ? "Unknown" : drive.DriveFormat;
                    label = drive.VolumeLabel ?? string.Empty;
                    total = Math.Max(0, drive.TotalSize);
                    free = Math.Max(0, drive.AvailableFreeSpace);
                }
                catch
                {
                    continue;
                }

                long used = Math.Max(0, total - free);
                double usedPct = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0;
                bool isNtfs = fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

                drives.Add(new SharedDriveTelemetry(
                    DriveRoot: root,
                    DriveLetter: letter,
                    VolumeLabel: label,
                    DriveType: drive.DriveType.ToString(),
                    FileSystem: fs,
                    TotalBytes: total,
                    FreeBytes: free,
                    UsedBytes: used,
                    UsedPercent: usedPct,
                    IsSystemDrive: isSys,
                    IsFixedDrive: isFixed,
                    SupportsRawMft: isFixed && isNtfs && isAdmin,
                    SupportsSymlinkMigrationTarget: isFixed && isNtfs && !isSys));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        string[] fixedRoots = drives.Where(d => d.IsFixedDrive).Select(d => d.DriveRoot).ToArray();
        SharedQuarantineSummary quarantine = CollectQuarantineSummary(fixedRoots, ct);
        SharedBackupSummary backups = CollectBackupSummary(ct);

        return new WorkspaceStateSnapshot(
            RefreshedAtUtc: DateTimeOffset.UtcNow,
            SystemDrive: systemDriveLetter,
            IsAdministrator: isAdmin,
            IsDeveloperModeEnabled: isDevMode,
            Drives: drives.OrderBy(d => d.DriveLetter, StringComparer.OrdinalIgnoreCase).ToArray(),
            Backups: backups,
            Quarantine: quarantine);
    }

    private static SharedQuarantineSummary CollectQuarantineSummary(string[] fixedDriveRoots, CancellationToken ct)
    {
        try
        {
            var store = new QuarantineStore();
            IReadOnlyList<QuarantineBatchInspection> list = store.ListAllInspections(
                checkRestoreConflicts: false,
                fixedDriveRoots: fixedDriveRoots,
                ct: ct);

            var byDrive = new Dictionary<string, (int BatchCount, long TotalBytes)>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (QuarantineBatchInspection item in list)
            {
                string driveRoot = VolumeSnapshotCache.NormalizeDriveRoot(item.Batch.DriveRoot);
                totalBytes += Math.Max(0, item.Batch.TotalBytes);
                if (byDrive.TryGetValue(driveRoot, out var existing))
                {
                    byDrive[driveRoot] = (existing.BatchCount + 1, existing.TotalBytes + Math.Max(0, item.Batch.TotalBytes));
                }
                else
                {
                    byDrive[driveRoot] = (1, Math.Max(0, item.Batch.TotalBytes));
                }
            }

            return new SharedQuarantineSummary(list.Count, totalBytes, byDrive);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SharedQuarantineSummary(0, 0, new Dictionary<string, (int, long)>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static SharedBackupSummary CollectBackupSummary(CancellationToken ct)
    {
        try
        {
            var store = new MigrationTransactionStore();
            IReadOnlyList<MigrationTransactionRecord> records = store.ReadAllAsync(ct).GetAwaiter().GetResult();

            int unverifiedCount = 0;
            int recoveryCount = 0;
            long estimatedBytes = 0;
            bool hasAnyEstimatedBytes = false;
            var byDrive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (MigrationTransactionRecord r in records.DistinctBy(x => x.TransactionId))
            {
                ct.ThrowIfCancellationRequested();

                if (r.Stage == MigrationStage.RecoveryRequired)
                {
                    recoveryCount++;
                }

                bool hasRetainedBackup = r.Stage == MigrationStage.AwaitingUserVerification
                    && r.BackupState is BackupState.RetainedUnverified or BackupState.UserVerified or BackupState.DeleteRequested or BackupState.DeleteFailed or BackupState.PartiallyDeleted
                    && !string.IsNullOrWhiteSpace(r.BackupPath)
                    && Directory.Exists(r.BackupPath);

                if (!hasRetainedBackup)
                {
                    continue;
                }

                unverifiedCount++;
                string? driveRoot = Path.GetPathRoot(r.BackupPath!);
                if (!string.IsNullOrWhiteSpace(driveRoot))
                {
                    string normRoot = VolumeSnapshotCache.NormalizeDriveRoot(driveRoot);
                    byDrive[normRoot] = byDrive.TryGetValue(normRoot, out int prev) ? prev + 1 : 1;

                    // 仅当内存里已存在该盘新鲜扫描快照时顺手做内存查表估算体积，绝不遍历磁盘
                    VolumeSnapshotLookup? cached = VolumeSnapshotCache.Shared.TryGet(normRoot, runLiveProbe: false, touchAccessTime: false);
                    if (cached is { IsFresh: true } && TryLookupDirectoryBytesInSnapshot(cached.Entry.Snapshot, r.BackupPath!, out long bytes))
                    {
                        estimatedBytes += bytes;
                        hasAnyEstimatedBytes = true;
                    }
                }
            }

            return new SharedBackupSummary(unverifiedCount, recoveryCount, estimatedBytes, hasAnyEstimatedBytes, byDrive);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SharedBackupSummary(0, 0, 0, false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool TryLookupDirectoryBytesInSnapshot(ScanSnapshot snapshot, string targetFullPath, out long bytes)
    {
        bytes = 0;
        try
        {
            string normTarget = PathIdentityService.Normalize(targetFullPath);
            string root = snapshot.Volume.DriveRoot;
            if (!normTarget.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string relative = normTarget[root.Length..].Trim('\\');
            if (string.IsNullOrEmpty(relative))
            {
                return false;
            }

            string[] segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            int currentId = snapshot.RootNodeId;
            foreach (string seg in segments)
            {
                int nextId = -1;
                foreach (int childId in snapshot.GetChildrenSpan(currentId))
                {
                    ref readonly ScanNodeRecord child = ref snapshot.Nodes[childId];
                    if (child.IsDirectory && snapshot.GetNodeName(childId).Equals(seg, StringComparison.OrdinalIgnoreCase))
                    {
                        nextId = childId;
                        break;
                    }
                }
                if (nextId < 0)
                {
                    return false;
                }
                currentId = nextId;
            }

            ref readonly ScanNodeRecord found = ref snapshot.Nodes[currentId];
            bytes = Math.Max(found.UniqueAllocatedBytes, found.LogicalBytes);
            return bytes > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDeveloperModeEnabled()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
            object? val = key?.GetValue("AllowDevelopmentWithoutDevLicense");
            return val is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }
}
