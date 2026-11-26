using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Ai;
using MoveWithSymlinkWPF.Helpers;

namespace MoveWithSymlinkWPF.ViewModels;

/// <summary>
/// AI 预设服务商胶囊按钮模型（对齐 docs/untitled.pen 画板 10 ProviderPresets）。
/// </summary>
public partial class AiPresetChipItem : ObservableObject
{
    public AiProviderPresetDefinition Definition { get; }
    public AiProviderPreset Preset => Definition.Preset;
    public string DisplayName => Definition.DisplayName;
    public string HintText => Definition.HintText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipTag))]
    private bool _isSelected;

    public string ChipTag => IsSelected ? "Active" : string.Empty;

    public AiPresetChipItem(AiProviderPresetDefinition definition) => Definition = definition;
}

/// <summary>
/// 当前共享给内置 AI 的扫描快照脱敏热点目录行（对齐画板 10 ScanSnapshotSummary）。
/// </summary>
public sealed class AiContextHotspotRow
{
    public int NodeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string SanitizedPath { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public MigrationAdmissionLevel Level { get; init; }
    public string BadgeText { get; init; } = string.Empty;
    public string BadgeBg { get; init; } = "#ECFDF5";
    public string BadgeBorder { get; init; } = "#A7F3D0";
    public string BadgeFg { get; init; } = "#0F7B4B";
}

/// <summary>
/// 抽屉中渲染的结构化建议卡片（绿色迁移建议 / 琥珀色疑似残留或原生引导 / 红色 L0 自动拦截）。
/// </summary>
public partial class AiProposalCardItem : ObservableObject
{
    public Guid ProposalId { get; init; }
    public Guid ScanId { get; init; }
    public int NodeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string SanitizedPath { get; init; } = string.Empty;
    public string ActualLocalPath { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string VerdictMessage { get; init; } = string.Empty;
    public string? SuggestedTargetPath { get; init; }
    public MigrationAdmissionLevel AdmissionLevel { get; init; }
    public AiProposalItemVerdict Verdict { get; init; }
    public bool IsAdoptableToMigrationPlan { get; init; }
    public bool IsBlockedByPolicy => Verdict == AiProposalItemVerdict.RejectedByPolicy;
    public bool IsSuspectedCacheAdvisory { get; init; }
    public bool IsNativeGuideAdvisory { get; init; }
    public bool IsCleanupResidualProposal { get; init; }
    public bool IsCleanupRuleAdvice { get; init; }
    public bool IsL2NativeGuide => !IsBlockedByPolicy && (AdmissionLevel == MigrationAdmissionLevel.VendorNativeRecommended || IsNativeGuideAdvisory);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdoptButtonText))]
    [NotifyPropertyChangedFor(nameof(CanAdopt))]
    private bool _isAdopted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdopt))]
    private bool _isInvalidated;

    public bool CanAdopt => IsAdoptableToMigrationPlan && !IsAdopted && !IsInvalidated;

    public string CardTitle => IsBlockedByPolicy
        ? $"已自动拦截高危建议 · {DisplayName}"
        : (IsSuspectedCacheAdvisory || IsCleanupResidualProposal)
            ? $"AI 疑似残留 · {DisplayName}"
            : IsCleanupRuleAdvice
                ? $"清理规则建议 · {DisplayName}"
                : IsL2NativeGuide
                    ? $"L2 原生引导 · {DisplayName}"
                    : $"建议迁移 · {DisplayName}";

    public string CardBg => IsBlockedByPolicy ? "#FEF2F2"
        : (IsSuspectedCacheAdvisory || IsCleanupResidualProposal || IsL2NativeGuide || AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm) ? "#FFFBEB"
        : IsCleanupRuleAdvice ? "#EBF5FF"
        : "#ECFDF5";

    public string CardBorder => IsBlockedByPolicy ? "#FECACA"
        : (IsSuspectedCacheAdvisory || IsCleanupResidualProposal || IsL2NativeGuide || AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm) ? "#FDE68A"
        : IsCleanupRuleAdvice ? "#BFDBFE"
        : "#A7F3D0";

    public string CardAccentFg => IsBlockedByPolicy ? "#C42B1C"
        : (IsSuspectedCacheAdvisory || IsCleanupResidualProposal || IsL2NativeGuide || AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm) ? "#B45309"
        : IsCleanupRuleAdvice ? "#0078D4"
        : "#0F7B4B";

    public string AdoptButtonText => IsInvalidated
        ? "快照已变更（建议失效）"
        : IsAdopted
            ? "✓ 已加入待迁移清单"
            : AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm
                ? "+ 加入待迁移清单（L3 需人工确认）"
                : "+ 加入待迁移清单（不立即执行）";

    public bool ShowOpenCleanupResidualButton => !IsBlockedByPolicy && (IsCleanupResidualProposal || IsSuspectedCacheAdvisory);
    public bool ShowLocateButton => !IsBlockedByPolicy && !IsAdoptableToMigrationPlan && !ShowOpenCleanupResidualButton && NodeId > 0;
}

