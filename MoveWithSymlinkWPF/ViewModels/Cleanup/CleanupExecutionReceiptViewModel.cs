using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;

namespace MoveWithSymlinkWPF.ViewModels;

public enum CleanupReceiptFilter
{
    All,
    Succeeded,
    Skipped,
    Issues
}

/// <summary>
/// 虚拟化回执列表的统一行基类（扁平化支持：规则头行 + 逐条独立明细行，可承载上万条明细 0ms 秒开滚动）。
/// </summary>
public abstract class CleanupReceiptDisplayEntry : ObservableObject
{
    public string EntryKey { get; init; } = string.Empty;
}

/// <summary>
/// 回执规则摘要头行（全量预冻结画刷 + 行内直绑命令，零 RelativeSource 遍历）。
/// </summary>
public sealed partial class CleanupReceiptRuleEntry : CleanupReceiptDisplayEntry
{
    public string TargetId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public CleanupItemExecutionStatus Status { get; init; }
    public string StatusBadgeText { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string ProcessedCountText { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public bool HasSkippedCount => SkippedCount > 0;
    public string SkippedBadgeText { get; init; } = string.Empty;
    public bool HasFailedCount => FailedCount > 0;
    public string FailedBadgeText { get; init; } = string.Empty;
    public int TotalDetailCount { get; init; }
    public bool HasDetails => TotalDetailCount > 0;
    public bool IsIssueRule { get; init; }
    public bool IsSucceededRule { get; init; }
    public string HeaderSearchIndex { get; init; } = string.Empty;

    public Brush StatusBadgeBackground { get; init; } = Brushes.Transparent;
    public Brush StatusBadgeBorderBrush { get; init; } = Brushes.Transparent;
    public Brush StatusBadgeForeground { get; init; } = Brushes.Black;
    public Brush SizeForeground { get; init; } = Brushes.Black;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleDetailsText))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleDetailsText))]
    private int _visibleDetailCount;

    public string ToggleDetailsText => IsExpanded
        ? (VisibleDetailCount > 0 && VisibleDetailCount < TotalDetailCount
            ? $"收起明细（命中 {VisibleDetailCount:N0} / {TotalDetailCount:N0} 条）▴"
            : $"收起全部 {TotalDetailCount:N0} 条明细 ▴")
        : $"展开全部 {TotalDetailCount:N0} 条明细 ▾";

    public IRelayCommand ToggleExpandCommand { get; init; } = null!;
    public IReadOnlyList<CleanupReceiptDetailEntry> Details { get; init; } = [];
}

/// <summary>
/// 单条独立虚拟化明细行（每条失败/跳过/保留记录作为独立 ListViewItem，彻底告别文本截断）。
/// </summary>
public sealed partial class CleanupReceiptDetailEntry : CleanupReceiptDisplayEntry
{
    public string ParentTargetId { get; init; } = string.Empty;
    public string IndexText { get; init; } = string.Empty;
    public string TagText { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public string SearchIndex { get; init; } = string.Empty;
    public bool IsLastInGroup { get; init; }

    public Brush TagBackground { get; init; } = Brushes.Transparent;
    public Brush TagBorderBrush { get; init; } = Brushes.Transparent;
    public Brush TagForeground { get; init; } = Brushes.Black;
    public Brush GuideLineBrush { get; init; } = Brushes.Transparent;

    [ObservableProperty]
    private string _copyHintText = "复制";

    public IRelayCommand CopyLineCommand { get; init; } = null!;
}

/// <summary>
/// 「清理执行完整回执」弹窗 ViewModel：
/// 结构化 KPI 卡片 + 分段筛选 + 实时搜索 + 扁平化虚拟列表（支持数万条完整明细零截断丝滑滚动）。
/// </summary>
public partial class CleanupExecutionReceiptViewModel : ObservableObject
{
    private const int AutoExpandTotalDetailThreshold = 200;

    private static readonly Brush PrimaryBrush = Freeze("#0078D4");
    private static readonly Brush PrimarySoftBrush = Freeze("#EBF5FF");
    private static readonly Brush PrimaryBorderBrush = Freeze("#BFDBFE");

