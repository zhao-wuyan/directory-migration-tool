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
/// 垃圾清理模块三子视图（严格对齐 docs/untitled.pen 画板 07 / 08 / 11）。
/// </summary>
public enum CleanupSubView
{
    RulesMain = 0,
    QuarantineManager = 1,
    AiSuspectedResidual = 2
}

/// <summary>
/// 左侧清理分类筛选卡片项（对齐画板 07 Categories）。
/// </summary>
public partial class CleanupCategoryNavItem : ObservableObject
{
    public CleanupCategory Category { get; }
    public string Title { get; }
    public string IconKey { get; }
    public bool IsCautionCategory => Category == CleanupCategory.CautionItems;

    [ObservableProperty]
    private string _subtitle = "等待扫描…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardBg))]
    [NotifyPropertyChangedFor(nameof(CardBorder))]
    [NotifyPropertyChangedFor(nameof(TitleFg))]
    [NotifyPropertyChangedFor(nameof(IconStroke))]
    private bool _isSelected;

    public string CardBg => IsCautionCategory
        ? (IsSelected ? "#FFFBEB" : "#FFFBEB")
        : (IsSelected ? "#EBF5FF" : "#F8FAFC");

    public string CardBorder => IsCautionCategory
        ? (IsSelected ? "#B45309" : "#FDE68A")
        : (IsSelected ? "#0078D4" : "#E2E8F0");

    public string TitleFg => IsCautionCategory
        ? "#B45309"
        : (IsSelected ? "#0078D4" : "#0F172A");

    public string IconStroke => IsCautionCategory
        ? "#B45309"
        : (IsSelected ? "#0078D4" : "#334155");

    public CleanupCategoryNavItem(CleanupCategory category, string title, string iconKey)
    {
        Category = category;
        Title = title;
        IconKey = iconKey;
    }
}

/// <summary>
/// 中栏单条清理规则卡片项（默认绝不勾选，含三档徽章与 ⓘ 悬浮快览）。
/// </summary>
public partial class CleanupRuleItemViewModel : ObservableObject
{
    private readonly Action _selectionChangedCallback;

    public CleanupRuleScanResult ScanResult { get; }
    public CleanupRuleDefinition Rule => ScanResult.Rule;
    public string RuleId => Rule.RuleId;
    public string DisplayName => Rule.DisplayName;
    public string ShortDescription => Rule.ShortDescription;
    public CleanupCategory Category => Rule.Category;
    public CleanupRecommendationTier Tier => Rule.Tier;
    public CleanupExecutionChannel Channel => Rule.Channel;
    public bool IsCaution => Tier == CleanupRecommendationTier.Caution;
    public bool HasCautionReason => Rule.CautionReason is not null;
    public bool HasBlockingProcesses => ScanResult.HasBlockingProcesses;
    public bool IsAvailable => ScanResult.IsAvailable;
    public bool CanCheck => ScanResult.CanSelect;
    public long TotalBytes => ScanResult.TotalBytes;
    public int MatchedCount => ScanResult.MatchedCount;

    public bool HasReclaimableContent => IsAvailable || TotalBytes > 0 || MatchedCount > 0;
    public double CardOpacity => HasReclaimableContent ? 1.0 : 0.65;

    public string SizeText => TotalBytes > 0
        ? FileStatsService.FormatBytes(TotalBytes)
        : (Channel == CleanupExecutionChannel.OfficialSystemApi && IsAvailable ? "官方接口" : (MatchedCount > 0 ? "0 B" : "无缓存"));

    public string TierBadgeText => Tier switch
    {
        CleanupRecommendationTier.Recommended => "建议清理",
        CleanupRecommendationTier.OnDemand => "按需清理",
        _ => "谨慎清理"
    };

    public string TierBadgeBg => Tier switch
    {
        CleanupRecommendationTier.Recommended => "#ECFDF5",
        CleanupRecommendationTier.OnDemand => "#EBF5FF",
        _ => "#FFFBEB"
    };

    public string TierBadgeBorder => Tier switch
    {
        CleanupRecommendationTier.Recommended => "#A7F3D0",
        CleanupRecommendationTier.OnDemand => "#BFDBFE",
        _ => "#FDE68A"
    };

    public string TierBadgeFg => Tier switch
    {
        CleanupRecommendationTier.Recommended => "#0F7B4B",
        CleanupRecommendationTier.OnDemand => "#0078D4",
        _ => "#B45309"
    };

