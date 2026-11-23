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

/// <summary>
/// 磁盘扫描模块四阶段子视图（严格对齐 docs/untitled.pen：00 选盘与扫描 → 01 目录树 → 02 迁移清单 → 04 迁移执行）。
/// </summary>
public enum DiskScanSubView
{
    VolumeSelectAndScan,
    DirectoryTree,
    MigrationQueue,
    BatchExecution
}

/// <summary>
/// 阶段 01 的三种目录浏览视角：
/// DirectoryTree = 全盘目录树；HotspotFlatList = 大软件直达列表（互不重叠）；HotspotPathTree = 大软件按路径展开（顶层向下剪枝树）。
/// </summary>
public enum ScanBrowseMode
{
    DirectoryTree,
    HotspotFlatList,
    HotspotPathTree
}

/// <summary>
/// 00 选盘入口磁盘卡片视图模型。
/// </summary>
public partial class VolumeCardItem : ObservableObject
{
    public ScanVolumeInfo Info { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardBorderBrush))]
    [NotifyPropertyChangedFor(nameof(IconBgBrush))]
    [NotifyPropertyChangedFor(nameof(IconStrokeBrush))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool _isCurrentlyScanning;

    public VolumeCardItem(ScanVolumeInfo info) => Info = info;

    public string DriveRoot => Info.DriveRoot;
    public string DriveLetter => Info.DriveRoot.TrimEnd('\\');
    public string DisplayTitle => string.IsNullOrWhiteSpace(Info.VolumeLabel)
        ? $"本地磁盘 ({DriveLetter})"
        : $"{Info.VolumeLabel} ({DriveLetter})";

    public string BackendSubtitle => Info.SupportsRawMft
        ? $"{Info.FileSystem} · MFT 高速"
        : $"{Info.FileSystem} · 普通扫描";

    public double UsedPercent => Info.UsedPercent;
    public string FreeText => $"{FileStatsService.FormatBytes(Info.FreeBytes)} 可用";
    public string UsedOverTotalText => $"已用 {FileStatsService.FormatBytes(Info.UsedBytes)} / {FileStatsService.FormatBytes(Info.TotalBytes)}";
    public string UsedPercentText => $"已占用 {UsedPercent:F0}%";
    public string CapabilityReason => Info.CapabilityReason;

    public bool IsSpaceTight => UsedPercent >= 80;
    public bool IsRecommendedTarget => Info.SupportsRawMft && !IsSpaceTight && Info.FreeBytes >= 50L * 1024 * 1024 * 1024 && !DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase);

    public string StatusBadgeText => IsSpaceTight ? "空间紧张"
        : !Info.SupportsRawMft ? "仅只读浏览"
        : IsRecommendedTarget ? "推荐目标盘"
        : "NTFS 卷";

    public string StatusBadgeBg => IsSpaceTight ? "#FFFBEB"
        : !Info.SupportsRawMft ? "#F8FAFC"
        : IsRecommendedTarget ? "#ECFDF5"
        : "#EBF5FF";

    public string StatusBadgeBorder => IsSpaceTight ? "#FDE68A"
        : !Info.SupportsRawMft ? "#E2E8F0"
        : IsRecommendedTarget ? "#A7F3D0"
        : "#BFDBFE";

    public string StatusBadgeFg => IsSpaceTight ? "#B45309"
        : !Info.SupportsRawMft ? "#64748B"
        : IsRecommendedTarget ? "#0F7B4B"
        : "#0078D4";

    public string FreeTextBrush => IsSpaceTight ? "#B45309" : "#0F172A";
    public string BarBrush => IsSpaceTight ? "#E67E22" : !Info.SupportsRawMft ? "#0EA5E9" : "#0078D4";
    public string CardBorderBrush => IsSelected || IsCurrentlyScanning ? "#0078D4" : "#E2E8F0";
    public string IconBgBrush => IsSelected || IsCurrentlyScanning ? "#EBF5FF" : "#EEF2F6";
    public string IconStrokeBrush => IsSelected || IsCurrentlyScanning ? "#0078D4" : "#334155";
    public string ActionText => IsCurrentlyScanning ? "正在扫描…" : (Info.SupportsRawMft ? "开始扫描" : "普通扫描");
}

/// <summary>
/// 最近扫描记录条目（会话内内存快照或 #if DEBUG 开发模式磁盘缓存，均可一键切回目录树）。
/// </summary>
public sealed record RecentScanItem(
    Guid ScanId,
    string VolumeTitle,
    string CompletedTimeText,
    string SummaryText,
    ScanSnapshot? Snapshot,
    string? DevCacheFilePath = null)
{
    public bool IsDevCached => !string.IsNullOrEmpty(DevCacheFilePath) && Snapshot is null;
}

/// <summary>
/// 虚拟化扁平目录树投影行模型（只为当前可见/已展开节点创建，避免百万节点对象爆炸）。
/// </summary>
public partial class ScanTreeRowItem : ObservableObject
{
    public int NodeId { get; init; }
    public int Depth { get; init; }
    public bool IsDirectory { get; init; }
    public bool HasChildren { get; init; }
    public bool IsLoadMorePlaceholder { get; init; }
    public int LoadMoreParentNodeId { get; init; } = -1;
    public int LoadMoreNextOffset { get; init; }

    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long LogicalBytes { get; init; }
    public long AllocatedBytes { get; init; }
    public long UniqueAllocatedBytes { get; init; }
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public double ParentSharePercent { get; init; }
    public string AttributionLabel { get; init; } = string.Empty;
    public MigrationAdmissionLevel AdmissionLevel { get; init; } = MigrationAdmissionLevel.ManualRiskConfirm;
    public bool IsToolSelfProtected { get; init; }
    public DirectoryHintLevel HintLevel { get; init; } = DirectoryHintLevel.None;
    public string HintToolTip { get; init; } = string.Empty;
    public ScanNodeFlags Flags { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminateInPlan))]
    private bool _isInPlan;

    /// <summary>
    /// 当前目录本身未直接加入待迁移清单，但其下级子目录中已有至少 1 项加入清单（用于显示三态勾选框的「半选」横杠）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminateInPlan))]
    private bool _hasDescendantInPlan;

    public bool IsIndeterminateInPlan => !IsInPlan && HasDescendantInPlan;

    public Thickness IndentMargin => new(Depth * 14, 0, 0, 0);
    public string ExpandGlyph => IsLoadMorePlaceholder ? "⋯" : (!HasChildren ? string.Empty : (IsExpanded ? "▾" : "▸"));
    public bool ShowExpandButton => HasChildren || IsLoadMorePlaceholder;
    public bool IsHardDenied => IsDirectory && !IsLoadMorePlaceholder && AdmissionLevel == MigrationAdmissionLevel.HardDenied;
    public bool IsNativeGuide => IsDirectory && !IsLoadMorePlaceholder && AdmissionLevel == MigrationAdmissionLevel.VendorNativeRecommended;
    public bool CanToggleInPlan => IsDirectory && !IsLoadMorePlaceholder && AdmissionLevel is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.ManualRiskConfirm;
    public bool ShowWarningHint => IsDirectory && !IsLoadMorePlaceholder && HintLevel == DirectoryHintLevel.Warning;
    public bool ShowInfoHint => IsDirectory && !IsLoadMorePlaceholder && HintLevel == DirectoryHintLevel.Info;

    public string AllocatedText => IsLoadMorePlaceholder ? string.Empty : FileStatsService.FormatBytes(UniqueAllocatedBytes);
    public string LogicalText => IsLoadMorePlaceholder ? string.Empty : FileStatsService.FormatBytes(LogicalBytes);
    public string ShareText => IsLoadMorePlaceholder ? string.Empty : $"{ParentSharePercent:F1}%";
    public bool ShowLevelBadge => IsDirectory && !IsLoadMorePlaceholder;
    public bool HasAttribution => !string.IsNullOrWhiteSpace(AttributionLabel);

    public string TypeIconColor => IsLoadMorePlaceholder ? "#0078D4"
        : !IsDirectory ? "#64748B"
        : AdmissionLevel switch
        {
            MigrationAdmissionLevel.HardDenied => "#C42B1C",
            MigrationAdmissionLevel.VerifiedEligible => "#0F7B4B",
            MigrationAdmissionLevel.VendorNativeRecommended => "#0078D4",
            _ => "#B45309"
        };

    public string BarBrush => !IsDirectory ? "#0078D4" : AdmissionLevel switch
    {
        MigrationAdmissionLevel.HardDenied => "#C42B1C",
        MigrationAdmissionLevel.VerifiedEligible => "#0F7B4B",
        MigrationAdmissionLevel.VendorNativeRecommended => "#0EA5E9",
        _ => "#B45309"
    };

    public string LevelBadgeText => IsLoadMorePlaceholder || !IsDirectory ? string.Empty : AdmissionLevel switch
    {
        MigrationAdmissionLevel.HardDenied => IsToolSelfProtected ? "L0 工具保护" : "L0 系统保护",
        MigrationAdmissionLevel.VerifiedEligible => "L1 推荐迁移",
        MigrationAdmissionLevel.VendorNativeRecommended => "L2 原生引导",
        _ => "L3 人工确认"
    };

    public string LevelBadgeForeground => AdmissionLevel switch
    {
        MigrationAdmissionLevel.HardDenied => "#C42B1C",
        MigrationAdmissionLevel.VerifiedEligible => "#0F7B4B",
        MigrationAdmissionLevel.VendorNativeRecommended => "#0078D4",
        _ => "#B45309"
    };

    public string LevelBadgeBackground => AdmissionLevel switch
    {
        MigrationAdmissionLevel.HardDenied => "#FEF2F2",
        MigrationAdmissionLevel.VerifiedEligible => "#ECFDF5",
        MigrationAdmissionLevel.VendorNativeRecommended => "#EBF5FF",
        _ => "#FFFBEB"
    };

    public string LevelBadgeBorder => AdmissionLevel switch
    {
        MigrationAdmissionLevel.HardDenied => "#FECACA",
        MigrationAdmissionLevel.VerifiedEligible => "#A7F3D0",
        MigrationAdmissionLevel.VendorNativeRecommended => "#BFDBFE",
        _ => "#FDE68A"
    };
}

