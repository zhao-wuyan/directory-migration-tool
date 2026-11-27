using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MigrationCore.Services.Cleanup;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

/// <summary>
/// 独立垃圾清理模块主视图模型（驱动画板 07 三档清理、画板 08 清理隔离区、画板 11 AI 疑似残留隔离确认与共享 AI 抽屉）。
/// </summary>
public partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupService _cleanupService;
    private readonly QuarantineStore _quarantineStore;
    private readonly object _scanSync = new();
    private Task<CleanupScanSnapshot>? _activeScanTask;
    private CancellationTokenSource? _scanCts;
    private readonly List<Action<(double Percent, string Message)>> _externalScanProgressListeners = [];
    private (double Percent, string Message)? _lastScanProgressBroadcast;
    private CancellationTokenSource? _execCts;
    private CancellationTokenSource? _overviewRefreshCts;
    private CancellationTokenSource? _quarantineRefreshCts;
    private long _overviewRefreshVersion;
    private long _quarantineRefreshVersion;
    private DateTimeOffset _lastOverviewRefreshedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastQuarantineFullInspectUtc = DateTimeOffset.MinValue;
    private bool _overviewDirty = true;
    private bool _quarantineDirty = true;
    private string _lastCrossDriveSignature = string.Empty;
    private string _lastIdlePreviewSignature = string.Empty;
    private string _lastDiskOverviewSignature = string.Empty;
    private AiAssistantViewModel? _aiAssistant;

    private static readonly TimeSpan OverviewRefreshTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuarantineInspectTtl = TimeSpan.FromSeconds(15);

    private readonly HashSet<string> _expandedGroupIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isBatchUpdatingSelection;
    private bool _isBatchUpdatingQuarantineSelection;

    public ObservableCollection<CleanupCategoryNavItem> CategoryNavItems { get; } = [];
    public ObservableCollection<CleanupRuleItemViewModel> AllRuleItems { get; } = [];
    public ObservableCollection<CleanupRuleItemViewModel> VisibleRuleItems { get; } = [];
    public ObservableCollection<object> VisibleEntries { get; } = [];
    public ObservableCollection<QuarantineBatchRowViewModel> QuarantineBatches { get; } = [];
    public ObservableCollection<AiResidualCandidateRowViewModel> AiResidualCandidates { get; } = [];
    public ObservableCollection<AiHardBlockedRowViewModel> AiHardBlockedCandidates { get; } = [];

    // ---- 右栏多盘空间概览（默认 Top 3 + >3 块弹框查看全部） ----
    public const int MaxInlineDiskOverviewCount = 3;
    public ObservableCollection<CleanupDiskOverviewItem> AllDiskOverviews { get; } = [];
    public ObservableCollection<CleanupDiskOverviewItem> TopDiskOverviews { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverflowDisks))]
    [NotifyPropertyChangedFor(nameof(ViewAllDisksButtonText))]
    private int _totalDiskCount;

    [ObservableProperty]
    private string _diskOverviewHeaderSummary = "本机磁盘空间";

    [ObservableProperty]
    private string _overflowDisksSummaryText = string.Empty;

    public bool HasOverflowDisks => TotalDiskCount > MaxInlineDiskOverviewCount;
    public string ViewAllDisksButtonText => $"查看全部 {TotalDiskCount} 块磁盘";

    // ---- 跨盘扫描源快速探测 & 待扫描覆盖范围预览 ----
    public ObservableCollection<CrossDriveDiscoveryItem> CrossDriveDiscoveries { get; } = [];
    public ObservableCollection<CleanupIdleScopePreviewItem> VisibleIdleScopePreviews { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrossDriveDiscoveries))]
    private int _crossDriveDiscoveryCount;

    [ObservableProperty]
    private string _crossDriveBadgeText = "多盘就绪";

    [ObservableProperty]
    private string _crossDriveSummaryText = "已支持跨盘识别：环境变量重定向、Steam/IDE 跨盘配置、全盘回收站及已登记符号链接迁移。";

    [ObservableProperty]
    private string _idleScopeHeaderText = "本次扫描覆盖范围（内置规则预览）";

    public bool HasCrossDriveDiscoveries => CrossDriveDiscoveryCount > 0;

    // ---- 扫描进行中实时发现大项与累计统计 ----
    private readonly List<CleanupLiveHitRowViewModel> _allLiveHits = [];
    private readonly Dictionary<CleanupCategory, long> _liveCategoryBytes = new();
    private readonly HashSet<string> _liveDiscoveredDrives = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<CleanupLiveHitRowViewModel> LiveScanHits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveDiscoveredSizeText))]
    private long _liveDiscoveredBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveScanHits))]
    [NotifyPropertyChangedFor(nameof(LiveHitSummaryText))]
    private int _liveHitRuleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveHitSummaryText))]
    private string _liveHitDrivesText = "等待命中";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveSkippedCountText))]
    private int _liveSkippedCount;

    public bool HasLiveScanHits => LiveHitRuleCount > 0;
    public string LiveDiscoveredSizeText => FileStatsService.FormatBytes(LiveDiscoveredBytes);
    public string LiveHitSummaryText => LiveHitRuleCount > 0
        ? (string.IsNullOrEmpty(LiveHitDrivesText) ? $"{LiveHitRuleCount} 项" : $"{LiveHitRuleCount} 项 · {LiveHitDrivesText}")
        : "0 项";
    public string LiveSkippedCountText => $"{LiveSkippedCount:N0} 项";

    // ---- 扫描完成后多盘快捷筛选胶囊 ----
    public ObservableCollection<CleanupDriveFilterChip> DriveFilterChips { get; } = [];

    [ObservableProperty]
    private string? _selectedDriveFilter;

    [ObservableProperty]
    private bool _hasMultipleDrivesInResults;

    // ---- 子视图切换（07 / 08 / 11） ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesMainView))]
    [NotifyPropertyChangedFor(nameof(IsQuarantineView))]
    [NotifyPropertyChangedFor(nameof(IsAiResidualView))]
    private CleanupSubView _activeSubView = CleanupSubView.RulesMain;

    public bool IsRulesMainView => ActiveSubView == CleanupSubView.RulesMain;
    public bool IsQuarantineView => ActiveSubView == CleanupSubView.QuarantineManager;
    public bool IsAiResidualView => ActiveSubView == CleanupSubView.AiSuspectedResidual;

    // ---- 共享内置 AI 抽屉 ----
    public AiAssistantViewModel? AiAssistant
    {
        get => _aiAssistant;
        set
        {
            if (_aiAssistant == value) return;
            if (_aiAssistant is not null)
            {
                _aiAssistant.Toolset.Inbox.CleanupProposalsChanged -= OnAiCleanupProposalsChanged;
            }
            _aiAssistant = value;
            if (_aiAssistant is not null)
            {
                _aiAssistant.Toolset.Inbox.CleanupProposalsChanged += OnAiCleanupProposalsChanged;
                SyncAiResidualCandidatesFromInbox();
                if (CurrentSnapshot is not null)
                {
                    _aiAssistant.SyncCleanupScanSnapshot(CurrentSnapshot);
                }
            }
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool _isAiDrawerOpen;

    // ---- 扫描与执行状态 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanSnapshot))]
    [NotifyPropertyChangedFor(nameof(ShowIdleScanPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowRuleList))]
    [NotifyPropertyChangedFor(nameof(ScanActionButtonText))]
    private CleanupScanSnapshot? _currentSnapshot;

    public bool HasScanSnapshot => CurrentSnapshot is not null;
    public bool ShowIdleScanPrompt => CurrentSnapshot is null && !IsScanning;
    public bool ShowRuleList => CurrentSnapshot is not null && !IsScanning;
    public string ScanActionButtonText => IsScanning ? "扫描中…" : (HasScanSnapshot ? "重新扫描" : "开始扫描");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanExecuteSelected))]
    [NotifyPropertyChangedFor(nameof(CanBatchDeleteQuarantine))]
    [NotifyPropertyChangedFor(nameof(ShowIdleScanPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowRuleList))]
    [NotifyPropertyChangedFor(nameof(ScanActionButtonText))]
    private bool _isScanning;

    [ObservableProperty]
    private string _mftCacheHintText = "点击「开始扫描」只读评估可清理空间（如已在磁盘扫描页用 MFT 扫过系统盘，将优先秒级复用新鲜 MFT 内存缓存）。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanExecuteSelected))]
    [NotifyPropertyChangedFor(nameof(CanConfirmAiQuarantine))]
    [NotifyPropertyChangedFor(nameof(CanBatchDeleteQuarantine))]
    [NotifyPropertyChangedFor(nameof(IsNotExecuting))]
    [NotifyPropertyChangedFor(nameof(CanSelectRecommendedInScope))]
    private bool _isExecuting;

    partial void OnIsExecutingChanged(bool value)
    {
        foreach (CleanupRuleItemViewModel item in AllRuleItems)
        {
            item.IsSelectionLocked = value;
        }
        foreach (CleanupRuleGroupViewModel grp in VisibleEntries.OfType<CleanupRuleGroupViewModel>())
        {
            grp.IsSelectionLocked = value;
        }
    }

    public bool IsNotExecuting => !IsExecuting;
    public bool IsBusy => IsScanning || IsExecuting || OperationExecutionGate.Shared.IsBusy;

    // ---- 执行中实时进度状态（彻底消除执行假死与无反馈感） ----
    [ObservableProperty]
    private double _executionProgressPercent;

    [ObservableProperty]
    private bool _executionProgressIndeterminate;

    [ObservableProperty]
    private string _executionProgressHeader = "正在执行所选清理…";

    [ObservableProperty]
    private string _executionProgressBadgeText = "0%";

    [ObservableProperty]
    private string _executionProgressMessage = "正在准备执行清理…";

    // ---- 方案 A：页内「清理完成回执横幅」（不打断连续清理，支持一键看详情/进隔离区） ----
    [ObservableProperty]
    private bool _hasExecutionReceipt;

    [ObservableProperty]
    private string _executionReceiptTitle = string.Empty;

    [ObservableProperty]
    private string _executionReceiptMessage = string.Empty;

    [ObservableProperty]
    private string _executionReceiptDetailText = string.Empty;

    [ObservableProperty]
    private bool _executionReceiptHasQuarantined;

    [ObservableProperty]
    private string _executionReceiptBg = "#ECFDF5";

    [ObservableProperty]
    private string _executionReceiptBorder = "#A7F3D0";

    [ObservableProperty]
    private string _executionReceiptFg = "#0F7B4B";

    [ObservableProperty]
    private bool _executionReceiptIsWarning;

    private CleanupExecutionResult? _lastExecutionResult;

    [ObservableProperty]
    private string _statusMessage = "点击「重新扫描」只读评估可清理空间；所有项目默认不勾选。";

    // ---- 真实确定性扫描进度（替代原 IsIndeterminate 假满条） ----
    [ObservableProperty]
    private double _scanProgressPercent;

    [ObservableProperty]
    private string _scanProgressBadgeText = "0%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandCollapseAllGroupsText))]
    private bool _hasVisibleGroups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandCollapseAllGroupsText))]
    private bool _areAllGroupsExpanded;

    public string ExpandCollapseAllGroupsText => AreAllGroupsExpanded ? "收起全部分组" : "展开全部分组";

    // ---- 分类筛选与谨慎项折叠开关（默认隐藏谨慎项！） ----
    [ObservableProperty]
    private CleanupCategory? _selectedCategoryFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectRecommendedButtonText))]
    [NotifyPropertyChangedFor(nameof(SelectRecommendedButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(CanSelectRecommendedInScope))]
    private bool _hasSelectableRecommendedInScope;

    public bool CanSelectRecommendedInScope => HasSelectableRecommendedInScope && IsNotExecuting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectRecommendedButtonText))]
    [NotifyPropertyChangedFor(nameof(SelectRecommendedButtonToolTip))]
    private bool _isCurrentScopeRecommendedSelected;

    public string SelectRecommendedButtonText => SelectedCategoryFilter is null
        ? (IsCurrentScopeRecommendedSelected ? "取消建议清理" : "仅勾选建议清理")
        : (IsCurrentScopeRecommendedSelected ? "取消本类建议" : "仅勾选本类建议");

    public string SelectRecommendedButtonToolTip
    {
        get
        {
            string? categoryTitle = SelectedCategoryFilter is CleanupCategory filter
                ? CategoryNavItems.FirstOrDefault(c => c.Category == filter)?.Title
                : null;

            if (!HasSelectableRecommendedInScope)
            {
                return categoryTitle is null
                    ? "当前暂无可勾选的「建议清理」项"
                    : $"当前分类「{categoryTitle}」暂无可勾选的「建议清理」项";
            }

            if (categoryTitle is null)
            {
                return IsCurrentScopeRecommendedSelected
                    ? "当前已勾选全部「建议清理」项，点击取消勾选"
                    : "勾选全部分类中可清理的「建议清理」项（再次点击可取消勾选）";
            }

            return IsCurrentScopeRecommendedSelected
                ? $"当前已勾选「{categoryTitle}」中的「建议清理」项，点击取消勾选"
                : $"仅勾选当前分类「{categoryTitle}」中的「建议清理」项（再次点击可取消勾选）";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CautionToggleText))]
    [NotifyPropertyChangedFor(nameof(CautionToggleBg))]
    [NotifyPropertyChangedFor(nameof(CautionToggleBorder))]
    [NotifyPropertyChangedFor(nameof(CautionToggleFg))]
    private bool _showCautionItems;

    public string CautionToggleText => ShowCautionItems ? "已显示谨慎项" : "显示谨慎项";
    public string CautionToggleBg => ShowCautionItems ? "#FFFBEB" : "#F8FAFC";
    public string CautionToggleBorder => ShowCautionItems ? "#FDE68A" : "#CBD5E1";
    public string CautionToggleFg => ShowCautionItems ? "#B45309" : "#64748B";

    // ---- 未安装或无缓存规则折叠控制（默认隐藏 0 B 空项，按需一键展开查看全部支持软件） ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyRulesToggleText))]
    private bool _showEmptyRules;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyRulesInScope))]
    [NotifyPropertyChangedFor(nameof(EmptyRulesToggleText))]
    private int _emptyRuleCountInScope;

    private enum NoVisibleRulesActionKind
    {
        None = 0,
        ShowCautionItems = 1,
        SwitchToAllCategories = 2,
        SwitchToCautionCategory = 3,
        ClearDriveFilter = 4
    }

    private NoVisibleRulesActionKind _noVisibleRulesActionKind;

    [ObservableProperty]
    private bool _showNoVisibleRulesHint;

    [ObservableProperty]
    private string _noVisibleRulesHintText = "当前分类下未检测到可清理缓存文件，系统或软件已经很干净。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleRulesAction))]
    private string _noVisibleRulesActionText = string.Empty;

    [ObservableProperty]
    private string _noVisibleRulesHintBg = "#F8FAFC";

    [ObservableProperty]
    private string _noVisibleRulesHintBorder = "#E2E8F0";

    [ObservableProperty]
    private string _noVisibleRulesHintFg = "#64748B";

    public bool HasNoVisibleRulesAction => !string.IsNullOrWhiteSpace(NoVisibleRulesActionText);

    public bool HasEmptyRulesInScope => EmptyRuleCountInScope > 0;

    public string EmptyRulesToggleText => ShowEmptyRules
        ? $"已展开全部规则（含 {EmptyRuleCountInScope} 项未安装或无缓存）· 点击收起空项"
        : $"已自动隐藏 {EmptyRuleCountInScope} 项未安装或无缓存的软件/系统规则 · 点击展开查看全部支持列表";

    [ObservableProperty]
    private string _categoryHeaderText = "清理分类（等待扫描）";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRule))]
    [NotifyPropertyChangedFor(nameof(SelectedRuleIsCaution))]
    [NotifyPropertyChangedFor(nameof(DetailHeaderTitle))]
    [NotifyPropertyChangedFor(nameof(HasSelectedPreviewItems))]
    private CleanupRuleItemViewModel? _selectedRule;

    public bool HasSelectedRule => SelectedRule is not null;
    public bool SelectedRuleIsCaution => SelectedRule?.IsCaution == true && SelectedRule.HasCautionReason;
    public string DetailHeaderTitle => HasSelectedRule
        ? (SelectedRuleIsCaution ? "谨慎项风险说明与明细" : "清理项安全说明与明细")
        : "安全机制与全盘空间概览";
    public bool HasSelectedPreviewItems => SelectedRule is { PreviewRows.Count: > 0 };

    // ---- 底部勾选汇总 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecuteSelected))]
    private int _checkedRuleCount;

    [ObservableProperty]
    private long _checkedTotalBytes;

    [ObservableProperty]
    private string _footerSelectionSummary = "默认全不勾选；可逐项选择，或点击「仅勾选建议清理」快捷辅助。";

    [ObservableProperty]
    private string _executeButtonText = "执行所选清理";

    public bool CanExecuteSelected => CheckedRuleCount > 0 && !IsBusy;

    // ---- 隔离区摘要（画板 07 顶栏角标 + 画板 08 横幅） ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuarantineButtonText))]
    [NotifyPropertyChangedFor(nameof(HasQuarantineBatches))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyQuarantineHint))]
    [NotifyPropertyChangedFor(nameof(QuarantineBannerTitle))]
    private int _quarantineBatchCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuarantineBannerTitle))]
    private long _quarantineTotalBytes;

    [ObservableProperty]
    private bool _hasAnyQuarantineConflict;

    [ObservableProperty]
    private string _quarantineConflictNote = string.Empty;

    [ObservableProperty]
    private bool _isRefreshingOverview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyQuarantineHint))]
    [NotifyPropertyChangedFor(nameof(QuarantineBannerTitle))]
    private bool _isRefreshingQuarantine;

    [ObservableProperty]
    private string _quarantineLoadingMessage = "正在后台核验隔离区批次与恢复冲突…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBatchDeleteQuarantine))]
    [NotifyPropertyChangedFor(nameof(BatchDeleteQuarantineButtonText))]
    [NotifyPropertyChangedFor(nameof(QuarantineSelectionSummaryText))]
    private int _checkedQuarantineBatchCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchDeleteQuarantineButtonText))]
    [NotifyPropertyChangedFor(nameof(QuarantineSelectionSummaryText))]
    private long _checkedQuarantineTotalBytes;

    [ObservableProperty]
    private bool? _allQuarantineSelectedState = false;

    public bool CanBatchDeleteQuarantine => CheckedQuarantineBatchCount > 0 && !IsBusy;
    public string BatchDeleteQuarantineButtonText => CheckedQuarantineBatchCount > 0
        ? $"批量永久删除所选（{CheckedQuarantineBatchCount} 批 · {FileStatsService.FormatBytes(CheckedQuarantineTotalBytes)}）"
        : "批量永久删除所选";
    public string QuarantineSelectionSummaryText => CheckedQuarantineBatchCount > 0
        ? $"已勾选 {CheckedQuarantineBatchCount} / {QuarantineBatchCount} 批（预计释放 {FileStatsService.FormatBytes(CheckedQuarantineTotalBytes)} · 需人工二次确认）"
        : "隔离根路径：<Drive>:\\.MoveFolderQuarantine（受 ACL 保护并自动排除出磁盘扫描与迁移策略）";

    public string QuarantineButtonText => $"隔离区 · {QuarantineBatchCount}";
    public bool HasQuarantineBatches => QuarantineBatchCount > 0;
    public bool ShowEmptyQuarantineHint => !HasQuarantineBatches && !IsRefreshingQuarantine;
    public string QuarantineBannerTitle => QuarantineBatchCount > 0
        ? $"当前隔离区保留 {QuarantineBatchCount} 批清理项（共 {FileStatsService.FormatBytes(QuarantineTotalBytes)} · 尚未释放磁盘空间）"
        : (IsRefreshingQuarantine
            ? "正在后台读取隔离区批次与路径冲突状态…"
            : "当前隔离区为空（所有文件级清理默认先移入同卷隔离区，确认无碍后再手动释放空间）");

    // ---- 画板 11：AI 疑似残留候选与右侧双确认面板 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiResidualButtonText))]
    [NotifyPropertyChangedFor(nameof(HasAiResidualItems))]
    [NotifyPropertyChangedFor(nameof(AiResidualCountSummary))]
    private int _pendingAiResidualCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiResidualCountSummary))]
    [NotifyPropertyChangedFor(nameof(HasAiHardBlockedItems))]
    private int _hardBlockedAiResidualCount;

    public string AiResidualButtonText => PendingAiResidualCount > 0
        ? $"AI 疑似残留 · {PendingAiResidualCount}"
        : "AI 疑似残留";

    public bool HasAiResidualItems => AiResidualCandidates.Count > 0;
    public bool HasAiHardBlockedItems => AiHardBlockedCandidates.Count > 0;
    public string AiResidualCountSummary => $"{PendingAiResidualCount} 项可选 · {HardBlockedAiResidualCount} 项已硬拦截";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmAiQuarantine))]
    [NotifyPropertyChangedFor(nameof(AiConfirmScopeHeader))]
    [NotifyPropertyChangedFor(nameof(AiConfirmButtonText))]
    private int _selectedAiResidualCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiConfirmScopeHeader))]
    [NotifyPropertyChangedFor(nameof(AiConfirmButtonText))]
    private long _selectedAiResidualBytes;

    [ObservableProperty]
    private string _aiConfirmSourcePreview = "请在左侧勾选经您人工核实后的 AI 疑似残留目录（默认全不勾选）。";

    [ObservableProperty]
    private string _aiConfirmTargetPreview = "隔离区：<Drive>:\\.MoveFolderQuarantine\\ai-yyyyMMdd-xxxxxx（同卷原子移入）";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmAiQuarantine))]
    private bool _isUserVerifiedContentChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmAiQuarantine))]
    private bool _isUserAcknowledgedDisclaimerChecked;

    public string AiConfirmScopeHeader => SelectedAiResidualCount > 0
        ? $"本次待隔离目录（共 {SelectedAiResidualCount} 项 · {FileStatsService.FormatBytes(SelectedAiResidualBytes)}）："
        : "本次待隔离目录（尚未勾选）：";

    public string AiConfirmButtonText => SelectedAiResidualCount > 0
        ? $"确认移入隔离区（{FileStatsService.FormatBytes(SelectedAiResidualBytes)}）"
        : "确认移入隔离区";

    public bool CanConfirmAiQuarantine =>
        SelectedAiResidualCount > 0
        && IsUserVerifiedContentChecked
        && IsUserAcknowledgedDisclaimerChecked
        && !IsBusy;

    public CleanupViewModel(CleanupService? cleanupService = null, QuarantineStore? quarantineStore = null)
    {
        _quarantineStore = quarantineStore ?? new QuarantineStore();
        _cleanupService = cleanupService ?? new CleanupService(_quarantineStore);

        CategoryNavItems.Add(new CleanupCategoryNavItem(CleanupCategory.SystemTempAndShader, "系统临时与着色器缓存", "IconCpu"));
        CategoryNavItems.Add(new CleanupCategoryNavItem(CleanupCategory.SystemUpdateAndComponent, "系统更新与组件回收", "IconRefreshCw"));
        CategoryNavItems.Add(new CleanupCategoryNavItem(CleanupCategory.SoftwareAndDeveloperCache, "软件与开发者缓存", "IconLayers"));
        CategoryNavItems.Add(new CleanupCategoryNavItem(CleanupCategory.CautionItems, "谨慎清理项（需展开）", "IconTriangleAlert"));

        OperationExecutionGate.Shared.StateChanged += (_, _) =>
        {
            DispatchToUi(() =>
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanExecuteSelected));
                OnPropertyChanged(nameof(CanConfirmAiQuarantine));
                OnPropertyChanged(nameof(CanBatchDeleteQuarantine));
            });
        };

        // 构造函数仅做零 I/O 的纯内存初始化；磁盘概览、跨盘探测与隔离区读取统一在进入页面时后台异步加载
        SetIdleCategorySubtitles();
        RebuildIdleScopePreviews();
    }


    [RelayCommand]
    private void SelectCategory(CleanupCategoryNavItem? navItem)
    {
        if (navItem is null)
        {
            SelectedCategoryFilter = null;
        }
        else if (SelectedCategoryFilter == navItem.Category)
        {
            // 再次点击当前分类取消筛选，展示全部
            SelectedCategoryFilter = null;
        }
        else
        {
            SelectedCategoryFilter = navItem.Category;
            if (navItem.Category == CleanupCategory.CautionItems && !ShowCautionItems)
            {
                ShowCautionItems = true;
            }
        }

        foreach (CleanupCategoryNavItem item in CategoryNavItems)
        {
            item.IsSelected = SelectedCategoryFilter == item.Category;
        }

        if (!HasScanSnapshot)
        {
            RebuildIdleScopePreviews();
            return;
        }

        RebuildVisibleRules();
    }

    [RelayCommand]
    private void ToggleShowCautionItems()
    {
        ShowCautionItems = !ShowCautionItems;
        if (!ShowCautionItems && SelectedCategoryFilter == CleanupCategory.CautionItems)
        {
            SelectedCategoryFilter = null;
            foreach (CleanupCategoryNavItem item in CategoryNavItems) item.IsSelected = false;
        }
        RebuildVisibleRules();
    }

    [RelayCommand]
    private void ToggleShowEmptyRules()
    {
        ShowEmptyRules = !ShowEmptyRules;
        RebuildVisibleRules();
    }

    [RelayCommand]
    private void ToggleExpandGroup(CleanupRuleGroupViewModel? group)
    {
        if (group is null) return;
        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded) _expandedGroupIds.Add(group.GroupId);
        else _expandedGroupIds.Remove(group.GroupId);

        if (group.IsExpanded && (SelectedRule is null || !group.Items.Contains(SelectedRule)))
        {
            CleanupRuleItemViewModel? first = group.Items.FirstOrDefault();
            if (first is not null) SelectRule(first);
        }

        UpdateGroupExpansionSummary();
    }

    [RelayCommand]
    private void ToggleGroupCheck(CleanupRuleGroupViewModel? group)
    {
        if (IsExecuting || group is null || !group.CanCheckGroup) return;

        // 简洁两段切换：未全选（false / null）→ 全选本组可选软件；已全选（true）→ 取消本组全部勾选
        bool targetChecked = group.GroupCheckState != true;
        _isBatchUpdatingSelection = true;
        try
        {
            foreach (CleanupRuleItemViewModel item in group.Items)
            {
                if (item.CanCheck)
                {
                    item.IsChecked = targetChecked;
                }
                else if (!targetChecked)
                {
                    item.IsChecked = false;
                }
            }
        }
        finally
        {
            _isBatchUpdatingSelection = false;
        }

        RecalculateSelectionSummary();
    }

    [RelayCommand]
    private void ToggleExpandCollapseAllGroups()
    {
        CleanupRuleGroupViewModel[] groups = VisibleEntries.OfType<CleanupRuleGroupViewModel>().ToArray();
        if (groups.Length == 0) return;

        bool expand = !AreAllGroupsExpanded;
        foreach (CleanupRuleGroupViewModel g in groups)
        {
            g.IsExpanded = expand;
            if (expand) _expandedGroupIds.Add(g.GroupId);
            else _expandedGroupIds.Remove(g.GroupId);
        }
        UpdateGroupExpansionSummary();
    }

    [RelayCommand]
    private void OpenSelectedRuleDetails()
    {
        if (SelectedRule is null || !SelectedRule.HasAnyDetailItems) return;

        var win = new CleanupDetailListWindow(SelectedRule.ScanResult);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            win.Owner = owner;
            win.Icon = owner.Icon;
        }
        win.ShowDialog();
    }

    [RelayCommand]
    private void SelectRule(CleanupRuleItemViewModel? rule)
    {
        if (rule is null) return;
        foreach (CleanupRuleItemViewModel r in AllRuleItems)
        {
            r.IsSelected = ReferenceEquals(r, rule);
        }
        SelectedRule = rule;
    }

    /// <summary>
    /// 快捷辅助（按当前选中的分类作用域生效，支持再次点击取消勾选）：
    /// - 选中单个分类时，仅操作该分类内的条目，不影响其他分类已勾选项；
    /// - 未选中分类时，操作全部分类条目；
    /// - 勾选时仅勾选 1 档「建议清理」且可安全清理的条目，绝不自动勾选 2 档按需或 3 档谨慎项；
    /// - 若当前作用域已处于「仅勾选建议清理」状态，再次点击则取消该作用域内的勾选。
    /// </summary>
    [RelayCommand]
    private void SelectRecommendedOnly()
    {
        if (IsExecuting) return;
        CleanupRuleItemViewModel[] scopeItems = GetCurrentScopeRuleItems().ToArray();
        CleanupRuleItemViewModel[] recommendedSelectable = scopeItems
            .Where(item => item.Tier == CleanupRecommendationTier.Recommended && item.CanCheck)
            .ToArray();

        if (recommendedSelectable.Length == 0)
        {
            UpdateSelectRecommendedButtonState();
            return;
        }

        bool shouldUncheck = IsExactRecommendedOnlySelected(scopeItems, recommendedSelectable);
        _isBatchUpdatingSelection = true;
        try
        {
            foreach (CleanupRuleItemViewModel item in scopeItems)
            {
                bool targetChecked = !shouldUncheck
                    && item.Tier == CleanupRecommendationTier.Recommended
                    && item.CanCheck;
                if (item.IsChecked != targetChecked)
                {
                    item.IsChecked = targetChecked;
                }
            }
        }
        finally
        {
            _isBatchUpdatingSelection = false;
        }

        RecalculateSelectionSummary();
    }

    [RelayCommand]
    private void ClearAllSelections()
    {
        if (IsExecuting) return;
        _isBatchUpdatingSelection = true;
        try
        {
            foreach (CleanupRuleItemViewModel item in AllRuleItems)
            {
                if (item.IsChecked)
                {
                    item.IsChecked = false;
                }
            }
        }
        finally
        {
            _isBatchUpdatingSelection = false;
        }
        RecalculateSelectionSummary();
    }

    [RelayCommand]
    private void OpenQuarantineView()
    {
        // 先立即切换子视图（零阻塞），再在后台异步执行隔离区完整路径冲突核验
        ActiveSubView = CleanupSubView.QuarantineManager;
        _ = RefreshQuarantineListAsync(checkRestoreConflicts: true, force: false);
    }

    [RelayCommand]
    private void OpenAiResidualView()
    {
        SyncAiResidualCandidatesFromInbox();
        ActiveSubView = CleanupSubView.AiSuspectedResidual;
    }

    /// <summary>
    /// 供「AI 建议统一审阅」弹框调用：仅允许勾选 1 档建议或 2 档按需内置规则，3 档谨慎项绝不自动勾选。
    /// </summary>
    public (bool Checked, string Message) TryCheckRuleFromAiAdvice(string ruleId)
    {
        if (IsExecuting)
        {
            return (false, "当前正在执行清理，暂不可修改勾选状态。");
        }

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return (false, "规则编号无效。");
        }

        CleanupRuleItemViewModel? target = AllRuleItems.FirstOrDefault(r =>
            r.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return (false, "当前垃圾清理扫描快照中未找到该规则，请先执行扫描。");
        }

        if (target.IsCaution)
        {
            return (false, $"「{target.DisplayName}」属于 3 档谨慎清理项，不可自动勾选，请前往垃圾清理页展开谨慎项手动核对。");
        }

        if (!target.CanCheck)
        {
            return (false, target.HasBlockingProcesses
                ? $"「{target.DisplayName}」存在正在运行的关联进程阻断，暂不可勾选。"
                : $"「{target.DisplayName}」当前无待清理内容或不可勾选。");
        }

        if (!target.IsChecked)
        {
            target.IsChecked = true;
        }
        return (true, $"已勾选清理规则「{target.DisplayName}」（尚未执行清理）。");
    }

    public event EventHandler<CleanupRuleItemViewModel>? RuleLocateRequested;

    /// <summary>
    /// 供「AI 建议统一审阅」弹框定位至指定内置清理规则（若为 3 档谨慎项仅展开聚焦、绝不自动勾选）。
    /// </summary>
    public bool TryLocateCleanupRule(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return false;

        CleanupRuleItemViewModel? target = AllRuleItems.FirstOrDefault(r =>
            r.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;

        ActiveSubView = CleanupSubView.RulesMain;
        if (target.IsCaution && !ShowCautionItems)
        {
            ShowCautionItems = true;
        }
        if (!target.HasReclaimableContent && !ShowEmptyRules)
        {
            ShowEmptyRules = true;
        }
        if (SelectedCategoryFilter is not null && SelectedCategoryFilter != target.Category)
        {
            SelectedCategoryFilter = null;
            foreach (CleanupCategoryNavItem nav in CategoryNavItems) nav.IsSelected = false;
        }
        if (!string.IsNullOrWhiteSpace(SelectedDriveFilter)
            && !target.AffectedDrives.Any(d => d.Equals(SelectedDriveFilter, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedDriveFilter = null;
            foreach (CleanupDriveFilterChip chip in DriveFilterChips)
            {
                chip.IsSelected = chip.DriveLabel is null;
            }
        }

        RebuildVisibleRules();

        foreach (CleanupRuleGroupViewModel grp in VisibleEntries.OfType<CleanupRuleGroupViewModel>())
        {
            if (grp.Items.Contains(target))
            {
                grp.IsExpanded = true;
                _expandedGroupIds.Add(grp.GroupId);
                UpdateGroupExpansionSummary();
                break;
            }
        }

        SelectRule(target);
        RuleLocateRequested?.Invoke(this, target);
        return true;
    }

    public bool IsRuleCurrentlyChecked(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return false;
        return AllRuleItems.Any(r => r.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase) && r.IsChecked);
    }

    [RelayCommand]
    private void BackToRulesView()
    {
        ActiveSubView = CleanupSubView.RulesMain;
    }

    [RelayCommand]
    private void ToggleAiDrawer()
    {
        IsAiDrawerOpen = !IsAiDrawerOpen;
    }

    [RelayCommand]
    private void CancelExecution()
    {
        _execCts?.Cancel();
        ExecutionProgressMessage = "正在安全中止剩余清理项（当前正在处理的文件会保持完整）…";
    }

    [RelayCommand]
    private void DismissExecutionReceipt()
    {
        HasExecutionReceipt = false;
    }

    [RelayCommand]
    private void ShowExecutionReceiptDetails()
    {
        if (_lastExecutionResult is not null)
        {
            CleanupExecutionReceiptWindow.ShowForResult(
                result: _lastExecutionResult,
                windowTitle: "清理执行完整回执",
                plainTextReport: ExecutionReceiptDetailText,
                openQuarantineCallback: () => OpenQuarantineViewCommand.Execute(null));
            return;
        }

        if (string.IsNullOrWhiteSpace(ExecutionReceiptDetailText)) return;
        CustomMessageBox.ShowInformation(ExecutionReceiptDetailText, "清理执行完整回执");
    }

    private void UpdateCategorySubtitles(CleanupScanSnapshot snapshot)
    {
        foreach (CleanupCategoryNavItem cat in CategoryNavItems)
        {
            CleanupRuleScanResult[] matching = snapshot.Rules.Where(r => r.Rule.Category == cat.Category).ToArray();
            long bytes = matching.Sum(r => r.TotalBytes);
            int availableCount = matching.Count(r => r.IsAvailable || r.TotalBytes > 0 || r.MatchedCount > 0);
            string sizeLabel = bytes > 0 ? FileStatsService.FormatBytes(bytes) : $"{availableCount} 项可用";

            cat.Subtitle = cat.Category switch
            {
                CleanupCategory.SystemTempAndShader => $"{sizeLabel} · 建议清理",
                CleanupCategory.SystemUpdateAndComponent => $"{sizeLabel} · 官方接口",
                CleanupCategory.SoftwareAndDeveloperCache => availableCount > 0 ? $"{sizeLabel} · {availableCount} 款命中" : "暂无可清缓存",
                CleanupCategory.CautionItems => $"{sizeLabel} · 附风险说明",
                _ => sizeLabel
            };
        }
    }

    private void RebuildVisibleRules()
    {
        VisibleRuleItems.Clear();
        VisibleEntries.Clear();

        IEnumerable<CleanupRuleItemViewModel> scoped = AllRuleItems;
        if (SelectedCategoryFilter is CleanupCategory filter)
        {
            scoped = scoped.Where(r => r.Category == filter);
        }

        if (!string.IsNullOrWhiteSpace(SelectedDriveFilter))
        {
            string driveFilter = SelectedDriveFilter;
            scoped = scoped.Where(r => r.AffectedDrives.Any(d => d.Equals(driveFilter, StringComparison.OrdinalIgnoreCase)));
        }

        if (!ShowCautionItems && SelectedCategoryFilter != CleanupCategory.CautionItems)
        {
            scoped = scoped.Where(r => !r.IsCaution);
        }

        CleanupRuleItemViewModel[] scopedList = scoped.ToArray();
        EmptyRuleCountInScope = scopedList.Count(r => !r.HasReclaimableContent);

        IEnumerable<CleanupRuleItemViewModel> displayQuery = ShowEmptyRules
            ? scopedList
            : scopedList.Where(r => r.HasReclaimableContent);

        // 1. 保留扁平 VisibleRuleItems（按体积降序，供快捷定位与单元测试兼容）
        IOrderedEnumerable<CleanupRuleItemViewModel> orderedLeaf = SelectedCategoryFilter is null
            ? displayQuery
                .OrderByDescending(r => r.HasReclaimableContent)
                .ThenBy(r => r.IsCaution ? 1 : 0)
                .ThenByDescending(r => r.TotalBytes)
                .ThenBy(r => (int)r.Tier)
                .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            : displayQuery
                .OrderByDescending(r => r.HasReclaimableContent)
                .ThenByDescending(r => r.TotalBytes)
                .ThenBy(r => (int)r.Tier)
                .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        CleanupRuleItemViewModel[] orderedLeafArray = orderedLeaf.ToArray();
        foreach (CleanupRuleItemViewModel item in orderedLeafArray)
        {
            VisibleRuleItems.Add(item);
        }

        // 2. 构建 UI 展示列表 VisibleEntries：
        //    - 「软件与开发者缓存」按软件分组折叠展示（点击可向下展开单选某几款软件）；
        //    - 其他系统/更新/谨慎分类条目少，保持扁平卡片直显；
        //    - 混合全览时按（非谨慎优先 → 体积降序）统一排序。
        var sortableEntries = new List<(object Entry, bool HasContent, bool IsCaution, long Bytes, int TierRank, string Title)>();

        var groupedSoftware = orderedLeafArray
            .Where(r => r.Category == CleanupCategory.SoftwareAndDeveloperCache && !string.IsNullOrWhiteSpace(r.Rule.GroupId))
            .GroupBy(r => r.Rule.GroupId!, StringComparer.OrdinalIgnoreCase);

        foreach (var grp in groupedSoftware)
        {
            CleanupRuleItemViewModel[] children = grp
                .OrderByDescending(c => c.HasReclaimableContent)
                .ThenByDescending(c => c.TotalBytes)
                .ThenBy(c => (int)c.Tier)
                .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            string groupTitle = children[0].Rule.GroupDisplayName ?? grp.Key;
            bool isExpanded = _expandedGroupIds.Contains(grp.Key);
            var groupVm = new CleanupRuleGroupViewModel(grp.Key, groupTitle, children, isExpanded, isSelectionLocked: IsExecuting);

            sortableEntries.Add((
                Entry: groupVm,
                HasContent: groupVm.HasReclaimableContent,
                IsCaution: false,
                Bytes: groupVm.TotalBytes,
                TierRank: children.Min(c => (int)c.Tier),
                Title: groupTitle));
        }

        foreach (CleanupRuleItemViewModel flat in orderedLeafArray.Where(r =>
            r.Category != CleanupCategory.SoftwareAndDeveloperCache || string.IsNullOrWhiteSpace(r.Rule.GroupId)))
        {
            sortableEntries.Add((
                Entry: flat,
                HasContent: flat.HasReclaimableContent,
                IsCaution: flat.IsCaution,
                Bytes: flat.TotalBytes,
                TierRank: (int)flat.Tier,
                Title: flat.DisplayName));
        }

        IEnumerable<(object Entry, bool HasContent, bool IsCaution, long Bytes, int TierRank, string Title)> orderedEntries =
            SelectedCategoryFilter is null
                ? sortableEntries
                    .OrderByDescending(e => e.HasContent)
                    .ThenBy(e => e.IsCaution ? 1 : 0)
                    .ThenByDescending(e => e.Bytes)
                    .ThenBy(e => e.TierRank)
                    .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
                : sortableEntries
                    .OrderByDescending(e => e.HasContent)
                    .ThenByDescending(e => e.Bytes)
                    .ThenBy(e => e.TierRank)
                    .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase);

        foreach (var entry in orderedEntries)
        {
            VisibleEntries.Add(entry.Entry);
        }

        UpdateGroupExpansionSummary();
        ShowNoVisibleRulesHint = VisibleEntries.Count == 0;
        UpdateNoVisibleRulesHint();
        UpdateSelectRecommendedButtonState();

        if (SelectedRule is null || !VisibleRuleItems.Contains(SelectedRule))
        {
            CleanupRuleItemViewModel? first = VisibleRuleItems.FirstOrDefault();
            if (first is not null) SelectRule(first);
            else SelectedRule = null;
        }
    }

    private void UpdateNoVisibleRulesHint()
    {
        if (!ShowNoVisibleRulesHint)
        {
            _noVisibleRulesActionKind = NoVisibleRulesActionKind.None;
            NoVisibleRulesActionText = string.Empty;
            return;
        }

        string? drive = SelectedDriveFilter;
        CleanupCategory? cat = SelectedCategoryFilter;
        string? catTitle = cat is CleanupCategory activeCat
            ? CategoryNavItems.FirstOrDefault(c => c.Category == activeCat)?.Title
            : null;

        CleanupRuleItemViewModel[] driveHits = AllRuleItems
            .Where(r => r.HasReclaimableContent
                && (string.IsNullOrWhiteSpace(drive)
                    || r.AffectedDrives.Any(d => d.Equals(drive, StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        if (driveHits.Length > 0)
        {
            CleanupRuleItemViewModel[] cautionHits = driveHits.Where(r => r.IsCaution).ToArray();
            CleanupRuleItemViewModel[] nonCautionOtherCatHits = cat is null
                ? []
                : driveHits.Where(r => r.Category != cat && (ShowCautionItems || !r.IsCaution)).ToArray();

            // 1. 仅因「显示谨慎项」未展开导致列表为空（全部分类下选中了只含回收站等谨慎项的磁盘，例如 E:）
            if (!ShowCautionItems && (cat is null || cat == CleanupCategory.CautionItems) && cautionHits.Length > 0)
            {
                long cautionBytes = cautionHits.Sum(r => r.GetBytesForDrive(drive));
                string sizePart = cautionBytes > 0 ? FileStatsService.FormatBytes(cautionBytes) : $"{cautionHits.Length} 项";
                NoVisibleRulesHintText = string.IsNullOrWhiteSpace(drive)
                    ? $"检测到 {sizePart} 可清理内容，均属于默认折叠的「谨慎清理项」（如全盘回收站）。"
                    : $"磁盘 {drive} 下检测到 {sizePart} 可清理内容（如回收站等），属于默认折叠的「谨慎清理项」。";
                NoVisibleRulesActionText = "展开谨慎项查看";
                NoVisibleRulesHintBg = "#FFFBEB";
                NoVisibleRulesHintBorder = "#FDE68A";
                NoVisibleRulesHintFg = "#B45309";
                _noVisibleRulesActionKind = NoVisibleRulesActionKind.ShowCautionItems;
                return;
            }

            // 2. 当前分类下为空，但该磁盘在其他可见分类有可清理项
            if (cat is not null && nonCautionOtherCatHits.Length > 0)
            {
                long otherBytes = nonCautionOtherCatHits.Sum(r => r.GetBytesForDrive(drive));
                string sizePart = otherBytes > 0 ? FileStatsService.FormatBytes(otherBytes) : $"{nonCautionOtherCatHits.Length} 项";
                NoVisibleRulesHintText = string.IsNullOrWhiteSpace(drive)
                    ? $"「{catTitle}」下暂无可清理项，其他分类下仍有 {sizePart} 可清理内容。"
                    : $"「{catTitle}」在 {drive} 下暂无可清理项，但 {drive} 在其他分类下检测到 {sizePart} 可清理内容。";
                NoVisibleRulesActionText = "切到全部分类查看";
                NoVisibleRulesHintBg = "#F0F7FF";
                NoVisibleRulesHintBorder = "#BFDBFE";
                NoVisibleRulesHintFg = "#0078D4";
                _noVisibleRulesActionKind = NoVisibleRulesActionKind.SwitchToAllCategories;
                return;
            }

            // 3. 当前分类下为空，且该磁盘仅在默认折叠的「谨慎清理项」中有内容
            if (cat is not null && cautionHits.Length > 0)
            {
                long cautionBytes = cautionHits.Sum(r => r.GetBytesForDrive(drive));
                string sizePart = cautionBytes > 0 ? FileStatsService.FormatBytes(cautionBytes) : $"{cautionHits.Length} 项";
                NoVisibleRulesHintText = string.IsNullOrWhiteSpace(drive)
                    ? $"「{catTitle}」下暂无可清理项；检测到 {sizePart} 属于默认折叠的「谨慎清理项」。"
                    : $"「{catTitle}」在 {drive} 下暂无可清理项；{drive} 的 {sizePart} 均属于默认折叠的「谨慎清理项」（如回收站）。";
                NoVisibleRulesActionText = "切到谨慎清理项查看";
                NoVisibleRulesHintBg = "#FFFBEB";
                NoVisibleRulesHintBorder = "#FDE68A";
                NoVisibleRulesHintFg = "#B45309";
                _noVisibleRulesActionKind = NoVisibleRulesActionKind.SwitchToCautionCategory;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(drive))
        {
            NoVisibleRulesHintText = $"磁盘 {drive} 下未检测到可清理缓存文件，空间已很干净。";
            NoVisibleRulesActionText = "查看全部磁盘";
            NoVisibleRulesHintBg = "#F8FAFC";
            NoVisibleRulesHintBorder = "#E2E8F0";
            NoVisibleRulesHintFg = "#64748B";
            _noVisibleRulesActionKind = NoVisibleRulesActionKind.ClearDriveFilter;
            return;
        }

        NoVisibleRulesHintText = "当前分类下未检测到可清理缓存文件，系统或软件已经很干净。";
        NoVisibleRulesActionText = string.Empty;
        NoVisibleRulesHintBg = "#F8FAFC";
        NoVisibleRulesHintBorder = "#E2E8F0";
        NoVisibleRulesHintFg = "#64748B";
        _noVisibleRulesActionKind = NoVisibleRulesActionKind.None;
    }

    [RelayCommand]
    private void ResolveNoVisibleRulesHint()
    {
        switch (_noVisibleRulesActionKind)
        {
            case NoVisibleRulesActionKind.ShowCautionItems:
                ShowCautionItems = true;
                RebuildVisibleRules();
                break;

            case NoVisibleRulesActionKind.SwitchToAllCategories:
                SelectedCategoryFilter = null;
                foreach (CleanupCategoryNavItem item in CategoryNavItems) item.IsSelected = false;
                RebuildVisibleRules();
                break;

            case NoVisibleRulesActionKind.SwitchToCautionCategory:
                SelectedCategoryFilter = CleanupCategory.CautionItems;
                ShowCautionItems = true;
                foreach (CleanupCategoryNavItem item in CategoryNavItems)
                {
                    item.IsSelected = item.Category == CleanupCategory.CautionItems;
                }
                RebuildVisibleRules();
                break;

            case NoVisibleRulesActionKind.ClearDriveFilter:
                SelectedDriveFilter = null;
                foreach (CleanupDriveFilterChip chip in DriveFilterChips)
                {
                    chip.IsSelected = chip.DriveLabel is null;
                }
                RebuildVisibleRules();
                break;
        }
    }

    private IEnumerable<CleanupRuleItemViewModel> GetCurrentScopeRuleItems()
    {
        return SelectedCategoryFilter is CleanupCategory filter
            ? AllRuleItems.Where(r => r.Category == filter)
            : AllRuleItems;
    }

    private static bool IsExactRecommendedOnlySelected(
        CleanupRuleItemViewModel[] scopeItems,
        CleanupRuleItemViewModel[] recommendedSelectable)
    {
        if (recommendedSelectable.Length == 0) return false;

        foreach (CleanupRuleItemViewModel item in scopeItems)
        {
            bool expected = item.Tier == CleanupRecommendationTier.Recommended && item.CanCheck;
            if (item.IsChecked != expected)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateSelectRecommendedButtonState()
    {
        CleanupRuleItemViewModel[] scopeItems = GetCurrentScopeRuleItems().ToArray();
        CleanupRuleItemViewModel[] recommendedSelectable = scopeItems
            .Where(item => item.Tier == CleanupRecommendationTier.Recommended && item.CanCheck)
            .ToArray();

        HasSelectableRecommendedInScope = recommendedSelectable.Length > 0;
        IsCurrentScopeRecommendedSelected = IsExactRecommendedOnlySelected(scopeItems, recommendedSelectable);
        OnPropertyChanged(nameof(SelectRecommendedButtonText));
        OnPropertyChanged(nameof(SelectRecommendedButtonToolTip));
    }

    private void UpdateGroupExpansionSummary()
    {
        CleanupRuleGroupViewModel[] groups = VisibleEntries.OfType<CleanupRuleGroupViewModel>().ToArray();
        HasVisibleGroups = groups.Length > 0;
        AreAllGroupsExpanded = groups.Length > 0 && groups.All(g => g.IsExpanded);
    }

    private void RecalculateSelectionSummary()
    {
        if (_isBatchUpdatingSelection) return;

        foreach (CleanupRuleGroupViewModel grp in VisibleEntries.OfType<CleanupRuleGroupViewModel>())
        {
            grp.RefreshMetrics();
        }

        UpdateSelectRecommendedButtonState();

        CleanupRuleItemViewModel[] checkedItems = AllRuleItems.Where(r => r.IsChecked).ToArray();
        CheckedRuleCount = checkedItems.Length;
        CheckedTotalBytes = checkedItems.Sum(r => r.TotalBytes);

        if (checkedItems.Length == 0)
        {
            FooterSelectionSummary = "尚未勾选清理项；点击规则可查看明细，或使用「仅勾选建议清理」。";
            ExecuteButtonText = "执行所选清理";
            return;
        }

        int recCount = checkedItems.Count(r => r.Tier == CleanupRecommendationTier.Recommended);
        int onDemandCount = checkedItems.Count(r => r.Tier == CleanupRecommendationTier.OnDemand);
        int cautionCount = checkedItems.Count(r => r.Tier == CleanupRecommendationTier.Caution);

        var parts = new List<string>();
        if (recCount > 0) parts.Add($"{recCount} 项「建议清理」");
        if (onDemandCount > 0) parts.Add($"{onDemandCount} 项「按需清理」");
        if (cautionCount > 0) parts.Add($"{cautionCount} 项「谨慎清理（需短语确认）」");

        string channelHint = checkedItems.All(r => r.Channel == CleanupExecutionChannel.FileQuarantine)
            ? "同卷隔离可恢复"
            : checkedItems.All(r => r.Channel == CleanupExecutionChannel.OfficialSystemApi)
                ? "官方系统接口"
                : "同卷隔离 + 官方接口";

        string sizeStr = CheckedTotalBytes > 0 ? FileStatsService.FormatBytes(CheckedTotalBytes) : "官方实时计算";
        FooterSelectionSummary = $"已勾选 {string.Join(" + ", parts)}（{sizeStr} · {channelHint}）。";
        ExecuteButtonText = CheckedTotalBytes > 0
            ? $"执行所选清理（{FileStatsService.FormatBytes(CheckedTotalBytes)}）"
            : $"执行所选清理（{checkedItems.Length} 项）";
    }

    private static string FormatExecutionResultDetail(CleanupExecutionResult result)
    {
        var lines = new List<string> { result.SummaryMessage, string.Empty };
        foreach (CleanupExecutionItemResult item in result.Items)
        {
            string tag = item.Status switch
            {
                CleanupItemExecutionStatus.Quarantined => "[已隔离 · 未释放物理空间]",
                CleanupItemExecutionStatus.Recycled => "[已移入回收站]",
                CleanupItemExecutionStatus.ReclaimedByOfficialApi => "[官方接口已回收]",
                CleanupItemExecutionStatus.PermanentlyDeleted => "[已直接删除 · 立即释放空间]",
                CleanupItemExecutionStatus.Blocked => "[已安全拦截]",
                CleanupItemExecutionStatus.Skipped => "[已跳过]",
                _ => "[失败]"
            };
            lines.Add($"• {tag} {item.DisplayName}：{item.Message}");
        }
        return string.Join("\n", lines);
    }

    private static void DispatchToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished
            && dispatcher.Thread.IsAlive)
        {
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static Task DispatchToUiAsync(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished
            && dispatcher.Thread.IsAlive)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(action).Task;
        }

        action();
        return Task.CompletedTask;
    }
}