    public string TierIconKey => Tier switch
    {
        CleanupRecommendationTier.Recommended => "IconCheckCircle",
        CleanupRecommendationTier.OnDemand => "IconInfo",
        _ => "IconTriangleAlert"
    };

    public string ChannelText => HasBlockingProcesses
        ? $"进程运行中（{string.Join("/", ScanResult.RunningBlockingProcesses)}）· 已阻断"
        : Rule.ChannelBadgeText;

    public string ChannelFg => HasBlockingProcesses
        ? "#C42B1C"
        : (IsCaution ? "#B45309" : "#64748B");

    public string InlineDescription => IsCaution && Rule.CautionReason is not null
        ? $"{Rule.CautionReason.OneLineImpact} 悬停 ⓘ 或看右侧详述。"
        : ShortDescription;

    public string CautionHoverTooltip => Rule.CautionReason?.HoverSummary ?? Rule.CostAndReclaimNote;

    public string StatusNote => ScanResult.StatusNote;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !CanCheck)
        {
            _isChecked = false;
            OnPropertyChanged(nameof(IsChecked));
            return;
        }
        _selectionChangedCallback();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardBg))]
    [NotifyPropertyChangedFor(nameof(CardBorder))]
    [NotifyPropertyChangedFor(nameof(TitleFg))]
    private bool _isSelected;

    public string CardBg => IsSelected
        ? (IsCaution ? "#FFFBEB" : "#EBF5FF")
        : "#F8FAFC";

    public string CardBorder => IsSelected
        ? (IsCaution ? "#B45309" : "#0078D4")
        : "#E2E8F0";

    public string TitleFg => IsSelected && IsCaution ? "#B45309" : "#0F172A";

    public const int MaxInlinePreviewCount = 200;

    public ObservableCollection<CleanupPreviewItemRow> PreviewRows { get; } = [];

    public int TotalDetailItemCount { get; }
    public bool HasAnyDetailItems => TotalDetailItemCount > 0;
    public bool HasMoreDetailItems => TotalDetailItemCount > PreviewRows.Count;
    public string PreviewHeaderSummary => TotalDetailItemCount > PreviewRows.Count
        ? $"已识别候选明细（共 {TotalDetailItemCount:N0} 项 · 预览前 {PreviewRows.Count} 大）"
        : TotalDetailItemCount > 0
            ? $"已识别候选明细（共 {TotalDetailItemCount:N0} 项）"
            : "已识别候选明细（默认未勾选）";
    public string ViewAllDetailsFooterText => TotalDetailItemCount > PreviewRows.Count
        ? $"查看全部 {TotalDetailItemCount:N0} 项明细（搜索与定位）"
        : $"查看 / 搜索全部 {TotalDetailItemCount:N0} 项明细";

    public string[] AffectedDrives { get; }
    public IReadOnlyDictionary<string, long> BytesByDrive { get; }
    public long GetBytesForDrive(string? driveLabel) =>
        string.IsNullOrWhiteSpace(driveLabel)
            ? TotalBytes
            : BytesByDrive.GetValueOrDefault(driveLabel);
    public bool HasDriveBadge => AffectedDrives.Length > 0 && HasReclaimableContent;
    public bool HasNonSystemDrive { get; }
    public string DriveBadgeText => AffectedDrives.Length == 0 ? string.Empty : string.Join(" · ", AffectedDrives);
    public string DriveBadgeBg => HasNonSystemDrive ? "#EBF5FF" : "#F1F5F9";
    public string DriveBadgeBorder => HasNonSystemDrive ? "#BFDBFE" : "#E2E8F0";
    public string DriveBadgeFg => HasNonSystemDrive ? "#0078D4" : "#475569";

    public CleanupRuleItemViewModel(CleanupRuleScanResult scanResult, Action selectionChangedCallback)
    {
        ScanResult = scanResult;
        _selectionChangedCallback = selectionChangedCallback;

        BytesByDrive = scanResult.GetBytesByDrive();
        AffectedDrives = BytesByDrive.Keys.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
        string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\")[..2];
        HasNonSystemDrive = AffectedDrives.Any(d => !d.Equals(sysDrive, StringComparison.OrdinalIgnoreCase));

        int expectationTotal = 0;
        foreach (var exp in scanResult.VerificationExpectations)
        {
            expectationTotal += exp.ExpectedFiles.Count;
        }
        TotalDetailItemCount = expectationTotal > 0 ? expectationTotal : scanResult.PreviewItems.Length;

        // 配合右侧严格虚拟化 ListView（仅渲染可视行，窗口缩放/最大化自动自适应铺满），切换零卡顿且无大块留白
        foreach (CleanupPreviewItem item in scanResult.PreviewItems.Take(MaxInlinePreviewCount))
        {
            PreviewRows.Add(new CleanupPreviewItemRow
            {
                DisplayName = item.DisplayName,
                FullPath = item.FullPath,
                TypeAndSizeText = item.SizeBytes > 0
                    ? $"{item.KindTag} · {FileStatsService.FormatBytes(item.SizeBytes)}"
                    : item.KindTag,
                AccentFg = IsCaution ? "#B45309" : (Channel == CleanupExecutionChannel.OfficialSystemApi ? "#0078D4" : "#0F7B4B")
            });
        }
    }
}