/// <summary>
/// 批量迁移清单行视图模型（对齐 docs/untitled.pen 02 与 04 卡片展示）。
/// </summary>
public partial class BatchPlanRowItem : ObservableObject
{
    public int? NodeId { get; init; }
    public Guid? ScanId { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string? ApplicationId { get; init; }
    public string ApplicationLabel { get; init; } = string.Empty;
    public long LogicalBytes { get; init; }
    public long AllocatedBytes { get; init; }
    public bool IsCompressedOrSparse { get; init; }
    /// <summary>是否存在阻断迁移的外部或未验证硬链接。</summary>
    public bool HasSubtreeHardLinks { get; init; }
    /// <summary>是否包含可在目标盘无损重建的纯目录内硬链接。</summary>
    public bool HasInternalHardLinks { get; init; }
    public bool HasIncompleteScanData { get; init; }
    public MigrationAdmissionLevel Level { get; init; }
    public BatchPlanItemSource Source { get; init; } = BatchPlanItemSource.ScanDiscovery;

    [ObservableProperty]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private bool _l3RiskAcknowledged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueCardBackground))]
    [NotifyPropertyChangedFor(nameof(QueueCardBorder))]
    [NotifyPropertyChangedFor(nameof(QueueStatusForeground))]
    private BatchPlanItemStatus _executionStatus = BatchPlanItemStatus.Pending;

    [ObservableProperty]
    private string _statusText = "待预检";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockingReason))]
    [NotifyPropertyChangedFor(nameof(CardBorderBrush))]
    private string _blockingReason = string.Empty;

    public string DisplayTitle
    {
        get
        {
            string leaf = Path.GetFileName(SourcePath.TrimEnd('\\'));
            if (string.IsNullOrWhiteSpace(leaf)) leaf = SourcePath;
            return string.IsNullOrWhiteSpace(ApplicationLabel) ? leaf : $"{ApplicationLabel} / {leaf}";
        }
    }

    public string SizeText => FileStatsService.FormatBytes(Math.Max(AllocatedBytes, LogicalBytes));
    public bool HasBlockingReason => !string.IsNullOrWhiteSpace(BlockingReason);
    public bool IsL3Item => Level == MigrationAdmissionLevel.ManualRiskConfirm;
    public bool ShowL3SafeHint => IsL3Item && !HasBlockingReason;

    public string CardBorderBrush => HasBlockingReason ? "#B45309" : "#E2E8F0";

    public string LevelText => Level switch
    {
        MigrationAdmissionLevel.VerifiedEligible => "L1 推荐迁移",
        MigrationAdmissionLevel.ManualRiskConfirm => "L3 人工确认",
        MigrationAdmissionLevel.VendorNativeRecommended => "L2 原生引导",
        _ => "L0 系统保护"
    };

    public string LevelBadgeBg => Level == MigrationAdmissionLevel.VerifiedEligible ? "#ECFDF5" : "#FFFBEB";
    public string LevelBadgeBorder => Level == MigrationAdmissionLevel.VerifiedEligible ? "#A7F3D0" : "#FDE68A";
    public string LevelBadgeFg => Level == MigrationAdmissionLevel.VerifiedEligible ? "#0F7B4B" : "#B45309";

    public string QueueCardBackground => ExecutionStatus switch
    {
        BatchPlanItemStatus.CompletedAwaitingVerification => "#ECFDF5",
        BatchPlanItemStatus.Running => "#EBF5FF",
        BatchPlanItemStatus.Failed => "#FEF2F2",
        _ => "#F8FAFC"
    };

    public string QueueCardBorder => ExecutionStatus switch
    {
        BatchPlanItemStatus.CompletedAwaitingVerification => "#A7F3D0",
        BatchPlanItemStatus.Running => "#0078D4",
        BatchPlanItemStatus.Failed => "#FECACA",
        _ => "#E2E8F0"
    };

    public string QueueStatusForeground => ExecutionStatus switch
    {
        BatchPlanItemStatus.CompletedAwaitingVerification => "#0F7B4B",
        BatchPlanItemStatus.Running => "#0078D4",
        BatchPlanItemStatus.Failed => "#C42B1C",
        _ => "#64748B"
    };
}

public partial class DiskScanViewModel : ObservableObject
{
    private const int DirectoryPageSize = 200;

    private readonly DiskScanService _scanService = new();
    private readonly MigrationPlanService _planService;
    private readonly MigrationBatchService _batchService;
    private readonly ApplicationCatalog _catalog;
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

    public DiskScanViewModel()
    {
        ApplicationCatalog.RegisterWithSafetyPolicy();
        _catalog = ApplicationCatalog.Shared;
        _planService = new MigrationPlanService(_catalog);
        _batchService = new MigrationBatchService(_planService);

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

        LoadVolumes();
        UpdateTargetSpaceHint();
#if DEBUG
        LoadDevCachedRecents();
#endif
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

    [RelayCommand]
    private void LoadVolumes()
    {
        string? prevRoot = SelectedVolumeCard?.DriveRoot;
        VolumeCards.Clear();
        foreach (ScanVolumeInfo v in _scanService.ListVolumes())
        {
            VolumeCards.Add(new VolumeCardItem(v));
        }

        SelectedVolumeCard = VolumeCards.FirstOrDefault(v => v.DriveRoot.Equals(prevRoot, StringComparison.OrdinalIgnoreCase))
            ?? VolumeCards.FirstOrDefault();

        // 若默认目标根目录盘符不存在，自动推荐一个非系统盘
        var preferredTarget = VolumeCards.FirstOrDefault(v => v.IsRecommendedTarget)
            ?? VolumeCards.FirstOrDefault(v => !v.DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase));
        if (preferredTarget is not null && !Directory.Exists(Path.GetPathRoot(UnifiedTargetRoot) ?? string.Empty))
        {
            UnifiedTargetRoot = Path.Combine(preferredTarget.DriveRoot, "MigratedData");
        }
        UpdateTargetSpaceHint();
    }

    [RelayCommand]
    private async Task StartScanForVolumeAsync(VolumeCardItem? card)
    {
        VolumeCardItem? target = card ?? SelectedVolumeCard;
        if (IsScanning || target is null) return;

        SelectedVolumeCard = target;
        ActiveSubView = DiskScanSubView.VolumeSelectAndScan;
        IsScanning = true;
        target.IsCurrentlyScanning = true;
        ScanProgressPercent = 6;
        ScanStageBadgeText = $"正在扫描 · {target.DriveLetter}";
        ScanStageHeading = target.Info.SupportsRawMft
            ? "阶段 1 / 3：读取原始 NTFS MFT 主表"
            : "阶段 1 / 3：普通目录遍历（降级只读模式）";
        ScanProcessedRecordsText = "0 项";
        ScanLargeDirCountText = "统计中…";
        ScanL1SummaryText = "统计中…";
        ScanStep1State = 1; ScanStep1Status = "进行中…";
        ScanStep2State = 0; ScanStep2Status = "等待中";
        ScanStep3State = 0; ScanStep3Status = "等待中";

        _scanStopwatch.Restart();
        _scanElapsedTimer.Start();
        _scanCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.ProcessedRecords > 0)
                {
                    ScanProcessedRecordsText = $"{p.ProcessedRecords:N0} 项";
                }

                switch (p.Phase)
                {
                    case ScanPhase.ProbingVolume:
                    case ScanPhase.ReadingMft:
                        ScanStageHeading = p.Backend == ScanBackendKind.RawNtfsMft
                            ? "阶段 1 / 3：读取原始 MFT 主表"
                            : "阶段 1 / 3：只读目录遍历（降级）";
                        ScanStep1State = 1;
                        ScanStep1Status = p.Percent is double pct1 ? $"进行中 · {pct1:F0}%" : "读取中…";
                        ScanProgressPercent = p.Percent is double p1 ? Math.Clamp(p1 * 0.55, 5, 55) : Math.Min(50, ScanProgressPercent + 1);
                        break;

                    case ScanPhase.BuildingTree:
                    case ScanPhase.AggregatingSizes:
                        ScanStageHeading = "阶段 2 / 3：构建目录邻接树与汇总空间";
                        ScanStep1State = 2;
                        if (ScanStep1Status.StartsWith("进行中") || ScanStep1Status == "读取中…")
                            ScanStep1Status = $"已完成 · {_scanStopwatch.Elapsed.TotalSeconds:F1} s";
                        ScanStep2State = 1;
                        ScanStep2Status = "聚合计算中…";
                        ScanProgressPercent = 72;
                        break;

                    case ScanPhase.IdentifyingApplications:
                        ScanStageHeading = "阶段 3 / 3：匹配软件归属与 4 级准入";
                        ScanStep1State = 2;
                        ScanStep2State = 2; ScanStep2Status = "已完成";
                        ScanStep3State = 1; ScanStep3Status = "匹配规则中…";
                        ScanProgressPercent = 90;
                        break;
                }
            });

            ScanSnapshot snapshot = await _scanService.ScanVolumeAsync(
                target.DriveRoot,
                allowFallbackToDirectoryWalk: true,
                progress,
                _scanCts.Token);

            _scanStopwatch.Stop();
            _scanElapsedTimer.Stop();
            double elapsedSec = Math.Max(0.1, (snapshot.CompletedAtUtc - snapshot.StartedAtUtc).TotalSeconds);

            // 后台汇总 >1GB 大目录与 L1 软件统计
            var (largeCount, l1Count, l1Bytes) = await Task.Run(() => SummarizeSnapshotHighlights(snapshot));

            ScanProgressPercent = 100;
            ScanStep1State = 2; ScanStep1Status = "已完成";
            ScanStep2State = 2; ScanStep2Status = "已完成";
            ScanStep3State = 2; ScanStep3Status = $"已完成 · {elapsedSec:F1} s";
            ScanProcessedRecordsText = $"{snapshot.Nodes.Length:N0} 项";
            ScanLargeDirCountText = $"{largeCount:N0} 个";
            ScanL1SummaryText = l1Count > 0 ? $"{l1Count} 项 · {FileStatsService.FormatBytes(l1Bytes)}" : "0 项";
            ScanElapsedText = $"耗时 {elapsedSec:F1} s";
            ScanStageBadgeText = $"扫描完成 · {target.DriveLetter}";
            ScanStageHeading = $"扫描完成，共解析 {snapshot.Nodes.Length:N0} 个对象";

            ApplySnapshot(snapshot, target.DisplayTitle, elapsedSec);

            // 记录到最近扫描历史
            var existingIdx = RecentScans.ToList().FindIndex(r => r.VolumeTitle == target.DisplayTitle);
            if (existingIdx >= 0) RecentScans.RemoveAt(existingIdx);
            RecentScans.Insert(0, new RecentScanItem(
                snapshot.ScanId,
                target.DisplayTitle,
                DateTime.Now.ToString("HH:mm"),
                $"已用 {FileStatsService.FormatBytes(snapshot.Volume.UsedBytes)} · 发现 {largeCount} 个 >1 GB 大目录",
                snapshot));
            if (RecentScans.Count > 5) RecentScans.RemoveAt(RecentScans.Count - 1);

#if DEBUG
            if (!MigrationRuntime.IsTestLab && snapshot.CanUseForMigrationPlanning)
            {
                string volTitleForCache = target.DisplayTitle;
                _ = Task.Run(() =>
                {
                    try
                    {
                        ScanSnapshotDevCache.Save(snapshot, volTitleForCache, largeCount, l1Count, l1Bytes);
                        Application.Current?.Dispatcher.BeginInvoke(() => HasDevCache = true);
                    }
                    catch { }
                });
            }
