using MigrationCore.Models.Scanning;

namespace MigrationCore.Models.Cleanup;

/// <summary>
/// 垃圾清理建议度 3 档分级（严格对齐设计方案 9.2 与画板 07）。
/// 永久排除区永不进入规则列表，无对应档位。
/// </summary>
public enum CleanupRecommendationTier
{
    /// <summary>1 档：建议清理（翠绿，100% 可重建且无功能副作用）。</summary>
    Recommended = 1,

    /// <summary>2 档：按需清理（主蓝，安全但存在重下或 DISM 耗时成本）。</summary>
    OnDemand = 2,

    /// <summary>3 档：谨慎清理（琥珀警示，默认折叠，存在不可回退代价或兼容性风险，须二次确认）。</summary>
    Caution = 3
}

/// <summary>
/// 垃圾清理左侧分类导航（严格对齐画板 07 Categories）。
/// </summary>
public enum CleanupCategory
{
    SystemTempAndShader = 0,
    SystemUpdateAndComponent = 1,
    SoftwareAndDeveloperCache = 2,
    CautionItems = 3
}

/// <summary>
/// 严格隔离的双执行通道（对齐设计方案 9.1）。
/// </summary>
public enum CleanupExecutionChannel
{
    /// <summary>通道 A：文件级安全隔离清理（默认同卷隔离区可恢复，确认框内可选移入回收站）。</summary>
    FileQuarantine = 0,

    /// <summary>通道 B：Windows 官方接口系统回收（DISM / pnputil / DeliveryOptimization 等，不进入隔离区）。</summary>
    OfficialSystemApi = 1
}

/// <summary>
/// 通道 A 文件级清理的用户确认动作。绝不提供直接永久删除，绝不因回收站失败自动退化为永久删除。
/// </summary>
public enum CleanupFileAction
{
    /// <summary>默认推荐：原子移入本应用受 ACL 保护的同卷隔离区（可恢复，不立即释放磁盘空间）。</summary>
    QuarantineSameVolume = 0,

    /// <summary>用户主动切换：通过 Windows Shell IFileOperation 移入回收站（不可回收时中止，绝不降级永久删除）。</summary>
    RecycleBin = 1
}

/// <summary>
/// 通道 B 与特殊检测规则的白名单官方系统接口类型（禁止拼接任意外部命令）。
/// </summary>
public enum CleanupOfficialApiKind
{
    None = 0,
    DismStartComponentCleanup = 1,
    DismResetBase = 2,
    DeliveryOptimization = 3,
    WindowsUpdateDownload = 4,
    WindowsOldOfficial = 5,
    PnpUtilSupersededDrivers = 6,
    OrphanedInstallerMsi = 7,
    RecycleBinAllDrives = 8
}

/// <summary>
/// 受限已知根目录定位器（禁止在规则中直接使用任意硬编码盘符或允许上级目录穿越）。
/// </summary>
public enum CleanupKnownRootKind
{
    UserTemp = 0,
    WindowsTemp = 1,
    LocalAppData = 2,
    RoamingAppData = 3,
    ProgramData = 4,
    WindowsDirectory = 5,
    UserProfile = 6,
    SystemDriveRoot = 7,
    /// <summary>白名单环境变量重定向根（CustomRootOverride 存环境变量名，自动校验非卷根与永久排除区）。</summary>
    EnvironmentVariable = 8,
    /// <summary>Steam 官方注册表与 libraryfolders.vdf 跨盘库根目录。</summary>
    SteamLibraries = 9,
    /// <summary>JetBrains idea.properties 中自定义的 idea.system.path 跨盘缓存根（CustomRootOverride 存目录通配名）。</summary>
    JetBrainsCustomSystem = 10,
    /// <summary>.npmrc 中配置的自定义 cache 路径。</summary>
    NpmConfigCache = 11,
    /// <summary>Maven settings.xml 中配置的自定义 localRepository 路径。</summary>
    MavenCustomRepository = 12,
    CustomTestRoot = 99
}

