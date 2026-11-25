using MigrationCore.Models.Scanning;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 进程级共享磁盘扫描快照临时缓存（仅在软件打开期间驻留内存，关闭软件即自然释放）。
/// 同时服务于磁盘扫描目录树、AI 助手与后续垃圾清理模块，具备四层协同防护：
/// 1. 两段式 TTL（默认 15 分钟新鲜期 / 60 分钟硬过期，后台定时自动回收未固定快照）；
/// 2. 事件驱动主动失效（订阅 OperationExecutionGate，迁移/还原/备份清理/垃圾清理完成后自动将受影响盘标为陈旧）；
/// 3. 廉价外部变动探针（对比实时卷可用空间与 VolumeGuid，外部大改动立即标陈旧）；
/// 4. 内存 LRU 上限（默认最多同时保留 3 个卷的完整快照）。
/// </summary>
public sealed class VolumeSnapshotCache : IDisposable
{
    public static VolumeSnapshotCache Shared { get; } = CreateSharedDefault();

    private readonly Lock _sync = new();
    private readonly Dictionary<string, CachedVolumeSnapshot> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly VolumeSnapshotCacheOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, VolumeLiveProbe?> _liveProbeProvider;
    private readonly OperationExecutionGate? _gate;
    private readonly Timer? _sweepTimer;

    private Guid? _pinnedScanId;
    private bool _disposed;

    public event EventHandler? CacheChanged;

    public VolumeSnapshotCacheOptions Options => _options;

    public VolumeSnapshotCache(
        VolumeSnapshotCacheOptions? options = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        Func<string, VolumeLiveProbe?>? liveProbeProvider = null,
        OperationExecutionGate? executionGate = null)
    {
        _options = options ?? new VolumeSnapshotCacheOptions();
        _utcNow = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _liveProbeProvider = liveProbeProvider ?? DefaultProbeVolume;
        _gate = executionGate;

        if (_gate is not null)
        {
            _gate.OperationCompleted += OnGateOperationCompleted;
        }

        if (_options.EvictionSweepInterval > TimeSpan.Zero)
        {
            _sweepTimer = new Timer(
                static state => ((VolumeSnapshotCache)state!).SweepExpiredSafe(),
                this,
                _options.EvictionSweepInterval,
                _options.EvictionSweepInterval);
        }
    }

    private static VolumeSnapshotCache CreateSharedDefault() =>
        new(
            options: new VolumeSnapshotCacheOptions(),
            utcNowProvider: null,
            liveProbeProvider: null,
            executionGate: OperationExecutionGate.Shared);

    /// <summary>
    /// 固定当前正在界面浏览/编辑清单的快照，避免后台硬过期定时器将其从活跃视图中强行移除。
    /// 传 null 解除固定。
    /// </summary>
    public void SetPinnedScanId(Guid? scanId)
    {
        bool changed;
        lock (_sync)
        {
            _pinnedScanId = scanId;
            changed = SweepExpiredLocked(_utcNow()) > 0;
        }

        if (changed)
        {
            RaiseCacheChanged();
        }
    }

    /// <summary>
    /// 写入或更新某卷的扫描快照（自动执行 LRU 容量上限裁剪）。
    /// </summary>
    public CachedVolumeSnapshot Put(
        ScanSnapshot snapshot,
        string volumeTitle,
        double scanElapsedSeconds,
        int largeDirCount = 0,
        int l1Count = 0,
        long l1Bytes = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string driveKey = NormalizeDriveRoot(snapshot.Volume.DriveRoot);
        DateTimeOffset now = _utcNow();

        var entry = new CachedVolumeSnapshot
        {
            DriveRoot = driveKey,
            VolumeTitle = string.IsNullOrWhiteSpace(volumeTitle) ? driveKey : volumeTitle,
            Snapshot = snapshot,
            CachedAtUtc = snapshot.CompletedAtUtc == default ? now : snapshot.CompletedAtUtc,
            LastAccessedUtc = now,
            ScanElapsedSeconds = scanElapsedSeconds,
            LargeDirCount = largeDirCount,
            L1Count = l1Count,
            L1Bytes = l1Bytes,
            IsInvalidated = false,
            InvalidationReason = null
        };

        lock (_sync)
        {
            _entries[driveKey] = entry;
            SweepExpiredLocked(now);
            EnforceLruCapLocked(keepDriveKey: driveKey);
        }

        RaiseCacheChanged();
        return entry;
    }