/// <summary>
/// 「软件与开发者缓存」可展开软件分组卡片模型（支持点击向下展开单选、三态全选/半选，并自动跳过进程阻断项）。
/// </summary>
public partial class CleanupRuleGroupViewModel : ObservableObject
{
    public string GroupId { get; }
    public string GroupTitle { get; }
    public ObservableCollection<CleanupRuleItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardBorder))]
    [NotifyPropertyChangedFor(nameof(ExpandTooltip))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool? _groupCheckState = false;

    public long TotalBytes { get; private set; }
    public int HitCount { get; private set; }
    public int SelectableCount { get; private set; }
    public int CheckedCount { get; private set; }
    public int BlockedCount { get; private set; }
    public bool CanCheckGroup => SelectableCount > 0;
    public bool HasReclaimableContent => HitCount > 0 || TotalBytes > 0;
    public double CardOpacity => HasReclaimableContent ? 1.0 : 0.65;

    public string SizeText => TotalBytes > 0 ? FileStatsService.FormatBytes(TotalBytes) : "无缓存";
    public string CountPillText => HitCount > 0 ? $"{HitCount} 款命中" : $"{Items.Count} 款无缓存";
    public string ExpandTooltip => IsExpanded ? "点击收起分组" : "点击展开按单款软件勾选";
    public string CardBorder => IsExpanded ? "#93C5FD" : "#E2E8F0";

    public string SummaryText { get; private set; } = string.Empty;
    public string TierBadgeText { get; private set; } = "建议清理";
    public string TierBadgeBg { get; private set; } = "#ECFDF5";
    public string TierBadgeBorder { get; private set; } = "#A7F3D0";
    public string TierBadgeFg { get; private set; } = "#0F7B4B";
    public string TierIconKey { get; private set; } = "IconCheckCircle";
    public string StatusRightText { get; private set; } = string.Empty;
    public string StatusRightFg { get; private set; } = "#64748B";
    public string GroupDriveBadgeText { get; private set; } = string.Empty;
    public bool HasGroupDriveBadge => !string.IsNullOrEmpty(GroupDriveBadgeText);
    public bool HasGroupNonSystemDrive { get; private set; }
    public string GroupDriveBadgeBg => HasGroupNonSystemDrive ? "#EBF5FF" : "#F1F5F9";
    public string GroupDriveBadgeBorder => HasGroupNonSystemDrive ? "#BFDBFE" : "#E2E8F0";
    public string GroupDriveBadgeFg => HasGroupNonSystemDrive ? "#0078D4" : "#475569";

    public CleanupRuleGroupViewModel(string groupId, string groupTitle, IEnumerable<CleanupRuleItemViewModel> items, bool isExpanded)
    {
        GroupId = groupId;
        GroupTitle = groupTitle;
        _isExpanded = isExpanded;

        foreach (CleanupRuleItemViewModel item in items)
        {
            Items.Add(item);
        }

        RefreshMetrics();
    }

    public void RefreshMetrics()
    {
        TotalBytes = Items.Sum(i => i.TotalBytes);
        HitCount = Items.Count(i => i.HasReclaimableContent);
        SelectableCount = Items.Count(i => i.CanCheck);
        CheckedCount = Items.Count(i => i.IsChecked);
        BlockedCount = Items.Count(i => i.HasBlockingProcesses && i.HasReclaimableContent);

        GroupCheckState = SelectableCount == 0 || CheckedCount == 0
            ? false
            : CheckedCount >= SelectableCount
                ? true
                : null;

        // 组装组内软件名称快览（优先展示有缓存的软件）
        var previewNames = Items
            .Where(i => i.HasReclaimableContent)
            .Take(4)
            .Select(i => i.HasBlockingProcesses ? $"{ShortAppName(i.DisplayName)}(运行中)" : ShortAppName(i.DisplayName))
            .ToList();

        if (previewNames.Count == 0)
        {
            previewNames = Items.Take(4).Select(i => ShortAppName(i.DisplayName)).ToList();
        }

        string namesJoined = string.Join(" · ", previewNames);
        int extraCount = (HitCount > 0 ? HitCount : Items.Count) - previewNames.Count;
        if (extraCount > 0) namesJoined += $" 等 {HitCount} 款";

        SummaryText = CheckedCount > 0
            ? $"已勾选 {CheckedCount}/{Math.Max(1, HitCount)} 款 · {namesJoined}"
            : namesJoined;

        bool hasRec = Items.Any(i => i.Tier == CleanupRecommendationTier.Recommended && (HitCount == 0 || i.HasReclaimableContent));
        bool hasOnDemand = Items.Any(i => i.Tier == CleanupRecommendationTier.OnDemand && (HitCount == 0 || i.HasReclaimableContent));
        if (hasRec && hasOnDemand)
        {
            TierBadgeText = "建议 / 按需";
            TierBadgeBg = "#F0F9FF";
            TierBadgeBorder = "#BAE6FD";
            TierBadgeFg = "#0369A1";
            TierIconKey = "IconInfo";
        }
        else if (hasOnDemand)
        {
            TierBadgeText = "按需清理";
            TierBadgeBg = "#EBF5FF";
            TierBadgeBorder = "#BFDBFE";
            TierBadgeFg = "#0078D4";
            TierIconKey = "IconInfo";
        }
        else
        {
            TierBadgeText = "建议清理";
            TierBadgeBg = "#ECFDF5";
            TierBadgeBorder = "#A7F3D0";
            TierBadgeFg = "#0F7B4B";
            TierIconKey = "IconCheckCircle";
        }

        if (BlockedCount > 0)
        {
            StatusRightText = SelectableCount > 0
                ? $"{BlockedCount} 款运行中已阻断 · 展开可单选其他"
                : $"{BlockedCount} 款进程运行中 · 已阻断";
            StatusRightFg = "#C42B1C";
        }
        else if (HitCount > 0)
        {
            StatusRightText = "同卷隔离可恢复 · 点击展开单选";
            StatusRightFg = "#64748B";
        }
        else
        {
            StatusRightText = "未检测到可清缓存";
            StatusRightFg = "#94A3B8";
        }

        string[] groupDrives = Items
            .Where(i => i.HasReclaimableContent)
            .SelectMany(i => i.AffectedDrives)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\")[..2];
        GroupDriveBadgeText = groupDrives.Length > 0 ? string.Join(" · ", groupDrives) : string.Empty;
        HasGroupNonSystemDrive = groupDrives.Any(d => !d.Equals(sysDrive, StringComparison.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(HitCount));
        OnPropertyChanged(nameof(SelectableCount));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(CanCheckGroup));
        OnPropertyChanged(nameof(HasReclaimableContent));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(CountPillText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TierBadgeText));
        OnPropertyChanged(nameof(TierBadgeBg));
        OnPropertyChanged(nameof(TierBadgeBorder));
        OnPropertyChanged(nameof(TierBadgeFg));
        OnPropertyChanged(nameof(TierIconKey));
        OnPropertyChanged(nameof(StatusRightText));
        OnPropertyChanged(nameof(StatusRightFg));
        OnPropertyChanged(nameof(GroupDriveBadgeText));
        OnPropertyChanged(nameof(HasGroupDriveBadge));
        OnPropertyChanged(nameof(HasGroupNonSystemDrive));
        OnPropertyChanged(nameof(GroupDriveBadgeBg));
        OnPropertyChanged(nameof(GroupDriveBadgeBorder));
        OnPropertyChanged(nameof(GroupDriveBadgeFg));
    }

    private static string ShortAppName(string displayName)
    {
        int idx = displayName.IndexOfAny([' ', '（', '(']);
        if (idx >= 2 && idx <= 16)
        {
            string head = displayName[..idx];
            // 保留 "Microsoft Edge" / "Google Chrome" / "Visual Studio" / "IntelliJ IDEA" 等双词品牌名
            if (head is "Microsoft" or "Google" or "Mozilla" or "Visual" or "IntelliJ" or "JetBrains" or "Android" or "Epic" or "Cherry" or "OBS" or "Brave" or "Vivaldi" or "Opera")
            {
                int nextSpace = displayName.IndexOf(' ', idx + 1);
                return nextSpace > idx ? displayName[..nextSpace] : displayName;
            }
            return head;
        }
        return displayName.Length > 14 ? displayName[..14] + "…" : displayName;
    }
}