/// <summary>
/// 通道 A 受限扫描目标规范（仅允许已知根 + 相对子路径 + 有界单层通配与显式排除）。
/// </summary>
public sealed record CleanupPathRuleSpec(
    CleanupKnownRootKind RootKind,
    string RelativeSubPath,
    string[] IncludeFilePatterns,
    bool Recursive = true,
    string[]? ExcludePathSegments = null,
    string[]? ExcludeFileNames = null,
    string? CustomRootOverride = null,
    int? MinAgeHoursOverride = null);

/// <summary>
/// 「为什么需要谨慎」三层渐进式提示结构（列表行内一句话 + ⓘ 悬浮气泡 + 右侧抽屉结构化风险卡）。
/// </summary>
public sealed record CleanupCautionReason(
    string OneLineImpact,
    string HoverSummary,
    string RiskCause,
    string PossibleImpact,
    string ProtectionStrategy,
    bool RequiresPhraseConfirmation = true,
    string ConfirmationPhrase = "确认清理");

/// <summary>
/// 声明式内置垃圾清理规则（随应用版本固定，不接受外部网络未签名规则直接执行）。
/// </summary>
public sealed record CleanupRuleDefinition(
    string RuleId,
    string Version,
    string DisplayName,
    string ShortDescription,
    CleanupCategory Category,
    CleanupRecommendationTier Tier,
    CleanupExecutionChannel Channel,
    CleanupOfficialApiKind OfficialApiKind,
    CleanupPathRuleSpec[] TargetSpecs,
    string[] RequiredStoppedProcesses,
    int MinAgeHours,
    string CostAndReclaimNote,
    string ChannelBadgeText,
    bool IsRebuildable,
    bool ForceSameVolumeQuarantineOnly = false,
    CleanupCautionReason? CautionReason = null,
    bool IsBrowserCacheRule = false,
    string? GroupId = null,
    string? GroupDisplayName = null);

/// <summary>
/// 扫描进行中的单条规则实时命中摘要（用于驱动扫描中动态累计体积与实时大项榜单）。
/// </summary>
public sealed record CleanupLiveRuleHit(
    string RuleId,
    string DisplayName,
    CleanupCategory Category,
    CleanupRecommendationTier Tier,
    long TotalBytes,
    int MatchedCount,
    int SkippedRecentOrLockedCount,
    bool IsAvailable,
    bool HasBlockingProcesses,
    string[] Drives);

/// <summary>
/// 扫描前快速探测到的跨盘（非系统盘）缓存源摘要，用于引导态与右栏安全概览展示。
/// </summary>
public sealed record CrossDriveDiscoveryItem(
    string DriveLabel,
    string SourceKind,
    string RuleId,
    string RuleDisplayName,
    string DisplayPath);

/// <summary>
/// 垃圾清理只读扫描结构化进度（用于驱动确定性百分比进度条与实时状态展示）。
/// </summary>
public sealed record CleanupScanProgress(
    int CompletedSteps,
    int TotalSteps,
    string CurrentRuleName,
    string Message,
    CleanupLiveRuleHit? CompletedRuleHit = null)
{
    public double Percent => TotalSteps > 0 ? Math.Clamp(CompletedSteps * 100.0 / TotalSteps, 0d, 100d) : 0d;
}

/// <summary>
/// 预览冻结的单个候选明细（文件、旧驱动 INF、孤立 MSI 或官方组件估算项）。
/// </summary>
public sealed record CleanupPreviewItem(
    string ItemId,
    string DisplayName,
    string FullPath,
    long SizeBytes,
    long LastWriteUtcTicks,
    ulong FileId,
    bool IsDirectory,
    string KindTag,
    string? ExtraMetadata = null);