    /// <summary>
    /// 按盘符查找可用快照（默认启用廉价卷空间/GUID 变动探针；超硬过期且未固定时自动驱逐并返回 null）。
    /// </summary>
    public VolumeSnapshotLookup? TryGet(string driveRoot, bool runLiveProbe = true, bool touchAccessTime = true)
    {
        if (string.IsNullOrWhiteSpace(driveRoot)) return null;
        string driveKey = NormalizeDriveRoot(driveRoot);
        DateTimeOffset now = _utcNow();

        CachedVolumeSnapshot? entry;
        bool evicted = false;

        lock (_sync)
        {
            if (!_entries.TryGetValue(driveKey, out entry))
            {
                return null;
            }

            TimeSpan age = entry.GetAge(now);
            bool isPinned = _pinnedScanId.HasValue && entry.Snapshot.ScanId == _pinnedScanId.Value;
            if (age >= _options.HardExpiryTtl && !isPinned)
            {
                _entries.Remove(driveKey);
                evicted = true;
                entry = null;
            }
            else if (touchAccessTime)
            {
                entry.LastAccessedUtc = now;
            }
        }

        if (evicted)
        {
            RaiseCacheChanged();
            return null;
        }

        if (entry is null) return null;

        VolumeLiveProbe? probe = runLiveProbe ? SafeProbe(driveKey) : null;
        return EvaluateLookup(entry, now, probe);
    }

    /// <summary>
    /// 按 ScanId 精确查找内存缓存中的快照（供执行前复核或跨模块复用）。
    /// </summary>
    public VolumeSnapshotLookup? TryGetByScanId(Guid scanId, bool runLiveProbe = false)
    {
        DateTimeOffset now = _utcNow();
        CachedVolumeSnapshot? entry;
        lock (_sync)
        {
            entry = _entries.Values.FirstOrDefault(e => e.Snapshot.ScanId == scanId);
            if (entry is not null)
            {
                entry.LastAccessedUtc = now;
            }
        }

        if (entry is null) return null;
        VolumeLiveProbe? probe = runLiveProbe ? SafeProbe(entry.DriveRoot) : null;
        return EvaluateLookup(entry, now, probe);
    }

    /// <summary>
    /// 列出当前会话中仍有效的全部卷快照（按最近缓存时间降序），自动清扫已硬过期条目。
    /// </summary>
    public IReadOnlyList<VolumeSnapshotLookup> ListActive(bool runLiveProbe = false)
    {
        DateTimeOffset now = _utcNow();
        CachedVolumeSnapshot[] snapshotList;
        bool evicted;

        lock (_sync)
        {
            evicted = SweepExpiredLocked(now) > 0;
            snapshotList = _entries.Values
                .OrderByDescending(e => e.CachedAtUtc)
                .ToArray();
        }

        if (evicted)
        {
            RaiseCacheChanged();
        }

        var results = new List<VolumeSnapshotLookup>(snapshotList.Length);
        foreach (CachedVolumeSnapshot entry in snapshotList)
        {
            VolumeLiveProbe? probe = runLiveProbe ? SafeProbe(entry.DriveRoot) : null;
            results.Add(EvaluateLookup(entry, now, probe));
        }
        return results;
    }