/// <summary>
/// 单条工具调用轨迹视图模型（兼容保留）。
/// </summary>
public sealed class AiTraceRowItem
{
    public string Prefix { get; init; } = "✓";
    public string Text { get; init; } = string.Empty;
    public string Foreground { get; init; } = "#64748B";
}

/// <summary>
/// AI 执行时间线单步基类（按真实发生顺序交错呈现思考过程、工具调用与系统提示）。
/// </summary>
public abstract partial class AiTimelineStepItem : ObservableObject
{
}

/// <summary>
/// 时间线中的单段模型思考过程（与前后工具调用严格按发生顺序分段，支持折叠预览与点击展开）。
/// </summary>
public partial class AiThinkingStepItem : AiTimelineStepItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    [NotifyPropertyChangedFor(nameof(AccentForeground))]
    private bool _isStreaming = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    private bool _isExpanded;

    public string HeaderText => IsStreaming ? "思考中…" : "思考过程";
    public string AccentForeground => IsStreaming ? "#0078D4" : "#64748B";
    public string ChevronGlyph => IsExpanded ? "⌄" : "›";

    public string PreviewText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text)) return string.Empty;
            string compact = string.Join(" ", Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return compact.Length > 140 ? compact[..140] + "…" : compact;
        }
    }
}

