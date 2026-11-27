using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;

namespace MoveWithSymlinkWPF.ViewModels;

public enum QuarantineOperationKind
{
    SingleDelete,
    BatchDelete,
    Restore
}

/// <summary>
/// 隔离区操作回执的单条明细行（全量预冻结画刷 + 行内直绑复制命令，支持万级明细虚拟化滚动）。
/// </summary>
public sealed partial class QuarantineResultDetailItem : ObservableObject
{
    public string Key { get; init; } = string.Empty;
    public string IndexText { get; init; } = string.Empty;
    public bool IsGroupSummaryLine { get; init; }
    public string ReasonTagText { get; init; } = string.Empty;
    public string ProcessHighlightText { get; init; } = string.Empty;
    public bool HasProcessHighlight => !string.IsNullOrWhiteSpace(ProcessHighlightText);
    public string PrimaryPathOrTitle { get; init; } = string.Empty;
    public string RawMessage { get; init; } = string.Empty;
    public string FriendlyHint { get; init; } = string.Empty;
    public bool HasFriendlyHint => !string.IsNullOrWhiteSpace(FriendlyHint);
    public string SearchIndex { get; init; } = string.Empty;

    public Brush RowBackground { get; init; } = Brushes.White;
    public Brush TagBackground { get; init; } = Brushes.Transparent;
    public Brush TagBorderBrush { get; init; } = Brushes.Transparent;
    public Brush TagForeground { get; init; } = Brushes.Black;
    public Brush GuideLineBrush { get; init; } = Brushes.Transparent;

    [ObservableProperty]
    private string _copyHintText = "复制";

    public IRelayCommand CopyLineCommand { get; init; } = null!;
}

/// <summary>
/// 隔离区操作结果弹窗 ViewModel（覆盖：单批永久删除、批量永久删除、一键恢复）。
/// </summary>
public partial class QuarantineOperationResultViewModel : ObservableObject
{
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

    private readonly IReadOnlyList<QuarantineResultDetailItem> _allDetails;
    private readonly string _plainTextReport;
    private readonly string? _quarantineDirectoryPath;
    private readonly Action? _closeWindowCallback;
    private string _lastVisibleSignature = string.Empty;

    public string WindowTitle { get; }
    public string HeaderTitle { get; }
    public string HeaderSubtitle { get; }
    public string StatusChipText { get; }
    public Brush HeaderAccentBackground { get; }
    public Brush HeaderAccentBorderBrush { get; }
    public Brush HeaderAccentForeground { get; }
    public bool IsSuccessHeader { get; }
    public bool IsWarningHeader { get; }
    public bool IsErrorHeader { get; }

    // 4 格 KPI
    public string Kpi1Label { get; }
    public string Kpi1Value { get; }
    public string Kpi1Sub { get; }
    public Brush Kpi1ValueBrush { get; }

    public string Kpi2Label { get; }
    public string Kpi2Value { get; }
    public string Kpi2Sub { get; }
    public Brush Kpi2ValueBrush { get; }

    public string Kpi3Label { get; }
    public string Kpi3Value { get; }
    public string Kpi3Sub { get; }
    public Brush Kpi3ValueBrush { get; }

    public string Kpi4Label { get; }
    public string Kpi4Value { get; }
    public string Kpi4Sub { get; }
    public Brush Kpi4ValueBrush { get; }

    public bool HasDetails => _allDetails.Count > 0;
    public bool ShowAllCleanBanner => _allDetails.Count == 0;
    public string AllCleanTitle { get; }
    public string AllCleanSubtitle { get; }
    public string DetailsSectionTitle { get; }