/// <summary>
/// 单条规则的只读扫描结果与冻结预览快照。
/// </summary>
public sealed record CleanupRuleScanResult(
    CleanupRuleDefinition Rule,
    CleanupPreviewItem[] PreviewItems,
    CleanupDirectoryExpectation[] VerificationExpectations,
    long TotalBytes,
    int MatchedCount,
    int SkippedRecentOrLockedCount,
    int ExcludedProtectedCount,
    string[] RunningBlockingProcesses,
    bool IsAvailable,
    string StatusNote,
    DateTimeOffset ScannedAtUtc)
{
    public bool HasBlockingProcesses => RunningBlockingProcesses.Length > 0;
    public bool CanSelect => IsAvailable && !HasBlockingProcesses && (MatchedCount > 0 || TotalBytes > 0 || Rule.Channel == CleanupExecutionChannel.OfficialSystemApi && IsAvailable);

    /// <summary>
    /// 提取该条规则命中所涉及的磁盘盘符列表（如 ["C:", "D:"]），按盘符升序去重。
    /// </summary>
    public string[] GetAffectedDrives()
    {
        var drives = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        static void TryAddDrive(SortedSet<string> set, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
            {
                set.Add(char.ToUpperInvariant(p[0]) + ":");
            }
        }

        foreach (CleanupDirectoryExpectation exp in VerificationExpectations)
        {
            TryAddDrive(drives, exp.RootDirectoryPath);
        }

        foreach (CleanupPreviewItem item in PreviewItems)
        {
            TryAddDrive(drives, item.FullPath);
        }

        if (drives.Count == 0 && IsAvailable && Rule.Channel == CleanupExecutionChannel.OfficialSystemApi)
        {
            string? sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
            TryAddDrive(drives, sysRoot);
        }

        return drives.ToArray();
    }
}

/// <summary>
/// 垃圾清理只读扫描总快照（绑定策略与规则版本，用于 UI 预览、执行前复核与 AI 只读分析）。
/// </summary>
public sealed record CleanupScanSnapshot(
    Guid CleanupScanId,
    DateTimeOffset ScannedAtUtc,
    string PolicyVersion,
    string RuleVersion,
    CleanupRuleScanResult[] Rules,
    long TotalReclaimableBytes,
    long RecommendedBytes,
    long OnDemandBytes,
    long CautionBytes);

/// <summary>
/// 清理执行单项结果状态：明确区分「已隔离（未释放空间）」「已移入回收站」「官方接口已回收」「跳过」「拦截」「失败」。
/// </summary>
public enum CleanupItemExecutionStatus
{
    Quarantined = 0,
    Recycled = 1,
    ReclaimedByOfficialApi = 2,
    Skipped = 3,
    Blocked = 4,
    Failed = 5
}

/// <summary>
/// 单条规则或单个 AI 疑似残留目录的清理执行明细。
/// </summary>
public sealed record CleanupExecutionItemResult(
    string TargetId,
    string DisplayName,
    CleanupExecutionChannel Channel,
    CleanupItemExecutionStatus Status,
    long ProcessedBytes,
    int ProcessedCount,
    int SkippedCount,
    int FailedCount,
    string? QuarantineBatchId,
    string Message,
    string[] Details);

/// <summary>
/// 垃圾清理执行阶段的实时结构化进度（覆盖执行前复核、通道 A 逐文件隔离/回收与通道 B 官方接口）。
/// </summary>
public sealed record CleanupExecutionProgress(
    int CompletedRules,
    int TotalRules,
    string CurrentRuleName,
    string PhaseText,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    double OverallPercent,
    bool IsIndeterminate,
    string Message);

/// <summary>
/// 整批垃圾清理执行总回执。
/// </summary>
public sealed record CleanupExecutionResult(
    Guid ExecutionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool WasCancelled,
    CleanupExecutionItemResult[] Items,
    long QuarantinedBytes,
    long RecycledBytes,
    long OfficialApiReclaimedBytes,
    int TotalSkippedCount,
    int TotalFailedCount,
    string SummaryMessage);