/// <summary>
/// 时间线中的单次只读工具调用（默认精简单行显示“调用 xxx 工具”，点击后展开参数与结果详情）。
/// </summary>
public partial class AiToolCallStepItem : AiTimelineStepItem
{
    public int StepNumber { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string FriendlyLabel { get; init; } = string.Empty;
    public string ArgumentsSummary { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(TitleForeground))]
    [NotifyPropertyChangedFor(nameof(RightMetaText))]
    [NotifyPropertyChangedFor(nameof(DetailHeaderText))]
    private bool _isRunning = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(TitleForeground))]
    [NotifyPropertyChangedFor(nameof(RightMetaText))]
    [NotifyPropertyChangedFor(nameof(ResultForeground))]
    private bool _succeeded = true;

    [ObservableProperty]
    private string _resultSummary = "执行中…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultPayload))]
    private string _resultPayload = string.Empty;

    public bool HasResultPayload => !string.IsNullOrWhiteSpace(ResultPayload);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightMetaText))]
    [NotifyPropertyChangedFor(nameof(DetailHeaderText))]
    private long _elapsedMilliseconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    private bool _isExpanded;

    /// <summary>默认单行展示文案：简洁呈现调用了哪个工具。</summary>
    public string CompactTitle => $"调用 {ToolName} 工具";

    /// <summary>参考极简列表风格的短动词标签（如「获取概览」「钻入目录」「查询候选」）。</summary>
    public string ActionVerb => ResolveActionVerb(ToolName);

    /// <summary>单行右侧主目标文案：展示工具名与精简关键参数（省略冗长 scanId）。</summary>
    public string CompactTarget => BuildCompactTarget(ToolName, ArgumentsSummary);

    public string StatusGlyph => IsRunning ? "⋯" : (Succeeded ? "✓" : "✕");
    public string StatusColor => IsRunning ? "#0078D4" : (Succeeded ? "#0F7B4B" : "#C42B1C");
    public string TitleForeground => !IsRunning && !Succeeded ? "#C42B1C" : "#1E293B";
    public string ResultForeground => !IsRunning && !Succeeded ? "#C42B1C" : "#334155";
    public string ChevronGlyph => IsExpanded ? "⌄" : "›";

    public string RightMetaText => IsRunning
        ? "执行中…"
        : !Succeeded
            ? "失败"
            : ElapsedMilliseconds >= 1000
                ? $"{ElapsedMilliseconds / 1000.0:F1}s"
                : ElapsedMilliseconds > 0
                    ? $"{ElapsedMilliseconds}ms"
                    : string.Empty;

    public string DetailHeaderText => string.IsNullOrWhiteSpace(FriendlyLabel)
        ? $"第 {StepNumber} 步 · {ToolName}"
        : $"第 {StepNumber} 步 · {FriendlyLabel}（{ToolName}）";

    public string DisplayArguments => string.IsNullOrWhiteSpace(ArgumentsSummary)
        ? "（无参数）"
        : ArgumentsSummary;

    public static string ResolveActionVerb(string toolName) => toolName switch
    {
        "get_capabilities" => "查询能力",
        "get_system_environment" => "查询环境",
        "list_scans" => "列出快照",
        "get_scan_summary" => "获取概览",
        "list_directory_children" => "钻入目录",
        "list_migration_candidates" => "查询候选",
        "get_candidate_details" => "查看详情",
        "inspect_unknown_directory_metadata" => "分析画像",
        "get_migration_policy" => "读取策略",
        "submit_migration_proposal" => "提交建议",
        "get_proposal_status" => "查询状态",
        "get_cleanup_scan_summary" => "清理概览",
        "list_cleanup_items" => "清理规则",
        "get_cleanup_item_details" => "清理详情",
        "submit_cleanup_proposal" => "清理建议",
        "request_user_scan_action" => "引导扫描",
        _ => "调用工具"
    };

    private static string BuildCompactTarget(string toolName, string? argumentsSummary)
    {
        if (string.IsNullOrWhiteSpace(argumentsSummary))
        {
            return toolName;
        }

        // 过滤掉冗长的 scanId=xxxx-xxxx-...，仅保留直观关键参数，保持单行清爽易读
        string[] meaningful = argumentsSummary
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("scanId=", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return meaningful.Length == 0
            ? toolName
            : $"{toolName} ({string.Join(", ", meaningful)})";
    }

    public static string ResolveFriendlyLabel(string toolName) => toolName switch
    {
        "get_capabilities" => "查询只读分析能力",
        "get_system_environment" => "获取脱敏系统与磁盘环境信息",
        "list_scans" => "列出扫描快照",
        "get_scan_summary" => "获取磁盘扫描概览",
        "list_directory_children" => "钻入查看子目录",
        "list_migration_candidates" => "查询可迁移候选",
        "get_candidate_details" => "查看目录安全详情",
        "inspect_unknown_directory_metadata" => "分析未知目录元数据",
        "get_migration_policy" => "读取 4 级安全策略",
        "submit_migration_proposal" => "提交建议卡片",
        "get_proposal_status" => "查询建议状态",
        "get_cleanup_scan_summary" => "获取垃圾清理扫描概览",
        "list_cleanup_items" => "列出内置垃圾清理规则",
        "get_cleanup_item_details" => "查看清理规则风险与明细",
        "submit_cleanup_proposal" => "提交垃圾清理与疑似残留建议",
        "request_user_scan_action" => "弹出交互式扫描确认卡片",
        _ => "只读工具调用"
    };
}

/// <summary>
/// 连续工具调用聚合分组（在时间线中将相邻的工具调用收拢为一行汇总 + 可折叠细竖线子列表）。
/// </summary>
public partial class AiToolBatchStepItem : AiTimelineStepItem
{
    public ObservableCollection<AiToolCallStepItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    private bool _isExpanded = true;

    public string ChevronGlyph => IsExpanded ? "⌄" : "›";

