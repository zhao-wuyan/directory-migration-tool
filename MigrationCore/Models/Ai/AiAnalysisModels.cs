using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;

namespace MigrationCore.Models.Ai;

/// <summary>
/// AI 建议生命周期状态：严格分离「收到建议 → 人工审阅 → 采纳入清单（≠执行）→ 忽略/失效」。
/// </summary>
public enum AiProposalStatus
{
    Received,
    Reviewed,
    AdoptedToPlan,
    Dismissed,
    Invalidated
}

/// <summary>
/// 单条 AI 建议的服务端策略复核裁决。
/// </summary>
public enum AiProposalItemVerdict
{
    Accepted,
    RejectedByPolicy,
    RejectedInvalidNode,
    RejectedDuplicateOrOverlap,
    RejectedSnapshotInvalid,
    RejectedInvalidTarget
}

/// <summary>
/// 二期共享只读能力宣告（内置 AI 与四期外部 MCP 共用同一契约）。
/// </summary>
public sealed record AiCapabilitiesResponse(
    string ProtocolVersion,
    string PolicyVersion,
    bool ReadOnlyAnalysisOnly,
    bool CanExecuteOperations,
    string[] AuthorizedScopes,
    string[] AvailableTools,
    string Disclaimer);

/// <summary>
/// 授权扫描快照摘要条目（仅包含脱敏卷与容量统计，不含文件内容或原始用户名）。
/// </summary>
public sealed record AiScanSummaryItem(
    Guid ScanId,
    string VolumeDisplay,
    string DriveLetter,
    ScanBackendKind Backend,
    ScanCoverageStatus Coverage,
    bool CanUseForMigrationPlanning,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    string PolicyVersion,
    DateTimeOffset CompletedAtUtc,
    long ScanAgeSeconds,
    int NodeCount,
    int IncompleteNodeCount,
    bool RequiresLiveReverification);

public sealed record AiScanListResponse(
    AiScanSummaryItem[] Scans,
    string? NextCursor,
    int TotalCount);

/// <summary>
/// 单个快照的聚合分析摘要（附带顶层大目录概览，便于 AI 自顶向下逐层钻入）。
/// </summary>
public sealed record AiScanSummaryResponse(
    AiScanSummaryItem Summary,
    int HotspotCount,
    int VerifiedL1Count,
    long VerifiedL1Bytes,
    int NativeGuideL2Count,
    int ManualConfirmL3Count,
    AiTargetCandidateDto[] TargetCandidates,
    string Disclaimer,
    int RootNodeId = 0,
    AiDirectoryNodeDto[]? TopLevelDirectories = null);

/// <summary>
/// 服务端颁发的合法迁移目标候选卷（AI 只能引用 targetCandidateId，不得自行编造任意绝对路径）。
/// </summary>
public sealed record AiTargetCandidateDto(
    string TargetCandidateId,
    string DriveLetter,
    string SuggestedRoot,
    long AvailableBytes,
    bool IsRecommended);

/// <summary>
/// 目录级只读子节点（仅通过服务端 nodeId 标识，路径已完成用户名脱敏）。
/// </summary>
public sealed record AiDirectoryNodeDto(
    int NodeId,
    int ParentNodeId,
    string Name,
    string SanitizedPath,
    bool IsDirectory,
    long LogicalBytes,
    long AllocatedBytes,
    long UniqueAllocatedBytes,
    int FileCount,
    int DirectoryCount,
    double ParentSharePercent,
    MigrationAdmissionLevel AdmissionLevel,
    string[] ReasonCodes,
    string? MatchedApplicationId,
    string? MatchedApplicationName,
    string? DirectoryRoleTitle,
    bool HasSpecialFsFlags,
    DateTimeOffset? LastWriteUtc,
    bool CanDrillDown = false);

public sealed record AiDirectoryChildrenResponse(
    Guid ScanId,
    int ParentNodeId,
    string ParentSanitizedPath,
    AiDirectoryNodeDto[] Children,
    string? NextCursor,
    int TotalDirectoryCount,
    bool TruncatedByPageLimit,
    int FilteredOutSmallCount = 0);

