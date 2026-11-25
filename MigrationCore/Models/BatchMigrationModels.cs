using MigrationCore.Models.Safety;

namespace MigrationCore.Models;

public enum BatchPlanItemSource
{
    ManualSelection,
    ScanDiscovery,
    AiProposal
}

public enum BatchPlanItemStatus
{
    Pending,
    Blocked,
    Ready,
    Running,
    CompletedAwaitingVerification,
    Failed,
    Cancelled,
    Skipped
}

/// <summary>
/// 批量迁移清单单项草案（供用户输入、编辑目标与 L3 人工确认）。
/// </summary>
public sealed record MigrationPlanDraftItem(
    string SourcePath,
    string? TargetPath,
    long LogicalBytes,
    long AllocatedBytes,
    bool IsCompressedOrSparse = false,
    bool HasSubtreeHardLinks = false,
    bool HasIncompleteScanData = false,
    bool L3RiskAcknowledged = false,
    Guid? ScanId = null,
    int? NodeId = null,
    BatchPlanItemSource Source = BatchPlanItemSource.ScanDiscovery,
    bool HasInternalHardLinks = false,
    int? ExpectedFileCount = null,
    bool HasReparseDescendants = false);

/// <summary>
/// 已冻结并经过安全策略、去重、碰撞与容量校验的批量迁移清单项。
/// </summary>
public sealed record MigrationPlanItem(
    Guid PlanItemId,
    Guid? ScanId,
    int? NodeId,
    DirectoryIdentity? SourceIdentity,
    string? ApplicationId,
    string? ApplicationDisplayName,
    string SourcePath,
    string TargetPath,
    long LogicalBytes,
    long AllocatedBytes,
    long RequiredTargetBytes,
    BatchPlanItemSource Source,
    SafetyPolicyDecision PolicyDecision,
    bool L3RiskAcknowledged,
    DateTimeOffset ValidatedAtUtc,
    BatchPlanItemStatus Status,
    string? BlockingReason = null,
    Guid? TransactionId = null,
    MigrationResult? ExecutionResult = null,
    bool IsCompressedOrSparse = false,
    int? ExpectedFileCount = null,
    bool HasReparseDescendants = false);

/// <summary>
/// 冻结后的批量迁移计划（携带递增 PlanRevision 与确定性 SHA-256 PlanDigest；任一变更即作废先前预检）。
/// </summary>
public sealed record FrozenMigrationPlan(
    long PlanRevision,
    string PlanDigest,
    VerificationMode VerificationMode,
    string? UnifiedTargetRoot,
    DateTimeOffset FrozenAtUtc,
    MigrationPlanItem[] Items,
    string[] BlockingIssues,
    IReadOnlyDictionary<string, long> RequiredBytesByTargetVolume)
{
    public bool HasBlockingIssues => BlockingIssues.Length > 0 || Items.Any(i => i.Status == BatchPlanItemStatus.Blocked);
}

public sealed record VolumeCapacityCheck(
    string VolumeRoot,
    long RequiredBytes,
    long AvailableBytes,
    bool IsSufficient);

/// <summary>
/// 批量启动前的全队列预检报告（只读进程检查 + 容量校验 + 计划摘要核验）。
/// </summary>
public sealed record BatchPreflightReport(
    bool CanStart,
    long PlanRevision,
    string PlanDigest,
    string[] BlockingIssues,
    string[] RunningBlockingProcesses,
    VolumeCapacityCheck[] VolumeCapacityChecks);

/// <summary>
/// 批量迁移实时单项进度事件。
/// </summary>
public sealed record BatchItemProgress(
    int ItemIndex,
    int TotalItems,
    Guid PlanItemId,
    string SourcePath,
    string TargetPath,
    MigrationProgress? InnerProgress,
    BatchPlanItemStatus ItemStatus,
    string Message);

/// <summary>
/// 批量迁移最终执行汇总（单项失败立即暂停后续队列，保留全部已成功项与当前项证据）。
/// </summary>
public sealed record BatchMigrationSummary(
    long PlanRevision,
    int SucceededCount,
    int FailedCount,
    int CancelledCount,
    int RemainingCount,
    bool PausedDueToFailure,
    string? PauseReason,
    MigrationPlanItem[] FinalItems);