    public bool HasRunningItem => Items.Any(i => i.IsRunning);

    public string HeaderForeground => HasRunningItem ? "#0078D4" : "#475569";

    public string SummaryText
    {
        get
        {
            if (Items.Count == 0) return "只读工具调用";

            AiToolCallStepItem? running = Items.LastOrDefault(i => i.IsRunning);
            if (running is not null)
            {
                return Items.Count == 1
                    ? $"正在{running.ActionVerb}（{running.ToolName}）…"
                    : $"正在{running.ActionVerb}…（本组共 {Items.Count} 项）";
            }

            var parts = new List<string>();
            int envCount = Items.Count(i => i.ToolName == "get_system_environment");
            int summaryCount = Items.Count(i => i.ToolName is "get_scan_summary" or "list_scans" or "get_capabilities");
            int drillCount = Items.Count(i => i.ToolName == "list_directory_children");
            int candidateCount = Items.Count(i => i.ToolName == "list_migration_candidates");
            int detailCount = Items.Count(i => i.ToolName == "get_candidate_details");
            int inspectCount = Items.Count(i => i.ToolName == "inspect_unknown_directory_metadata");
            int policyCount = Items.Count(i => i.ToolName == "get_migration_policy");
            int proposalCount = Items.Count(i => i.ToolName is "submit_migration_proposal" or "get_proposal_status");
            int otherCount = Items.Count - (envCount + summaryCount + drillCount + candidateCount + detailCount + inspectCount + policyCount + proposalCount);

            if (envCount > 0) parts.Add(envCount == 1 ? "获取系统环境" : $"查询环境 {envCount} 次");
            if (summaryCount > 0) parts.Add(summaryCount == 1 ? "获取扫描概览" : $"获取概览 {summaryCount} 次");
            if (policyCount > 0) parts.Add(policyCount == 1 ? "读取安全策略" : $"读取策略 {policyCount} 次");
            if (candidateCount > 0) parts.Add(candidateCount == 1 ? "查询迁移候选" : $"查询候选 {candidateCount} 次");
            if (drillCount > 0) parts.Add($"钻入 {drillCount} 个目录");
            if (detailCount > 0) parts.Add($"查看 {detailCount} 项详情");
            if (inspectCount > 0) parts.Add($"画像 {inspectCount} 个未知目录");
            if (proposalCount > 0) parts.Add($"提交 {proposalCount} 项建议");
            if (otherCount > 0) parts.Add($"调用 {otherCount} 次工具");

            string body = "已" + string.Join("、", parts);
            int failedCount = Items.Count(i => !i.Succeeded);
            return failedCount > 0 ? $"{body}（{failedCount} 项未通过）" : body;
        }
    }

    public void AddItem(AiToolCallStepItem item)
    {
        Items.Add(item);
        RefreshSummary();
    }

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasRunningItem));
        OnPropertyChanged(nameof(HeaderForeground));
        OnPropertyChanged(nameof(SummaryText));
    }
}

/// <summary>
/// 时间线中的 AI 文字输出段（包含工具调用前的过程说明以及最终 Markdown 结论，按真实发生顺序穿插显示）。
/// </summary>
public partial class AiAssistantMessageStepItem : AiTimelineStepItem
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isStreaming = true;
}

/// <summary>
/// 时间线中的系统纠偏或安全提示条目（如复读死循环自动恢复提示）。
/// </summary>
public sealed class AiNoticeStepItem : AiTimelineStepItem
{
    public string Prefix { get; init; } = "⚠";
    public string Message { get; init; } = string.Empty;
    public string Foreground { get; init; } = "#B45309";
}