    public bool CanOpenQuarantineDir => !string.IsNullOrWhiteSpace(_quarantineDirectoryPath) && Directory.Exists(_quarantineDirectoryPath);
    public string FooterHintText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    private string _searchQuery = string.Empty;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSearchEmptyState))]
    private IReadOnlyList<QuarantineResultDetailItem> _visibleDetails = [];

    public bool ShowSearchEmptyState => HasDetails && VisibleDetails.Count == 0;

    [ObservableProperty]
    private string _visibleCountText = string.Empty;

    [ObservableProperty]
    private string _copyButtonText = "复制完整报告";

    private QuarantineOperationResultViewModel(
        string windowTitle,
        string headerTitle,
        string headerSubtitle,
        string statusChipText,
        bool isSuccess,
        bool isWarning,
        bool isError,
        (string Label, string Value, string Sub, Brush Brush) kpi1,
        (string Label, string Value, string Sub, Brush Brush) kpi2,
        (string Label, string Value, string Sub, Brush Brush) kpi3,
        (string Label, string Value, string Sub, Brush Brush) kpi4,
        string detailsSectionTitle,
        string allCleanTitle,
        string allCleanSubtitle,
        string footerHintText,
        IReadOnlyList<string> rawDetails,
        string plainTextReport,
        string? quarantineDirectoryPath,
        Action? closeWindowCallback)
    {
        WindowTitle = windowTitle;
        HeaderTitle = headerTitle;
        HeaderSubtitle = headerSubtitle;
        StatusChipText = statusChipText;
        IsSuccessHeader = isSuccess;
        IsWarningHeader = isWarning;
        IsErrorHeader = isError;

        if (isError)
        {
            HeaderAccentBackground = RedSoftBrush;
            HeaderAccentBorderBrush = RedBorderBrush;
            HeaderAccentForeground = RedBrush;
        }
        else if (isWarning)
        {
            HeaderAccentBackground = AmberSoftBrush;
            HeaderAccentBorderBrush = AmberBorderBrush;
            HeaderAccentForeground = AmberBrush;
        }
        else
        {
            HeaderAccentBackground = GreenSoftBrush;
            HeaderAccentBorderBrush = GreenBorderBrush;
            HeaderAccentForeground = GreenBrush;
        }

        (Kpi1Label, Kpi1Value, Kpi1Sub, Kpi1ValueBrush) = kpi1;
        (Kpi2Label, Kpi2Value, Kpi2Sub, Kpi2ValueBrush) = kpi2;
        (Kpi3Label, Kpi3Value, Kpi3Sub, Kpi3ValueBrush) = kpi3;
        (Kpi4Label, Kpi4Value, Kpi4Sub, Kpi4ValueBrush) = kpi4;

        DetailsSectionTitle = detailsSectionTitle;
        AllCleanTitle = allCleanTitle;
        AllCleanSubtitle = allCleanSubtitle;
        FooterHintText = footerHintText;
        _plainTextReport = plainTextReport;
        _quarantineDirectoryPath = quarantineDirectoryPath;
        _closeWindowCallback = closeWindowCallback;

        string[] cleaned = rawDetails.Where(d => !string.IsNullOrWhiteSpace(d)).ToArray();
        var items = new QuarantineResultDetailItem[cleaned.Length];
        for (int i = 0; i < cleaned.Length; i++)
        {
            items[i] = BuildDetailItem(cleaned[i], i);
        }
        _allDetails = items;

        ApplyFilter();
    }

    public static QuarantineOperationResultViewModel FromSingleDelete(
        QuarantineDeleteResult result,
        string batchDisplayTitle,
        string? quarantineDirectoryPath = null,
        Action? closeWindowCallback = null)
    {
        bool isSuccess = result.FullyDeleted;
        bool isError = !result.FullyDeleted && result.DeletedEntryCount == 0;
        bool isWarning = !isSuccess && !isError;

        string windowTitle = isSuccess ? "永久删除完成" : "永久删除结果";
        string headerTitle = isSuccess
            ? $"已永久删除「{batchDisplayTitle}」"
            : isWarning
                ? $"部分永久删除完成（仍有 {result.FailedEntryCount:N0} 项保留在隔离区）"
                : $"未能删除「{batchDisplayTitle}」";
        string statusChip = isSuccess ? "已全部释放" : isWarning ? "部分删除" : "删除失败";
        string headerSubtitle = $"完成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 批次：{batchDisplayTitle}";

        string freedText = FileStatsService.FormatBytes(result.FreedBytesEstimate);
        var kpi1 = ("预计释放物理空间", freedText, result.FreedBytesEstimate > 0 ? "已从物理磁盘永久回收" : "本次未释放物理空间", result.FreedBytesEstimate > 0 ? GreenBrush : NeutralMutedBrush);
        var kpi2 = ("已永久删除", $"{result.DeletedEntryCount:N0} 项", "已安全移除出隔离区", result.DeletedEntryCount > 0 ? GreenBrush : NeutralMutedBrush);
        var kpi3 = ("未能删除（已保留）", $"{result.FailedEntryCount:N0} 项", result.FailedEntryCount > 0 ? "仍在隔离区保留 · 可重试或手动处理" : "零失败 · 全部删除", result.FailedEntryCount > 0 ? RedBrush : GreenBrush);
        var kpi4 = ("隔离批次状态", result.FullyDeleted ? "已清空移除" : "保留待处理", result.FullyDeleted ? "批次目录已同步清理" : "未删文件继续受隔离区管理", result.FullyDeleted ? GreenBrush : AmberBrush);

        string report = BuildPlainText(windowTitle, result.Message, result.Details);
        string footerHint = result.FullyDeleted
            ? "物理磁盘空间已释放，隔离区列表已同步更新。"
            : "未能删除的文件通常受进程占用、只读属性或安全软件锁定；关闭占用程序后可再次重试，或直接打开隔离目录查看。";

        return new QuarantineOperationResultViewModel(
            windowTitle: windowTitle,
            headerTitle: headerTitle,
            headerSubtitle: headerSubtitle,
            statusChipText: statusChip,
            isSuccess: isSuccess,
            isWarning: isWarning,
            isError: isError,
            kpi1: kpi1,
            kpi2: kpi2,
            kpi3: kpi3,
            kpi4: kpi4,
            detailsSectionTitle: $"未删除 / 异常明细（共 {result.Details.Length:N0} 条）",
            allCleanTitle: "该隔离批次已彻底清除并释放物理空间",
            allCleanSubtitle: $"共永久删除 {result.DeletedEntryCount:N0} 项（约 {freedText}），隔离目录空壳已同步移除。",
            footerHintText: footerHint,
            rawDetails: result.Details,
            plainTextReport: report,
            quarantineDirectoryPath: result.FullyDeleted ? null : quarantineDirectoryPath,
            closeWindowCallback: closeWindowCallback);
    }

    public static QuarantineOperationResultViewModel FromBatchDelete(
        bool allSucceeded,
        bool wasCancelled,
        int totalSelectedBatches,
        int fullyDeletedBatches,
        int partialOrFailedBatches,
        long freedBytes,
        string summaryMessage,
        IReadOnlyList<string> details,
        Action? closeWindowCallback = null)
    {
        bool isSuccess = allSucceeded && !wasCancelled;
        bool isError = !wasCancelled && fullyDeletedBatches == 0 && partialOrFailedBatches > 0;
        bool isWarning = !isSuccess && !isError;

        string windowTitle = wasCancelled ? "批量永久删除已中止" : isSuccess ? "批量永久删除完成" : "批量永久删除结果";
        string headerTitle = wasCancelled
            ? "批量永久删除已由用户中止"
            : isSuccess
                ? $"已批量永久删除全部 {fullyDeletedBatches:N0} 个隔离批次"
                : $"批量永久删除部分完成（{partialOrFailedBatches:N0} 批存在残留）";
        string statusChip = wasCancelled ? "已中止" : isSuccess ? "全部释放" : "部分完成";
        string headerSubtitle = $"完成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 共处理 {totalSelectedBatches:N0} 个隔离批次";

        string freedText = FileStatsService.FormatBytes(freedBytes);
        int untouched = Math.Max(0, totalSelectedBatches - fullyDeletedBatches - partialOrFailedBatches);
        var kpi1 = ("预计释放物理空间", freedText, freedBytes > 0 ? "已从物理磁盘永久回收" : "尚未释放物理空间", freedBytes > 0 ? GreenBrush : NeutralMutedBrush);
        var kpi2 = ("已清空批次", $"{fullyDeletedBatches:N0} / {totalSelectedBatches:N0} 批", "批次文件与空壳已移除", fullyDeletedBatches > 0 ? GreenBrush : NeutralMutedBrush);
        var kpi3 = ("部分/失败批次", $"{partialOrFailedBatches:N0} 批", partialOrFailedBatches > 0 ? "未删文件仍保留在隔离区" : "零异常批次", partialOrFailedBatches > 0 ? RedBrush : GreenBrush);
        var kpi4 = ("未执行（已保留）", $"{untouched:N0} 批", untouched > 0 ? "中止后保持原状" : "所选批次均已处理", untouched > 0 ? AmberBrush : NeutralMutedBrush);

        string report = BuildPlainText(windowTitle, summaryMessage, details);
        string footerHint = isSuccess
            ? "所选隔离批次已全部永久释放，隔离区列表已同步刷新。"
            : "未删除或中止的批次已完整保留在隔离区中，可解决占用后随时重试。";

        return new QuarantineOperationResultViewModel(
            windowTitle: windowTitle,
            headerTitle: headerTitle,
            headerSubtitle: headerSubtitle,
            statusChipText: statusChip,
            isSuccess: isSuccess,
            isWarning: isWarning,
            isError: isError,
            kpi1: kpi1,
            kpi2: kpi2,
            kpi3: kpi3,
            kpi4: kpi4,
            detailsSectionTitle: $"残留 / 异常明细（共 {details.Count:N0} 条）",
            allCleanTitle: $"全部 {fullyDeletedBatches:N0} 个所选隔离批次已彻底清除",
            allCleanSubtitle: $"累计释放约 {freedText} 物理空间，无残留错误。",
            footerHintText: footerHint,
            rawDetails: details,
            plainTextReport: report,
            quarantineDirectoryPath: null,
            closeWindowCallback: closeWindowCallback);
    }

    public static QuarantineOperationResultViewModel FromRestore(
        QuarantineRestoreResult result,
        string batchDisplayTitle,
        string? quarantineDirectoryPath = null,
        Action? closeWindowCallback = null)
    {
        bool isSuccess = result.Succeeded && result.SkippedOrFailedCount == 0;
        bool isError = !result.Succeeded && result.RestoredCount == 0;
        bool isWarning = !isSuccess && !isError;

        string windowTitle = isSuccess ? "恢复完成" : "恢复结果";
        string headerTitle = isSuccess
            ? $"已完整恢复「{batchDisplayTitle}」"
            : isWarning
                ? $"部分恢复完成（仍有 {result.SkippedOrFailedCount:N0} 项留在隔离区）"
                : $"未能恢复「{batchDisplayTitle}」";
        string statusChip = isSuccess ? "恢复完成" : isWarning ? "部分恢复" : "恢复受阻";
        string headerSubtitle = $"完成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 批次：{batchDisplayTitle}";

        var kpi1 = ("已恢复文件", $"{result.RestoredCount:N0} 项", "已原子移回原目录", result.RestoredCount > 0 ? GreenBrush : NeutralMutedBrush);
        var kpi2 = ("对比保留双方", $"{result.RestoredWithSuffixCount:N0} 项", result.RestoredWithSuffixCount > 0 ? "已附加 .restored_ 后缀防覆盖" : "无同名冲突", result.RestoredWithSuffixCount > 0 ? PrimaryBrush : NeutralMutedBrush);
        var kpi3 = ("跳过 / 失败", $"{result.SkippedOrFailedCount:N0} 项", result.SkippedOrFailedCount > 0 ? "仍安全保留在隔离区" : "零失败 · 全部归位", result.SkippedOrFailedCount > 0 ? RedBrush : GreenBrush);
        var kpi4 = ("原隔离批次", result.BatchRemovedAfterFullRestore ? "已自动清理" : "保留剩余项", result.BatchRemovedAfterFullRestore ? "全部归位后已移除空批次" : "未恢复文件继续保存在隔离区", result.BatchRemovedAfterFullRestore ? GreenBrush : AmberBrush);

        string report = BuildPlainText(windowTitle, result.Message, result.Details);
        string footerHint = result.BatchRemovedAfterFullRestore
            ? "所有文件均已放回原路径，原隔离批次已同步清空。"
            : "未能恢复的文件已原样保留在隔离区，绝不静默覆盖原路径现有文件。";

        return new QuarantineOperationResultViewModel(
            windowTitle: windowTitle,
            headerTitle: headerTitle,
            headerSubtitle: headerSubtitle,
            statusChipText: statusChip,
            isSuccess: isSuccess,
            isWarning: isWarning,
            isError: isError,
            kpi1: kpi1,
            kpi2: kpi2,
            kpi3: kpi3,
            kpi4: kpi4,
            detailsSectionTitle: $"恢复过程明细（共 {result.Details.Length:N0} 条）",
            allCleanTitle: $"已将「{batchDisplayTitle}」全部 {result.RestoredCount:N0} 项完整恢复至原路径",
            allCleanSubtitle: "全程未发生任何覆盖或冲突，空隔离批次已自动清理。",
            footerHintText: footerHint,
            rawDetails: result.Details,
            plainTextReport: report,
            quarantineDirectoryPath: result.BatchRemovedAfterFullRestore ? null : quarantineDirectoryPath,
            closeWindowCallback: closeWindowCallback);
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

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
    private void OpenQuarantineDirectory()
    {
        if (!CanOpenQuarantineDir || string.IsNullOrWhiteSpace(_quarantineDirectoryPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_quarantineDirectoryPath}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void CloseWindow() => _closeWindowCallback?.Invoke();

    private void ApplyFilter()
    {
        IEnumerable<QuarantineResultDetailItem> query = _allDetails;
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string[] tokens = SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string token in tokens)
            {
                string t = token;
                query = query.Where(item => item.SearchIndex.Contains(t, StringComparison.OrdinalIgnoreCase));
            }
        }

        QuarantineResultDetailItem[] filtered = query.ToArray();
        string sig = string.Join("|", filtered.Select(i => i.Key));
        if (!string.Equals(sig, _lastVisibleSignature, StringComparison.Ordinal))
        {
            VisibleDetails = filtered;
            _lastVisibleSignature = sig;
        }

        VisibleCountText = filtered.Length == _allDetails.Count
            ? $"共 {filtered.Length:N0} 条明细"
            : $"命中 {filtered.Length:N0} / {_allDetails.Count:N0} 条";
    }

    private static QuarantineResultDetailItem BuildDetailItem(string rawLine, int index)
    {
        string normalized = rawLine.Trim();
        bool isGroupSummary = normalized.StartsWith("•", StringComparison.Ordinal);
        string cleanedText = normalized
            .TrimStart('•', '-', ' ')
            .Replace(@"\\?\", string.Empty, StringComparison.Ordinal);

        string tagText;
        string processHighlight = string.Empty;
        string friendlyHint;
        Brush rowBg = Brushes.White;
        Brush bg;
        Brush border;
        Brush fg;
        Brush guide;

        if (isGroupSummary)
        {
            tagText = normalized.Contains("已取消", StringComparison.Ordinal) ? "已中止" : "批次汇总";
            friendlyHint = string.Empty;
            rowBg = NeutralSurfaceBrush;
            bg = Brushes.White;
            border = NeutralBorderBrush;
            fg = NeutralMutedBrush;
            guide = NeutralBorderBrush;
        }
        else if (normalized.Contains("正被进程占用：", StringComparison.Ordinal))
        {
            string lockers = ExtractLockingProcessSummary(cleanedText);
            tagText = "进程占用中";
            processHighlight = string.IsNullOrWhiteSpace(lockers) ? string.Empty : $"占用进程：{lockers}";
            friendlyHint = string.IsNullOrWhiteSpace(lockers)
                ? "请先退出占用该文件的程序后再重试删除。"
                : $"关闭 {lockers} 后即可在隔离区重试删除。";
            bg = RedSoftBrush;
            border = RedBorderBrush;
            fg = RedBrush;
            guide = RedBorderBrush;
        }
        else if (normalized.Contains("is denied", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("拒绝访问", StringComparison.Ordinal)
            || normalized.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase))
        {
            tagText = "权限/镜像受限";
            friendlyHint = "可能为运行中的 .exe/.dll 镜像、只读保护或安全软件锁定；关闭对应程序或重启后重试。";
            bg = RedSoftBrush;
            border = RedBorderBrush;
            fg = RedBrush;
            guide = RedBorderBrush;
        }
        else if (normalized.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("正由另一进程使用", StringComparison.Ordinal)
            || normalized.Contains("占用", StringComparison.Ordinal))
        {
            tagText = "进程占用中";
            friendlyHint = "请先退出正在使用该文件的程序后再重试。";
            bg = RedSoftBrush;
            border = RedBorderBrush;
            fg = RedBrush;
            guide = RedBorderBrush;
        }
        else if (normalized.Contains("已拒绝覆盖", StringComparison.Ordinal)
            || normalized.Contains("已存在", StringComparison.Ordinal))
        {
            tagText = "同名冲突保护";
            friendlyHint = "原路径已有同名新文件，可使用「对比后恢复」保留双方。";
            bg = AmberSoftBrush;
            border = AmberBorderBrush;
            fg = AmberBrush;
            guide = AmberBorderBrush;
        }
        else if (normalized.Contains("已保留双方", StringComparison.Ordinal))
        {
            tagText = "对比恢复";
            friendlyHint = string.Empty;
            bg = PrimarySoftBrush;
            border = PrimaryBorderBrush;
            fg = PrimaryBrush;
            guide = PrimaryBorderBrush;
        }
        else if (normalized.Contains("已取消", StringComparison.Ordinal))
        {
            tagText = "用户中止";
            friendlyHint = string.Empty;
            bg = NeutralSurfaceBrush;
            border = NeutralBorderBrush;
            fg = NeutralMutedBrush;
            guide = NeutralBorderBrush;
        }
        else if (normalized.Contains("失败", StringComparison.Ordinal)
            || normalized.Contains("异常", StringComparison.Ordinal))
        {
            tagText = "处理失败";
            friendlyHint = string.Empty;
            bg = RedSoftBrush;
            border = RedBorderBrush;
            fg = RedBrush;
            guide = RedBorderBrush;
        }
        else
        {
            tagText = "明细";
            friendlyHint = string.Empty;
            bg = NeutralSurfaceBrush;
            border = NeutralBorderBrush;
            fg = NeutralMutedBrush;
            guide = NeutralBorderBrush;
        }

        QuarantineResultDetailItem item = null!;
        item = new QuarantineResultDetailItem
        {
            Key = $"QD:{index}",
            IndexText = $"#{index + 1}",
            IsGroupSummaryLine = isGroupSummary,
            ReasonTagText = tagText,
            ProcessHighlightText = processHighlight,
            PrimaryPathOrTitle = cleanedText,
            RawMessage = normalized,
            FriendlyHint = friendlyHint,
            SearchIndex = $"{tagText} {processHighlight} {cleanedText} {friendlyHint}",
            RowBackground = rowBg,
            TagBackground = bg,
            TagBorderBrush = border,
            TagForeground = fg,
            GuideLineBrush = guide,
            CopyLineCommand = new RelayCommand(() => CopyDetailLine(item))
        };
        return item;
    }

    private static string ExtractLockingProcessSummary(string message)
    {
        const string marker = "正被进程占用：";
        int start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += marker.Length;
        int end = message.IndexOf('；', start);
        if (end < 0) end = message.IndexOf('）', start);
        return end > start ? message[start..end].Trim() : message[start..].Trim();
    }

    private static void CopyDetailLine(QuarantineResultDetailItem item)
    {
        try
        {
            Clipboard.SetText(item.RawMessage);
            item.CopyHintText = "已复制 ✓";
        }
        catch
        {
            item.CopyHintText = "失败";
        }
    }

    private static string BuildPlainText(string title, string summary, IReadOnlyList<string> details)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(summary);
        if (details.Count > 0)
        {
            sb.AppendLine();
            for (int i = 0; i < details.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(details[i]))
                {
                    sb.AppendLine($"#{i + 1} {details[i]}");
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
