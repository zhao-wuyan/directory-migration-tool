using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class DiskScanViewModel : ObservableObject
{
    private const int DirectoryPageSize = 200;

    private readonly DiskScanService _scanService = new();
    private readonly VolumeSnapshotCache _snapshotCache;
    private readonly MigrationPlanService _planService;
    private readonly MigrationBatchService _batchService;
    private readonly ApplicationCatalog _catalog;
    private readonly Dispatcher _uiDispatcher;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly DispatcherTimer _scanElapsedTimer;
    private readonly Stopwatch _scanStopwatch = new();

    private ScanSnapshot? _snapshot;
    private FrozenMigrationPlan? _frozenPlan;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _batchCts;

    public ScanSnapshot? CurrentSnapshot => _snapshot;
    public event EventHandler<ScanSnapshot?>? SnapshotChanged;

    [ObservableProperty]
    private AiAssistantViewModel? _aiAssistant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiDrawerButtonTag))]
    private bool _isAiDrawerOpen;

    public string AiDrawerButtonTag => IsAiDrawerOpen ? "Active" : string.Empty;

    public ObservableCollection<VolumeCardItem> VolumeCards { get; } = [];
    public ObservableCollection<RecentScanItem> RecentScans { get; } = [];
    public ObservableCollection<ScanTreeRowItem> VisibleTreeRows { get; } = [];
    public ObservableCollection<BatchPlanRowItem> PlanItems { get; } = [];
    public ObservableCollection<string> BatchLogs { get; } = [];

    // ---- 四阶段子视图路由 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVolumeSelectView))]
    [NotifyPropertyChangedFor(nameof(IsDirectoryTreeView))]
    [NotifyPropertyChangedFor(nameof(IsMigrationQueueView))]
    [NotifyPropertyChangedFor(nameof(IsBatchExecutionView))]
    private DiskScanSubView _activeSubView = DiskScanSubView.VolumeSelectAndScan;

    public bool IsVolumeSelectView => ActiveSubView == DiskScanSubView.VolumeSelectAndScan;
    public bool IsDirectoryTreeView => ActiveSubView == DiskScanSubView.DirectoryTree;
    public bool IsMigrationQueueView => ActiveSubView == DiskScanSubView.MigrationQueue;
    public bool IsBatchExecutionView => ActiveSubView == DiskScanSubView.BatchExecution;

    // ---- 00 选盘与扫描态 ----
    [ObservableProperty]
    private VolumeCardItem? _selectedVolumeCard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReturnToTreeFromSelect))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReturnToTreeFromSelect))]
    private bool _hasSnapshot;

    public bool CanReturnToTreeFromSelect => HasSnapshot && !IsScanning;

    [ObservableProperty]
    private double _scanProgressPercent;

    [ObservableProperty]
    private string _scanStageBadgeText = "准备就绪";

    [ObservableProperty]
    private string _scanStageHeading = "选择上方任意磁盘卡片即可开始只读高速扫描";

    [ObservableProperty]
    private string _scanElapsedText = "0.0 s";

    [ObservableProperty]
    private string _scanProcessedRecordsText = "—";

    [ObservableProperty]
    private string _scanLargeDirCountText = "—";

    [ObservableProperty]
    private string _scanL1SummaryText = "—";

    // 三阶段状态（0=等待, 1=进行中, 2=已完成）
    [ObservableProperty] private int _scanStep1State;
    [ObservableProperty] private int _scanStep2State;
    [ObservableProperty] private int _scanStep3State;
    [ObservableProperty] private string _scanStep1Status = "等待开始";
    [ObservableProperty] private string _scanStep2Status = "等待中";
    [ObservableProperty] private string _scanStep3Status = "等待中";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnverifiedBackupReminder))]
    [NotifyPropertyChangedFor(nameof(UnverifiedReminderTitle))]
    private int _unverifiedBackupCount;

    public bool HasUnverifiedBackupReminder => UnverifiedBackupCount > 0;
    public string UnverifiedReminderTitle => $"有 {UnverifiedBackupCount} 项迁移备份待验证";

    public bool HasRecentScans => RecentScans.Count > 0;

    [ObservableProperty]
    private bool _hasDevCache;

    // ---- 01 目录树与 4 级详情 ----
    [ObservableProperty] private string _snapshotVolumeTitle = string.Empty;
    [ObservableProperty] private string _snapshotModeBadge = "未扫描";
    [ObservableProperty] private bool _isSnapshotMftBackend;
    [ObservableProperty] private string _snapshotVolumeSummary = string.Empty;
    [ObservableProperty] private string _usnStatusNote = string.Empty;
    [ObservableProperty] private bool _isSnapshotStale;
    [ObservableProperty] private string _snapshotStaleMessage = string.Empty;
    [ObservableProperty] private string _breadcrumbText = "此电脑";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHotspotMode))]
    [NotifyPropertyChangedFor(nameof(TreeViewTag))]
    [NotifyPropertyChangedFor(nameof(HotspotViewTag))]
    [NotifyPropertyChangedFor(nameof(HotspotFlatTag))]
    [NotifyPropertyChangedFor(nameof(HotspotPathTreeTag))]
    [NotifyPropertyChangedFor(nameof(HotspotPathTreeToolTip))]
    [NotifyPropertyChangedFor(nameof(ShareColumnHeader))]
    [NotifyPropertyChangedFor(nameof(ShareColumnToolTip))]
    private ScanBrowseMode _browseMode = ScanBrowseMode.DirectoryTree;

    private ScanBrowseMode _lastHotspotMode = ScanBrowseMode.HotspotPathTree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotspotPathTreeToolTip))]
    private bool _isHotspotPathTreeExpanded;

    private HashSet<int>? _hotspotPathKeptNodes;
    private HashSet<int>? _hotspotPathAncestorNodes;

    [ObservableProperty]
    private bool _showFilesInTree;

    public bool IsHotspotMode => BrowseMode is ScanBrowseMode.HotspotFlatList or ScanBrowseMode.HotspotPathTree;
    public string TreeViewTag => BrowseMode == ScanBrowseMode.DirectoryTree ? "Active" : string.Empty;
    public string HotspotViewTag => IsHotspotMode ? "Active" : string.Empty;
    public string HotspotFlatTag => BrowseMode == ScanBrowseMode.HotspotFlatList ? "Active" : string.Empty;
    public string HotspotPathTreeTag => BrowseMode == ScanBrowseMode.HotspotPathTree ? "Active" : string.Empty;
    public string HotspotPathTreeToolTip => BrowseMode != ScanBrowseMode.HotspotPathTree
        ? "按目录层级查看大软件（默认收起）；再次点击可一键展开或收起"
        : IsHotspotPathTreeExpanded
            ? "当前已展开大软件路径，再次点击一键收起到顶层"
            : "当前已收起，再次点击一键展开全部大软件路径";
    public string ShareColumnHeader => (IsHotspotMode || !string.IsNullOrWhiteSpace(SearchFilter)) ? "全盘占比" : "父级占比";
    public string ShareColumnToolTip => (IsHotspotMode || !string.IsNullOrWhiteSpace(SearchFilter))
        ? "占当前扫描磁盘总已用空间的比例（全局统一标尺，子目录占比恒不超过父目录）"
        : "占直接父目录已用空间的比例";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareColumnHeader))]
    [NotifyPropertyChangedFor(nameof(ShareColumnToolTip))]
    private string _searchFilter = string.Empty;

    [ObservableProperty]
    private ScanTreeRowItem? _selectedRow;

    [ObservableProperty]
    private SafetyPolicyDecision? _selectedDecision;

    [ObservableProperty] private string _selectedLevelBadgeText = "未选中目录";
    [ObservableProperty] private string _selectedLevelBadgeBg = "#F8FAFC";
    [ObservableProperty] private string _selectedLevelBadgeBorder = "#E2E8F0";
    [ObservableProperty] private string _selectedLevelBadgeFg = "#64748B";

    [ObservableProperty] private string _selectedTitle = "请选择左侧目录";
    [ObservableProperty] private string _selectedPathDisplay = "点击目录树中的任意目录查看空间占用与 L0–L3 安全准入评估。";
    [ObservableProperty] private string _selectedAllocatedDisplay = "—";
    [ObservableProperty] private string _selectedFileCountDisplay = "—";
    [ObservableProperty] private bool _hasDirectoryExplanation;
    [ObservableProperty] private bool _selectedExplanationIsWarning;
    [ObservableProperty] private string _selectedDirectoryRoleTitle = string.Empty;
    [ObservableProperty] private string _selectedDirectoryPurpose = string.Empty;
    [ObservableProperty] private string _selectedDirectoryAdvice = string.Empty;
    [ObservableProperty] private string _selectedEvidenceIdentity = "• 识别依据：等待选择目录";
    [ObservableProperty] private string _selectedEvidenceStructure = "• 结构核验：—";
    [ObservableProperty] private string _selectedEvidenceStorage = "• 空间口径：—";
    [ObservableProperty] private string _selectedSafeNote = "迁移后原目录将在源盘改名保留为备份，待你运行软件验证正常并双确认后再手动删除。";

    [ObservableProperty] private string _selectedNativeGuidance = string.Empty;
    [ObservableProperty] private bool _showNativeGuidance;
    [ObservableProperty] private bool _showL3ConfirmCheck;
    [ObservableProperty] private bool _isL3RiskConfirmedForSelection;
    [ObservableProperty] private bool _canAddSelectedToPlan;
    [ObservableProperty] private bool _isSelectedAlreadyInPlan;

    public string SelectedActionButtonText => IsSelectedAlreadyInPlan ? "已勾选加入待迁移清单" : "加入待迁移清单";

    // 清单汇总脚栏
    public int PlanItemCount => PlanItems.Count;
    public bool HasPlanItems => PlanItems.Count > 0;
    public string PlanSelectionBadgeText => PlanItems.Count == 0
        ? "尚未勾选目录"
        : $"已勾选 {PlanItems.Count} 个目录 · {FileStatsService.FormatBytes(PlanItems.Sum(p => Math.Max(p.AllocatedBytes, p.LogicalBytes)))}";
    public string GoToQueueButtonText => $"前往迁移清单（{PlanItems.Count}）";

    // ---- 02 迁移清单与预检 ----
    [ObservableProperty]
    private string _unifiedTargetRoot = @"D:\MigratedData";

    [ObservableProperty]
    private string _targetSpaceHintText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FastVerifyTag))]
    [NotifyPropertyChangedFor(nameof(HashVerifyTag))]
    [NotifyPropertyChangedFor(nameof(ActiveVerifyModeText))]
    private bool _useContentHash;

    public string FastVerifyTag => !UseContentHash ? "Active" : string.Empty;
    public string HashVerifyTag => UseContentHash ? "Active" : string.Empty;

    [ObservableProperty]
    private string _preflightSummary = "添加目录至迁移清单后，系统将自动或按需执行碰撞、目标空间与运行进程预检。";

    [ObservableProperty]
    private bool _hasPreflightBlocking;

    [ObservableProperty]
    private bool _canStartBatch;

    public string StartBatchButtonText => $"开始迁移（{PlanItems.Count}）";

    // ---- 04 迁移执行与实时进度 ----
    [ObservableProperty]
    private bool _isBatchExecuting;

    [ObservableProperty]
    private bool _isBatchFinished;

    [ObservableProperty]
    private string _executionHeaderTitle = "正在执行批量安全迁移";

    [ObservableProperty]
    private string _executionHeaderSubtitle = "串行复用手动迁移底座；完成后原目录将在源盘改名保留，绝不自动删除。";

    [ObservableProperty]
    private string _executionQueueBadgeText = "0 / 0";

    [ObservableProperty]
    private string _activeStageBadgeText = "准备启动";

    [ObservableProperty]
    private string _activeItemTitle = string.Empty;

    [ObservableProperty]
    private string _activeItemSourcePath = string.Empty;

    [ObservableProperty]
    private string _activeItemTargetPath = string.Empty;

    [ObservableProperty]
    private double _activeItemPercent;

    [ObservableProperty]
    private string _activePercentText = "0%";

    [ObservableProperty]
    private string _activeCopiedText = "—";

    [ObservableProperty]
    private string _activeSpeedText = "—";

    [ObservableProperty]
    private string _activeEtaText = "计算中…";

    public string ActiveVerifyModeText => UseContentHash ? "完整 SHA-256 哈希" : "快速元数据核对";

    [ObservableProperty] private int _execStep1State;
    [ObservableProperty] private int _execStep2State;
    [ObservableProperty] private int _execStep3State;
    [ObservableProperty] private int _execStep4State;
    [ObservableProperty] private string _execStep1Note = "等待开始";
    [ObservableProperty] private string _execStep2Note = "等待复核通过";
    [ObservableProperty] private string _execStep3Note = "等待复制完成";
    [ObservableProperty] private string _execStep4Note = "等待核对通过";

    [ObservableProperty]
    private bool _showDetailedLogs;

    public DiskScanViewModel(VolumeSnapshotCache? snapshotCache = null)
    {
        ApplicationCatalog.RegisterWithSafetyPolicy();
        _catalog = ApplicationCatalog.Shared;
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _snapshotCache = snapshotCache ?? VolumeSnapshotCache.Shared;
        _planService = new MigrationPlanService(_catalog);
        _batchService = new MigrationBatchService(_planService, snapshotCache: _snapshotCache);

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RebuildVisibleRows();
        };

        _scanElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _scanElapsedTimer.Tick += (_, _) =>
        {
            ScanElapsedText = $"已耗时 {_scanStopwatch.Elapsed.TotalSeconds:F1} s";
        };

        PlanItems.CollectionChanged += (_, _) => NotifyPlanSummariesChanged();
        RecentScans.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentScans));
        _snapshotCache.CacheChanged += OnSnapshotCacheChanged;

        LoadVolumes();
        UpdateTargetSpaceHint();