public sealed class CleanupPreviewItemRow
{
    public string DisplayName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string TypeAndSizeText { get; init; } = string.Empty;
    public string AccentFg { get; init; } = "#64748B";
}

/// <summary>
/// 右栏「全盘空间概览」及「全部磁盘弹框」使用的单块磁盘实时态势卡片模型（零磁盘遍历开销）。
/// </summary>
public sealed class CleanupDiskOverviewItem
{
    public string DriveRoot { get; init; } = @"C:\";
    public string DriveLabel { get; init; } = "C:";
    public string VolumeLabel { get; init; } = string.Empty;
    public bool IsSystemDrive { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes > 0 ? Math.Clamp(UsedBytes * 100.0 / TotalBytes, 0d, 100d) : 0d;

    public bool HasFreshMftCache { get; init; }
    public int MftAgeMinutes { get; init; }
    public long QuarantineBytes { get; init; }
    public int QuarantineBatchCount { get; init; }
    public int CrossDriveSourceCount { get; init; }

    public string DisplayTitle
    {
        get
        {
            if (IsSystemDrive)
            {
                return string.IsNullOrWhiteSpace(VolumeLabel)
                    ? $"{DriveLabel} 系统盘"
                    : $"{DriveLabel} {VolumeLabel}（系统）";
            }
            return string.IsNullOrWhiteSpace(VolumeLabel)
                ? $"{DriveLabel} 本地磁盘"
                : $"{DriveLabel} {VolumeLabel}";
        }
    }

    public string CapacitySummaryText => $"剩余 {FileStatsService.FormatBytes(FreeBytes)} / {FileStatsService.FormatBytes(TotalBytes)}";
    public string UsedPercentText => $"已用 {UsedPercent:0}%";

    public string BarBrush => UsedPercent >= 90d ? "#DC2626" : (UsedPercent >= 80d ? "#D97706" : "#0078D4");
    public string IconBg => UsedPercent >= 90d ? "#FEF2F2" : (UsedPercent >= 80d ? "#FFFBEB" : "#EBF5FF");
    public string IconStroke => UsedPercent >= 90d ? "#C42B1C" : (UsedPercent >= 80d ? "#B45309" : "#0078D4");

    public string StatusBadgeText => UsedPercent >= 90d ? "紧张" : (UsedPercent >= 80d ? "偏高" : "充裕");
    public string StatusBadgeBg => UsedPercent >= 90d ? "#FEF2F2" : (UsedPercent >= 80d ? "#FFFBEB" : "#ECFDF5");
    public string StatusBadgeBorder => UsedPercent >= 90d ? "#FECACA" : (UsedPercent >= 80d ? "#FDE68A" : "#A7F3D0");
    public string StatusBadgeFg => UsedPercent >= 90d ? "#C42B1C" : (UsedPercent >= 80d ? "#B45309" : "#0F7B4B");

    public string MftBadgeText => MftAgeMinutes <= 1 ? "MFT 就绪" : $"MFT {MftAgeMinutes}m";
    public bool HasQuarantineData => QuarantineBatchCount > 0 && QuarantineBytes > 0;
    public string QuarantineBadgeText => $"隔离区 {FileStatsService.FormatBytes(QuarantineBytes)}";
    public bool HasCrossDriveSources => CrossDriveSourceCount > 0;
    public string CrossDriveBadgeText => $"跨盘源 {CrossDriveSourceCount}";
}

/// <summary>
/// 未扫描引导态中栏展示的「本次扫描覆盖范围」静态能力预览卡。
/// </summary>
public sealed class CleanupIdleScopePreviewItem
{
    public CleanupCategory Category { get; init; }
    public string Title { get; init; } = string.Empty;
    public string RuleCountText { get; init; } = string.Empty;
    public string SampleSummary { get; init; } = string.Empty;
    public string TierBadgeText { get; init; } = "建议清理";
    public string TierBadgeBg { get; init; } = "#ECFDF5";
    public string TierBadgeBorder { get; init; } = "#A7F3D0";
    public string TierBadgeFg { get; init; } = "#0F7B4B";
    public string CrossDriveHint { get; init; } = string.Empty;
    public bool HasCrossDriveHint => !string.IsNullOrEmpty(CrossDriveHint);
}

/// <summary>
/// 扫描进行中实时浮现的大项发现卡片模型（按体积降序排列，最新命中高亮）。
/// </summary>
public partial class CleanupLiveHitRowViewModel : ObservableObject
{
    public string RuleId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public CleanupCategory Category { get; init; }
    public CleanupRecommendationTier Tier { get; init; }
    public long TotalBytes { get; init; }
    public int MatchedCount { get; init; }
    public bool HasBlockingProcesses { get; init; }
    public string[] Drives { get; init; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardBg))]
    [NotifyPropertyChangedFor(nameof(CardBorder))]
    private bool _isLatestHighlight;

    public string SizeText => TotalBytes > 0 ? FileStatsService.FormatBytes(TotalBytes) : "官方接口";
    public string CountText => MatchedCount > 0 ? $"{MatchedCount:N0} 项" : "官方组件";
    public string DrivesText => Drives.Length > 0 ? string.Join(" · ", Drives) : string.Empty;
    public bool HasDrives => Drives.Length > 0;
    public bool HasNonSystemDrive { get; init; }

    public string DriveBg => HasNonSystemDrive ? "#EBF5FF" : "#F1F5F9";
    public string DriveBorder => HasNonSystemDrive ? "#BFDBFE" : "#E2E8F0";
    public string DriveFg => HasNonSystemDrive ? "#0078D4" : "#475569";

    public string CardBg => IsLatestHighlight ? "#F0F9FF" : "#FFFFFF";
    public string CardBorder => IsLatestHighlight ? "#38BDF8" : "#E2E8F0";

    public string TierBadgeText => Tier switch
    {
        CleanupRecommendationTier.Recommended => "建议清理",
        CleanupRecommendationTier.OnDemand => "按需清理",
        _ => "谨慎清理"
    };

    public string TierBadgeBg => Tier switch
    {
        CleanupRecommendationTier.Recommended => "#ECFDF5",
        CleanupRecommendationTier.OnDemand => "#EBF5FF",
        _ => "#FFFBEB"
    };

    public string TierBadgeBorder => Tier switch
    {
        CleanupRecommendationTier.Recommended => "#A7F3D0",
        CleanupRecommendationTier.OnDemand => "#BFDBFE",
        _ => "#FDE68A"
    };

    public string TierBadgeFg => Tier switch
    {
        CleanupRecommendationTier.Recommended => "#0F7B4B",
        CleanupRecommendationTier.OnDemand => "#0078D4",
        _ => "#B45309"
    };
}