    /// <summary>
    /// 主动将指定卷的缓存标为陈旧（例如完成迁移、还原、删除备份或垃圾清理后）。
    /// </summary>
    public bool InvalidateDrive(string driveRoot, string reason)
    {
        if (string.IsNullOrWhiteSpace(driveRoot)) return false;
        string driveKey = NormalizeDriveRoot(driveRoot);
        bool changed = false;

        lock (_sync)
        {
            if (_entries.TryGetValue(driveKey, out CachedVolumeSnapshot? entry))
            {
                entry.IsInvalidated = true;
                entry.InvalidationReason = string.IsNullOrWhiteSpace(reason) ? "卷内容已变更，建议重新扫描" : reason;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseCacheChanged();
        }
        return changed;
    }

    /// <summary>
    /// 按一组操作路径批量将受影响卷标为陈旧。
    /// </summary>
    public int InvalidateForPaths(IEnumerable<string> paths, string reason)
    {
        if (paths is null) return 0;

        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                string? root = Path.GetPathRoot(path);
                if (!string.IsNullOrWhiteSpace(root) && root.Length >= 2 && root[1] == ':')
                {
                    drives.Add(NormalizeDriveRoot(root));
                }
            }
            catch
            {
            }
        }

        int count = 0;
        lock (_sync)
        {
            foreach (string driveKey in drives)
            {
                if (_entries.TryGetValue(driveKey, out CachedVolumeSnapshot? entry))
                {
                    entry.IsInvalidated = true;
                    entry.InvalidationReason = string.IsNullOrWhiteSpace(reason) ? "本工具刚修改过该卷，建议重新扫描" : reason;
                    count++;
                }
            }
        }