#endif

            // 扫描完成后自动进入阶段 01 目录树（严格对齐 docs/untitled.pen 00 脚栏说明）
            ActiveSubView = DiskScanSubView.DirectoryTree;
        }
        catch (OperationCanceledException)
        {
            _scanStopwatch.Stop();
            _scanElapsedTimer.Stop();
            ScanStageBadgeText = "已取消";
            ScanStageHeading = "扫描已安全取消，未修改任何卷数据";
            ScanStep1Status = "已取消";
        }
        catch (Exception ex)
        {
            _scanStopwatch.Stop();
            _scanElapsedTimer.Stop();
            ScanStageBadgeText = "扫描失败";
            ScanStageHeading = ex.Message;
            CustomMessageBox.ShowError($"磁盘扫描失败：\n{ex.Message}", "扫描错误");
        }
        finally
        {
            IsScanning = false;
            target.IsCurrentlyScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private Task StartScanAsync() => StartScanForVolumeAsync(SelectedVolumeCard);

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
        ScanStageHeading = "正在安全取消扫描…";
    }

    [RelayCommand]
    private async Task OpenRecentScanAsync(RecentScanItem? recent)
    {
        if (recent is null || IsScanning) return;

        if (recent.Snapshot is not null)
        {
            double sec = Math.Max(0.1, (recent.Snapshot.CompletedAtUtc - recent.Snapshot.StartedAtUtc).TotalSeconds);
            ApplySnapshot(recent.Snapshot, recent.VolumeTitle, sec);
            ActiveSubView = DiskScanSubView.DirectoryTree;
            return;
        }

        if (!string.IsNullOrEmpty(recent.DevCacheFilePath))
        {
            IsScanning = true;
            ScanProgressPercent = 45;
            ScanStageBadgeText = "开发缓存加载";
            ScanStageHeading = $"正在从本地开发缓存秒级恢复 {recent.VolumeTitle} 快照…";
            var sw = Stopwatch.StartNew();
            try
            {
                var (loaded, header) = await Task.Run(() =>
                {
                    ScanSnapshot snap = ScanSnapshotDevCache.Load(recent.DevCacheFilePath, out DevScanCacheHeader h);
                    return (snap, h);
                });
                sw.Stop();

                // 用内存中的已加载快照替换条目，本会话再次切回零等待
                int idx = RecentScans.IndexOf(recent);
                if (idx >= 0)
                {
                    RecentScans[idx] = recent with { Snapshot = loaded };
                }

                ScanProgressPercent = 100;
                ScanElapsedText = $"缓存加载 {sw.Elapsed.TotalSeconds:F1} s";
                ScanProcessedRecordsText = $"{loaded.Nodes.Length:N0} 项";
                ScanLargeDirCountText = $"{header.LargeDirCount:N0} 个";
                ScanL1SummaryText = header.L1Count > 0 ? $"{header.L1Count} 项 · {FileStatsService.FormatBytes(header.L1Bytes)}" : "0 项";
                ScanStageBadgeText = "已加载开发缓存";
                ScanStageHeading = $"已从开发缓存恢复 {loaded.Nodes.Length:N0} 个对象（执行迁移前仍实时复核）";
                ScanStep1State = 2; ScanStep1Status = "缓存命中";
                ScanStep2State = 2; ScanStep2Status = "缓存命中";
                ScanStep3State = 2; ScanStep3Status = $"耗时 {sw.Elapsed.TotalSeconds:F1} s";

                ApplySnapshot(loaded, recent.VolumeTitle, sw.Elapsed.TotalSeconds);
                SnapshotModeBadge = $"开发缓存 · {sw.Elapsed.TotalSeconds:F1} s";
                UsnStatusNote = "当前展示为开发模式临时缓存快照；点击「重新扫描」可实时重读 MFT，执行迁移前仍强制逐项实时核验。";
                ActiveSubView = DiskScanSubView.DirectoryTree;
            }
            catch (Exception ex)
            {
                ScanStageBadgeText = "缓存失效";
                ScanStageHeading = $"开发缓存读取失败（已忽略，可重新扫描）：{ex.Message}";
            }
            finally
            {
                IsScanning = false;
            }
        }
    }

    [RelayCommand]
    private void ClearDevCache()
    {
        ScanSnapshotDevCache.ClearAll();
        HasDevCache = false;
        for (int i = RecentScans.Count - 1; i >= 0; i--)
        {
            if (RecentScans[i].IsDevCached)
            {
                RecentScans.RemoveAt(i);
            }
        }
    }

#if DEBUG
    private void LoadDevCachedRecents()
    {
        if (MigrationRuntime.IsTestLab) return;
        try
        {
            var headers = ScanSnapshotDevCache.ListValidHeaders(MigrationSafetyPolicy.CurrentPolicyVersion, TimeSpan.FromHours(24));
            foreach (DevScanCacheHeader h in headers)
            {
                RecentScans.Add(new RecentScanItem(
                    h.ScanId,
                    h.VolumeTitle,
                    $"开发缓存 · {h.CompletedAtUtc.ToLocalTime():HH:mm}",
                    $"{h.NodeCount:N0} 项 · 发现 {h.LargeDirCount} 个 >1 GB 大目录（点击秒开）",
                    Snapshot: null,
                    DevCacheFilePath: h.FilePath));
            }
            HasDevCache = headers.Count > 0;
        }
        catch { }
    }