/// <summary>
/// 同卷隔离区内的单个文件/目录映射记录（采用短 ID 落盘防止 260 字符 MAX_PATH 溢出）。
/// </summary>
public sealed record QuarantineEntryRecord(
    string ShortId,
    string OriginalFullPath,
    string QuarantinedRelativePath,
    long SizeBytes,
    bool IsDirectory,
    ulong OriginalFileId,
    string? OriginalVolumeGuid);

/// <summary>
/// 同卷隔离区持久化批次清单（落盘于 &lt;Drive&gt;:\.MoveFolderQuarantine\&lt;BatchId&gt;\batch.json）。
/// </summary>
public sealed record QuarantineBatchRecord(
    int SchemaVersion,
    string BatchId,
    string DriveRoot,
    string QuarantineDirectoryPath,
    string DisplayTitle,
    string SourceDisplaySummary,
    string? RuleId,
    bool IsFromAiSuspectedResidual,
    DateTimeOffset CreatedAtUtc,
    long TotalBytes,
    int ItemCount,
    QuarantineEntryRecord[] Entries);

/// <summary>
/// 隔离区批次实时冲突检测视图（用于在画板 08 展示「路径冲突」与「对比后恢复」）。
/// </summary>
public sealed record QuarantineBatchInspection(
    QuarantineBatchRecord Batch,
    bool HasRestoreConflict,
    int ConflictCount,
    string[] ConflictSamplePaths,
    bool IsQuarantineDirectoryMissing);

/// <summary>
/// 隔离区恢复结果（绝不静默覆盖原路径已有新文件）。
/// </summary>
public sealed record QuarantineRestoreResult(
    string BatchId,
    bool Succeeded,
    int RestoredCount,
    int RestoredWithSuffixCount,
    int SkippedOrFailedCount,
    bool BatchRemovedAfterFullRestore,
    string Message,
    string[] Details);

/// <summary>
/// 隔离区人工二次确认永久删除结果（如实报告部分删除，绝不伪报全成功）。
/// </summary>
public sealed record QuarantineDeleteResult(
    string BatchId,
    bool FullyDeleted,
    bool PartiallyDeleted,
    long FreedBytesEstimate,
    int DeletedEntryCount,
    int FailedEntryCount,
    string Message,
    string[] Details);

/// <summary>
/// 永久排除区裁决结果。
/// </summary>
public sealed record CleanupExclusionVerdict(
    bool IsExcluded,
    string? ReasonCode,
    string? CategoryTitle,
    string? Message);

/// <summary>
/// AI 疑似残留候选的裁决状态（对齐画板 11：通过硬拦截的仅允许通道 A 同卷隔离；命中永久排除区的直接展示红色已硬拦截卡）。
/// </summary>
public enum AiCleanupCandidateStatus
{
    PendingManualReview = 0,
    HardBlockedByExclusion = 1,
    Quarantined = 2,
    Invalidated = 3
}

/// <summary>
/// 三期规则外「AI 疑似残留」候选条目（强制绑定 scanId + nodeId，仅允许通道 A 同卷隔离）。
/// </summary>
public sealed record AiCleanupCandidateRecord(
    Guid CandidateId,
    Guid ProposalId,
    Guid ScanId,
    int NodeId,
    string DisplayName,
    string SanitizedPath,
    string ActualLocalPath,
    long SizeBytes,
    int FileCount,
    string Reason,
    string RiskWarning,
    AiCleanupCandidateStatus Status,
    string? ExclusionReasonCode,
    string? ExclusionCategoryTitle,
    string? ExclusionMessage,
    DateTimeOffset CreatedAtUtc,
    string? QuarantineBatchId = null);

/// <summary>
/// AI 对内置规则项的排序/解读建议条目（只解读与排序，绝不能降低档位或绕过确认）。
/// </summary>
public sealed record AiCleanupRuleAdviceRecord(
    Guid ProposalId,
    Guid CleanupScanId,
    string RuleId,
    string RuleDisplayName,
    CleanupRecommendationTier OriginalTier,
    int Priority,
    string Reason,
    string? RiskWarning,
    DateTimeOffset CreatedAtUtc);