/// <summary>
/// 通过本地 4 级安全策略筛出的迁移候选摘要。
/// </summary>
public sealed record AiMigrationCandidateDto(
    int NodeId,
    string DisplayName,
    string SanitizedPath,
    long LogicalBytes,
    long AllocatedBytes,
    long UniqueAllocatedBytes,
    long ConservativeTargetBytes,
    MigrationAdmissionLevel AdmissionLevel,
    string[] ReasonCodes,
    string? MatchedApplicationId,
    string? MatchedApplicationName,
    string? DirectoryRoleTitle,
    string? RecommendedTargetCandidateId,
    string? SuggestedTargetDisplay,
    string[] RequiredStoppedProcesses,
    bool HasSubtreeHardLinks,
    bool HasIncompleteNodes,
    string Summary);

public sealed record AiMigrationCandidatesResponse(
    Guid ScanId,
    string PolicyVersion,
    AiMigrationCandidateDto[] Candidates,
    string? NextCursor,
    int TotalCandidateCount,
    string Disclaimer,
    bool TruncatedByBudget = false);

/// <summary>
/// 单个候选节点的详细只读画像（应用归属、策略依据、用途说明、需关闭进程与空间口径）。
/// </summary>
public sealed record AiCandidateDetailsResponse(
    Guid ScanId,
    int NodeId,
    string DisplayName,
    string SanitizedPath,
    long LogicalBytes,
    long AllocatedBytes,
    long UniqueAllocatedBytes,
    long ConservativeTargetBytes,
    int FileCount,
    int DirectoryCount,
    MigrationAdmissionLevel AdmissionLevel,
    bool IsAllowedForNewMigration,
    string[] ReasonCodes,
    string PolicySummary,
    string? MatchedApplicationId,
    string? MatchedApplicationName,
    string? NativeGuidance,
    string[] Evidence,
    string[] Warnings,
    string[] RequiredStoppedProcesses,
    string? DirectoryRoleTitle,
    string? DirectoryPurpose,
    string? DirectoryAdvice,
    bool HasSubtreeHardLinks,
    bool HasIncompleteNodes,
    bool HasReparseDescendants,
    string SpaceAccountingNote);

/// <summary>
/// 扩展名只读统计桶（仅依据扫描快照文件名后缀聚合，永不读取文件内容）。
/// </summary>
public sealed record AiExtensionStatDto(
    string Extension,
    int FileCount,
    long TotalBytes);

/// <summary>
/// 修改时间分布桶（仅基于元数据时间戳）。
/// </summary>
public sealed record AiAgeBucketDto(
    string BucketLabel,
    int FileCount,
    long TotalBytes);

/// <summary>
/// 未知大占用目录的只读元数据画像（仅限非 L0 硬拒绝区，零文件内容读取）。
/// </summary>
public sealed record AiUnknownDirectoryMetadataResponse(
    Guid ScanId,
    int NodeId,
    string SanitizedPath,
    MigrationAdmissionLevel AdmissionLevel,
    long UniqueAllocatedBytes,
    long LogicalBytes,
    int FileCount,
    int DirectoryCount,
    string[] TopSubdirectoryNames,
    AiExtensionStatDto[] TopExtensions,
    AiAgeBucketDto[] ModifiedAgeBuckets,
    string? DirectoryRoleTitle,
    string? DirectoryPurpose,
    string ReadOnlyNotice);

/// <summary>
/// 只读公开策略说明（不可修改）。
/// </summary>
public sealed record AiMigrationPolicyResponse(
    string PolicyVersion,
    bool IsReadOnly,
    AiPolicyLevelDescription[] Levels,
    string[] HardDeniedCategories,
    string[] StableReasonCodes);