        if (count > 0)
        {
            RaiseCacheChanged();
        }
        return count;
    }

    /// <summary>
    /// 主动驱逐超过硬过期时间且未固定的快照（供后台定时器及单元测试调用）。
    /// </summary>
    public int SweepExpired()
    {
        int removed;
        lock (_sync)
        {
            removed = SweepExpiredLocked(_utcNow());
        }

        if (removed > 0)
        {
            RaiseCacheChanged();
        }
        return removed;
    }

    public void Clear()
    {
        bool hadItems;
        lock (_sync)
        {
            hadItems = _entries.Count > 0;
            _entries.Clear();
            _pinnedScanId = null;
        }

        if (hadItems)
        {
            RaiseCacheChanged();
        }
    }

    public int Count
    {
        get
        {
            lock (_sync) return _entries.Count;
        }
    }

    private VolumeSnapshotLookup EvaluateLookup(CachedVolumeSnapshot entry, DateTimeOffset now, VolumeLiveProbe? probe)
    {
        TimeSpan age = entry.GetAge(now);

        if (entry.IsInvalidated)
        {
            return new VolumeSnapshotLookup(
                entry,
                SnapshotFreshness.Stale,
                age,
                entry.InvalidationReason ?? "卷状态已更新，建议重新扫描");
        }

        if (probe is not null)
        {
            if (!string.IsNullOrWhiteSpace(entry.Snapshot.Volume.VolumeGuid)
                && !string.IsNullOrWhiteSpace(probe.VolumeGuid)
                && !entry.Snapshot.Volume.VolumeGuid.Equals(probe.VolumeGuid, StringComparison.OrdinalIgnoreCase))
            {
                return new VolumeSnapshotLookup(
                    entry,
                    SnapshotFreshness.Stale,
                    age,
                    "检测到驱动器卷标识已变化（可能重新挂载或更换介质），请重新扫描");
            }

            long freeDelta = Math.Abs(probe.FreeBytes - entry.Snapshot.Volume.FreeBytes);
            double ratio = entry.Snapshot.Volume.TotalBytes > 0
                ? (double)freeDelta / entry.Snapshot.Volume.TotalBytes
                : 0;

            if (freeDelta >= _options.FreeSpaceDriftThresholdBytes || ratio >= _options.FreeSpaceDriftRatio)
            {
                return new VolumeSnapshotLookup(
                    entry,
                    SnapshotFreshness.Stale,
                    age,
                    $"检测到该盘剩余空间已变动 {FileStatsService.FormatBytes(freeDelta)}，建议重新扫描");
            }
        }

        if (age >= _options.HardExpiryTtl)
        {
            return new VolumeSnapshotLookup(
                entry,
                SnapshotFreshness.Stale,
                age,
                $"扫描快照已超过 {(int)_options.HardExpiryTtl.TotalMinutes} 分钟（当前仍锁定在视图中），建议重新扫描");
        }

        if (age >= _options.FreshTtl)
        {
            return new VolumeSnapshotLookup(
                entry,
                SnapshotFreshness.Stale,
                age,
                $"扫描快照距今已 {Math.Max(1, (int)age.TotalMinutes)} 分钟，执行前将自动定向复核，也可点击刷新");
        }

        return new VolumeSnapshotLookup(entry, SnapshotFreshness.Fresh, age, null);
    }

    private int SweepExpiredLocked(DateTimeOffset now)
    {
        if (_entries.Count == 0) return 0;

        List<string>? toRemove = null;
        foreach (var (key, entry) in _entries)
        {
            bool isPinned = _pinnedScanId.HasValue && entry.Snapshot.ScanId == _pinnedScanId.Value;
            if (isPinned) continue;

            if (entry.GetAge(now) >= _options.HardExpiryTtl)
            {
                toRemove ??= [];
                toRemove.Add(key);
            }
        }

        if (toRemove is null) return 0;
        foreach (string key in toRemove)
        {
            _entries.Remove(key);
        }
        return toRemove.Count;
    }

    private void EnforceLruCapLocked(string keepDriveKey)
    {
        int cap = Math.Max(1, _options.MaxCachedVolumes);
        while (_entries.Count > cap)
        {
            string? victim = null;
            DateTimeOffset oldest = DateTimeOffset.MaxValue;

            foreach (var (key, entry) in _entries)
            {
                if (key.Equals(keepDriveKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (_pinnedScanId.HasValue && entry.Snapshot.ScanId == _pinnedScanId.Value) continue;
                if (entry.LastAccessedUtc < oldest)
                {
                    oldest = entry.LastAccessedUtc;
                    victim = key;
                }
            }

            if (victim is null) break;
            _entries.Remove(victim);
        }
    }

    private void OnGateOperationCompleted(object? sender, OperationGateStatus completed)
    {
        if (completed.ActivePaths.Length == 0) return;

        string opLabel = string.IsNullOrWhiteSpace(completed.OperationTitle)
            ? (completed.OperationKind ?? "写操作")
            : completed.OperationTitle;

        InvalidateForPaths(completed.ActivePaths, $"刚完成「{opLabel}」，建议重新扫描以获取最新占用");
    }

    private void SweepExpiredSafe()
    {
        if (_disposed) return;
        try
        {
            SweepExpired();
        }
        catch
        {
        }
    }

    private VolumeLiveProbe? SafeProbe(string driveRoot)
    {
        try
        {
            return _liveProbeProvider(driveRoot);
        }
        catch
        {
            return null;
        }
    }

    private static VolumeLiveProbe? DefaultProbeVolume(string driveRoot)
    {
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady) return null;
        return new VolumeLiveProbe(driveRoot, drive.AvailableFreeSpace, drive.TotalSize);
    }

    private void RaiseCacheChanged()
    {
        try
        {
            CacheChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    public static string NormalizeDriveRoot(string driveRoot)
    {
        string trimmed = driveRoot.Trim();
        if (trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:\\";
        }
        return trimmed.EndsWith('\\') ? trimmed : trimmed + "\\";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweepTimer?.Dispose();
        if (_gate is not null)
        {
            _gate.OperationCompleted -= OnGateOperationCompleted;
        }
    }
}
