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
/// 最近扫描记录条目（会话内可一键切回目录树）。
/// </summary>
public sealed record RecentScanItem(
    Guid ScanId,
    string VolumeTitle,
    string CompletedTimeText,
    string SummaryText,
    ScanSnapshot Snapshot);

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
    public string FullPath { get; init; } = string.Empty;
    public long LogicalBytes { get; init; }
    public long AllocatedBytes { get; init; }
    public long UniqueAllocatedBytes { get; init; }
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public double ParentSharePercent { get; init; }
    public string AttributionLabel { get; init; } = string.Empty;
    public MigrationAdmissionLevel AdmissionLevel { get; init; } = MigrationAdmissionLevel.ManualRiskConfirm;
    public ScanNodeFlags Flags { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isInPlan;

    public Thickness IndentMargin => new(Depth * 14, 0, 0, 0);
    public string ExpandGlyph => IsLoadMorePlaceholder ? "⋯" : (!HasChildren ? string.Empty : (IsExpanded ? "▾" : "▸"));
    public bool ShowExpandButton => HasChildren || IsLoadMorePlaceholder;
    public bool IsHardDenied => IsDirectory && !IsLoadMorePlaceholder && AdmissionLevel == MigrationAdmissionLevel.HardDenied;
    public bool IsNativeGuide => IsDirectory && !IsLoadMorePlaceholder && AdmissionLevel == MigrationAdmissionLevel.VendorNativeRecommended;
    public bool CanToggleInPlan => IsDirectory && !IsLoadMorePlaceholder && AdmissionLevel is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.ManualRiskConfirm;

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
        MigrationAdmissionLevel.HardDenied => "L0 系统保护",
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
    public bool HasSubtreeHardLinks { get; init; }
    public bool HasIncompleteScanData { get; init; }
    public MigrationAdmissionLevel Level { get; init; }

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

    // ---- 01 目录树与 4 级详情 ----
    [ObservableProperty] private string _snapshotVolumeTitle = string.Empty;
    [ObservableProperty] private string _snapshotModeBadge = "未扫描";
    [ObservableProperty] private bool _isSnapshotMftBackend;
    [ObservableProperty] private string _snapshotVolumeSummary = string.Empty;
    [ObservableProperty] private string _usnStatusNote = string.Empty;
    [ObservableProperty] private string _breadcrumbText = "此电脑";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TreeViewTag))]
    [NotifyPropertyChangedFor(nameof(HotspotViewTag))]
    private bool _isHotspotView;

    public string TreeViewTag => !IsHotspotView ? "Active" : string.Empty;
    public string HotspotViewTag => IsHotspotView ? "Active" : string.Empty;

    [ObservableProperty]
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

    partial void OnIsHotspotViewChanged(bool value) => RebuildVisibleRows();

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
    private void OpenRecentScan(RecentScanItem? recent)
    {
        if (recent is null || IsScanning) return;
        double sec = Math.Max(0.1, (recent.Snapshot.CompletedAtUtc - recent.Snapshot.StartedAtUtc).TotalSeconds);
        ApplySnapshot(recent.Snapshot, recent.VolumeTitle, sec);
        ActiveSubView = DiskScanSubView.DirectoryTree;
    }

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
    private void SwitchToTreeView() => IsHotspotView = false;

    [RelayCommand]
    private void SwitchToHotspotView() => IsHotspotView = true;

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
            InsertChildRows(row.NodeId, row.Depth + 1, index + 1, startOffset: 0);
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

        var (hasHardLinks, hasIncomplete, _) = _snapshot.InspectSubtreeHazards(SelectedRow.NodeId);
        if (hasHardLinks)
        {
            CustomMessageBox.ShowWarning("该目录包含硬链接文件，为防止跨卷复制破坏共享语义，当前版本不允许加入迁移清单。", "硬链接拦截");
            return;
        }
        if (hasIncomplete)
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
            HasSubtreeHardLinks = hasHardLinks,
            HasIncompleteScanData = hasIncomplete,
            Level = SelectedDecision.Level,
            L3RiskAcknowledged = SelectedDecision.Level == MigrationAdmissionLevel.ManualRiskConfirm && IsL3RiskConfirmedForSelection,
            StatusText = "待预检"
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
            NodeId: p.NodeId)).ToArray();

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
    }

    private (int LargeDirCount, int L1Count, long L1Bytes) SummarizeSnapshotHighlights(ScanSnapshot snapshot)
    {
        const long OneGb = 1024L * 1024 * 1024;
        int largeDirs = 0;
        int l1Count = 0;
        long l1Bytes = 0;

        int[] hotspots = snapshot.GetHotspotDirectories(maxCount: 120, minAllocatedBytes: OneGb);
        largeDirs = hotspots.Length;
        foreach (int nodeId in hotspots)
        {
            string fullPath = snapshot.GetFullPath(nodeId);
            SafetyPolicyDecision d = MigrationSafetyPolicy.EvaluateSource(fullPath, _catalog, SafetyEvaluationContext.ScanPreview, checkFilesystem: false);
            if (d.Level == MigrationAdmissionLevel.VerifiedEligible)
            {
                l1Count++;
                l1Bytes += Math.Max(snapshot.Nodes[nodeId].UniqueAllocatedBytes, snapshot.Nodes[nodeId].LogicalBytes);
            }
        }
        return (largeDirs, l1Count, l1Bytes);
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

            long rootTotal = Math.Max(1, _snapshot.Nodes[_snapshot.RootNodeId].UniqueAllocatedBytes);
            foreach (var (nodeId, _) in matches.OrderByDescending(m => m.Size).Take(300))
            {
                VisibleTreeRows.Add(BuildRowItem(nodeId, depth: 0, parentBytes: rootTotal));
            }
            return;
        }

        if (IsHotspotView)
        {
            long rootTotal = Math.Max(1, _snapshot.Nodes[_snapshot.RootNodeId].UniqueAllocatedBytes);
            int[] hotspots = _snapshot.GetHotspotDirectories(maxCount: 80, minAllocatedBytes: 10L * 1024 * 1024);
            foreach (int nodeId in hotspots)
            {
                VisibleTreeRows.Add(BuildRowItem(nodeId, depth: 0, parentBytes: rootTotal));
            }
            return;
        }

        InsertChildRows(_snapshot.RootNodeId, depth: 0, insertAt: 0, startOffset: 0);
    }

    private void InsertChildRows(int parentNodeId, int depth, int insertAt, int startOffset)
    {
        if (_snapshot is null) return;
        ReadOnlySpan<int> children = _snapshot.GetChildrenSpan(parentNodeId);
        long parentBytes = Math.Max(1, Math.Max(_snapshot.Nodes[parentNodeId].UniqueAllocatedBytes, _snapshot.Nodes[parentNodeId].LogicalBytes));

        int end = Math.Min(children.Length, startOffset + DirectoryPageSize);
        int cursor = insertAt;
        for (int i = startOffset; i < end; i++)
        {
            VisibleTreeRows.Insert(cursor++, BuildRowItem(children[i], depth, parentBytes));
        }

        if (end < children.Length)
        {
            int remaining = children.Length - end;
            VisibleTreeRows.Insert(cursor, new ScanTreeRowItem
            {
                IsLoadMorePlaceholder = true,
                LoadMoreParentNodeId = parentNodeId,
                LoadMoreNextOffset = end,
                Depth = depth,
                Name = $"加载更多子项（剩余 {remaining:N0} 项）…"
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

    private ScanTreeRowItem BuildRowItem(int nodeId, int depth, long parentBytes)
    {
        ref readonly ScanNodeRecord node = ref _snapshot!.Nodes[nodeId];
        string fullPath = _snapshot.GetFullPath(nodeId);
        string name = _snapshot.GetNodeName(nodeId);
        long sizeForShare = Math.Max(node.UniqueAllocatedBytes, node.LogicalBytes);
        double share = Math.Clamp(sizeForShare * 100.0 / parentBytes, 0, 100);

        MigrationAdmissionLevel level = MigrationAdmissionLevel.ManualRiskConfirm;
        string attrLabel = string.Empty;
        if (node.IsDirectory)
        {
            SafetyPolicyDecision preview = MigrationSafetyPolicy.EvaluateSource(
                fullPath,
                _catalog,
                SafetyEvaluationContext.ScanPreview,
                checkFilesystem: false);
            level = preview.Level;
            attrLabel = preview.MatchedApplicationName ?? _catalog.IdentifyAttributionLabel(fullPath) ?? string.Empty;
        }

        bool hasChildren = node.IsDirectory && !node.IsReparsePoint && _snapshot.GetChildrenSpan(nodeId).Length > 0;
        return new ScanTreeRowItem
        {
            NodeId = nodeId,
            Depth = depth,
            IsDirectory = node.IsDirectory,
            HasChildren = hasChildren,
            Name = name,
            FullPath = fullPath,
            LogicalBytes = node.LogicalBytes,
            AllocatedBytes = node.AllocatedBytes,
            UniqueAllocatedBytes = node.UniqueAllocatedBytes,
            FileCount = node.FileCount,
            DirectoryCount = node.DirectoryCount,
            ParentSharePercent = share,
            AttributionLabel = attrLabel,
            AdmissionLevel = level,
            Flags = node.Flags,
            IsInPlan = PlanItems.Any(p => p.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
        };
    }

    private void SyncTreePlanChecks()
    {
        if (VisibleTreeRows.Count == 0) return;
        var set = PlanItems.Select(p => p.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in VisibleTreeRows)
        {
            row.IsInPlan = set.Contains(row.FullPath);
        }
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

        SelectedTitle = string.IsNullOrWhiteSpace(row.AttributionLabel) ? row.Name : $"{row.AttributionLabel} / {row.Name}";
        SelectedPathDisplay = row.FullPath;
        SelectedAllocatedDisplay = FileStatsService.FormatBytes(row.UniqueAllocatedBytes);
        SelectedFileCountDisplay = row.IsDirectory ? $"{row.FileCount:N0} 个" : "1 个文件";

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
        (SelectedLevelBadgeText, SelectedLevelBadgeBg, SelectedLevelBadgeBorder, SelectedLevelBadgeFg) = decision.Level switch
        {
            MigrationAdmissionLevel.HardDenied => ("L0 硬拒绝 · 系统/高危保护", "#FEF2F2", "#FECACA", "#C42B1C"),
            MigrationAdmissionLevel.VerifiedEligible => ("L1 推荐迁移 · 已验证规则", "#ECFDF5", "#A7F3D0", "#0F7B4B"),
            MigrationAdmissionLevel.VendorNativeRecommended => ("L2 原生引导 · 推荐官方方案", "#EBF5FF", "#BFDBFE", "#0078D4"),
            _ => ("L3 人工确认 · 普通/未收录目录", "#FFFBEB", "#FDE68A", "#B45309")
        };

        SelectedEvidenceIdentity = decision.MatchedApplicationId is not null
            ? $"• 识别依据：内置规则 #{decision.MatchedApplicationId}（{decision.MatchedApplicationName}）"
            : $"• 识别依据：{decision.Summary}";

        SelectedEvidenceStructure = decision.ReasonCodes.Length > 0
            ? $"• 结构核验：{string.Join(" / ", decision.ReasonCodes)}"
            : "• 结构核验：无重解析点、无系统禁区命中、路径身份正常";

        SelectedSafeNote = decision.Level == MigrationAdmissionLevel.HardDenied
            ? $"已由统一安全底座拦截：{decision.Summary}"
            : "迁移后原目录将在源盘改名保留为备份，待你运行对应软件验证加载正常并经两次确认后再手动删除。";

        ShowNativeGuidance = decision.Level == MigrationAdmissionLevel.VendorNativeRecommended;
        SelectedNativeGuidance = decision.NativeGuidance ?? string.Empty;
        ShowL3ConfirmCheck = decision.Level == MigrationAdmissionLevel.ManualRiskConfirm;
        UpdateCanAddSelected();
    }

    private void UpdateCanAddSelected()
    {
        IsSelectedAlreadyInPlan = SelectedRow is not null
            && PlanItems.Any(p => p.SourcePath.Equals(SelectedRow.FullPath, StringComparison.OrdinalIgnoreCase));

        if (SelectedRow is null || !SelectedRow.IsDirectory || SelectedDecision is null)
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
