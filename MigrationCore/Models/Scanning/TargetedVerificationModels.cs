using MigrationCore.Models;

namespace MigrationCore.Models.Scanning;

/// <summary>
/// 定向路径实时复核结论。
/// </summary>
public enum TargetedVerificationOutcome
{
    /// <summary>核验通过（包括零变动 USN 快路径与无害缩小），可继续执行。</summary>
    Passed = 0,
    /// <summary>实质内容漂移（如迁移体积暴增、清理目录出现新增或刚修改的文件），必须停止并提示用户重新扫描确认。</summary>
    DriftStop = 1,
    /// <summary>硬性安全阻断（路径消失、FileId 身份变更、新出现重解析点/外部硬链接、权限受限），绝不允许继续。</summary>
    HardStop = 2
}

/// <summary>
/// 定向路径复核拦截原因代码。
/// </summary>
public enum TargetedVerificationReasonCode
{
    None = 0,
    UsnUnchangedFastPath = 1,
    PathMissingOrInaccessible = 2,
    IdentityChanged = 3,
    RootBecameReparsePoint = 4,
    NewReparsePointDetected = 5,
    SubtreeIncompleteOrAccessDenied = 6,
    NewExternalHardLinkDetected = 7,
    SizeGrowthExceededThreshold = 8,
    UnexpectedNewFileDetected = 9,
    FileModifiedSinceSnapshot = 10,
    FileSizeChangedSinceSnapshot = 11
}

/// <summary>
/// 迁移场景定向复核阈值配置。
/// </summary>
public sealed record TargetedMigrationVerifyOptions
{
    /// <summary>允许的最大相对体积增长比例（默认 10%）。需同时满足超过最小绝对增长阈值才触发 DriftStop。</summary>
    public double MaxRelativeSizeGrowth { get; init; } = 0.10;

    /// <summary>触发体积漂移停止的最小绝对增长字节数（默认 100 MiB，避免小目录新增几 KB 日志误拦截）。</summary>
    public long MinSignificantGrowthBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>对新发现文件做硬链接核验的最大抽检上限（默认 256，超出时直接按保守策略触发漂移停止）。</summary>
    public int MaxNewFilesForHardLinkProbe { get; init; } = 256;
}

/// <summary>
/// 迁移目录的定向实时复核结果。
/// </summary>
public sealed record TargetedMigrationVerificationResult(
    TargetedVerificationOutcome Outcome,
    TargetedVerificationReasonCode ReasonCode,
    string? Message,
    bool UsedUsnFastPath,
    long LiveLogicalBytes,
    long LiveAllocatedBytes,
    int LiveFileCount,
    int LiveDirectoryCount,
    bool HasReparseDescendants,
    DirectoryIdentity? LiveRootIdentity)
{
    public bool IsAllowed => Outcome == TargetedVerificationOutcome.Passed;
}

/// <summary>
/// 垃圾清理场景的单个预期文件快照绑定条目（“宁漏勿错、绝不多删”原则）。
/// </summary>
public sealed record CleanupFileExpectation(
    string FullPath,
    ulong ExpectedFileId,
    long ExpectedLogicalBytes,
    long ExpectedLastWriteUtcTicks);

/// <summary>
/// 垃圾清理场景的定向复核请求（按目标根目录绑定扫描时看到的精确文件清单）。
/// </summary>
public sealed record CleanupDirectoryExpectation(
    string RootDirectoryPath,
    DirectoryIdentity? ExpectedRootIdentity,
    DateTimeOffset SnapshotCompletedAtUtc,
    IReadOnlyList<CleanupFileExpectation> ExpectedFiles);

/// <summary>
/// 垃圾清理场景定向复核结果。
/// </summary>
public sealed record TargetedCleanupVerificationResult(
    TargetedVerificationOutcome Outcome,
    TargetedVerificationReasonCode ReasonCode,
    string? Message,
    string[] VerifiedReadyPaths,
    string[] SkippedMissingPaths,
    string[] ConflictingPaths)
{
    public bool IsAllowed => Outcome == TargetedVerificationOutcome.Passed;
}