/// <summary>
/// 单块逻辑卷的只读摘要信息（不含底层敏感凭据，卷标已防注入清洗）。
/// </summary>
public sealed record AiDiskVolumeInfoDto(
    string DriveLetter,
    string VolumeLabel,
    string DriveType,
    string FileSystem,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    double UsedPercent,
    bool IsSystemDrive,
    bool SupportsSymlinkMigrationTarget,
    string? TargetCandidateId,
    string? TargetNote);

/// <summary>
/// Windows 常用特殊目录（桌面/下载/文档/AppData/Temp）的脱敏位置信息（仅含盘符与 &lt;USER&gt; 脱敏路径）。
/// </summary>
public sealed record AiSpecialFolderLocationDto(
    string FolderKey,
    string DisplayName,
    string DriveLetter,
    string SanitizedPath,
    bool IsOnSystemDrive);

/// <summary>
/// 脱敏只读系统与磁盘环境总览（严禁包含真实用户名、计算机名、SID、IP/MAC、环境变量全文或任何文件内容）。
/// </summary>
public sealed record AiSystemEnvironmentResponse(
    string OsDescription,
    string OsVersion,
    string OsArchitecture,
    string SystemDrive,
    bool IsAdministrator,
    bool IsDeveloperModeEnabled,
    bool CanCreateSymlinks,
    bool CanUseRawNtfsMftScan,
    int AuthorizedScanCount,
    AiDiskVolumeInfoDto[] Drives,
    AiSpecialFolderLocationDto[] SpecialFolders,
    string PrivacyNotice);

public sealed record AiPolicyLevelDescription(
    string Code,
    MigrationAdmissionLevel Level,
    string Title,
    string RuleSummary);

/// <summary>
/// AI 提交的迁移建议单项（严格白名单：仅允许 nodeId 与服务端 targetCandidateId，严禁任意绝对路径或 approved=true）。
/// </summary>
public sealed record AiMigrationProposalItemInput(
    int NodeId,
    string? TargetCandidateId,
    int Priority,
    string Reason,
    string? CompatibilityNotes = null,
    string? SuggestedCategory = null);

/// <summary>
/// AI 提交到收件箱的迁移建议文档。
/// </summary>
public sealed record AiMigrationProposalRequest(
    int SchemaVersion,
    string ClientRequestId,
    Guid ScanId,
    string PolicyVersion,
    AiMigrationProposalItemInput[] Items);

/// <summary>
/// 经服务端策略复核后的单条建议记录。
/// </summary>
public sealed record AiMigrationProposalItemRecord(
    Guid ProposalItemId,
    int NodeId,
    string DisplayName,
    string SanitizedPath,
    string ActualLocalPath,
    long LogicalBytes,
    long UniqueAllocatedBytes,
    MigrationAdmissionLevel AdmissionLevel,
    string? MatchedApplicationId,
    string? MatchedApplicationName,
    string? TargetCandidateId,
    string? SuggestedTargetPath,
    int Priority,
    string Reason,
    string? CompatibilityNotes,
    string SuggestedCategory,
    AiProposalItemVerdict Verdict,
    string[] ReasonCodes,
    string VerdictMessage,
    bool IsAdoptableToMigrationPlan,
    bool IsAdvisoryOnly);

/// <summary>
/// AI 建议提交回执（含幂等标识、逐项裁决与 L0 自动拦截记录）。
/// </summary>
public sealed record AiMigrationProposalReceipt(
    Guid ProposalId,
    string ClientRequestId,
    Guid ScanId,
    string PolicyVersion,
    DateTimeOffset ReceivedAtUtc,
    AiProposalStatus Status,
    bool IsIdempotentReplay,
    int AcceptedCount,
    int BlockedByPolicyCount,
    int RejectedCount,
    AiMigrationProposalItemRecord[] Items,
    string Disclaimer,
    string? RejectionMessage = null);

/// <summary>
/// 建议状态查询响应（只读，不授权执行）。
/// </summary>
public sealed record AiProposalStatusResponse(
    Guid ProposalId,
    Guid ScanId,
    AiProposalStatus Status,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    int AcceptedCount,
    int BlockedByPolicyCount,
    bool ExecutionAuthorized,
    string StatusNote);
