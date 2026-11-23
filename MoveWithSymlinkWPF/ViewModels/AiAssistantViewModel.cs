using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Ai;
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
        : IsSuspectedCacheAdvisory
            ? $"AI 疑似残留 · {DisplayName}"
            : IsL2NativeGuide
                ? $"L2 原生引导 · {DisplayName}"
                : $"建议迁移 · {DisplayName}";

    public string CardBg => IsBlockedByPolicy ? "#FEF2F2"
        : (IsSuspectedCacheAdvisory || IsL2NativeGuide || AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm) ? "#FFFBEB"
        : "#ECFDF5";

    public string CardBorder => IsBlockedByPolicy ? "#FECACA"
        : (IsSuspectedCacheAdvisory || IsL2NativeGuide || AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm) ? "#FDE68A"
        : "#A7F3D0";

    public string CardAccentFg => IsBlockedByPolicy ? "#C42B1C"
        : (IsSuspectedCacheAdvisory || IsL2NativeGuide || AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm) ? "#B45309"
        : "#0F7B4B";

    public string AdoptButtonText => IsInvalidated
        ? "快照已变更（建议失效）"
        : IsAdopted
            ? "✓ 已加入待迁移清单"
            : AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm
                ? "+ 加入待迁移清单（L3 需人工确认）"
                : "+ 加入待迁移清单（不立即执行）";

    public bool ShowLocateButton => !IsBlockedByPolicy && !IsAdoptableToMigrationPlan && NodeId > 0;
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
    public ObservableCollection<AiProposalCardItem> ProposalCards { get; } = [];

    public bool HasTimelineItems => TimelineItems.Count > 0;

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
    }
}

/// <summary>
/// 二期「工具内置 AI 助手（BYOK / 本地模型）」视图模型。
/// 驱动画板 10 完整工作台以及磁盘扫描页共用的右侧可折叠「AI 分析助手」抽屉。
/// </summary>
public partial class AiAssistantViewModel : ObservableObject
{
    private readonly ReadOnlyAiAnalysisToolset _toolset;
    private readonly AiProviderConfigStore _configStore;
    private readonly BuiltInAiAgentService _agentService;
    private readonly Func<Guid, int, string?, (bool Added, string Message)>? _adoptToPlanCallback;
    private readonly Func<Guid, int, bool>? _locateNodeInTreeCallback;
    private readonly Action? _openDiskScanCallback;
    private readonly Action? _openAiSettingsCallback;
    private readonly Action? _closeDrawerCallback;

    private AiProviderSettings _savedSettings;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _connectionTestCts;
    private Guid? _lastSyncedPlanScanId;
    private HashSet<int> _currentPlanNodeIds = [];
    private HashSet<string> _currentPlanSourcePaths = new(StringComparer.OrdinalIgnoreCase);

    public ReadOnlyAiAnalysisToolset Toolset => _toolset;

    public ObservableCollection<AiPresetChipItem> PresetChips { get; } = [];
    public ObservableCollection<AiContextHotspotRow> ContextHotspots { get; } = [];
    public ObservableCollection<AiConversationTurnItem> ConversationTurns { get; } = [];

    // ---- 当前共享扫描快照上下文 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthorizedSnapshot))]
    private Guid? _activeScanId;

    public bool HasAuthorizedSnapshot => ActiveScanId.HasValue;

    [ObservableProperty]
    private string _snapshotContextTitle = "当前尚未授权磁盘扫描快照";

    [ObservableProperty]
    private string _snapshotContextMeta = "请先在「磁盘扫描」页完成任意磁盘扫描，系统将自动脱敏并共享目录结构元数据。";

    [ObservableProperty]
    private bool _isSnapshotSharingEnabled = true;

    private ScanSnapshot? _latestKnownSnapshot;

