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
    [NotifyPropertyChangedFor(nameof(ShowRescanButton))]
    private bool _isCurrentlyScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(ShowRescanButton))]
    [NotifyPropertyChangedFor(nameof(HasCacheBadge))]
    [NotifyPropertyChangedFor(nameof(CacheBadgeBg))]
    [NotifyPropertyChangedFor(nameof(CacheBadgeBorder))]
    [NotifyPropertyChangedFor(nameof(CacheBadgeFg))]
    private bool _hasCachedSnapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheBadgeBg))]
    [NotifyPropertyChangedFor(nameof(CacheBadgeBorder))]
    [NotifyPropertyChangedFor(nameof(CacheBadgeFg))]
    private bool _isCacheStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCacheBadge))]
    private string _cacheStatusText = string.Empty;

    [ObservableProperty]
    private string _cacheStatusToolTip = string.Empty;

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
    public bool ShowRescanButton => HasCachedSnapshot && !IsCurrentlyScanning;
    public bool HasCacheBadge => HasCachedSnapshot && !string.IsNullOrWhiteSpace(CacheStatusText);
    public string CacheBadgeBg => IsCacheStale ? "#FFFBEB" : "#ECFDF5";
    public string CacheBadgeBorder => IsCacheStale ? "#FDE68A" : "#A7F3D0";
    public string CacheBadgeFg => IsCacheStale ? "#B45309" : "#0F7B4B";
    public string ActionText => IsCurrentlyScanning
        ? "正在扫描…"
        : HasCachedSnapshot
            ? "查看结果"
            : (Info.SupportsRawMft ? "开始扫描" : "普通扫描");
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
    public int? ExpectedFileCount { get; init; }
    public bool HasReparseDescendants { get; init; }
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