/// <summary>
/// 聊天与受控工具调用轮次卡片（纯文本渲染，严禁超链接执行）。
/// </summary>
public partial class AiConversationTurnItem : ObservableObject
{
    public string UserPrompt { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TraceHeaderText))]
    private int _stepsUsed;

    /// <summary>配置的步数上限（0 表示不限制）。</summary>
    public int MaxSteps { get; init; } = AiProviderSettings.DefaultMaxToolSteps;

    [ObservableProperty]
    private string _statusBadgeText = "分析中…";

    [ObservableProperty]
    private string _statusBadgeFg = "#0078D4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThinkingText))]
    private string _thinkingText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssistantText))]
    private string _assistantText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorText))]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _hasToolActivity;

    public ObservableCollection<AiTimelineStepItem> TimelineItems { get; } = [];
    public ObservableCollection<AiTraceRowItem> Traces { get; } = [];
    public ObservableCollection<AiScanActionCardItem> ScanActionCards { get; } = [];
    public ObservableCollection<AiProposalCardItem> ProposalCards { get; } = [];

    public bool HasTimelineItems => TimelineItems.Count > 0;
    public bool HasScanActionCards => ScanActionCards.Count > 0;

    public string TraceHeaderText
    {
        get
        {
            if (StepsUsed <= 0)
            {
                return "AI 只读分析（已脱敏）";
            }
            return MaxSteps > 0
                ? $"AI 只读分析 · 已调用 {StepsUsed} / {MaxSteps} 次工具"
                : $"AI 只读分析 · 已调用 {StepsUsed} 次工具";
        }
    }

    public bool HasThinkingText => !string.IsNullOrWhiteSpace(ThinkingText);
    public bool HasAssistantText => !string.IsNullOrWhiteSpace(AssistantText);
    public bool HasErrorText => !string.IsNullOrWhiteSpace(ErrorText);

    public AiConversationTurnItem()
    {
        TimelineItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTimelineItems));
        ScanActionCards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasScanActionCards));
    }
}

/// <summary>
/// 独立 AI 页「分析上下文」面板中的本机固定磁盘余量行（复用 SharedWorkspaceStateCache）。
/// </summary>
public sealed class AiSharedDriveRowItem
{
    public string DriveRoot { get; init; } = string.Empty;
    public string DriveLetter { get; init; } = string.Empty;
    public string TitleText { get; init; } = string.Empty;
    public string SpaceMetaText { get; init; } = string.Empty;
    public double UsedPercent { get; init; }
    public string BarColor { get; init; } = "#0078D4";
    public string StatusBadgeText { get; init; } = string.Empty;
    public string StatusBadgeBg { get; init; } = "#F1F5F9";
    public string StatusBadgeBorder { get; init; } = "#E2E8F0";
    public string StatusBadgeFg { get; init; } = "#475569";
    public bool IsSystemDrive { get; init; }
    public bool HasAuthorizedSnapshot { get; init; }
}

/// <summary>
/// 独立 AI 页「分析上下文」面板中的已授权磁盘扫描快照胶囊项（支持多盘共存与单盘撤销）。
/// </summary>
public sealed class AiAuthorizedScanRowItem
{
    public Guid ScanId { get; init; }
    public string DriveLetter { get; init; } = string.Empty;
    public string SummaryText { get; init; } = string.Empty;
}

/// <summary>
/// AI 交互式扫描卡片中的单块磁盘勾选行（智能展示推荐标记、紧张度、新鲜快照复用状态）。
/// </summary>
public partial class AiScanDriveOptionItem : ObservableObject
{
    private readonly Action _selectionChangedCallback;