/// <summary>
/// 扫描完成后的多盘快捷筛选胶囊（当结果横跨多个磁盘时自动呈现）。
/// </summary>
public partial class CleanupDriveFilterChip : ObservableObject
{
    public string? DriveLabel { get; init; }
    public string DisplayText { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipBg))]
    [NotifyPropertyChangedFor(nameof(ChipBorder))]
    [NotifyPropertyChangedFor(nameof(ChipFg))]
    private bool _isSelected;

    public string ChipBg => IsSelected ? "#EBF5FF" : "#F8FAFC";
    public string ChipBorder => IsSelected ? "#0078D4" : "#CBD5E1";
    public string ChipFg => IsSelected ? "#0078D4" : "#475569";
}

/// <summary>
/// 隔离区表格单行（对齐画板 08 QuarantineTable，支持勾选参与批量永久删除）。
/// </summary>
public sealed partial class QuarantineBatchRowViewModel : ObservableObject
{
    private readonly Action? _selectionChangedCallback;

    public QuarantineBatchInspection Inspection { get; }
    public QuarantineBatchRecord Batch => Inspection.Batch;
    public string BatchId => Batch.BatchId;
    public string DisplayTitle => Batch.DisplayTitle;
    public bool HasRestoreConflict => Inspection.HasRestoreConflict;
    public bool IsFromAi => Batch.IsFromAiSuspectedResidual;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBg))]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _selectionChangedCallback?.Invoke();

    public string SourceSummaryText => HasRestoreConflict
        ? $"{Batch.SourceDisplaySummary}（原路径已生成新文件 · 存在 {Inspection.ConflictCount} 处恢复冲突）"
        : Batch.SourceDisplaySummary;

    public string SourceSummaryFg => HasRestoreConflict ? "#B45309" : "#64748B";
    public string RowBg => HasRestoreConflict ? "#FFFBEB" : (IsChecked ? "#FEF2F2" : "#FFFFFF");
    public string VolumeAndTimeText => $"{Batch.DriveRoot.TrimEnd('\\')} · {FormatRelativeTime(Batch.CreatedAtUtc)}";
    public string SizeText => FileStatsService.FormatBytes(Batch.TotalBytes);
    public string RestoreButtonText => HasRestoreConflict ? "对比后恢复" : "一键恢复";

    public QuarantineBatchRowViewModel(QuarantineBatchInspection inspection, Action? selectionChangedCallback = null)
    {
        Inspection = inspection;
        _selectionChangedCallback = selectionChangedCallback;
    }

    private static string FormatRelativeTime(DateTimeOffset utc)
    {
        DateTime local = utc.ToLocalTime().DateTime;
        DateTime today = DateTime.Today;
        if (local.Date == today) return $"今天 {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        return local.ToString("MM-dd HH:mm");
    }
}