    private static readonly Brush GreenBrush = Freeze("#0F7B4B");
    private static readonly Brush GreenSoftBrush = Freeze("#ECFDF5");
    private static readonly Brush GreenBorderBrush = Freeze("#A7F3D0");

    private static readonly Brush AmberBrush = Freeze("#B45309");
    private static readonly Brush AmberSoftBrush = Freeze("#FFFBEB");
    private static readonly Brush AmberBorderBrush = Freeze("#FDE68A");

    private static readonly Brush RedBrush = Freeze("#C42B1C");
    private static readonly Brush RedSoftBrush = Freeze("#FEF2F2");
    private static readonly Brush RedBorderBrush = Freeze("#FECACA");

    private static readonly Brush NeutralSurfaceBrush = Freeze("#F8FAFC");
    private static readonly Brush NeutralBorderBrush = Freeze("#E2E8F0");
    private static readonly Brush NeutralMutedBrush = Freeze("#64748B");
    private static readonly Brush InkBrush = Freeze("#0F172A");

    private readonly IReadOnlyList<CleanupReceiptRuleEntry> _allRuleEntries;
    private readonly string _plainTextReport;
    private readonly Action? _openQuarantineCallback;
    private readonly Action? _closeWindowCallback;
    private string _lastVisibleSignature = string.Empty;

    public string WindowTitle { get; }
    public string HeaderTitle { get; }
    public string HeaderMetaText { get; }
    public string SummaryBannerText { get; }
    public string StatusChipText { get; }
    public Brush HeaderAccentBackground { get; }
    public Brush HeaderAccentBorderBrush { get; }
    public Brush HeaderAccentForeground { get; }
    public bool IsWarningHeader { get; }
    public bool IsErrorHeader { get; }
    public bool IsSuccessHeader => !IsWarningHeader && !IsErrorHeader;

    // 4 格 KPI 汇总卡
    public string QuarantinedSizeText { get; }
    public string QuarantinedSubText { get; }
    public Brush QuarantinedValueBrush { get; }

    public string ReclaimedSizeText { get; }
    public string ReclaimedSubText { get; }
    public Brush ReclaimedValueBrush { get; }

    public string SkippedCountText { get; }
    public string SkippedSubText { get; }

    public string FailedCountText { get; }
    public string FailedSubText { get; }
    public Brush FailedValueBrush { get; }

    // 筛选标签文案
    public string TabAllText { get; }
    public string TabSucceededText { get; }
    public string TabSkippedText { get; }
    public string TabIssuesText { get; }

    public int TotalRuleCount => _allRuleEntries.Count;
    public int TotalDetailCount { get; }
    public bool HasAnyDetails => TotalDetailCount > 0;