#if DEBUG
        LoadDevCachedRecents();
#endif
        RefreshVolumeCardCacheStates();
    }

    private void OnSnapshotCacheChanged(object? sender, EventArgs e)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            if (!_uiDispatcher.HasShutdownStarted && !_uiDispatcher.HasShutdownFinished)
            {
                _uiDispatcher.BeginInvoke(RefreshCacheBindings);
            }
            return;
        }
        RefreshCacheBindings();
    }

    private void RefreshCacheBindings()
    {
        RefreshVolumeCardCacheStates();
        SyncCurrentSnapshotFreshness();
    }

    partial void OnActiveSubViewChanged(DiskScanSubView value)
    {
        if (value == DiskScanSubView.VolumeSelectAndScan)
        {
            // 回到选盘页时解除固定，使超过硬过期（60 分钟）的旧快照可被自动回收释放内存
            _snapshotCache.SetPinnedScanId(null);
            RefreshVolumeCardCacheStates();
        }
        else if (_snapshot is not null)
        {
            // 正在浏览目录树/编辑清单/执行迁移时固定当前快照，避免后台定时器中途驱逐
            _snapshotCache.SetPinnedScanId(_snapshot.ScanId);
            SyncCurrentSnapshotFreshness();
        }
    }

    public void SyncUnverifiedBackupCount(int count)
    {
        UnverifiedBackupCount = count;
    }

    partial void OnSelectedVolumeCardChanged(VolumeCardItem? oldValue, VolumeCardItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    partial void OnBrowseModeChanged(ScanBrowseMode value) => RebuildVisibleRows();

    partial void OnShowFilesInTreeChanged(bool value) => RebuildVisibleRows();

    partial void OnSearchFilterChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnSelectedRowChanged(ScanTreeRowItem? value) => InspectSelectedRow(value);

    partial void OnIsL3RiskConfirmedForSelectionChanged(bool value) => UpdateCanAddSelected();

    partial void OnIsSelectedAlreadyInPlanChanged(bool value) => OnPropertyChanged(nameof(SelectedActionButtonText));

    partial void OnUnifiedTargetRootChanged(string value)
    {
        InvalidateFrozenPlan();
        RecomputeDraftTargets();
        UpdateTargetSpaceHint();
    }

    partial void OnUseContentHashChanged(bool value) => InvalidateFrozenPlan();

    private void NotifyPlanSummariesChanged()
    {
        OnPropertyChanged(nameof(PlanItemCount));
        OnPropertyChanged(nameof(HasPlanItems));
        OnPropertyChanged(nameof(PlanSelectionBadgeText));
        OnPropertyChanged(nameof(GoToQueueButtonText));
        OnPropertyChanged(nameof(StartBatchButtonText));
        UpdateTargetSpaceHint();
        SyncTreePlanChecks();
        SyncAiProposalAdoptedStates();
    }

    partial void OnAiAssistantChanged(AiAssistantViewModel? value)
    {
        SyncAiProposalAdoptedStates();
    }

    private void SyncAiProposalAdoptedStates()
    {
        if (AiAssistant is null) return;

        var nodeIds = PlanItems
            .Where(p => p.NodeId.HasValue)
            .Select(p => p.NodeId!.Value)
            .ToArray();
        var paths = PlanItems
            .Select(p => p.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        AiAssistant.SyncAdoptedPlanItems(_snapshot?.ScanId, nodeIds, paths);
    }
}