/// <summary>
/// 画板 11 左侧「AI 疑似残留」可自判候选项（默认绝不勾选）。
/// </summary>
public partial class AiResidualCandidateRowViewModel : ObservableObject
{
    private readonly Action _selectionChangedCallback;

    public AiCleanupCandidateRecord Record { get; }
    public Guid CandidateId => Record.CandidateId;
    public string DisplayName => Record.DisplayName;
    public string SanitizedPath => Record.SanitizedPath;
    public string ActualLocalPath => Record.ActualLocalPath;
    public long SizeBytes => Record.SizeBytes;
    public string SizeText => FileStatsService.FormatBytes(Record.SizeBytes);
    public string ReasonText => $"AI 推测（仅供参考）：{Record.Reason}";
    public bool IsQuarantined => Record.Status == AiCleanupCandidateStatus.Quarantined;
    public bool IsInvalidated => Record.Status == AiCleanupCandidateStatus.Invalidated;
    public bool CanCheck => Record.Status == AiCleanupCandidateStatus.PendingManualReview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardBg))]
    [NotifyPropertyChangedFor(nameof(CardBorder))]
    [NotifyPropertyChangedFor(nameof(SizeFg))]
    [NotifyPropertyChangedFor(nameof(ChannelStatusText))]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !CanCheck)
        {
            _isChecked = false;
            OnPropertyChanged(nameof(IsChecked));
            return;
        }
        _selectionChangedCallback();
    }

    public string CardBg => IsChecked ? "#FFFBEB" : "#F8FAFC";
    public string CardBorder => IsChecked ? "#B45309" : "#E2E8F0";
    public string SizeFg => IsChecked ? "#B45309" : "#0F172A";
    public string ChannelStatusText => IsQuarantined
        ? $"已移入同卷隔离区（{Record.QuarantineBatchId}）"
        : IsInvalidated
            ? "扫描快照已变更 · 建议已失效"
            : IsChecked
                ? "仅通道 A 同卷隔离 · 已勾选"
                : "仅通道 A 同卷隔离 · 未勾选";

    public AiResidualCandidateRowViewModel(AiCleanupCandidateRecord record, Action selectionChangedCallback)
    {
        Record = record;
        _selectionChangedCallback = selectionChangedCallback;
    }
}

/// <summary>
/// 画板 11 红色「已自动拦截」永久排除区审计卡片。
/// </summary>
public sealed class AiHardBlockedRowViewModel
{
    public string Title { get; init; } = string.Empty;
    public string ExclusionMessage { get; init; } = string.Empty;
    public string PathText { get; init; } = string.Empty;
}