    // ---- BYOK 模型配置表单 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsLocalEndpoint))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBg))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBorder))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeFg))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeText))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeFg))]
    private AiProviderPreset _selectedPreset = AiProviderPreset.DeepSeek;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsLocalEndpoint))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBg))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBorder))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeFg))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeText))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeFg))]
    private string _endpoint = "https://api.deepseek.com/v1";

    [ObservableProperty]
    private string _modelName = "deepseek-chat";

    [ObservableProperty]
    private int _maxToolSteps = AiProviderSettings.DefaultMaxToolSteps;

    [ObservableProperty]
    private int _timeoutSeconds = AiProviderSettings.DefaultTimeoutSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChineseLanguageChipTag))]
    [NotifyPropertyChangedFor(nameof(EnglishLanguageChipTag))]
    private AiResponseLanguage _responseLanguage = AiResponseLanguage.Chinese;

    public string ChineseLanguageChipTag => ResponseLanguage == AiResponseLanguage.Chinese ? "Active" : string.Empty;
    public string EnglishLanguageChipTag => ResponseLanguage == AiResponseLanguage.English ? "Active" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedApiKey))]
    [NotifyPropertyChangedFor(nameof(ApiKeyStatusHint))]
    private string _maskedApiKeyPreview = string.Empty;

    public bool HasSavedApiKey => !string.IsNullOrWhiteSpace(MaskedApiKeyPreview);
    public bool IsLocalEndpoint => AiProviderSettings.IsLoopbackEndpoint(Endpoint);

    public string OriginBadgeText => BuildDraftSettings().GetOriginBadgeText();
    public string OriginBadgeBg => IsLocalEndpoint ? "#ECFDF5" : "#FFFBEB";
    public string OriginBadgeBorder => IsLocalEndpoint ? "#A7F3D0" : "#FDE68A";
    public string OriginBadgeFg => IsLocalEndpoint ? "#0F7B4B" : "#B45309";

    public string PrivacyNoticeText => IsLocalEndpoint
        ? "本地推理端点：目录结构元数据 100% 留在你本机，不发送至任何外部云端服务。"
        : "云端仅发送脱敏目录元数据，永不读文件内容；需 100% 离线请选 Ollama。";

    public string PrivacyNoticeFg => IsLocalEndpoint ? "#0F7B4B" : "#B45309";

    public string ApiKeyStatusHint => IsLocalEndpoint
        ? "本地模型通常无需 API Key（可留空直接测试连接）"
        : HasSavedApiKey
            ? $"{MaskedApiKeyPreview}（Windows DPAPI 当前用户加密保存，留空则沿用已存密钥）"
            : "输入后将以 Windows DPAPI 当前用户加密保存，绝不明文落盘";

    [ObservableProperty]
    private string _connectionBadgeText = "尚未测试连接";

    [ObservableProperty]
    private string _connectionBadgeBg = "#F8FAFC";

    [ObservableProperty]
    private string _connectionBadgeBorder = "#E2E8F0";

    [ObservableProperty]
    private string _connectionBadgeFg = "#64748B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigFeedback))]
    private string _configFeedbackMessage = string.Empty;

    public bool HasConfigFeedback => !string.IsNullOrWhiteSpace(ConfigFeedbackMessage);

    [ObservableProperty]
    private bool _isTestingConnection;

    // ---- 右侧抽屉提问与状态 ----
    [ObservableProperty]
    private string _userPromptInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendPrompt))]
    private bool _isAnalyzing;

    public bool CanSendPrompt => !IsAnalyzing;
    public bool HasConversationTurns => ConversationTurns.Count > 0;
    public string DisclaimerText => "AI 建议仅供参考，请用户自行判断，本工具不对 AI 误判负责。";

    /// <summary>
    /// 用于 UI PasswordBox 在测试/保存时读取用户刚输入的未保存 API Key（不回写明文到可序列化状态）。
    /// </summary>
    public Func<string?>? PendingApiKeyAccessor { get; set; }
    public Action? ClearPendingApiKeyBoxAction { get; set; }

    public AiAssistantViewModel(
        ReadOnlyAiAnalysisToolset? toolset = null,
        AiProviderConfigStore? configStore = null,
        BuiltInAiAgentService? agentService = null,
        Func<Guid, int, string?, (bool Added, string Message)>? adoptToPlanCallback = null,
        Func<Guid, int, bool>? locateNodeInTreeCallback = null,
        Action? openDiskScanCallback = null,
        Action? openAiSettingsCallback = null,
        Action? closeDrawerCallback = null)
    {
        _toolset = toolset ?? new ReadOnlyAiAnalysisToolset();
        _configStore = configStore ?? new AiProviderConfigStore();
        _agentService = agentService ?? new BuiltInAiAgentService(_toolset);
        _adoptToPlanCallback = adoptToPlanCallback;
        _locateNodeInTreeCallback = locateNodeInTreeCallback;
        _openDiskScanCallback = openDiskScanCallback;
        _openAiSettingsCallback = openAiSettingsCallback;
        _closeDrawerCallback = closeDrawerCallback;

        foreach (AiProviderPresetDefinition def in AiProviderSettings.Presets)
        {
            PresetChips.Add(new AiPresetChipItem(def));
        }

        ConversationTurns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConversationTurns));

        _savedSettings = _configStore.Load();
        ApplySettingsToForm(_savedSettings);

        _toolset.AuthorizedScansChanged += (_, _) => DispatchToUi(RefreshContextSummary);
        _toolset.Inbox.InboxChanged += (_, _) => DispatchToUi(SyncProposalInvalidations);
    }

    /// <summary>
    /// 接收来自磁盘扫描模块的新快照并同步脱敏上下文。
    /// </summary>
    public void SyncScanSnapshot(ScanSnapshot? snapshot)
    {
        _latestKnownSnapshot = snapshot;
        if (snapshot is null)
        {
            _toolset.RevokeSnapshot();
            RefreshContextSummary();
            return;
        }

        if (IsSnapshotSharingEnabled)
        {
            _toolset.AuthorizeSnapshot(snapshot, replacePreviousSnapshots: true);
        }
        else
        {
            RefreshContextSummary();
        }
    }

    [RelayCommand]
    private void ToggleSnapshotSharing()
    {
        if (IsSnapshotSharingEnabled)
        {
            IsSnapshotSharingEnabled = false;
            _toolset.RevokeSnapshot();
            ConfigFeedbackMessage = "已撤销当前会话的快照共享，旧 AI 建议已全部失效。";
        }
        else
        {
            IsSnapshotSharingEnabled = true;
            if (_latestKnownSnapshot is not null)
            {
                _toolset.AuthorizeSnapshot(_latestKnownSnapshot, replacePreviousSnapshots: true);
                ConfigFeedbackMessage = "已重新授权当前脱敏扫描快照供 AI 只读分析。";
            }
        }
        RefreshContextSummary();
    }

    [RelayCommand]
    private void SelectPreset(AiPresetChipItem? chip)
    {
        if (chip is null) return;
        SelectedPreset = chip.Preset;
        foreach (AiPresetChipItem item in PresetChips)
        {
            item.IsSelected = item.Preset == chip.Preset;
        }

        if (chip.Preset != AiProviderPreset.Custom)
        {
            Endpoint = chip.Definition.DefaultEndpoint;
            ModelName = chip.Definition.DefaultModel;
        }
        ConfigFeedbackMessage = chip.HintText;
    }

    [RelayCommand]
    private void SelectResponseLanguage(string? languageKey)
    {
        ResponseLanguage = string.Equals(languageKey, "English", StringComparison.OrdinalIgnoreCase)
            ? AiResponseLanguage.English
            : AiResponseLanguage.Chinese;
    }

    [RelayCommand]
    private void ClearSavedApiKey()
    {
        _savedSettings = _configStore.Save(_savedSettings, plainApiKey: string.Empty);
        MaskedApiKeyPreview = string.Empty;
        ClearPendingApiKeyBoxAction?.Invoke();
        ConfigFeedbackMessage = "已清除本地 DPAPI 加密保存的 API Key。";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTestingConnection) return;

        string? validationError = ValidateDraftConfig(out AiProviderSettings draft, out string? plainKey);
        if (validationError is not null)
        {
            SetConnectionBadge("配置不完整", "#FEF2F2", "#FECACA", "#C42B1C");
            ConfigFeedbackMessage = validationError;
            return;
        }

        if (!EnsureCloudPrivacyAcknowledged(draft, out bool acknowledgedNow))
        {
            return;
        }
        if (acknowledgedNow)
        {
            draft = draft with { CloudPrivacyAcknowledged = true };
        }

        IsTestingConnection = true;
        SetConnectionBadge("正在测试连接…", "#EBF5FF", "#BFDBFE", "#0078D4");
        ConfigFeedbackMessage = $"正在探活 {draft.Endpoint} ({draft.ModelName})…";

        _connectionTestCts?.Cancel();
        _connectionTestCts?.Dispose();
        _connectionTestCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        try
        {
            using var client = new OpenAiCompatibleChatClient(draft.Endpoint, draft.ModelName, plainKey, TimeSpan.FromSeconds(25));
            AiConnectionTestResult result = await client.TestConnectionAsync(_connectionTestCts.Token);

            if (result.Success)
            {
                if (result.SupportsToolCalling)
                {
                    SetConnectionBadge(result.StatusBadgeText, "#ECFDF5", "#A7F3D0", "#0F7B4B");
                }
                else
                {
                    SetConnectionBadge(result.StatusBadgeText, "#FFFBEB", "#FDE68A", "#B45309");
                }
            }
            else
            {
                SetConnectionBadge(result.StatusBadgeText, "#FEF2F2", "#FECACA", "#C42B1C");
            }

            ConfigFeedbackMessage = result.DetailMessage;
            _savedSettings = _savedSettings with
            {
                CloudPrivacyAcknowledged = draft.CloudPrivacyAcknowledged,
                LastConnectionTestedUtc = DateTimeOffset.UtcNow,
                LastConnectionSucceeded = result.Success,
                LastConnectionSupportsToolCalling = result.SupportsToolCalling,
                LastConnectionSummary = result.StatusBadgeText
            };
        }
        catch (Exception ex)
        {
            SetConnectionBadge("连接失败", "#FEF2F2", "#FECACA", "#C42B1C");
            ConfigFeedbackMessage = AiPathSanitizer.RedactSecret(ex.Message, plainKey);
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        string? validationError = ValidateDraftConfig(out AiProviderSettings draft, out _);
        if (validationError is not null)
        {
            ConfigFeedbackMessage = validationError;
            return;
        }

        if (!EnsureCloudPrivacyAcknowledged(draft, out bool acknowledgedNow))
        {
            return;
        }
        if (acknowledgedNow)
        {
            draft = draft with { CloudPrivacyAcknowledged = true };
        }

        string? enteredKey = PendingApiKeyAccessor?.Invoke();
        string? keyToSave = string.IsNullOrWhiteSpace(enteredKey) ? null : enteredKey.Trim();

        _savedSettings = _configStore.Save(draft, keyToSave);
        ApplySettingsToForm(_savedSettings);
        ClearPendingApiKeyBoxAction?.Invoke();

        ConfigFeedbackMessage = _savedSettings.IsLocalEndpoint
            ? "已保存本地模型配置（零外发端点）。"
            : "已保存配置，API Key 已通过 Windows DPAPI（CurrentUser）加密存储。";
    }

    [RelayCommand]
    private Task RunQuickPromptAsync(string? quickType)
    {
        string prompt = quickType switch
        {
            "LargeDirs" => "请优先查看当前扫描磁盘的顶层大目录与头部候选（按需逐层钻入大目录，不要拉全量），按空间收益给出优先迁移建议（严禁建议 L0 系统目录），并提交可审阅的建议卡片。",
            "UnknownDirs" => "请聚焦当前磁盘头部未知大占用目录（可按需层级钻入定位真正的大子目录），使用只读元数据画像分析它们的后缀与修改时间分布，判断哪些适合迁出、哪些疑似旧缓存残留。",
            "MigrateVsClean" => "请聚焦头部大目录，帮我按「适合安全迁移（L1/L3）」与「疑似缓存残留/需人工核实」分类梳理当前磁盘空间大户，说明迁移与保留备份策略，并给出结构化建议卡片。",
            _ => "请分析当前扫描磁盘的主要空间占用并给出安全迁移建议。"
        };
        UserPromptInput = prompt;
        return SendPromptAsync();
    }

    [RelayCommand]
    private async Task SendPromptAsync()
    {
        if (IsAnalyzing) return;

        string prompt = UserPromptInput.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        string? validationError = ValidateDraftConfig(out AiProviderSettings draft, out string? plainKey);
        if (validationError is not null)
        {
            CustomMessageBox.ShowWarning($"请先完成 AI 模型配置：\n{validationError}", "模型未配置");
            _openAiSettingsCallback?.Invoke();
            return;
        }

        if (!EnsureCloudPrivacyAcknowledged(draft, out bool acknowledgedNow))
        {
            return;
        }
        if (acknowledgedNow)
        {
            draft = draft with { CloudPrivacyAcknowledged = true };
            _savedSettings = _configStore.Save(draft, plainApiKey: null);
        }

        UserPromptInput = string.Empty;
        IsAnalyzing = true;

        var history = ConversationTurns
            .SelectMany(t => new[]
            {
                new AiConversationTurn(true, t.UserPrompt),
                new AiConversationTurn(false, t.AssistantText)
            })
            .Where(h => !string.IsNullOrWhiteSpace(h.Content))
            .ToList();

        var turnItem = new AiConversationTurnItem
        {
            UserPrompt = prompt,
            MaxSteps = draft.EffectiveMaxToolSteps,
            StatusBadgeText = "正在分析…",
            StatusBadgeFg = "#0078D4"
        };
        ConversationTurns.Add(turnItem);

        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();

        var progress = new InlineProgress<AiAgentRunEvent>(ev => HandleAgentRunEvent(turnItem, ev));

        try
        {
            AiAgentAnalysisResult result = await _agentService.RunAnalysisAsync(
                draft,
                plainKey,
                new AiAgentAnalysisRequest(ActiveScanId, prompt, history),
                progress,
                _analysisCts.Token);

            DispatchToUi(() =>
            {
                FinalizeActiveTimelineSteps(turnItem, result.WasCancelled ? "已停止" : "已中断");
                turnItem.StepsUsed = result.StepsUsed;
                if (!string.IsNullOrWhiteSpace(result.AssistantMessage))
                {
                    turnItem.AssistantText = result.AssistantMessage;
                    SyncFinalAssistantMessageToTimeline(turnItem, result.AssistantMessage);
                }

                if (result.WasCancelled)
                {
                    turnItem.StatusBadgeText = "已手动停止";
                    turnItem.StatusBadgeFg = "#B45309";
                    turnItem.ErrorText = result.ErrorMessage ?? "分析已停止。";
                }
                else if (!result.Succeeded)
                {
                    turnItem.StatusBadgeText = "分析失败";
                    turnItem.StatusBadgeFg = "#C42B1C";
                    turnItem.ErrorText = result.ErrorMessage ?? "模型调用失败。";
                }
                else
                {
                    turnItem.StatusBadgeText = result.HitStepLimit
                        ? $"已达上限 · {result.ElapsedMilliseconds / 1000.0:F1}s"
                        : $"已完成 · {result.ElapsedMilliseconds / 1000.0:F1}s";
                    turnItem.StatusBadgeFg = result.HitStepLimit ? "#B45309" : "#0F7B4B";
                }
            });
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void StopAnalysis()
    {
        _analysisCts?.Cancel();
    }

    [RelayCommand]
    private void ClearConversation()
    {
        if (IsAnalyzing) return;
        ConversationTurns.Clear();
    }

    [RelayCommand]
    private void AdoptProposalCard(AiProposalCardItem? card)
    {
        if (card is null || !card.CanAdopt) return;

        if (_adoptToPlanCallback is null)
        {
            CustomMessageBox.ShowInformation("当前未连接到磁盘扫描迁移清单，请先在「磁盘扫描」页完成扫描后再加入清单。", "提示");
            return;
        }

        var (added, message) = _adoptToPlanCallback(card.ScanId, card.NodeId, card.SuggestedTargetPath);
        if (added)
        {
            card.IsAdopted = true;
            _toolset.Inbox.UpdateStatus(card.ProposalId, AiProposalStatus.AdoptedToPlan,
                $"用户已将「{card.DisplayName}」加入待迁移清单（未执行迁移，需在清单页预检并手动点击开始）。");
            ConfigFeedbackMessage = message;
        }
        else
        {
            CustomMessageBox.ShowWarning(message, "无法加入待迁移清单");
        }
    }

    [RelayCommand]
    private void LocateProposalInTree(AiProposalCardItem? card)
    {
        if (card is null) return;
        if (_locateNodeInTreeCallback is null || !_locateNodeInTreeCallback(card.ScanId, card.NodeId))
        {
            CustomMessageBox.ShowInformation("未能在当前目录树中定位该节点，可能扫描快照已切换或已释放。", "定位提示");
        }
    }

    [RelayCommand]
    private void OpenDiskScan() => _openDiskScanCallback?.Invoke();

    [RelayCommand]
    private void OpenAiSettings() => _openAiSettingsCallback?.Invoke();

    [RelayCommand]
    private void CloseDrawer() => _closeDrawerCallback?.Invoke();

    [RelayCommand]
    private void InspectToolResult(AiToolCallStepItem? step)
    {
        if (step is null || !step.HasResultPayload) return;

        var dialog = new MoveWithSymlinkWPF.Views.AiToolPayloadWindow(step);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
            dialog.Icon = owner.Icon;
        }
        dialog.ShowDialog();
    }

    private void HandleAgentRunEvent(AiConversationTurnItem turn, AiAgentRunEvent ev)
    {
        DispatchToUi(() =>
        {
            if (ev.CurrentStep > 0)
            {
                turn.StepsUsed = ev.CurrentStep;
            }

            switch (ev.Kind)
            {
                case AiAgentEventKind.ThinkingDelta when !string.IsNullOrEmpty(ev.TextDelta):
                    if (turn.TimelineItems.Count > 0
                        && turn.TimelineItems[^1] is AiThinkingStepItem activeThinking
                        && activeThinking.IsStreaming)
                    {
                        activeThinking.Text += ev.TextDelta;
                    }
                    else
                    {
                        CloseActiveStreamingSteps(turn);
                        // 紧接在工具调用或文字发言之后的新一轮思考，自动创建新时间线节点，保持严格顺序化展示
                        turn.TimelineItems.Add(new AiThinkingStepItem
                        {
                            Text = ev.TextDelta,
                            IsStreaming = true
                        });
                        if (!string.IsNullOrEmpty(turn.ThinkingText))
                        {
                            turn.ThinkingText += "\n\n";
                        }
                    }

                    turn.ThinkingText += ev.TextDelta;
                    break;

                case AiAgentEventKind.AssistantTextDelta when !string.IsNullOrEmpty(ev.TextDelta):
                    if (turn.TimelineItems.Count > 0
                        && turn.TimelineItems[^1] is AiAssistantMessageStepItem activeMessage
                        && activeMessage.IsStreaming)
                    {
                        activeMessage.Text += ev.TextDelta;
                    }
                    else if (!string.IsNullOrWhiteSpace(ev.TextDelta))
                    {
                        CloseActiveStreamingSteps(turn);
                        // 将工具调用前的过程说明或最终回复按真实发生顺序放入统一时间线，彻底避免与工具调用错位
                        turn.TimelineItems.Add(new AiAssistantMessageStepItem
                        {
                            Text = ev.TextDelta.TrimStart('\r', '\n'),
                            IsStreaming = true
                        });
                    }

                    turn.AssistantText += ev.TextDelta;
                    break;

                case AiAgentEventKind.ToolCallStarted when ev.TraceEntry is not null:
                    CloseActiveStreamingSteps(turn);
                    turn.HasToolActivity = true;
                    turn.StatusBadgeText = ev.MaxSteps > 0
                        ? $"调用中 · {ev.CurrentStep}/{ev.MaxSteps}"
                        : $"调用中 · 第 {ev.CurrentStep} 步";
                    turn.StatusBadgeFg = "#0078D4";
                    AiToolBatchStepItem startBatch = GetOrCreateTrailingToolBatch(turn);
                    startBatch.AddItem(new AiToolCallStepItem
                    {
                        StepNumber = ev.TraceEntry.StepNumber,
                        ToolName = ev.TraceEntry.ToolName,
                        FriendlyLabel = AiToolCallStepItem.ResolveFriendlyLabel(ev.TraceEntry.ToolName),
                        ArgumentsSummary = ev.TraceEntry.ArgumentsSummary ?? string.Empty,
                        ResultSummary = "执行中…",
                        IsRunning = true,
                        Succeeded = true,
                        IsExpanded = false
                    });
                    break;

                case AiAgentEventKind.ToolCallCompleted when ev.TraceEntry is not null:
                    CloseActiveStreamingSteps(turn);
                    turn.HasToolActivity = true;
                    string argDisplay = string.IsNullOrWhiteSpace(ev.TraceEntry.ArgumentsSummary)
                        ? string.Empty
                        : ev.TraceEntry.ArgumentsSummary;
                    turn.Traces.Add(new AiTraceRowItem
                    {
                        Prefix = ev.TraceEntry.Succeeded ? "✓" : "✕",
                        Text = $"{ev.TraceEntry.ToolName}({argDisplay}) → {ev.TraceEntry.ResultSummary}",
                        Foreground = ev.TraceEntry.Succeeded ? "#64748B" : "#C42B1C"
                    });

                    (AiToolBatchStepItem? ownerBatch, AiToolCallStepItem? runningStep) =
                        FindMatchingRunningToolStep(turn, ev.TraceEntry.StepNumber, ev.TraceEntry.ToolName);
                    if (runningStep is not null)
                    {
                        runningStep.IsRunning = false;
                        runningStep.Succeeded = ev.TraceEntry.Succeeded;
                        runningStep.ResultSummary = ev.TraceEntry.ResultSummary;
                        runningStep.ResultPayload = ev.TraceEntry.ResultPayload ?? string.Empty;
                        runningStep.ElapsedMilliseconds = ev.TraceEntry.ElapsedMilliseconds;
                        ownerBatch?.RefreshSummary();
                    }
                    else
                    {
                        AiToolBatchStepItem batch = GetOrCreateTrailingToolBatch(turn);
                        batch.AddItem(new AiToolCallStepItem
                        {
                            StepNumber = ev.TraceEntry.StepNumber,
                            ToolName = ev.TraceEntry.ToolName,
                            FriendlyLabel = AiToolCallStepItem.ResolveFriendlyLabel(ev.TraceEntry.ToolName),
                            ArgumentsSummary = argDisplay,
                            ResultSummary = ev.TraceEntry.ResultSummary,
                            ResultPayload = ev.TraceEntry.ResultPayload ?? string.Empty,
                            ElapsedMilliseconds = ev.TraceEntry.ElapsedMilliseconds,
                            IsRunning = false,
                            Succeeded = ev.TraceEntry.Succeeded,
                            IsExpanded = false
                        });
                    }
                    break;

                case AiAgentEventKind.ProposalRecorded when ev.ProposalReceipt is not null:
                    AppendProposalCards(turn, ev.ProposalReceipt);
                    break;

                case AiAgentEventKind.Warning when !string.IsNullOrWhiteSpace(ev.Message):
                    // 复读死循环触发自动纠偏恢复时，清理当前轮次已污染的重复半截内容并展示清晰轨迹提示
                    if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiThinkingStepItem pollutedThinking)
                    {
                        pollutedThinking.Text = AiRepetitionLoopDetector.TrimTrailingRepetition(pollutedThinking.Text);
                        pollutedThinking.IsStreaming = false;
                    }
                    else if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiAssistantMessageStepItem pollutedMsg && pollutedMsg.IsStreaming)
                    {
                        turn.TimelineItems.RemoveAt(turn.TimelineItems.Count - 1);
                    }
                    turn.HasToolActivity = true;
                    turn.StatusBadgeText = "检测到复读 · 重试中…";
                    turn.StatusBadgeFg = "#B45309";
                    turn.AssistantText = string.Empty;
                    turn.ThinkingText = AiRepetitionLoopDetector.TrimTrailingRepetition(turn.ThinkingText);
                    turn.Traces.Add(new AiTraceRowItem
                    {
                        Prefix = "⚠",
                        Text = ev.Message,
                        Foreground = "#B45309"
                    });
                    turn.TimelineItems.Add(new AiNoticeStepItem
                    {
                        Prefix = "⚠",
                        Message = ev.Message,
                        Foreground = "#B45309"
                    });
                    break;
            }
        });
    }

    private static void CloseActiveStreamingSteps(AiConversationTurnItem turn)
    {
        if (turn.TimelineItems.Count == 0) return;

        if (turn.TimelineItems[^1] is AiThinkingStepItem thinking && thinking.IsStreaming)
        {
            thinking.IsStreaming = false;
        }
        else if (turn.TimelineItems[^1] is AiAssistantMessageStepItem msg && msg.IsStreaming)
        {
            msg.IsStreaming = false;
        }
    }

    private static void SyncFinalAssistantMessageToTimeline(AiConversationTurnItem turn, string cleanedFinalMessage)
    {
        if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiAssistantMessageStepItem lastMsg)
        {
            lastMsg.Text = cleanedFinalMessage;
            lastMsg.IsStreaming = false;
            return;
        }

        turn.TimelineItems.Add(new AiAssistantMessageStepItem
        {
            Text = cleanedFinalMessage,
            IsStreaming = false
        });
    }

    private static AiToolBatchStepItem GetOrCreateTrailingToolBatch(AiConversationTurnItem turn)
    {
        if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiToolBatchStepItem existing)
        {
            return existing;
        }

        var batch = new AiToolBatchStepItem();
        turn.TimelineItems.Add(batch);
        return batch;
    }

    private static (AiToolBatchStepItem? Batch, AiToolCallStepItem? Step) FindMatchingRunningToolStep(
        AiConversationTurnItem turn, int stepNumber, string toolName)
    {
        for (int i = turn.TimelineItems.Count - 1; i >= 0; i--)
        {
            if (turn.TimelineItems[i] is AiToolBatchStepItem batch)
            {
                for (int j = batch.Items.Count - 1; j >= 0; j--)
                {
                    AiToolCallStepItem step = batch.Items[j];
                    if (step.IsRunning
                        && (step.StepNumber == stepNumber || string.Equals(step.ToolName, toolName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return (batch, step);
                    }
                }
            }
        }
        return (null, null);
    }

    private static void FinalizeActiveTimelineSteps(AiConversationTurnItem turn, string interruptedReason)
    {
        foreach (AiTimelineStepItem item in turn.TimelineItems)
        {
            if (item is AiThinkingStepItem thinking && thinking.IsStreaming)
            {
                thinking.IsStreaming = false;
            }
            else if (item is AiAssistantMessageStepItem msg && msg.IsStreaming)
            {
                msg.IsStreaming = false;
            }
            else if (item is AiToolBatchStepItem batch)
            {
                bool changed = false;
                foreach (AiToolCallStepItem tool in batch.Items)
                {
                    if (tool.IsRunning)
                    {
                        tool.IsRunning = false;
                        tool.Succeeded = false;
                        tool.ResultSummary = interruptedReason;
                        changed = true;
                    }
                }
                if (changed)
                {
                    batch.RefreshSummary();
                }
            }
        }
    }

    /// <summary>
    /// 双向同步当前待迁移清单状态到所有 AI 建议卡片（当用户在目录树勾选/取消勾选或在清单页删减时立即联动）。
    /// </summary>
    public void SyncAdoptedPlanItems(Guid? planScanId, IReadOnlyCollection<int> planNodeIds, IReadOnlyCollection<string> planSourcePaths)
    {
        DispatchToUi(() =>
        {
            _lastSyncedPlanScanId = planScanId;
            _currentPlanNodeIds = planNodeIds.ToHashSet();
            _currentPlanSourcePaths = new HashSet<string>(planSourcePaths, StringComparer.OrdinalIgnoreCase);

            foreach (AiConversationTurnItem turn in ConversationTurns)
            {
                foreach (AiProposalCardItem card in turn.ProposalCards)
                {
                    if (!card.IsAdoptableToMigrationPlan || card.IsInvalidated)
                    {
                        continue;
                    }

                    card.IsAdopted = IsCardCurrentlyInPlan(card.ScanId, card.NodeId, card.ActualLocalPath);
                }
            }
        });
    }

    private bool IsCardCurrentlyInPlan(Guid cardScanId, int cardNodeId, string actualLocalPath)
    {
        if (!string.IsNullOrWhiteSpace(actualLocalPath) && _currentPlanSourcePaths.Contains(actualLocalPath))
        {
            return true;
        }
        return _lastSyncedPlanScanId == cardScanId && _currentPlanNodeIds.Contains(cardNodeId);
    }

    private void AppendProposalCards(AiConversationTurnItem turn, AiMigrationProposalReceipt receipt)
    {
        foreach (AiMigrationProposalItemRecord item in receipt.Items)
        {
            if (item.Verdict is not (AiProposalItemVerdict.Accepted or AiProposalItemVerdict.RejectedByPolicy))
            {
                continue;
            }

            bool isAdoptable = item.IsAdoptableToMigrationPlan;
            bool isAlreadyInPlan = isAdoptable && IsCardCurrentlyInPlan(receipt.ScanId, item.NodeId, item.ActualLocalPath);

            turn.ProposalCards.Add(new AiProposalCardItem
            {
                ProposalId = receipt.ProposalId,
                ScanId = receipt.ScanId,
                NodeId = item.NodeId,
                DisplayName = item.DisplayName,
                SanitizedPath = item.SanitizedPath,
                ActualLocalPath = item.ActualLocalPath,
                SizeText = FileStatsService.FormatBytes(Math.Max(item.UniqueAllocatedBytes, item.LogicalBytes)),
                Reason = item.Verdict == AiProposalItemVerdict.RejectedByPolicy
                    ? item.VerdictMessage
                    : item.Reason,
                VerdictMessage = item.VerdictMessage,
                SuggestedTargetPath = item.SuggestedTargetPath,
                AdmissionLevel = item.AdmissionLevel,
                Verdict = item.Verdict,
                IsAdoptableToMigrationPlan = isAdoptable,
                IsAdopted = isAlreadyInPlan,
                IsSuspectedCacheAdvisory = item.SuggestedCategory.Equals("SuspectedCacheAdvisory", StringComparison.OrdinalIgnoreCase),
                IsNativeGuideAdvisory = item.SuggestedCategory.Equals("NativeGuideAdvisory", StringComparison.OrdinalIgnoreCase)
            });
        }
    }

    private void RefreshContextSummary()
    {
        ContextHotspots.Clear();
        ScanSnapshot? snapshot = _toolset.GetLatestAuthorizedSnapshot();
        if (snapshot is null)
        {
            ActiveScanId = null;
            SnapshotContextTitle = IsSnapshotSharingEnabled
                ? "当前尚未授权磁盘扫描快照"
                : "已暂停向内置 AI 共享扫描快照";
            SnapshotContextMeta = IsSnapshotSharingEnabled
                ? "请先在「磁盘扫描」页完成任意磁盘扫描，再回到此处或直接展开右侧 AI 抽屉进行分析。"
                : "你可以随时点击右侧按钮重新授权当前快照。";
            return;
        }

        ActiveScanId = snapshot.ScanId;
        string drive = snapshot.Volume.DriveRoot.TrimEnd('\\');
        string shortId = snapshot.ScanId.ToString("N")[..6];
        SnapshotContextTitle = $"当前分析上下文 · {drive} 盘扫描快照 #scan-{shortId}（已脱敏）";
        SnapshotContextMeta = $"共 {FileStatsService.FormatBytes(snapshot.Volume.UsedBytes)} · 仅共享目录结构元数据";

        AiMigrationCandidatesResponse candidates = _toolset.ListMigrationCandidates(snapshot.ScanId, pageSize: 5);
        foreach (AiMigrationCandidateDto c in candidates.Candidates.Take(4))
        {
            (string badge, string bg, string border, string fg) = c.AdmissionLevel switch
            {
                MigrationAdmissionLevel.VerifiedEligible => ("L1 推荐迁移", "#ECFDF5", "#A7F3D0", "#0F7B4B"),
                MigrationAdmissionLevel.VendorNativeRecommended => ("L2 原生引导", "#EBF5FF", "#BFDBFE", "#0078D4"),
                _ => ("未知大占用 · 可让 AI 诊断", "#FFFBEB", "#FDE68A", "#B45309")
            };

            ContextHotspots.Add(new AiContextHotspotRow
            {
                NodeId = c.NodeId,
                DisplayName = c.DisplayName,
                SanitizedPath = c.SanitizedPath,
                SizeText = FileStatsService.FormatBytes(Math.Max(c.UniqueAllocatedBytes, c.LogicalBytes)),
                Level = c.AdmissionLevel,
                BadgeText = badge,
                BadgeBg = bg,
                BadgeBorder = border,
                BadgeFg = fg
            });
        }
    }

    private void SyncProposalInvalidations()
    {
        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiProposalCardItem card in turn.ProposalCards)
            {
                AiProposalStatusResponse? status = _toolset.Inbox.GetStatus(card.ProposalId);
                if (status is not null && status.Status == AiProposalStatus.Invalidated)
                {
                    card.IsInvalidated = true;
                }
            }
        }
    }

    private void ApplySettingsToForm(AiProviderSettings settings)
    {
        SelectedPreset = settings.Preset;
        foreach (AiPresetChipItem item in PresetChips)
        {
            item.IsSelected = item.Preset == settings.Preset;
        }

        Endpoint = settings.Endpoint;
        ModelName = settings.ModelName;
        MaxToolSteps = settings.EffectiveMaxToolSteps;
        TimeoutSeconds = settings.EffectiveTimeoutSeconds;
        ResponseLanguage = settings.ResponseLanguage;
        MaskedApiKeyPreview = settings.MaskedApiKeyPreview ?? string.Empty;

        if (settings.LastConnectionTestedUtc.HasValue)
        {
            if (settings.LastConnectionSucceeded && settings.LastConnectionSupportsToolCalling)
            {
                SetConnectionBadge(settings.LastConnectionSummary ?? "已测通 · 支持 Tool Calling", "#ECFDF5", "#A7F3D0", "#0F7B4B");
            }
            else if (settings.LastConnectionSucceeded)
            {
                SetConnectionBadge(settings.LastConnectionSummary ?? "已测通 · 不支持 Tool Calling", "#FFFBEB", "#FDE68A", "#B45309");
            }
            else
            {
                SetConnectionBadge("上次测试失败", "#FEF2F2", "#FECACA", "#C42B1C");
            }
        }
        else if (settings.IsLocalEndpoint)
        {
            SetConnectionBadge("本地零外发端点", "#ECFDF5", "#A7F3D0", "#0F7B4B");
        }
        else if (settings.HasSavedApiKey)
        {
            SetConnectionBadge("已配置密钥 · 待测试", "#EBF5FF", "#BFDBFE", "#0078D4");
        }
        else
        {
            SetConnectionBadge("未配置 API Key", "#F8FAFC", "#E2E8F0", "#64748B");
        }
    }

    private AiProviderSettings BuildDraftSettings() =>
        _savedSettings with
        {
            Preset = SelectedPreset,
            Endpoint = Endpoint.Trim(),
            ModelName = ModelName.Trim(),
            MaxToolSteps = MaxToolSteps <= 0 ? 0 : Math.Clamp(MaxToolSteps, 1, AiProviderSettings.HardMaxToolSteps),
            TimeoutSeconds = Math.Clamp(TimeoutSeconds, 15, AiProviderSettings.HardMaxTimeoutSeconds),
            ResponseLanguage = ResponseLanguage
        };

    private string? ValidateDraftConfig(out AiProviderSettings draft, out string? resolvedApiKey)
    {
        draft = BuildDraftSettings();
        resolvedApiKey = null;

        try
        {
            _ = OpenAiCompatibleChatClient.ResolveChatCompletionsUri(draft.Endpoint);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (string.IsNullOrWhiteSpace(draft.ModelName))
        {
            return "请填写模型名称（例如 deepseek-chat、qwen-plus 或 qwen2.5:14b）。";
        }

        string? pendingKey = PendingApiKeyAccessor?.Invoke();
        resolvedApiKey = !string.IsNullOrWhiteSpace(pendingKey)
            ? pendingKey.Trim()
            : _configStore.TryUnprotectApiKey(_savedSettings);

        if (!draft.IsLocalEndpoint && string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            return "当前为云端模型端点，请先输入 API Key（或切换为本地 Ollama / LM Studio 零外发端点）。";
        }

        return null;
    }

    private bool EnsureCloudPrivacyAcknowledged(AiProviderSettings draft, out bool acknowledgedNow)
    {
        acknowledgedNow = false;
        if (draft.IsLocalEndpoint || draft.CloudPrivacyAcknowledged || _savedSettings.CloudPrivacyAcknowledged)
        {
            return true;
        }

        bool confirmed = CustomMessageBox.ShowQuestion(
            "你即将启用非本机（云端）AI 模型端点。\n\n" +
            "工具仅会向你配置的模型服务商发送经脱敏的目录结构与占用元数据（本机用户名替换为 <USER>），绝不上传任何文件内容、注册表密钥或凭据。\n" +
            "AI 建议仅供参考，不代表工具官方安全背书，绝不会自动执行迁移或删除。\n\n" +
            "若希望 100% 数据不出本机，请点「否」并改用本地 Ollama / LM Studio。\n\n是否确认继续使用该云端端点？",
            "云端元数据外发与隐私告知");

        if (!confirmed)
        {
            ConfigFeedbackMessage = "已取消云端端点启用；如需 100% 离线分析，可选择「本地 Ollama」或「LM Studio」。";
            return false;
        }

        acknowledgedNow = true;
        return true;
    }

    private void SetConnectionBadge(string text, string bg, string border, string fg)
    {
        ConnectionBadgeText = text;
        ConnectionBadgeBg = bg;
        ConnectionBadgeBorder = border;
        ConnectionBadgeFg = fg;
    }

    private static void DispatchToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                try
                {
                    dispatcher.Invoke(action);
                }
                catch (TaskCanceledException)
                {
                    // 窗口关闭瞬间取消调度时安全忽略
                }
                return;
            }
        }

        action();
    }

    /// <summary>
    /// 严格保序的同步进度转发器，避免 Progress&lt;T&gt; 在后台线程池或异步 Post 队列中造成时间线事件乱序或收尾竞态。
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