    public string DriveRoot { get; init; } = string.Empty;
    public string DriveLetter { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public double UsedPercent { get; init; }
    public bool IsSystemDrive { get; init; }
    public bool SupportsRawMft { get; init; }
    public bool IsAiRecommended { get; init; }
    public bool HasFreshSnapshot { get; init; }
    public string FreshSnapshotAgeText { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _selectionChangedCallback();

    public string DisplayTitle => string.IsNullOrWhiteSpace(VolumeLabel)
        ? (IsSystemDrive ? $"{DriveLetter} 系统盘" : $"{DriveLetter} 本地盘")
        : $"{DriveLetter} {VolumeLabel}";

    public string CapacityText => $"剩 {FileStatsService.FormatBytes(FreeBytes)} / {FileStatsService.FormatBytes(TotalBytes)}";

    public string PrimaryBadgeText => HasFreshSnapshot
        ? (string.IsNullOrEmpty(FreshSnapshotAgeText) ? "已有快照 ✓" : $"已有快照·{FreshSnapshotAgeText}")
        : IsAiRecommended
            ? "AI 推荐"
            : UsedPercent >= 85
                ? $"已用 {UsedPercent:F0}%"
                : SupportsRawMft
                    ? "NTFS·MFT"
                    : "目录扫描";

    public string PrimaryBadgeBg => HasFreshSnapshot ? "#ECFDF5"
        : IsAiRecommended ? "#EBF5FF"
        : UsedPercent >= 85 ? "#FEF2F2"
        : "#F1F5F9";

    public string PrimaryBadgeBorder => HasFreshSnapshot ? "#A7F3D0"
        : IsAiRecommended ? "#BFDBFE"
        : UsedPercent >= 85 ? "#FECACA"
        : "#E2E8F0";

    public string PrimaryBadgeFg => HasFreshSnapshot ? "#0F7B4B"
        : IsAiRecommended ? "#0078D4"
        : UsedPercent >= 85 ? "#C42B1C"
        : "#475569";

    public string SecondaryUrgencyText => !HasFreshSnapshot && IsAiRecommended && UsedPercent >= 85
        ? $"已用 {UsedPercent:F0}%"
        : string.Empty;

    public bool HasSecondaryUrgency => !string.IsNullOrEmpty(SecondaryUrgencyText);

    public AiScanDriveOptionItem(Action selectionChangedCallback)
    {
        _selectionChangedCallback = selectionChangedCallback;
    }
}

/// <summary>
/// 聊天流内嵌的交互式扫描确认卡片（AI 绝不自动扫盘；多盘默认仅勾选推荐/紧张盘，超过 4 盘自动折叠，原地显示进度，扫完默认自动续跑分析）。
/// </summary>
public partial class AiScanActionCardItem : ObservableObject
{
    public const int CollapseThreshold = 5;
    public const int CollapsedVisibleCount = 3;

    public Guid RequestId { get; init; } = Guid.NewGuid();
    public AiScanActionKind Kind { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string OriginalUserPrompt { get; init; } = string.Empty;
    public bool ShowDrivePicker { get; init; }
    public bool ShowCleanupOption { get; init; }

    public ObservableCollection<AiScanDriveOptionItem> AllDrives { get; } = [];
    public ObservableCollection<AiScanDriveOptionItem> VisibleDrives { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartScanButtonText))]
    [NotifyPropertyChangedFor(nameof(CanStartScan))]
    private bool _includeCleanupScan;

    [ObservableProperty]
    private bool _autoContinueAfterScan = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverflowToggleText))]
    private bool _isExpandedAllDrives;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan))]
    [NotifyPropertyChangedFor(nameof(ShowIdleActions))]
    [NotifyPropertyChangedFor(nameof(ShowRunningActions))]
    [NotifyPropertyChangedFor(nameof(ShowCompletedActions))]
    [NotifyPropertyChangedFor(nameof(CardBorder))]
    [NotifyPropertyChangedFor(nameof(CardBg))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan))]
    [NotifyPropertyChangedFor(nameof(ShowIdleActions))]
    [NotifyPropertyChangedFor(nameof(ShowCompletedActions))]
    [NotifyPropertyChangedFor(nameof(CardBorder))]
    [NotifyPropertyChangedFor(nameof(CardBg))]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isCancelled;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _progressIsIndeterminate = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string _errorMessage = string.Empty;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasOverflowDrives => AllDrives.Count >= CollapseThreshold;
    public bool ShowQuickDrivePresets => ShowDrivePicker && AllDrives.Count >= 3;
    public int CheckedDriveCount => AllDrives.Count(d => d.IsChecked);
    public bool HasAnyFreshReusableSnapshot => AllDrives.Any(d => d.HasFreshSnapshot);

    public bool ShowIdleActions => !IsScanning && !IsCompleted;
    public bool ShowRunningActions => IsScanning;
    public bool ShowCompletedActions => IsCompleted && !IsScanning;

    public bool CanStartScan => !IsScanning && !IsCompleted
        && (CheckedDriveCount > 0 || (ShowCleanupOption && IncludeCleanupScan) || (ShowDrivePicker && HasAnyFreshReusableSnapshot));

    public string CardTitle => Kind switch
    {
        AiScanActionKind.CleanupScan => "需要垃圾清理扫描数据（AI 不会自动扫描）",
        AiScanActionKind.Both => "需要补充扫描数据（AI 不会自动扫描）",
        _ => "需要选择磁盘进行只读扫描（AI 不会自动扫描）"
    };

    public string CardBg => IsCompleted ? "#ECFDF5" : "#F8FAFC";
    public string CardBorder => IsCompleted ? "#A7F3D0" : "#93C5FD";

    public string OverflowToggleText
    {
        get
        {
            if (!HasOverflowDrives) return string.Empty;
            int hiddenCount = Math.Max(0, AllDrives.Count - CollapsedVisibleCount);
            if (IsExpandedAllDrives)
            {
                return $"收起其余 {hiddenCount} 块磁盘 ▴";
            }

            string hiddenLabels = string.Join(" / ", AllDrives.Skip(CollapsedVisibleCount).Take(4).Select(d => d.DriveLetter));
            if (hiddenCount > 4) hiddenLabels += " …";
            return $"展开其余 {hiddenCount} 块磁盘（{hiddenLabels}）▾";
        }
    }

    public string StartScanButtonText
    {
        get
        {
            int driveCount = CheckedDriveCount;
            bool runCleanup = ShowCleanupOption && IncludeCleanupScan;
            if (runCleanup && driveCount > 0) return $"开始扫描（清理 + {driveCount} 块盘）";
            if (runCleanup) return "立即执行垃圾清理扫描";
            if (driveCount > 0) return $"开始扫描（{driveCount} 块盘）";
            if (ShowDrivePicker && HasAnyFreshReusableSnapshot) return "复用已有快照继续分析";
            return "请至少勾选 1 项";
        }
    }

    public CancellationTokenSource? ActiveScanCts { get; set; }

    public void PopulateDrives(IEnumerable<AiScanDriveOptionItem> orderedDrives)
    {
        AllDrives.Clear();
        foreach (AiScanDriveOptionItem item in orderedDrives)
        {
            AllDrives.Add(item);
        }
        RebuildVisibleDrives();
        NotifySelectionChanged();
    }

    public void RebuildVisibleDrives()
    {
        VisibleDrives.Clear();
        IEnumerable<AiScanDriveOptionItem> slice = HasOverflowDrives && !IsExpandedAllDrives
            ? AllDrives.Take(CollapsedVisibleCount)
            : AllDrives;
        foreach (AiScanDriveOptionItem item in slice)
        {
            VisibleDrives.Add(item);
        }
        OnPropertyChanged(nameof(HasOverflowDrives));
        OnPropertyChanged(nameof(ShowQuickDrivePresets));
        OnPropertyChanged(nameof(OverflowToggleText));
    }

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(CheckedDriveCount));
        OnPropertyChanged(nameof(CanStartScan));
        OnPropertyChanged(nameof(StartScanButtonText));
    }

    [RelayCommand]
    private void ToggleOverflowDrives()
    {
        IsExpandedAllDrives = !IsExpandedAllDrives;
        RebuildVisibleDrives();
    }

    [RelayCommand]
    private void SelectSystemDriveOnly()
    {
        if (IsScanning || IsCompleted) return;
        foreach (AiScanDriveOptionItem d in AllDrives)
        {
            d.IsChecked = d.IsSystemDrive;
        }
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void SelectTightDrives()
    {
        if (IsScanning || IsCompleted) return;
        bool anyTight = AllDrives.Any(d => d.UsedPercent >= 85);
        foreach (AiScanDriveOptionItem d in AllDrives)
        {
            d.IsChecked = anyTight ? d.UsedPercent >= 85 : d.IsSystemDrive;
        }
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void SelectAllDrives()
    {
        if (IsScanning || IsCompleted) return;
        foreach (AiScanDriveOptionItem d in AllDrives)
        {
            d.IsChecked = true;
        }
        NotifySelectionChanged();
    }
}