#endif

    [RelayCommand]
    private void GoToVolumeSelect()
    {
        if (IsBatchExecuting) return;
        ActiveSubView = DiskScanSubView.VolumeSelectAndScan;
    }

    [RelayCommand]
    private void GoToDirectoryTree()
    {
        if (!HasSnapshot || IsBatchExecuting) return;
        ActiveSubView = DiskScanSubView.DirectoryTree;
    }

    [RelayCommand]
    private async Task GoToMigrationQueueAsync()
    {
        if (IsBatchExecuting) return;
        ActiveSubView = DiskScanSubView.MigrationQueue;
        if (PlanItems.Count > 0)
        {
            await FreezeAndPreflightAsync();
        }
    }

    [RelayCommand]
    private void OpenBackupCenter()
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
        {
            main.ShowBackupCenterCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void SwitchToTreeView() => BrowseMode = ScanBrowseMode.DirectoryTree;

    [RelayCommand]
    private void SwitchToHotspotView() => BrowseMode = _lastHotspotMode;

    [RelayCommand]
    private void SwitchToHotspotFlat()
    {
        _lastHotspotMode = ScanBrowseMode.HotspotFlatList;
        BrowseMode = ScanBrowseMode.HotspotFlatList;
    }

    [RelayCommand]
    private void SwitchToHotspotPathTree()
    {
        if (BrowseMode == ScanBrowseMode.HotspotPathTree)
        {
            // 已在“按路径”模式下再次点击：切换一键展开 / 收起
            IsHotspotPathTreeExpanded = !IsHotspotPathTreeExpanded;
            RebuildVisibleRows();
            return;
        }

        _lastHotspotMode = ScanBrowseMode.HotspotPathTree;
        BrowseMode = ScanBrowseMode.HotspotPathTree;
    }

    [RelayCommand]
    private void SetFastVerification() => UseContentHash = false;

    [RelayCommand]
    private void SetHashVerification() => UseContentHash = true;

    [RelayCommand]
    private void ToggleDetailedLogs() => ShowDetailedLogs = !ShowDetailedLogs;

    [RelayCommand]
    private void ToggleExpand(ScanTreeRowItem? row)
    {
        if (row is null || _snapshot is null) return;

        if (row.IsLoadMorePlaceholder)
        {
            LoadMoreChildren(row);
            return;
        }

        if (!row.HasChildren) return;

        int index = VisibleTreeRows.IndexOf(row);
        if (index < 0) return;

        if (row.IsExpanded)
        {
            int removeIdx = index + 1;
            while (removeIdx < VisibleTreeRows.Count && VisibleTreeRows[removeIdx].Depth > row.Depth)
            {
                VisibleTreeRows.RemoveAt(removeIdx);
            }
            row.IsExpanded = false;
        }
        else
        {
            row.IsExpanded = true;
            if (BrowseMode == ScanBrowseMode.HotspotPathTree
                && _hotspotPathAncestorNodes is not null
                && _hotspotPathKeptNodes is not null
                && _hotspotPathAncestorNodes.Contains(row.NodeId))
            {
                // 按路径模式下手动展开祖先容器时，仅展开通向大软件的剪枝子目录（保持全盘统一标尺）
                long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);
                ReadOnlySpan<int> children = _snapshot.GetChildrenSpan(row.NodeId);
                int cursor = index + 1;
                for (int i = 0; i < children.Length; i++)
                {
                    int cid = children[i];
                    if (!_hotspotPathKeptNodes.Contains(cid)) continue;
                    VisibleTreeRows.Insert(cursor++, BuildRowItem(cid, row.Depth + 1, rootTotal));
                }
            }
            else
            {
                InsertChildRows(row.NodeId, row.Depth + 1, index + 1, startOffset: 0);
            }
        }
    }

    [RelayCommand]
    private void ToggleRowInPlan(ScanTreeRowItem? row)
    {
        if (row is null || _snapshot is null || !row.CanToggleInPlan) return;

        SelectedRow = row;
        var existing = PlanItems.FirstOrDefault(p => p.SourcePath.Equals(row.FullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!IsBatchExecuting)
            {
                PlanItems.Remove(existing);
                InvalidateFrozenPlan();
                UpdateCanAddSelected();
            }
            return;
        }

        // L3 目录需要用户在右侧详情确认风险勾选框，避免行内误点绕过 L3 风险告知
        if (SelectedDecision?.Level == MigrationAdmissionLevel.ManualRiskConfirm && !IsL3RiskConfirmedForSelection)
        {
            CustomMessageBox.ShowInformation(
                "该目录属于 L3（未收录或普通个人数据），请先在右侧详情面板核对路径并勾选风险确认框后，再点击「加入待迁移清单」。",
                "需要人工风险确认");
            return;
        }

        AddSelectedToPlan();
    }

    [RelayCommand]
    private void CopyNativeGuidance()
    {
        if (string.IsNullOrWhiteSpace(SelectedNativeGuidance)) return;
        try
        {
            Clipboard.SetText(SelectedNativeGuidance);
            CustomMessageBox.ShowSuccess("官方原生迁移步骤已复制到剪贴板。", "已复制");
        }
        catch { }
    }

    [RelayCommand]
    private void ShowNativeGuidanceModal()
    {
        if (SelectedRow is null || string.IsNullOrWhiteSpace(SelectedNativeGuidance)) return;
        CustomMessageBox.ShowInformation(
            $"目录：{SelectedRow.FullPath}\n\n" +
            $"为何不建议直接建符号链接？\n{SelectedDecision?.Summary}\n\n" +
            $"官方原生迁移指引：\n{SelectedNativeGuidance}",
            "L2 厂商原生迁移指引");
    }

    [RelayCommand]
    private void BrowseUnifiedTarget()
    {
        var picker = new FolderPickerWindow { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedPath))
        {
            UnifiedTargetRoot = picker.SelectedPath;
        }
    }

    [RelayCommand]
    private void EditPlanItemTarget(BatchPlanRowItem? item)
    {
        if (item is null || IsBatchExecuting) return;
        var picker = new FolderPickerWindow { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedPath))
        {
            item.TargetPath = picker.SelectedPath;
            InvalidateFrozenPlan();
            UpdateTargetSpaceHint();
        }
    }

    [RelayCommand]
    private void AddSelectedToPlan()
    {
        if (_snapshot is null || SelectedRow is null || SelectedDecision is null)
            return;

        if (IsSelectedAlreadyInPlan)
        {
            var existing = PlanItems.FirstOrDefault(p => p.SourcePath.Equals(SelectedRow.FullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !IsBatchExecuting)
            {
                PlanItems.Remove(existing);
                InvalidateFrozenPlan();
                UpdateCanAddSelected();
            }
            return;
        }

        if (!CanAddSelectedToPlan) return;

        if (!_snapshot.CanUseForMigrationPlanning)
        {
            CustomMessageBox.ShowWarning("当前扫描快照处于已取消或访问受限状态，不能直接用于生成迁移清单，请先完成完整扫描。", "无法加入清单");
            return;
        }

        SubtreeHazardReport hazards = _snapshot.AnalyzeSubtreeHazards(SelectedRow.NodeId, maxExternalSamples: 2);
        if (hazards.HasBlockingHardLinks)
        {
            string peerInfo = hazards.SampleExternalPeerPaths.Length > 0
                ? $"\n\n外部关联路径示例：\n• {string.Join("\n• ", hazards.SampleExternalPeerPaths)}"
                : string.Empty;
            string detail = hazards.HardLinkKind == SubtreeHardLinkKind.ExternalLinked
                ? $"该目录包含 {hazards.ExternalHardLinkFileCount} 个指向目录外部的硬链接文件；跨卷迁移会断开共享语义且无法释放源盘对应空间，禁止直接迁移。建议改用软件原生路径设置。{peerInfo}"
                : "该目录包含未能验证归属闭环的硬链接文件，为防止跨卷复制破坏共享语义，不允许加入迁移清单。";
            CustomMessageBox.ShowWarning(detail, "跨外部硬链接拦截");
            return;
        }
        if (hazards.HasIncompleteNodes)
        {
            CustomMessageBox.ShowWarning("该目录子树在扫描中存在无法读取或损坏节点，禁止加入批量清单。", "扫描不完整拦截");
            return;
        }

        string targetPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(UnifiedTargetRoot))
        {
            try
            {
                targetPath = MigrationPlanService.BuildDefaultTargetPath(
                    SelectedRow.FullPath,
                    UnifiedTargetRoot,
                    SelectedDecision.MatchedApplicationId);
            }
            catch { }
        }

        PlanItems.Add(new BatchPlanRowItem
        {
            NodeId = SelectedRow.NodeId,
            ScanId = _snapshot.ScanId,
            SourcePath = SelectedRow.FullPath,
            TargetPath = targetPath,
            ApplicationId = SelectedDecision.MatchedApplicationId,
            ApplicationLabel = SelectedDecision.MatchedApplicationName ?? SelectedRow.AttributionLabel,
            LogicalBytes = SelectedRow.LogicalBytes,
            AllocatedBytes = SelectedRow.UniqueAllocatedBytes,
            IsCompressedOrSparse = (SelectedRow.Flags & (ScanNodeFlags.IsSparseOrCompressed | ScanNodeFlags.IsWofCompressed)) != 0,
            HasSubtreeHardLinks = hazards.HasBlockingHardLinks,
            HasInternalHardLinks = hazards.HasInternalOnlyHardLinks,
            HasIncompleteScanData = hazards.HasIncompleteNodes,
            Level = SelectedDecision.Level,
            L3RiskAcknowledged = SelectedDecision.Level == MigrationAdmissionLevel.ManualRiskConfirm && IsL3RiskConfirmedForSelection,
            StatusText = hazards.HasInternalOnlyHardLinks ? "待预检（无损重建内部硬链接）" : "待预检"
        });

        InvalidateFrozenPlan();
        UpdateCanAddSelected();
    }

    [RelayCommand]
    private void RemovePlanItem(BatchPlanRowItem? item)
    {
        if (item is null || IsBatchExecuting) return;
        PlanItems.Remove(item);
        InvalidateFrozenPlan();
        UpdateCanAddSelected();
    }

    [RelayCommand]
    private void ClearPlan()
    {
        if (IsBatchExecuting) return;
        PlanItems.Clear();
        InvalidateFrozenPlan();
        UpdateCanAddSelected();
    }

    [RelayCommand]
    private void OpenSelectedInExplorer()
    {
        if (SelectedRow is null || !Directory.Exists(SelectedRow.FullPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SelectedRow.FullPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task FreezeAndPreflightAsync()
    {
        if (IsBatchExecuting) return;
        if (PlanItems.Count == 0)
        {
            PreflightSummary = "迁移清单为空，请先返回目录树勾选允许迁移的目录。";
            HasPreflightBlocking = false;
            CanStartBatch = false;
            return;
        }

        var drafts = PlanItems.Select(p => new MigrationPlanDraftItem(
            SourcePath: p.SourcePath,
            TargetPath: string.IsNullOrWhiteSpace(p.TargetPath) ? null : p.TargetPath,
            LogicalBytes: p.LogicalBytes,
            AllocatedBytes: p.AllocatedBytes,
            IsCompressedOrSparse: p.IsCompressedOrSparse,
            HasSubtreeHardLinks: p.HasSubtreeHardLinks,
            HasIncompleteScanData: p.HasIncompleteScanData,
            L3RiskAcknowledged: p.L3RiskAcknowledged,
            ScanId: p.ScanId,
            NodeId: p.NodeId,
            Source: p.Source,
            HasInternalHardLinks: p.HasInternalHardLinks)).ToArray();

        MigrationTransactionRecord[] existing = [];
        try { existing = await new MigrationTransactionStore().ReadAllAsync(); }
        catch { }

        VerificationMode mode = UseContentHash ? VerificationMode.ContentHash : VerificationMode.Basic;
        FrozenMigrationPlan frozen = _planService.FreezeAndValidatePlan(
            drafts,
            mode,
            UnifiedTargetRoot,
            checkFilesystem: true,
            existingTransactions: existing);

        BatchPreflightReport report = _planService.RunReadOnlyProcessPreflight(frozen);
        _frozenPlan = frozen;

        int blockedCount = 0;
        for (int i = 0; i < PlanItems.Count; i++)
        {
            var matched = frozen.Items.FirstOrDefault(x => x.SourcePath.Equals(PlanItems[i].SourcePath, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                PlanItems[i].TargetPath = matched.TargetPath;
                PlanItems[i].ExecutionStatus = matched.Status;
                PlanItems[i].StatusText = matched.Status == BatchPlanItemStatus.Ready ? "预检通过" : "存在阻断";
                PlanItems[i].BlockingReason = matched.BlockingReason ?? string.Empty;
                if (matched.Status != BatchPlanItemStatus.Ready) blockedCount++;
            }
        }

        UpdateTargetSpaceHint();
        CanStartBatch = report.CanStart;
        HasPreflightBlocking = !report.CanStart;
        long totalBytes = PlanItems.Sum(p => Math.Max(p.AllocatedBytes, p.LogicalBytes));
        PreflightSummary = report.CanStart
            ? $"全部 {frozen.Items.Length} 项（共 {FileStatsService.FormatBytes(totalBytes)}）已通过预检（核对模式：{(UseContentHash ? "完整 SHA-256" : "快速核对")}），可开始串行迁移。"
            : $"{Math.Max(1, blockedCount)} 项存在预检阻断（共 {frozen.Items.Length} 项 · {FileStatsService.FormatBytes(totalBytes)}）：{string.Join("；", report.BlockingIssues.Take(2))}";
    }

    [RelayCommand]
    private async Task ExecuteBatchAsync()
    {
        if (IsBatchExecuting || _frozenPlan is null || !CanStartBatch) return;

        bool confirmed = CustomMessageBox.ShowQuestion(
            $"确认按冻结计划 #{_frozenPlan.PlanRevision} 串行迁移 {_frozenPlan.Items.Length} 个目录吗？\n\n" +
            $"• 核对模式：{(UseContentHash ? "完整内容 SHA-256 哈希核对（较耗时）" : "默认快速核对（相对路径 + 大小 + 元数据）")}\n" +
            "• 每项迁移都会保留原盘备份，不会自动删除原件；\n" +
            "• 任一单项失败会立即暂停后续队列并保留现场证据。",
            "确认开始批量安全迁移");

        if (!confirmed) return;

        ActiveSubView = DiskScanSubView.BatchExecution;
        IsBatchExecuting = true;
        IsBatchFinished = false;
        ExecutionHeaderTitle = "正在执行批量安全迁移";
        ExecutionHeaderSubtitle = $"串行复用手动迁移底座（{ActiveVerifyModeText}）；完成后原目录将在源盘改名保留，绝不自动删除。";
        BatchLogs.Clear();

        foreach (var row in PlanItems)
        {
            row.ExecutionStatus = BatchPlanItemStatus.Ready;
            row.StatusText = "排队等待中";
            row.BlockingReason = string.Empty;
        }

        _batchCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<BatchItemProgress>(p =>
            {
                ExecutionQueueBadgeText = $"{p.ItemIndex + 1} / {p.TotalItems} 进行中";
                var row = PlanItems.FirstOrDefault(r => r.SourcePath.Equals(p.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.ExecutionStatus = p.ItemStatus;
                    ActiveItemTitle = row.DisplayTitle;
                    ActiveItemSourcePath = row.SourcePath;
                    ActiveItemTargetPath = row.TargetPath;

                    double pct = p.InnerProgress?.PercentComplete
                        ?? (p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification ? 100 : 0);
                    ActiveItemPercent = Math.Clamp(pct, 0, 100);
                    ActivePercentText = $"{ActiveItemPercent:F0}%";

                    row.StatusText = p.ItemStatus switch
                    {
                        BatchPlanItemStatus.Running => $"正在迁移 · {ActiveItemPercent:F0}%",
                        BatchPlanItemStatus.CompletedAwaitingVerification => "已完成 · 待用户验证",
                        BatchPlanItemStatus.Failed => "失败（队列已暂停）",
                        BatchPlanItemStatus.Cancelled => "已取消",
                        _ => "排队等待中"
                    };
                }

                UpdateExecutionMetricsAndSteps(p);
            });

            var logProgress = new Progress<string>(msg =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() => BatchLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}"));
            });

            BatchMigrationSummary summary = await _batchService.ExecuteBatchAsync(
                _frozenPlan,
                robocopyThreads: 8,
                largeFileThresholdMB: 1024,
                progress,
                logProgress,
                _batchCts.Token);

            foreach (MigrationPlanItem finalItem in summary.FinalItems)
            {
                var row = PlanItems.FirstOrDefault(r => r.SourcePath.Equals(finalItem.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.ExecutionStatus = finalItem.Status;
                    row.BlockingReason = finalItem.BlockingReason ?? string.Empty;
                    row.StatusText = finalItem.Status switch
                    {
                        BatchPlanItemStatus.CompletedAwaitingVerification => "已完成 · 待用户验证",
                        BatchPlanItemStatus.Failed => "失败（队列已暂停）",
                        BatchPlanItemStatus.Cancelled => "已取消",
                        _ => row.StatusText
                    };
                }
            }

            IsBatchFinished = true;
            ExecutionQueueBadgeText = $"{summary.SucceededCount} / {summary.FinalItems.Length} 已完成";
            ExecutionHeaderTitle = summary.PausedDueToFailure ? "批量迁移已暂停（检测到失败项）" : "批量安全迁移已完成（待用户验证）";
            ExecutionHeaderSubtitle = summary.PausedDueToFailure
                ? (summary.PauseReason ?? "单项失败已自动暂停后续队列并保留现场证据。")
                : $"已完成 {summary.SucceededCount} 项技术迁移，全部原始备份已保留在源盘；请验证软件正常后前往「备份管理」处理。";

            if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
            {
                await main.RefreshGlobalStatusAsync();
            }

            if (summary.PausedDueToFailure)
            {
                CustomMessageBox.ShowWarning(
                    $"{summary.PauseReason}\n\n已成功的 {summary.SucceededCount} 项原始备份均已保留，请前往顶栏「备份管理」验证或核查。",
                    "批量队列已暂停");
            }
            else if (summary.SucceededCount > 0)
            {
                CustomMessageBox.ShowSuccess(
                    $"已完成 {summary.SucceededCount} 项技术迁移！\n\n原始备份均已保留在源盘（不会释放原盘空间，也不会自动删除）。\n请先启动软件验证功能正常，再前往顶栏「备份管理」经两次确认后处理备份。",
                    "批量迁移完成（待用户验证）");
            }
        }
        catch (Exception ex)
        {
            IsBatchFinished = true;
            ExecutionHeaderTitle = "批量迁移异常中止";
            ExecutionHeaderSubtitle = ex.Message;
            CustomMessageBox.ShowError($"批量迁移失败：\n{ex.Message}", "执行错误");
        }
        finally
        {
            IsBatchExecuting = false;
            InvalidateFrozenPlan();
            _batchCts?.Dispose();
            _batchCts = null;
        }
    }

    [RelayCommand]
    private void CancelBatch()
    {
        _batchCts?.Cancel();
        ActiveStageBadgeText = "正在安全停止…";
    }

    private void ApplySnapshot(ScanSnapshot snapshot, string volumeTitle, double elapsedSec)
    {
        _snapshot = snapshot;
        _hotspotPathKeptNodes = null;
        _hotspotPathAncestorNodes = null;
        IsHotspotPathTreeExpanded = false;
        HasSnapshot = true;
        SnapshotVolumeTitle = volumeTitle;
        IsSnapshotMftBackend = snapshot.Backend == ScanBackendKind.RawNtfsMft;
        SnapshotModeBadge = IsSnapshotMftBackend
            ? $"MFT · {elapsedSec:F1} s"
            : $"目录遍历降级 · {elapsedSec:F1} s";
        SnapshotVolumeSummary = $"已用 {FileStatsService.FormatBytes(snapshot.Volume.UsedBytes)} / {FileStatsService.FormatBytes(snapshot.Volume.TotalBytes)} · 剩余 {FileStatsService.FormatBytes(snapshot.Volume.FreeBytes)}";
        UsnStatusNote = snapshot.Backend == ScanBackendKind.DirectoryWalkFallback
            ? (snapshot.FallbackReason ?? "降级遍历模式，执行迁移前将逐项实时核验")
            : snapshot.RequiresLiveReverification
                ? "活跃卷或 USN 水位变化，执行前将强制实时复核"
                : $"USN 水位稳定（{snapshot.UsnStartNextUsn}）";
        BreadcrumbText = $"此电脑  ›  {snapshot.Volume.DriveRoot.TrimEnd('\\')}";

        SelectedRow = null;
        RebuildVisibleRows();
        SnapshotChanged?.Invoke(this, snapshot);
    }

    [RelayCommand]
    private void ToggleAiDrawer()
    {
        IsAiDrawerOpen = !IsAiDrawerOpen;
    }

    /// <summary>
    /// 接收用户人工审核并采纳的 AI 迁移建议（仅加入待迁移清单，绝不自动启动执行，仍须全量策略与子树复核）。
    /// </summary>
    public (bool Added, string Message) TryAddAiProposalToPlan(Guid scanId, int nodeId, string? suggestedTargetPath)
    {
        if (IsBatchExecuting)
        {
            return (false, "当前正在执行批量迁移，请等待完成后再修改清单。");
        }

        if (_snapshot is null || _snapshot.ScanId != scanId)
        {
            return (false, "当前磁盘扫描快照已变更或已释放，请重新扫描后再审阅 AI 建议。");
        }

        if (!_snapshot.CanUseForMigrationPlanning)
        {
            return (false, "当前扫描快照处于已取消或访问受限状态，不能用于生成迁移清单。");
        }

        if ((uint)nodeId >= (uint)_snapshot.Nodes.Length || !_snapshot.Nodes[nodeId].IsDirectory)
        {
            return (false, "建议指向的目录节点在当前快照中无效。");
        }

        ref readonly ScanNodeRecord node = ref _snapshot.Nodes[nodeId];
        string fullPath = _snapshot.GetFullPath(nodeId);

        if (PlanItems.Any(p => p.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return (true, $"「{fullPath}」已在待迁移清单中。");
        }

        SafetyPolicyDecision decision = MigrationSafetyPolicy.EvaluateSource(
            fullPath, _catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false);

        if (decision.IsHardDenied)
        {
            return (false, $"安全策略硬拦截（L0）：{decision.Summary}");
        }

        if (decision.Level == MigrationAdmissionLevel.VendorNativeRecommended)
        {
            return (false, "该目录属于 L2 厂商原生引导项，不建议符号链接迁移，请使用官方原生方案。");
        }

        SubtreeHazardReport hazards = _snapshot.AnalyzeSubtreeHazards(nodeId, maxExternalSamples: 2);
        if (hazards.HasBlockingHardLinks)
        {
            string peerHint = hazards.SampleExternalPeerPaths.Length > 0
                ? $"（外部关联：{string.Join("；", hazards.SampleExternalPeerPaths)}）"
                : string.Empty;
            return (false, $"该目录子树包含指向目录外部的硬链接文件{peerHint}，为防止破坏共享语义与释放空间落空，禁止加入迁移清单。");
        }
        if (hazards.HasIncompleteNodes)
        {
            return (false, "该目录子树扫描不完整或存在损坏节点，禁止加入迁移清单。");
        }

        string targetPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(suggestedTargetPath))
        {
            targetPath = suggestedTargetPath;
        }
        else if (!string.IsNullOrWhiteSpace(UnifiedTargetRoot))
        {
            try
            {
                targetPath = MigrationPlanService.BuildDefaultTargetPath(
                    fullPath, UnifiedTargetRoot, decision.MatchedApplicationId);
            }
            catch { }
        }

        PlanItems.Add(new BatchPlanRowItem
        {
            NodeId = nodeId,
            ScanId = _snapshot.ScanId,
            SourcePath = fullPath,
            TargetPath = targetPath,
            ApplicationId = decision.MatchedApplicationId,
            ApplicationLabel = decision.MatchedApplicationName ?? string.Empty,
            LogicalBytes = node.LogicalBytes,
            AllocatedBytes = node.UniqueAllocatedBytes,
            IsCompressedOrSparse = (node.Flags & (ScanNodeFlags.IsSparseOrCompressed | ScanNodeFlags.IsWofCompressed)) != 0,
            HasSubtreeHardLinks = hazards.HasBlockingHardLinks,
            HasInternalHardLinks = hazards.HasInternalOnlyHardLinks,
            HasIncompleteScanData = hazards.HasIncompleteNodes,
            Level = decision.Level,
            Source = BatchPlanItemSource.AiProposal,
            // L3 项严禁替用户自动勾选确认，必须由用户在清单页人工核对后勾选
            L3RiskAcknowledged = false,
            StatusText = decision.Level == MigrationAdmissionLevel.ManualRiskConfirm
                ? (hazards.HasInternalOnlyHardLinks ? "待预检（AI 建议 · 内部硬链接无损重建 · 需人工确认 L3）" : "待预检（AI 建议 · 需人工确认 L3）")
                : (hazards.HasInternalOnlyHardLinks ? "待预检（AI 建议 · 内部硬链接无损重建）" : "待预检（AI 建议）")
        });

        foreach (ScanTreeRowItem row in VisibleTreeRows)
        {
            if (row.NodeId == nodeId)
            {
                row.IsInPlan = true;
            }
        }

        InvalidateFrozenPlan();
        UpdateCanAddSelected();

        return (true, $"已将「{Path.GetFileName(fullPath.TrimEnd('\\'))}」加入待迁移清单（未执行，请前往清单页核对目标并手动启动）。");
    }

    /// <summary>
    /// 在目录树中自动展开祖先路径并选中指定节点（供 AI 诊断卡片一键核实）。
    /// </summary>
    public bool TryLocateNodeInTree(Guid scanId, int nodeId)
    {
        if (_snapshot is null || _snapshot.ScanId != scanId) return false;
        if ((uint)nodeId >= (uint)_snapshot.Nodes.Length) return false;

        ActiveSubView = DiskScanSubView.DirectoryTree;
        IsAiDrawerOpen = false;
        if (!string.IsNullOrEmpty(SearchFilter))
        {
            SearchFilter = string.Empty;
        }
        if (BrowseMode != ScanBrowseMode.DirectoryTree)
        {
            BrowseMode = ScanBrowseMode.DirectoryTree;
        }
        else
        {
            RebuildVisibleRows();
        }

        var ancestorChain = new Stack<int>();
        int cur = nodeId;
        int guard = 0;
        while (cur >= 0 && cur != _snapshot.RootNodeId && guard++ < 64)
        {
            ancestorChain.Push(cur);
            int parent = _snapshot.Nodes[cur].ParentNodeId;
            if (parent == cur) break;
            cur = parent;
        }

        ScanTreeRowItem? located = null;
        while (ancestorChain.Count > 0)
        {
            int nextId = ancestorChain.Pop();
            ScanTreeRowItem? row = VisibleTreeRows.FirstOrDefault(r => !r.IsLoadMorePlaceholder && r.NodeId == nextId);
            if (row is null) break;

            located = row;
            if (ancestorChain.Count > 0 && !row.IsExpanded && row.HasChildren)
            {
                ToggleExpand(row);
            }
        }

        if (located is not null)
        {
            SelectedRow = located;
            return true;
        }

        return false;
    }

    private (int LargeDirCount, int L1Count, long L1Bytes) SummarizeSnapshotHighlights(ScanSnapshot snapshot)
    {
        const long OneGb = 1024L * 1024 * 1024;
        int l1Count = 0;
        long l1Bytes = 0;

        int[] hotspots = CollectDisjointHotspots(snapshot, minAllocatedBytes: OneGb, maxCount: 120);
        int largeDirs = hotspots.Length;
        foreach (int nodeId in hotspots)
        {
            string fullPath = snapshot.GetFullPath(nodeId);
            SafetyPolicyDecision d = MigrationSafetyPolicy.EvaluateSource(fullPath, _catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false);
            if (d.Level == MigrationAdmissionLevel.VerifiedEligible)
            {
                l1Count++;
                l1Bytes += snapshot.Nodes[nodeId].UniqueAllocatedBytes;
            }
        }
        return (largeDirs, l1Count, l1Bytes);
    }

    /// <summary>
    /// 采集互不嵌套的大软件/热点目录（穿透 L0 容器目录与单子项包装层，保证任意两项无父子包含关系，绝不重复计算空间）。
    /// </summary>
    private int[] CollectDisjointHotspots(ScanSnapshot snapshot, long minAllocatedBytes, int maxCount)
    {
        var results = new List<(int NodeId, long Size)>();

        void Visit(int nodeId, int depth)
        {
            ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
            for (int i = 0; i < children.Length; i++)
            {
                int childId = children[i];
                ref readonly ScanNodeRecord child = ref snapshot.Nodes[childId];
                if (!child.IsDirectory || child.IsOrphaned) continue;

                long size = child.UniqueAllocatedBytes;
                if (size < minAllocatedBytes) continue;

                string fullPath = snapshot.GetFullPath(childId);
                string name = snapshot.GetNodeName(childId);
                SafetyPolicyDecision decision = MigrationSafetyPolicy.EvaluateSource(
                    fullPath,
                    _catalog,
                    SafetyEvaluationContext.ScanPreview,
                    checkFilesystem: false);

                // L1 推荐迁移或 L2 原生引导：直接作为精准软件热点输出并停止下钻（绝不重复计入子项）
                if (decision.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.VendorNativeRecommended)
                {
                    results.Add((childId, size));
                    continue;
                }

                // L0 系统容器（Users / 用户名 / AppData / Local / Program Files 等）：本身绝不作为大软件展示；
                // 对纯系统内核黑盒直接跳过，对可能包含应用数据的容器继续下钻
                if (decision.Level == MigrationAdmissionLevel.HardDenied)
                {
                    if (!child.IsReparsePoint && depth < 10 && ShouldDescendL0Container(name, fullPath))
                    {
                        Visit(childId, depth + 1);
                    }
                    continue;
                }

                // L3 普通目录：若为包装目录（如 Programs）或包含已知 L1/L2 子目录或存在单一超大子目录（>=85%），则继续下钻到具体软件目录
                if (!child.IsReparsePoint && depth < 10 && ShouldDescendL3Wrapper(snapshot, childId, name, size, minAllocatedBytes))
                {
                    Visit(childId, depth + 1);
                    continue;
                }

                results.Add((childId, size));
            }
        }

        Visit(snapshot.RootNodeId, depth: 0);

        return results
            .OrderByDescending(x => x.Size)
            .Take(maxCount)
            .Select(x => x.NodeId)
            .ToArray();
    }

    private static bool ShouldDescendL0Container(string name, string fullPath)
    {
        // 纯 OS 内部黑盒目录不下钻，节省遍历开销且无任何可迁项
        if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Boot", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WinSxS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Temp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Packages", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Users、用户根目录、AppData、Local/Roaming/LocalLow、Program Files、ProgramData 等容器下钻查找大软件
        return !fullPath.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldDescendL3Wrapper(ScanSnapshot snapshot, int nodeId, string name, long parentSize, long minAllocatedBytes)
    {
        ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
        if (children.Length == 0) return false;

        bool isWrapperName = name.Equals("Programs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AI_Workspace", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Games", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Apps", StringComparison.OrdinalIgnoreCase);

        long largestChildDirSize = 0;
        int qualifyingChildDirs = 0;
        for (int i = 0; i < children.Length; i++)
        {
            int cid = children[i];
            ref readonly ScanNodeRecord c = ref snapshot.Nodes[cid];
            if (!c.IsDirectory || c.IsOrphaned || c.UniqueAllocatedBytes < minAllocatedBytes) continue;
            qualifyingChildDirs++;
            if (c.UniqueAllocatedBytes > largestChildDirSize) largestChildDirSize = c.UniqueAllocatedBytes;

            // 若子目录本身命中 L1 或 L2 规则（如 NovaStudio\comfyui\models），强制下钻暴露精准规则目录
            string cPath = snapshot.GetFullPath(cid);
            SafetyPolicyDecision cd = MigrationSafetyPolicy.EvaluateSource(cPath, _catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false);
            if (cd.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.VendorNativeRecommended)
                return true;
        }

        if (qualifyingChildDirs == 0) return false;
        if (isWrapperName) return true;

        // 若包含已知规则应用名（如 NovaStudio、Steam），继续下钻一层让其子数据目录（如 NovaMax/comfyui/models）浮出
        if (BuiltInApplicationRules.All.Any(r =>
            r.PathContainsMarkers.Any(m => name.Equals(m, StringComparison.OrdinalIgnoreCase))
            || r.InstallDetectionNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        // 单一子目录占比 >= 85%（典型中间套壳目录），下钻一层直达真正的大目录
        return qualifyingChildDirs == 1 && parentSize > 0 && largestChildDirSize * 100.0 / parentSize >= 85.0;
    }

    private void UpdateExecutionMetricsAndSteps(BatchItemProgress p)
    {
        MigrationProgress? inner = p.InnerProgress;
        if (inner is not null)
        {
            ActiveCopiedText = inner.TotalBytes > 0
                ? $"{FileStatsService.FormatBytes(inner.CopiedBytes)} / {FileStatsService.FormatBytes(inner.TotalBytes)}"
                : FileStatsService.FormatBytes(inner.CopiedBytes);
            ActiveSpeedText = inner.SpeedBytesPerSecond > 0
                ? $"{FileStatsService.FormatBytes((long)inner.SpeedBytesPerSecond)}/s"
                : "—";
            ActiveEtaText = inner.EstimatedTimeRemaining is TimeSpan eta && eta.TotalSeconds > 0
                ? (eta.TotalMinutes >= 1 ? $"约 {(int)eta.TotalMinutes} 分 {eta.Seconds} 秒" : $"约 {eta.Seconds} 秒")
                : "—";

            int phase = inner.CurrentPhase;
            if (phase <= 1)
            {
                ActiveStageBadgeText = "阶段 1 / 4 · 安全复核";
                ExecStep1State = 1; ExecStep1Note = "复核中…";
                ExecStep2State = 0; ExecStep2Note = "等待复核通过";
                ExecStep3State = 0; ExecStep3Note = "等待复制完成";
                ExecStep4State = 0; ExecStep4Note = "等待核对通过";
            }
            else if (phase == 2)
            {
                ActiveStageBadgeText = "阶段 2 / 4 · 复制文件";
                ExecStep1State = 2; ExecStep1Note = "已通过";
                ExecStep2State = 1; ExecStep2Note = "进行中 · 8 线程";
                ExecStep3State = 0; ExecStep3Note = "等待复制完成";
                ExecStep4State = 0; ExecStep4Note = "等待核对通过";
            }
            else if (phase == 3)
            {
                ActiveStageBadgeText = "阶段 3 / 4 · 完整性核对";
                ExecStep1State = 2; ExecStep1Note = "已通过";
                ExecStep2State = 2; ExecStep2Note = "复制完成";
                ExecStep3State = 1; ExecStep3Note = UseContentHash ? "SHA-256 核对中…" : "快速核对中…";
                ExecStep4State = 0; ExecStep4Note = "等待核对通过";
            }
            else
            {
                ActiveStageBadgeText = "阶段 4 / 4 · 改名保留与建链";
                ExecStep1State = 2; ExecStep1Note = "已通过";
                ExecStep2State = 2; ExecStep2Note = "复制完成";
                ExecStep3State = 2; ExecStep3Note = "核对通过";
                ExecStep4State = p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification ? 2 : 1;
                ExecStep4Note = p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification ? "已完成（备份保留）" : "建链与校验中…";
            }
        }
        else if (p.ItemStatus == BatchPlanItemStatus.CompletedAwaitingVerification)
        {
            ActiveStageBadgeText = "已完成 · 备份已保留";
            ExecStep1State = 2; ExecStep1Note = "已通过";
            ExecStep2State = 2; ExecStep2Note = "复制完成";
            ExecStep3State = 2; ExecStep3Note = "核对通过";
            ExecStep4State = 2; ExecStep4Note = "已完成（备份保留）";
        }
    }

    private void RebuildVisibleRows()
    {
        VisibleTreeRows.Clear();
        if (_snapshot is null) return;

        string filter = SearchFilter.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            var matches = new List<(int NodeId, long Size)>();
            for (int i = 1; i < _snapshot.Nodes.Length; i++)
            {
                ref readonly ScanNodeRecord node = ref _snapshot.Nodes[i];
                if (!node.IsDirectory) continue;
                string name = _snapshot.GetNodeName(i);
                if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((i, Math.Max(node.UniqueAllocatedBytes, node.LogicalBytes)));
                }
            }

            long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);
            foreach (var (nodeId, _) in matches.OrderByDescending(m => m.Size).Take(300))
            {
                VisibleTreeRows.Add(BuildRowItem(nodeId, depth: 0, parentBytes: rootTotal));
            }
            return;
        }

        if (BrowseMode == ScanBrowseMode.HotspotFlatList)
        {
            long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);
            int[] hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 50L * 1024 * 1024, maxCount: 80);
            if (hotspots.Length == 0)
            {
                hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 1L * 1024 * 1024, maxCount: 80);
            }
            foreach (int nodeId in hotspots)
            {
                VisibleTreeRows.Add(BuildRowItem(nodeId, depth: 0, parentBytes: rootTotal, includeParentHint: true));
            }
            return;
        }

        if (BrowseMode == ScanBrowseMode.HotspotPathTree)
        {
            BuildHotspotPathTreeRows();
            return;
        }

        InsertChildRows(_snapshot.RootNodeId, depth: 0, insertAt: 0, startOffset: 0);
    }

    private long GetNodeSizeForShare(int nodeId)
    {
        ref readonly ScanNodeRecord node = ref _snapshot!.Nodes[nodeId];
        long bytes = node.UniqueAllocatedBytes > 0 ? node.UniqueAllocatedBytes : node.LogicalBytes;
        return Math.Max(1, bytes);
    }

    /// <summary>
    /// 构筑“按路径展开”的大软件剪枝路径树：仅保留通向真实大软件/大目录的祖先支路，从顶层自动缩进展开，
    /// 所有层级的空间占比均以全盘已用空间为统一标尺，避免出现“5 GB 子目录 100% 超过 500 GB 父目录”的视觉倒挂。
    /// </summary>
    private void BuildHotspotPathTreeRows()
    {
        if (_snapshot is null) return;

        int[] hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 50L * 1024 * 1024, maxCount: 50);
        if (hotspots.Length == 0)
        {
            hotspots = CollectDisjointHotspots(_snapshot, minAllocatedBytes: 1L * 1024 * 1024, maxCount: 50);
        }
        if (hotspots.Length == 0) return;

        var keptNodes = new HashSet<int>();
        var autoExpandNodes = new HashSet<int>();
        foreach (int h in hotspots)
        {
            keptNodes.Add(h);
            int curr = _snapshot.Nodes[h].ParentNodeId;
            int guard = 0;
            while (curr >= 0 && curr != _snapshot.RootNodeId && guard++ < 64)
            {
                keptNodes.Add(curr);
                autoExpandNodes.Add(curr);
                curr = _snapshot.Nodes[curr].ParentNodeId;
            }
        }

        _hotspotPathKeptNodes = keptNodes;
        _hotspotPathAncestorNodes = autoExpandNodes;

        long rootTotal = GetNodeSizeForShare(_snapshot.RootNodeId);

        void EmitSubtree(int parentId, int depth)
        {
            ReadOnlySpan<int> children = _snapshot.GetChildrenSpan(parentId);
            for (int i = 0; i < children.Length; i++)
            {
                int cid = children[i];
                if (!keptNodes.Contains(cid)) continue;
                ScanTreeRowItem row = BuildRowItem(cid, depth, rootTotal);
                bool expand = IsHotspotPathTreeExpanded && autoExpandNodes.Contains(cid);
                row.IsExpanded = expand;
                VisibleTreeRows.Add(row);
                if (expand)
                {
                    EmitSubtree(cid, depth + 1);
                }
            }
        }

        EmitSubtree(_snapshot.RootNodeId, depth: 0);
    }

    private void InsertChildRows(int parentNodeId, int depth, int insertAt, int startOffset)
    {
        if (_snapshot is null) return;
        ReadOnlySpan<int> children = _snapshot.GetChildrenSpan(parentNodeId);
        // 在“按路径展开”大软件模式下手动展开节点时，仍延续全盘统一标尺，防止子项占比突然跳回 100%
        long shareBaseBytes = BrowseMode == ScanBrowseMode.HotspotPathTree
            ? GetNodeSizeForShare(_snapshot.RootNodeId)
            : GetNodeSizeForShare(parentNodeId);

        // 默认仅展示文件夹（隐藏无法迁移的单个文件如 hiberfil.sys/pagefile.sys），用户勾选“显示文件”时全部显示
        int cursor = insertAt;
        int matched = 0;
        int displayed = 0;
        int remaining = 0;
        for (int i = 0; i < children.Length; i++)
        {
            int cid = children[i];
            ref readonly ScanNodeRecord child = ref _snapshot.Nodes[cid];
            if (!ShowFilesInTree && !child.IsDirectory) continue;

            if (matched < startOffset)
            {
                matched++;
                continue;
            }

            if (displayed < DirectoryPageSize)
            {
                VisibleTreeRows.Insert(cursor++, BuildRowItem(cid, depth, shareBaseBytes));
                displayed++;
                matched++;
            }
            else
            {
                remaining++;
            }
        }

        if (remaining > 0)
        {
            VisibleTreeRows.Insert(cursor, new ScanTreeRowItem
            {
                IsLoadMorePlaceholder = true,
                LoadMoreParentNodeId = parentNodeId,
                LoadMoreNextOffset = startOffset + displayed,
                Depth = depth,
                Name = $"加载更多子项（剩余 {remaining:N0} 项）…",
                DisplayName = $"加载更多子项（剩余 {remaining:N0} 项）…"
            });
        }
    }

    private void LoadMoreChildren(ScanTreeRowItem placeholder)
    {
        int idx = VisibleTreeRows.IndexOf(placeholder);
        if (idx < 0) return;
        VisibleTreeRows.RemoveAt(idx);
        InsertChildRows(placeholder.LoadMoreParentNodeId, placeholder.Depth, idx, placeholder.LoadMoreNextOffset);
    }

    private ScanTreeRowItem BuildRowItem(int nodeId, int depth, long parentBytes, bool includeParentHint = false)
    {
        ref readonly ScanNodeRecord node = ref _snapshot!.Nodes[nodeId];
        string fullPath = _snapshot.GetFullPath(nodeId);
        string name = _snapshot.GetNodeName(nodeId);
        string displayName = name;
        long sizeForShare = node.UniqueAllocatedBytes > 0 ? node.UniqueAllocatedBytes : node.LogicalBytes;
        double share = Math.Clamp(sizeForShare * 100.0 / parentBytes, 0, 100);

        MigrationAdmissionLevel level = MigrationAdmissionLevel.ManualRiskConfirm;
        bool isToolSelfProtected = false;
        DirectoryHintLevel hintLevel = DirectoryHintLevel.None;
        string hintToolTip = string.Empty;
        string attrLabel = string.Empty;
        if (node.IsDirectory)
        {
            if (node.IsOrphaned || nodeId == _snapshot.OrphanContainerNodeId)
            {
                // 虚拟孤立/未解析容器及其子项绝不允许作为 L3 迁移，强制标为 L0 系统保护
                level = MigrationAdmissionLevel.HardDenied;
                attrLabel = "只读诊断";
            }
            else
            {
                SafetyPolicyDecision preview = MigrationSafetyPolicy.EvaluateSource(
                    fullPath,
                    _catalog,
                    SafetyEvaluationContext.ScanPreview,
                    checkFilesystem: false);
                level = preview.Level;
                isToolSelfProtected = preview.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase);

                DirectoryExplanation? explanation = KnownDirectoryExplainer.Explain(fullPath, _catalog, preview);
                if (explanation is { HintLevel: not DirectoryHintLevel.None })
                {
                    hintLevel = explanation.HintLevel;
                    hintToolTip = KnownDirectoryExplainer.FormatHoverToolTip(explanation);
                }

                // O(1) 预聚合子树硬链接与不完整风险警示：在左侧目录树直接标出感叹号（不可迁外部硬链接/损坏）或信息图标（可无损重建内部硬链接）
                if (level != MigrationAdmissionLevel.HardDenied)
                {
                    SubtreeHazardFlags subtreeFlags = _snapshot.GetSubtreeHazardFlags(nodeId);
                    bool hasExternalHardLinks = (subtreeFlags & SubtreeHazardFlags.HasExternalHardLinks) != 0;
                    bool hasInternalHardLinks = (subtreeFlags & SubtreeHazardFlags.HasInternalHardLinks) != 0;
                    bool hasIncompleteNodes = (subtreeFlags & SubtreeHazardFlags.HasIncompleteNodes) != 0;

                    if (hasExternalHardLinks || hasIncompleteNodes)
                    {
                        string hazardTip = hasExternalHardLinks
                            ? "⚠ 包含跨目录外部硬链接（禁止直接迁移）\n该目录存在与目录外部共享底层数据的 NTFS 硬链接；跨盘迁移会断开共享语义且无法释放源盘对应空间。\n建议：点击选中查看外部关联路径，或改用软件自身路径设置。"
                            : "⚠ 子树包含扫描不完整或受限节点（禁止迁移）\n该目录下存在扫描期间无法读取或损坏的记录，为保证数据安全已禁止加入迁移清单。";
                        hintLevel = DirectoryHintLevel.Warning;
                        hintToolTip = string.IsNullOrWhiteSpace(hintToolTip)
                            ? hazardTip
                            : $"{hazardTip}\n\n{hintToolTip}";
                    }
                    else if (hasInternalHardLinks)
                    {
                        string internalTip = "ℹ 包含目录内闭环硬链接（支持无损迁移）\n该目录内的硬链接全部闭环在目录内部；迁移时工具将在目标盘自动按 FileId 无损重建硬链接，零空间膨胀。";
                        if (hintLevel == DirectoryHintLevel.None)
                        {
                            hintLevel = DirectoryHintLevel.Info;
                        }
                        hintToolTip = string.IsNullOrWhiteSpace(hintToolTip)
                            ? internalTip
                            : $"{hintToolTip}\n\n{internalTip}";
                    }
                }

                string rawAttr = preview.MatchedApplicationName ?? _catalog.IdentifyAttributionLabel(fullPath) ?? string.Empty;

                if (includeParentHint)
                {
                    string? parentDir = Path.GetDirectoryName(fullPath);
                    string parentName = string.IsNullOrEmpty(parentDir) ? string.Empty : Path.GetFileName(parentDir.TrimEnd('\\'));
                    if (!string.IsNullOrEmpty(parentName) && !IsGenericContainerName(parentName))
                    {
                        displayName = $"{parentName} / {name}";
                    }

                    if (!string.IsNullOrEmpty(rawAttr)
                        && !displayName.Contains(rawAttr, StringComparison.OrdinalIgnoreCase))
                    {
                        attrLabel = rawAttr;
                    }
                    else if (parentDir is not null)
                    {
                        if (parentDir.EndsWith(@"\AppData\Local", StringComparison.OrdinalIgnoreCase)) attrLabel = "AppData\\Local";
                        else if (parentDir.EndsWith(@"\AppData\Roaming", StringComparison.OrdinalIgnoreCase)) attrLabel = "AppData\\Roaming";
                    }
                }
                else if (!string.IsNullOrEmpty(rawAttr) && !name.Equals(rawAttr, StringComparison.OrdinalIgnoreCase))
                {
                    attrLabel = rawAttr;
                }
            }
        }

        bool hasChildren = false;
        if (node.IsDirectory && !node.IsReparsePoint)
        {
            ReadOnlySpan<int> childIds = _snapshot.GetChildrenSpan(nodeId);
            if (ShowFilesInTree)
            {
                hasChildren = childIds.Length > 0;
            }
            else
            {
                for (int i = 0; i < childIds.Length; i++)
                {
                    if (_snapshot.Nodes[childIds[i]].IsDirectory) { hasChildren = true; break; }
                }
            }
        }
        return new ScanTreeRowItem
        {
            NodeId = nodeId,
            Depth = depth,
            IsDirectory = node.IsDirectory,
            HasChildren = hasChildren,
            Name = name,
            DisplayName = displayName,
            FullPath = fullPath,
            LogicalBytes = node.LogicalBytes,
            AllocatedBytes = node.AllocatedBytes,
            UniqueAllocatedBytes = node.UniqueAllocatedBytes,
            FileCount = node.FileCount,
            DirectoryCount = node.DirectoryCount,
            ParentSharePercent = share,
            AttributionLabel = attrLabel,
            AdmissionLevel = level,
            IsToolSelfProtected = isToolSelfProtected,
            HintLevel = hintLevel,
            HintToolTip = hintToolTip,
            Flags = node.Flags,
            IsInPlan = PlanItems.Any(p => p.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)),
            HasDescendantInPlan = HasDescendantPathInPlan(fullPath, PlanItems.Select(p => p.SourcePath))
        };
    }

    private static bool IsGenericContainerName(string folderName) =>
        folderName.Equals("Users", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("AppData", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Local", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Roaming", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("LocalLow", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Programs", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
        || folderName.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase);

    private void SyncTreePlanChecks()
    {
        if (VisibleTreeRows.Count == 0) return;
        string[] planPaths = PlanItems
            .Select(p => p.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        var set = planPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in VisibleTreeRows)
        {
            bool direct = set.Contains(row.FullPath);
            row.IsInPlan = direct;
            row.HasDescendantInPlan = !direct && row.IsDirectory && HasDescendantPathInPlan(row.FullPath, planPaths);
        }
    }

    private static bool HasDescendantPathInPlan(string directoryPath, IEnumerable<string> planPaths)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return false;
        string normalized = directoryPath.TrimEnd('\\');
        string prefix = normalized + "\\";

        foreach (string candidate in planPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string trimmedCandidate = candidate.TrimEnd('\\');
            if (trimmedCandidate.Length > prefix.Length
                && trimmedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void InspectSelectedRow(ScanTreeRowItem? row)
    {
        IsL3RiskConfirmedForSelection = false;
        if (row is null || row.IsLoadMorePlaceholder || _snapshot is null)
        {
            SelectedDecision = null;
            SelectedLevelBadgeText = "未选中目录";
            SelectedLevelBadgeBg = "#F8FAFC";
            SelectedLevelBadgeBorder = "#E2E8F0";
            SelectedLevelBadgeFg = "#64748B";
            SelectedTitle = "请选择左侧目录";
            SelectedPathDisplay = "点击目录树中的任意目录查看空间占用与 L0–L3 安全准入评估。";
            SelectedAllocatedDisplay = "—";
            SelectedFileCountDisplay = "—";
            HasDirectoryExplanation = false;
            SelectedExplanationIsWarning = false;
            SelectedDirectoryRoleTitle = string.Empty;
            SelectedDirectoryPurpose = string.Empty;
            SelectedDirectoryAdvice = string.Empty;
            SelectedEvidenceIdentity = "• 识别依据：等待选择目录";
            SelectedEvidenceStructure = "• 结构核验：—";
            SelectedEvidenceStorage = "• 空间口径：—";
            SelectedNativeGuidance = string.Empty;
            ShowNativeGuidance = false;
            ShowL3ConfirmCheck = false;
            CanAddSelectedToPlan = false;
            IsSelectedAlreadyInPlan = false;
            return;
        }

        SelectedTitle = string.IsNullOrWhiteSpace(row.DisplayName) ? row.Name : row.DisplayName;
        SelectedPathDisplay = row.FullPath;
        SelectedAllocatedDisplay = FileStatsService.FormatBytes(row.UniqueAllocatedBytes);
        SelectedFileCountDisplay = row.IsDirectory ? $"{row.FileCount:N0} 个" : "1 个文件";

        HasDirectoryExplanation = false;
        SelectedExplanationIsWarning = false;
        SelectedDirectoryRoleTitle = string.Empty;
        SelectedDirectoryPurpose = string.Empty;
        SelectedDirectoryAdvice = string.Empty;

        // 更新面包屑
        string[] segs = row.FullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        BreadcrumbText = "此电脑  ›  " + string.Join("  ›  ", segs);

        var storageTags = new List<string>();
        if ((row.Flags & ScanNodeFlags.IsResidentData) != 0) storageTags.Add("含常驻 MFT 小文件");
        if ((row.Flags & ScanNodeFlags.IsWofCompressed) != 0) storageTags.Add("含 WOF 压缩（跨卷可能膨胀）");
        if ((row.Flags & ScanNodeFlags.IsCloudPlaceholder) != 0) storageTags.Add("含云端占位文件");
        if ((row.Flags & ScanNodeFlags.IsHardLinkSecondary) != 0) storageTags.Add("含硬链接共享文件");
        if ((row.Flags & ScanNodeFlags.IsIncomplete) != 0) storageTags.Add("存在受限/损坏子节点");
        SelectedEvidenceStorage = $"• 空间口径：逻辑 {FileStatsService.FormatBytes(row.LogicalBytes)} / 唯一分配 {FileStatsService.FormatBytes(row.UniqueAllocatedBytes)}"
            + (storageTags.Count > 0 ? $"（{string.Join("、", storageTags)}）" : string.Empty);

        if ((row.Flags & ScanNodeFlags.IsOrphaned) != 0)
        {
            SelectedDecision = null;
            SelectedLevelBadgeText = "L0 硬拒绝 · 未解析系统/孤立记录";
            SelectedLevelBadgeBg = "#FEF2F2";
            SelectedLevelBadgeBorder = "#FECACA";
            SelectedLevelBadgeFg = "#C42B1C";
            SelectedEvidenceIdentity = "• 识别依据：无法解析到真实磁盘路径的孤立或元数据记录（仅供只读空间诊断）";
            SelectedEvidenceStructure = "• 结构核验：禁止迁移或删除此类虚拟诊断项";
            SelectedSafeNote = "该节点为只读诊断项，不可加入迁移清单。";
            ShowNativeGuidance = false;
            ShowL3ConfirmCheck = false;
            CanAddSelectedToPlan = false;
            IsSelectedAlreadyInPlan = false;
            return;
        }

        if (!row.IsDirectory)
        {
            SelectedDecision = null;
            SelectedLevelBadgeText = "单个文件 · 不可迁移";
            SelectedLevelBadgeBg = "#F8FAFC";
            SelectedLevelBadgeBorder = "#E2E8F0";
            SelectedLevelBadgeFg = "#64748B";
            SelectedEvidenceIdentity = "• 识别依据：单个文件对象（本工具仅支持目录级符号链接迁移）";
            SelectedEvidenceStructure = "• 结构核验：请选择其上级业务目录进行评估";
            ShowNativeGuidance = false;
            ShowL3ConfirmCheck = false;
            CanAddSelectedToPlan = false;
            IsSelectedAlreadyInPlan = false;
            return;
        }

        SafetyPolicyDecision decision = MigrationSafetyPolicy.EvaluateSource(
            row.FullPath,
            _catalog,
            SafetyEvaluationContext.NewMigration,
            checkFilesystem: true);

        SelectedDecision = decision;
        bool isSelfProtected = decision.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase);
        SubtreeHazardReport subtreeHazards = _snapshot.AnalyzeSubtreeHazards(row.NodeId, maxExternalSamples: 3);

        DirectoryExplanation? explanation = KnownDirectoryExplainer.Explain(row.FullPath, _catalog, decision);
        if (decision.Level != MigrationAdmissionLevel.HardDenied && (subtreeHazards.HasBlockingHardLinks || subtreeHazards.HasIncompleteNodes))
        {
            HasDirectoryExplanation = true;
            SelectedExplanationIsWarning = true;
            if (subtreeHazards.HasBlockingHardLinks)
            {
                string peerPreview = subtreeHazards.SampleExternalPeerPaths.Length > 0
                    ? $"（外部关联如：{string.Join("；", subtreeHazards.SampleExternalPeerPaths)}）"
                    : string.Empty;
                SelectedDirectoryRoleTitle = "检测到跨目录外部硬链接（不可直接迁移）";
                SelectedDirectoryPurpose = subtreeHazards.HardLinkKind == SubtreeHardLinkKind.ExternalLinked
                    ? $"该目录下有 {subtreeHazards.ExternalHardLinkFileCount} 个文件与目录外部共享底层 NTFS 数据{peerPreview}。"
                    : "该目录下存在未能核实闭环归属的硬链接文件。";
                SelectedDirectoryAdvice = "NTFS 硬链接无法跨盘维持，直接迁出会断开共享关系且源盘对应空间无法释放。建议改用软件原生路径设置，或扩大选择范围至包含所有硬链接的共同父目录（若安全）。";
            }
            else
            {
                SelectedDirectoryRoleTitle = "子树包含扫描不完整或受限节点";
                SelectedDirectoryPurpose = "扫描该目录子树时存在访问受限或损坏的节点记录。";
                SelectedDirectoryAdvice = "为保证迁移核对完整，禁止直接加入迁移清单；请尝试以管理员身份重新完整扫描后再评估。";
            }
        }
        else if (decision.Level != MigrationAdmissionLevel.HardDenied && subtreeHazards.HasInternalOnlyHardLinks && explanation is null)
        {
            HasDirectoryExplanation = true;
            SelectedExplanationIsWarning = false;
            SelectedDirectoryRoleTitle = "包含目录内闭环硬链接（支持无损重建）";
            SelectedDirectoryPurpose = $"检测到 {subtreeHazards.InternalHardLinkGroupCount} 组目录内硬链接（共 {subtreeHazards.InternalHardLinkEntryCount} 个文件项，节省约 {FileStatsService.FormatBytes(subtreeHazards.HardLinkSharedBytes)} 重复占用）。";
            SelectedDirectoryAdvice = "所有硬链接均闭环在本目录内部；迁移时工具会去重复制唯一文件并在目标盘无损重建硬链接，不会造成目标盘空间膨胀。";
        }
        else
        {
            HasDirectoryExplanation = explanation is not null;
            SelectedExplanationIsWarning = explanation?.HintLevel == DirectoryHintLevel.Warning;
            SelectedDirectoryRoleTitle = explanation?.RoleTitle ?? string.Empty;
            SelectedDirectoryPurpose = explanation?.Purpose ?? string.Empty;
            SelectedDirectoryAdvice = explanation?.MigrationAdvice ?? string.Empty;
        }

        (SelectedLevelBadgeText, SelectedLevelBadgeBg, SelectedLevelBadgeBorder, SelectedLevelBadgeFg) = decision.Level switch
        {
            MigrationAdmissionLevel.HardDenied => (isSelfProtected ? "L0 硬拒绝 · 工具自身/事务保护" : "L0 硬拒绝 · 系统/高危保护", "#FEF2F2", "#FECACA", "#C42B1C"),
            MigrationAdmissionLevel.VerifiedEligible => ("L1 推荐迁移 · 已验证规则", "#ECFDF5", "#A7F3D0", "#0F7B4B"),
            MigrationAdmissionLevel.VendorNativeRecommended => ("L2 原生引导 · 推荐官方方案", "#EBF5FF", "#BFDBFE", "#0078D4"),
            _ => ("L3 人工确认 · 普通/未收录目录", "#FFFBEB", "#FDE68A", "#B45309")
        };

        SelectedEvidenceIdentity = decision.MatchedApplicationId is not null
            ? $"• 识别依据：内置规则 #{decision.MatchedApplicationId}（{decision.MatchedApplicationName}）"
            : $"• 识别依据：{decision.Summary}";

        string hardLinkStructureNote = subtreeHazards.HardLinkKind switch
        {
            SubtreeHardLinkKind.InternalOnly => $"；含 {subtreeHazards.InternalHardLinkGroupCount} 组内部硬链接（支持目标盘无损重建）",
            SubtreeHardLinkKind.ExternalLinked => $"；含 {subtreeHazards.ExternalHardLinkFileCount} 个跨外部硬链接（禁止跨卷迁移）",
            SubtreeHardLinkKind.UnverifiedExternal => "；含未验证硬链接（禁止迁移）",
            _ => string.Empty
        };

        SelectedEvidenceStructure = (decision.ReasonCodes.Length > 0
            ? $"• 结构核验：{string.Join(" / ", decision.ReasonCodes)}"
            : "• 结构核验：无重解析点、无系统禁区命中、路径身份正常") + hardLinkStructureNote;

        SelectedSafeNote = decision.Level == MigrationAdmissionLevel.HardDenied
            ? $"已由统一安全底座拦截：{decision.Summary}"
            : subtreeHazards.HasBlockingHardLinks
                ? "检测到跨外部硬链接：跨卷复制会破坏共享语义且无法释放源盘空间，已禁止加入迁移清单。"
                : subtreeHazards.HasInternalOnlyHardLinks
                    ? $"检测到 {subtreeHazards.InternalHardLinkGroupCount} 组目录内硬链接：复制时将在目标盘自动无损重建硬链接（零空间膨胀），原目录仍保留为备份。"
                    : "迁移后原目录将在源盘改名保留为备份，待你运行对应软件验证加载正常并经两次确认后再手动删除。";

        ShowNativeGuidance = decision.Level == MigrationAdmissionLevel.VendorNativeRecommended;
        SelectedNativeGuidance = decision.NativeGuidance ?? string.Empty;
        ShowL3ConfirmCheck = decision.Level == MigrationAdmissionLevel.ManualRiskConfirm
            && !subtreeHazards.HasBlockingHardLinks
            && !subtreeHazards.HasIncompleteNodes;
        _selectedHasBlockingSubtreeHazard = subtreeHazards.HasBlockingHardLinks || subtreeHazards.HasIncompleteNodes;
        UpdateCanAddSelected();
    }

    private bool _selectedHasBlockingSubtreeHazard;

    private void UpdateCanAddSelected()
    {
        IsSelectedAlreadyInPlan = SelectedRow is not null
            && PlanItems.Any(p => p.SourcePath.Equals(SelectedRow.FullPath, StringComparison.OrdinalIgnoreCase));

        if (SelectedRow is null || !SelectedRow.IsDirectory || SelectedDecision is null)
        {
            CanAddSelectedToPlan = false;
            return;
        }

        if (_selectedHasBlockingSubtreeHazard && !IsSelectedAlreadyInPlan)
        {
            CanAddSelectedToPlan = false;
            return;
        }

        CanAddSelectedToPlan = IsSelectedAlreadyInPlan || SelectedDecision.Level switch
        {
            MigrationAdmissionLevel.VerifiedEligible => true,
            MigrationAdmissionLevel.ManualRiskConfirm => IsL3RiskConfirmedForSelection,
            _ => false
        };
    }

    private void RecomputeDraftTargets()
    {
        if (string.IsNullOrWhiteSpace(UnifiedTargetRoot)) return;
        foreach (BatchPlanRowItem item in PlanItems)
        {
            try
            {
                item.TargetPath = MigrationPlanService.BuildDefaultTargetPath(item.SourcePath, UnifiedTargetRoot, item.ApplicationId);
            }
            catch { }
        }
    }

    private void UpdateTargetSpaceHint()
    {
        try
        {
            string? root = Path.GetPathRoot(UnifiedTargetRoot);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    long required = PlanItems.Sum(p => Math.Max(p.AllocatedBytes, p.LogicalBytes));
                    TargetSpaceHintText = required > 0
                        ? $"可用 {FileStatsService.FormatBytes(drive.AvailableFreeSpace)} · 需预留 {FileStatsService.FormatBytes(required)}"
                        : $"可用 {FileStatsService.FormatBytes(drive.AvailableFreeSpace)}";
                    return;
                }
            }
        }
        catch { }

        TargetSpaceHintText = "请输入有效目标磁盘目录";
    }

    private void InvalidateFrozenPlan()
    {
        _frozenPlan = null;
        CanStartBatch = false;
    }
}