    public bool HasQuarantinedSpace { get; }
    public bool CanOpenQuarantine => HasQuarantinedSpace && _openQuarantineCallback is not null;
    public string FooterHintText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterAllTag))]
    [NotifyPropertyChangedFor(nameof(FilterSucceededTag))]
    [NotifyPropertyChangedFor(nameof(FilterSkippedTag))]
    [NotifyPropertyChangedFor(nameof(FilterIssuesTag))]
    private CleanupReceiptFilter _activeFilter = CleanupReceiptFilter.All;

    public string FilterAllTag => ActiveFilter == CleanupReceiptFilter.All ? "Active" : string.Empty;
    public string FilterSucceededTag => ActiveFilter == CleanupReceiptFilter.Succeeded ? "Active" : string.Empty;
    public string FilterSkippedTag => ActiveFilter == CleanupReceiptFilter.Skipped ? "Active" : string.Empty;
    public string FilterIssuesTag => ActiveFilter == CleanupReceiptFilter.Issues ? "Active" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    private string _searchQuery = string.Empty;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleEntries))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private IReadOnlyList<CleanupReceiptDisplayEntry> _visibleEntries = [];

    public bool HasVisibleEntries => VisibleEntries.Count > 0;
    public bool ShowEmptyState => VisibleEntries.Count == 0;

    [ObservableProperty]
    private int _visibleRuleCount;

    [ObservableProperty]
    private int _visibleDetailCount;

    [ObservableProperty]
    private string _visibleCountHintText = string.Empty;

    [ObservableProperty]
    private string _toggleAllDetailsButtonText = "收起全部明细";

    [ObservableProperty]
    private string _copyButtonText = "复制完整报告";

    public CleanupExecutionReceiptViewModel(
        CleanupExecutionResult result,
        string? windowTitle = null,
        string? plainTextReport = null,
        Action? openQuarantineCallback = null,
        Action? closeWindowCallback = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        _openQuarantineCallback = openQuarantineCallback;
        _closeWindowCallback = closeWindowCallback;
        WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? "清理执行完整回执" : windowTitle;

        bool hasFailuresOrBlocked = result.TotalFailedCount > 0
            || result.Items.Any(IsIssueItem);
        int succeededCount = result.Items.Count(IsSucceededItem);
        int skippedItemCount = result.Items.Count(i => i.Status == CleanupItemExecutionStatus.Skipped);
        int issueItemCount = result.Items.Count(IsIssueItem);

        if (result.WasCancelled)
        {
            HeaderTitle = "清理已由用户取消（已完成部分已保留）";
            StatusChipText = "已中止";
            IsWarningHeader = true;
            HeaderAccentBackground = AmberSoftBrush;
            HeaderAccentBorderBrush = AmberBorderBrush;
            HeaderAccentForeground = AmberBrush;
        }
        else if (hasFailuresOrBlocked && succeededCount == 0)
        {
            HeaderTitle = "未完成清理（存在阻断或失败项）";
            StatusChipText = "存在异常";
            IsErrorHeader = true;
            HeaderAccentBackground = RedSoftBrush;
            HeaderAccentBorderBrush = RedBorderBrush;
            HeaderAccentForeground = RedBrush;
        }
        else if (hasFailuresOrBlocked)
        {
            HeaderTitle = "清理部分完成（含阻断或失败项）";
            StatusChipText = "部分完成";
            IsWarningHeader = true;
            HeaderAccentBackground = AmberSoftBrush;
            HeaderAccentBorderBrush = AmberBorderBrush;
            HeaderAccentForeground = AmberBrush;
        }
        else
        {
            HeaderTitle = "清理执行已完成";
            StatusChipText = "全部完成";
            HeaderAccentBackground = GreenSoftBrush;
            HeaderAccentBorderBrush = GreenBorderBrush;
            HeaderAccentForeground = GreenBrush;
        }

        TimeSpan elapsed = result.CompletedAtUtc > result.StartedAtUtc
            ? result.CompletedAtUtc - result.StartedAtUtc
            : TimeSpan.Zero;
        string elapsedText = elapsed.TotalSeconds >= 60
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{elapsed.TotalSeconds:F1} 秒";
        HeaderMetaText = $"完成于 {result.CompletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 耗时 {elapsedText} · 共 {result.Items.Length} 条规则";
        SummaryBannerText = result.SummaryMessage;

        // 4 格 KPI
        HasQuarantinedSpace = result.QuarantinedBytes > 0;
        QuarantinedSizeText = HasQuarantinedSpace ? FileStatsService.FormatBytes(result.QuarantinedBytes) : "0 B";
        QuarantinedSubText = HasQuarantinedSpace ? "尚未释放物理空间 · 可随时恢复" : "本次未产生同卷隔离项";
        QuarantinedValueBrush = HasQuarantinedSpace ? PrimaryBrush : NeutralMutedBrush;

        long directReclaimedBytes = result.OfficialApiReclaimedBytes + result.PermanentlyDeletedBytes + result.RecycledBytes;
        ReclaimedSizeText = directReclaimedBytes > 0 ? FileStatsService.FormatBytes(directReclaimedBytes) : "0 B";
        var reclaimedParts = new List<string>(3);
        if (result.PermanentlyDeletedBytes > 0)
            reclaimedParts.Add($"直接删除 {FileStatsService.FormatBytes(result.PermanentlyDeletedBytes)}");
        if (result.OfficialApiReclaimedBytes > 0)
            reclaimedParts.Add($"官方接口 {FileStatsService.FormatBytes(result.OfficialApiReclaimedBytes)}");
        if (result.RecycledBytes > 0)
            reclaimedParts.Add($"回收站 {FileStatsService.FormatBytes(result.RecycledBytes)}");
        ReclaimedSubText = reclaimedParts.Count switch
        {
            > 1 => string.Join(" + ", reclaimedParts),
            1 when result.PermanentlyDeletedBytes > 0 => "1 档推荐缓存直接删除释放",
            1 when result.OfficialApiReclaimedBytes > 0 => "Windows 官方接口直接回收",
            1 when result.RecycledBytes > 0 => "已安全移入系统回收站",
            _ => "未通过直接删除/官方接口/回收站回收"
        };
        ReclaimedValueBrush = directReclaimedBytes > 0 ? GreenBrush : NeutralMutedBrush;

        SkippedCountText = $"{result.TotalSkippedCount:N0} 项";
        SkippedSubText = result.TotalSkippedCount > 0 ? "进程占用 / 24h 保护 / 安全红线" : "无跳过文件";

        FailedCountText = $"{result.TotalFailedCount:N0} 项";
        FailedSubText = result.TotalFailedCount > 0 ? "已保持原状 · 未破坏任何数据" : "零失败 · 全程安全";
        FailedValueBrush = result.TotalFailedCount > 0 ? RedBrush : GreenBrush;

        // 构建规则头 + 完整明细子行
        var rules = new List<CleanupReceiptRuleEntry>(result.Items.Length);
        int totalDetails = 0;
        for (int idx = 0; idx < result.Items.Length; idx++)
        {
            CleanupReceiptRuleEntry entry = BuildRuleEntry(result.Items[idx], idx);
            rules.Add(entry);
            totalDetails += entry.TotalDetailCount;
        }

        _allRuleEntries = rules;
        TotalDetailCount = totalDetails;

        // 智能默认展开：单规则或总明细 <= 200 时默认全部展开（填满视口零隐藏）；多规则大数据量时优先展开含失败规则，其余一键展开
        bool expandAllByDefault = rules.Count <= 1 || totalDetails <= AutoExpandTotalDetailThreshold;
        foreach (CleanupReceiptRuleEntry rule in _allRuleEntries)
        {
            if (!rule.HasDetails) continue;
            rule.IsExpanded = expandAllByDefault || (rule.IsIssueRule && rule.TotalDetailCount <= 500);
        }

        TabAllText = $"全部 ({_allRuleEntries.Count})";
        TabSucceededText = $"已处理 ({succeededCount})";
        TabSkippedText = $"已跳过 ({skippedItemCount})";
        TabIssuesText = $"含失败/阻断 ({issueItemCount})";

        FooterHintText = HasQuarantinedSpace
            ? "同卷隔离项仍在原盘保留；确认软件正常后，前往隔离区即可永久释放空间。"
            : "列表已启用虚拟化滚动，支持查看全部明细与按文件名/原因实时搜索。";

        _plainTextReport = string.IsNullOrWhiteSpace(plainTextReport)
            ? BuildPlainTextReport(result)
            : plainTextReport;

        RebuildVisibleEntries();
    }

    partial void OnActiveFilterChanged(CleanupReceiptFilter value) => RebuildVisibleEntries();
    partial void OnSearchQueryChanged(string value) => RebuildVisibleEntries();

    [RelayCommand]
    private void SetFilter(string? filterName)
    {
        if (Enum.TryParse<CleanupReceiptFilter>(filterName, out var parsed))
        {
            ActiveFilter = parsed;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    [RelayCommand]
    private void ToggleAllDetails()
    {
        if (!HasAnyDetails) return;
        bool anyCollapsed = _allRuleEntries.Any(r => r.HasDetails && !r.IsExpanded);
        foreach (CleanupReceiptRuleEntry rule in _allRuleEntries)
        {
            if (rule.HasDetails)
            {
                rule.IsExpanded = anyCollapsed;
            }
        }
        RebuildVisibleEntries();
    }

    [RelayCommand]
    private void CopyReport()
    {
        try
        {
            Clipboard.SetText(_plainTextReport);
            CopyButtonText = "已复制报告 ✓";
        }
        catch
        {
            CopyButtonText = "复制失败";
        }
    }

    [RelayCommand]
    private void OpenQuarantine()
    {
        if (!CanOpenQuarantine) return;
        _closeWindowCallback?.Invoke();
        _openQuarantineCallback?.Invoke();
    }

    [RelayCommand]
    private void CloseWindow()
    {
        _closeWindowCallback?.Invoke();
    }

    private void ToggleSingleRule(CleanupReceiptRuleEntry rule)
    {
        if (!rule.HasDetails) return;
        rule.IsExpanded = !rule.IsExpanded;
        RebuildVisibleEntries();
    }

    private static void CopyDetailLine(CleanupReceiptDetailEntry detail)
    {
        try
        {
            Clipboard.SetText(detail.DetailText);
            detail.CopyHintText = "已复制 ✓";
        }
        catch
        {
            detail.CopyHintText = "失败";
        }
    }

    private void RebuildVisibleEntries()
    {
        IEnumerable<CleanupReceiptRuleEntry> scopedRules = ActiveFilter switch
        {
            CleanupReceiptFilter.Succeeded => _allRuleEntries.Where(r => r.IsSucceededRule),
            CleanupReceiptFilter.Skipped => _allRuleEntries.Where(r => r.Status == CleanupItemExecutionStatus.Skipped || r.SkippedCount > 0),
            CleanupReceiptFilter.Issues => _allRuleEntries.Where(r => r.IsIssueRule),
            _ => _allRuleEntries
        };

        string[] tokens = string.IsNullOrWhiteSpace(SearchQuery)
            ? []
            : SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var flattened = new List<CleanupReceiptDisplayEntry>();
        var sigBuilder = new StringBuilder();
        int matchedRules = 0;
        int shownDetails = 0;

        foreach (CleanupReceiptRuleEntry rule in scopedRules)
        {
            if (tokens.Length == 0)
            {
                matchedRules++;
                rule.VisibleDetailCount = rule.TotalDetailCount;
                flattened.Add(rule);
                sigBuilder.Append(rule.EntryKey).Append(':').Append(rule.IsExpanded ? '1' : '0').Append('|');

                if (rule.IsExpanded && rule.Details.Count > 0)
                {
                    flattened.AddRange(rule.Details);
                    shownDetails += rule.Details.Count;
                    sigBuilder.Append('D').Append(rule.Details.Count).Append('|');
                }
            }
            else
            {
                bool headerMatches = MatchesAllTokens(rule.HeaderSearchIndex, tokens);
                List<CleanupReceiptDetailEntry>? matchedDetails = null;
                if (rule.Details.Count > 0)
                {
                    foreach (CleanupReceiptDetailEntry detail in rule.Details)
                    {
                        if (MatchesAllTokens(detail.SearchIndex, tokens))
                        {
                            matchedDetails ??= [];
                            matchedDetails.Add(detail);
                        }
                    }
                }

                if (!headerMatches && (matchedDetails is null || matchedDetails.Count == 0))
                {
                    continue;
                }

                matchedRules++;
                IReadOnlyList<CleanupReceiptDetailEntry> detailsToShow = matchedDetails is { Count: > 0 }
                    ? matchedDetails
                    : (headerMatches && rule.IsExpanded ? rule.Details : []);

                // 搜索直接命中明细时自动展示命中行，方便万级明细秒定位
                bool expandForDisplay = matchedDetails is { Count: > 0 } || rule.IsExpanded;
                rule.VisibleDetailCount = detailsToShow.Count;
                flattened.Add(rule);
                sigBuilder.Append(rule.EntryKey).Append(':').Append(expandForDisplay ? '1' : '0').Append(':').Append(detailsToShow.Count).Append('|');

                if (expandForDisplay && detailsToShow.Count > 0)
                {
                    flattened.AddRange(detailsToShow);
                    shownDetails += detailsToShow.Count;
                    foreach (CleanupReceiptDetailEntry d in detailsToShow)
                    {
                        sigBuilder.Append(d.EntryKey).Append(',');
                    }
                    sigBuilder.Append('|');
                }
            }
        }

        string sig = sigBuilder.ToString();
        if (!string.Equals(sig, _lastVisibleSignature, StringComparison.Ordinal))
        {
            VisibleEntries = flattened;
            _lastVisibleSignature = sig;
        }

        VisibleRuleCount = matchedRules;
        VisibleDetailCount = shownDetails;
        VisibleCountHintText = TotalDetailCount > 0
            ? $"{matchedRules} 条规则 · 展开 {shownDetails:N0} / {TotalDetailCount:N0} 条明细"
            : $"显示 {matchedRules} / {_allRuleEntries.Count} 条规则";

        bool anyCollapsed = _allRuleEntries.Any(r => r.HasDetails && !r.IsExpanded);
        ToggleAllDetailsButtonText = anyCollapsed ? "展开全部明细" : "收起全部明细";
    }

    private static bool MatchesAllTokens(string source, string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (!source.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSucceededItem(CleanupExecutionItemResult item) =>
        item.Status is CleanupItemExecutionStatus.Quarantined
            or CleanupItemExecutionStatus.Recycled
            or CleanupItemExecutionStatus.ReclaimedByOfficialApi
            or CleanupItemExecutionStatus.PermanentlyDeleted;

    private static bool IsIssueItem(CleanupExecutionItemResult item) =>
        item.Status is CleanupItemExecutionStatus.Blocked or CleanupItemExecutionStatus.Failed
            || item.FailedCount > 0;

    private CleanupReceiptRuleEntry BuildRuleEntry(CleanupExecutionItemResult item, int ruleIndex)
    {
        (string badge, Brush bg, Brush border, Brush fg, Brush sizeFg) = item.Status switch
        {
            CleanupItemExecutionStatus.Quarantined => ("已隔离 · 未释放", PrimarySoftBrush, PrimaryBorderBrush, PrimaryBrush, PrimaryBrush),
            CleanupItemExecutionStatus.Recycled => ("已移入回收站", GreenSoftBrush, GreenBorderBrush, GreenBrush, GreenBrush),
            CleanupItemExecutionStatus.ReclaimedByOfficialApi => ("官方接口已回收", GreenSoftBrush, GreenBorderBrush, GreenBrush, GreenBrush),
            CleanupItemExecutionStatus.PermanentlyDeleted => ("已直接释放", GreenSoftBrush, GreenBorderBrush, GreenBrush, GreenBrush),
            CleanupItemExecutionStatus.Skipped => ("已跳过", NeutralSurfaceBrush, NeutralBorderBrush, NeutralMutedBrush, NeutralMutedBrush),
            CleanupItemExecutionStatus.Blocked => ("安全拦截", AmberSoftBrush, AmberBorderBrush, AmberBrush, AmberBrush),
            CleanupItemExecutionStatus.Failed => ("执行失败", RedSoftBrush, RedBorderBrush, RedBrush, RedBrush),
            _ => (item.Status.ToString(), NeutralSurfaceBrush, NeutralBorderBrush, NeutralMutedBrush, InkBrush)
        };

        string sizeText = item.ProcessedBytes > 0
            ? FileStatsService.FormatBytes(item.ProcessedBytes)
            : (item.Status == CleanupItemExecutionStatus.ReclaimedByOfficialApi ? "已执行" : "0 B");
        string countText = item.ProcessedCount > 0 ? $"{item.ProcessedCount:N0} 个文件" : string.Empty;

        string[] cleanDetails = (item.Details ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToArray();

        var detailEntries = new CleanupReceiptDetailEntry[cleanDetails.Length];
        for (int i = 0; i < cleanDetails.Length; i++)
        {
            string line = cleanDetails[i];
            (string tagText, Brush tagBg, Brush tagBorder, Brush tagFg, Brush guideBrush) = ClassifyDetailLine(line);
            CleanupReceiptDetailEntry detailEntry = null!;
            detailEntry = new CleanupReceiptDetailEntry
            {
                EntryKey = $"D:{ruleIndex}:{i}",
                ParentTargetId = item.TargetId,
                IndexText = $"#{i + 1}",
                TagText = tagText,
                DetailText = line,
                SearchIndex = $"{item.DisplayName} {tagText} {line}",
                IsLastInGroup = i == cleanDetails.Length - 1,
                TagBackground = tagBg,
                TagBorderBrush = tagBorder,
                TagForeground = tagFg,
                GuideLineBrush = guideBrush,
                CopyLineCommand = new RelayCommand(() => CopyDetailLine(detailEntry))
            };
            detailEntries[i] = detailEntry;
        }

        CleanupReceiptRuleEntry ruleEntry = null!;
        ruleEntry = new CleanupReceiptRuleEntry
        {
            EntryKey = $"R:{ruleIndex}:{item.TargetId}",
            TargetId = item.TargetId,
            DisplayName = item.DisplayName,
            Status = item.Status,
            StatusBadgeText = badge,
            SizeText = sizeText,
            ProcessedCountText = countText,
            Message = item.Message,
            SkippedCount = item.SkippedCount,
            FailedCount = item.FailedCount,
            SkippedBadgeText = $"跳过 {item.SkippedCount:N0}",
            FailedBadgeText = $"失败 {item.FailedCount:N0}",
            TotalDetailCount = detailEntries.Length,
            IsIssueRule = IsIssueItem(item),
            IsSucceededRule = IsSucceededItem(item),
            HeaderSearchIndex = $"{item.DisplayName} {item.TargetId} {badge} {item.Message}",
            StatusBadgeBackground = bg,
            StatusBadgeBorderBrush = border,
            StatusBadgeForeground = fg,
            SizeForeground = sizeFg,
            Details = detailEntries,
            ToggleExpandCommand = new RelayCommand(() => ToggleSingleRule(ruleEntry))
        };

        return ruleEntry;
    }

    private static (string Tag, Brush Bg, Brush Border, Brush Fg, Brush Guide) ClassifyDetailLine(string line)
    {
        if (line.Contains("失败", StringComparison.Ordinal)
            || line.Contains("保留原文件", StringComparison.Ordinal)
            || line.Contains("未能", StringComparison.Ordinal)
            || line.Contains("占用", StringComparison.Ordinal)
            || line.Contains("拒绝", StringComparison.Ordinal))
        {
            return ("保留/失败", RedSoftBrush, RedBorderBrush, RedBrush, RedBorderBrush);
        }

        if (line.Contains("拦截", StringComparison.Ordinal)
            || line.Contains("永久排除", StringComparison.Ordinal)
            || line.Contains("重解析点", StringComparison.Ordinal))
        {
            return ("安全拦截", AmberSoftBrush, AmberBorderBrush, AmberBrush, AmberBorderBrush);
        }

        if (line.Contains("跳过", StringComparison.Ordinal))
        {
            return ("已跳过", NeutralSurfaceBrush, NeutralBorderBrush, NeutralMutedBrush, NeutralBorderBrush);
        }

        return ("明细", PrimarySoftBrush, PrimaryBorderBrush, PrimaryBrush, PrimaryBorderBrush);
    }

    public static string BuildPlainTextReport(CleanupExecutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.SummaryMessage);
        sb.AppendLine($"完成时间：{result.CompletedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (CleanupExecutionItemResult item in result.Items)
        {
            string tag = item.Status switch
            {
                CleanupItemExecutionStatus.Quarantined => "已隔离·未释放物理空间",
                CleanupItemExecutionStatus.Recycled => "已移入回收站",
                CleanupItemExecutionStatus.ReclaimedByOfficialApi => "官方接口已回收",
                CleanupItemExecutionStatus.PermanentlyDeleted => "已直接永久删除·立即释放空间",
                CleanupItemExecutionStatus.Skipped => "已跳过",
                CleanupItemExecutionStatus.Blocked => "已拦截",
                _ => "失败"
            };
            sb.AppendLine($"• [{tag}] {item.DisplayName}：{item.Message}");
            if (item.Details is { Length: > 0 })
            {
                foreach (string d in item.Details)
                {
                    if (!string.IsNullOrWhiteSpace(d))
                    {
                        sb.AppendLine($"    - {d}");
                    }
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
