namespace MigrationCore.Models.Scanning;

/// <summary>
/// 会话级磁盘扫描快照的新鲜度状态。
/// </summary>
public enum SnapshotFreshness
{
    /// <summary>未命中缓存或已被硬过期驱逐。</summary>
    Missing = 0,
    /// <summary>处于新鲜期内，无主动失效与明显外部容量漂移，可直接秒开复用。</summary>
    Fresh = 1,
    /// <summary>超过新鲜期（未硬过期）、本软件刚执行过写操作、或检测到卷可用空间显著变动，允许浏览但提示刷新。</summary>
    Stale = 2,
    /// <summary>已超过硬过期时长（例如软件挂机超过 60 分钟），等待或已被自动释放。</summary>
    Expired = 3
}

/// <summary>
/// 轻量卷实时探针结果（微秒级读取卷可用空间与可选 GUID，用于检测软件外部大改动）。
/// </summary>
public sealed record VolumeLiveProbe(
    string DriveRoot,
    long FreeBytes,
    long TotalBytes,
    string? VolumeGuid = null);

/// <summary>
/// 会话级共享快照缓存策略配置（仅驻留进程内存，关闭应用即失效）。
/// </summary>
public sealed record VolumeSnapshotCacheOptions
{
    /// <summary>新鲜期（默认 15 分钟）：期内直接复用且无陈旧提示。</summary>
    public TimeSpan FreshTtl { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>硬过期时间（默认 60 分钟）：超期后由后台定时清扫自动驱逐释放内存，防止软件长开数日占用内存或展示多日前旧数据。</summary>
    public TimeSpan HardExpiryTtl { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>后台定时清扫间隔（默认 5 分钟；设为 TimeSpan.Zero 或负数则不启动定时器，便于单元测试）。</summary>
    public TimeSpan EvictionSweepInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>内存中最多同时保留的卷快照数（LRU 淘汰，默认 3 个盘）。</summary>
    public int MaxCachedVolumes { get; init; } = 3;

    /// <summary>外部可用空间变动触发 Stale 的绝对阈值（默认 512 MiB）。</summary>
    public long FreeSpaceDriftThresholdBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>外部可用空间变动触发 Stale 的相对总容量比例阈值（默认 2%）。</summary>
    public double FreeSpaceDriftRatio { get; init; } = 0.02;
}

/// <summary>
/// 缓存在会话内存中的单卷扫描快照及元数据。
/// </summary>
public sealed class CachedVolumeSnapshot
{
    public required string DriveRoot { get; init; }
    public required string VolumeTitle { get; init; }
    public required ScanSnapshot Snapshot { get; init; }
    public required DateTimeOffset CachedAtUtc { get; init; }
    public DateTimeOffset LastAccessedUtc { get; set; }
    public double ScanElapsedSeconds { get; init; }
    public int LargeDirCount { get; init; }
    public int L1Count { get; init; }
    public long L1Bytes { get; init; }
    public bool IsInvalidated { get; set; }
    public string? InvalidationReason { get; set; }

    public TimeSpan GetAge(DateTimeOffset nowUtc)
    {
        TimeSpan age = nowUtc - CachedAtUtc;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}

/// <summary>
/// 缓存查询结果，附带实时评估后的新鲜度与原因说明。
/// </summary>
public sealed record VolumeSnapshotLookup(
    CachedVolumeSnapshot Entry,
    SnapshotFreshness Freshness,
    TimeSpan Age,
    string? StaleReason)
{
    public bool IsStale => Freshness == SnapshotFreshness.Stale;
    public bool IsFresh => Freshness == SnapshotFreshness.Fresh;
}
