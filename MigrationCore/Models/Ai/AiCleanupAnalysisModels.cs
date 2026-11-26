using MigrationCore.Models.Cleanup;

namespace MigrationCore.Models.Ai;

/// <summary>
/// 三期清理建议单项的服务端安全裁决。
/// </summary>
public enum AiCleanupProposalItemVerdict
{
    AcceptedRuleAdvice = 0,
    AcceptedResidualQuarantineOnly = 1,
    HardBlockedByExclusion = 2,
    RejectedInvalidTarget = 3,
    RejectedDuplicateOrOverlap = 4,
    RejectedSnapshotInvalid = 5
}

/// <summary>
/// get_cleanup_scan_summary 响应（按三档聚合脱敏摘要）。
/// </summary>
public sealed record AiCleanupScanSummaryResponse(
    Guid CleanupScanId,
    DateTimeOffset ScannedAtUtc,
    string PolicyVersion,
    string RuleVersion,
    long TotalReclaimableBytes,
    long RecommendedBytes,
    long OnDemandBytes,
    long CautionBytes,
    int RecommendedRuleCount,
    int OnDemandRuleCount,
    int CautionRuleCount,
    int AvailableRuleCount,
    string Disclaimer);

/// <summary>
/// list_cleanup_items 单个内置规则摘要项（只读，不含执行能力）。
/// </summary>
public sealed record AiCleanupRuleSummaryDto(
    string RuleId,
    string DisplayName,
    CleanupCategory Category,
    CleanupRecommendationTier Tier,
    CleanupExecutionChannel Channel,
    long TotalBytes,
    int MatchedCount,
    bool IsAvailable,
    bool HasBlockingProcesses,
    string[] BlockingProcesses,
    string CostAndReclaimNote,
    string StatusNote,
    string? OneLineCautionImpact);

public sealed record AiCleanupItemsResponse(
    Guid CleanupScanId,
    string? TierFilter,
    AiCleanupRuleSummaryDto[] Items,
    string? NextCursor,
    int TotalCount,
    string Disclaimer);

/// <summary>
/// 清理预览明细的脱敏视图（绝不暴露原始用户名或读取文件内容）。
/// </summary>
public sealed record AiCleanupPreviewSampleDto(
    string DisplayName,
    string SanitizedPath,
    long SizeBytes,
    string KindTag);

/// <summary>
/// get_cleanup_item_details 响应（含完整「为什么需要谨慎」结构化风险卡）。
/// </summary>
public sealed record AiCleanupItemDetailsResponse(
    Guid CleanupScanId,
    string RuleId,
    string DisplayName,
    string ShortDescription,
    CleanupCategory Category,
    CleanupRecommendationTier Tier,
    CleanupExecutionChannel Channel,
    long TotalBytes,
    int MatchedCount,
    int SkippedRecentOrLockedCount,
    string[] RunningBlockingProcesses,
    string CostAndReclaimNote,
    bool IsRebuildable,
    CleanupCautionReason? CautionReason,
    AiCleanupPreviewSampleDto[] SamplePreviewItems,
    string Disclaimer);

/// <summary>
/// submit_cleanup_proposal 单项输入（仅接受服务端 ruleId 或 scanId+nodeId，严禁任意路径、命令或永久删除请求）。
/// </summary>
public sealed record AiCleanupProposalItemInput(
    string Kind,
    string? RuleId = null,
    int? NodeId = null,
    string? SuggestedAction = "QuarantineSameVolume",
    int Priority = 1,
    string Reason = "",
    string? RiskWarning = null);

/// <summary>
/// submit_cleanup_proposal 请求契约（对齐设计方案 8.5.2）。
/// </summary>
public sealed record AiCleanupProposalRequest(
    int SchemaVersion,
    string ClientRequestId,
    Guid? ScanId,
    Guid? CleanupScanId,
    string PolicyVersion,
    AiCleanupProposalItemInput[] Items);

/// <summary>
/// 经服务端永久排除区与策略复核后的单条清理建议记录。
/// </summary>
public sealed record AiCleanupProposalItemRecord(
    Guid ProposalItemId,
    string Kind,
    string? RuleId,
    int? NodeId,
    string DisplayName,
    string SanitizedPath,
    string ActualLocalPath,
    long SizeBytes,
    int FileCount,
    CleanupRecommendationTier? OriginalRuleTier,
    string EnforcedChannel,
    int Priority,
    string Reason,
    string RiskWarning,
    AiCleanupProposalItemVerdict Verdict,
    string? ExclusionReasonCode,
    string? ExclusionCategoryTitle,
    string VerdictMessage);

/// <summary>
/// submit_cleanup_proposal 回执（附强制免责声明与永久排除区拦截计数）。
/// </summary>
public sealed record AiCleanupProposalReceipt(
    Guid ProposalId,
    string ClientRequestId,
    Guid? ScanId,
    Guid? CleanupScanId,
    string PolicyVersion,
    DateTimeOffset ReceivedAtUtc,
    AiProposalStatus Status,
    bool IsIdempotentReplay,
    int AcceptedRuleAdviceCount,
    int AcceptedResidualCount,
    int HardBlockedCount,
    int RejectedCount,
    AiCleanupProposalItemRecord[] Items,
    string Disclaimer,
    string? RejectionMessage = null);
