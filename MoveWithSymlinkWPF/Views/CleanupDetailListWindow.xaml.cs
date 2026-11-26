using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MoveWithSymlinkWPF.Helpers;

namespace MoveWithSymlinkWPF.Views;

public enum CleanupDetailSortMode
{
    SizeDescending = 0,
    NameAscending = 1,
    PathAscending = 2
}

public sealed class CleanupDetailListRow
{
    public string FileName { get; init; } = string.Empty;
    public string DirectoryPath { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string KindTag { get; init; } = string.Empty;
    public string TypeAndSizeText { get; init; } = string.Empty;
    public string AccentFg { get; init; } = "#0F7B4B";
}

/// <summary>
/// 清理项完整明细查看与搜索弹窗：
/// - 后台线程构建与筛选，严格 UI 虚拟化 + 容器回收，支持数万条候选明细秒级搜索不卡顿；
/// - 仅提供只读浏览与资源管理器定位，不提供任何直接删除能力。
/// </summary>
public partial class CleanupDetailListWindow : Window
{
    private readonly CleanupRuleScanResult _scanResult;
    private readonly string _accentFg;
    private CleanupDetailListRow[] _allRows = [];
    private CleanupDetailSortMode _sortMode = CleanupDetailSortMode.SizeDescending;
    private CancellationTokenSource? _filterCts;
    private bool _isInitialized;

    public CleanupDetailListWindow(CleanupRuleScanResult scanResult)
    {
        InitializeComponent();
        _scanResult = scanResult ?? throw new ArgumentNullException(nameof(scanResult));

        CleanupRuleDefinition rule = scanResult.Rule;
        bool isCaution = rule.Tier == CleanupRecommendationTier.Caution;
        _accentFg = isCaution
            ? "#B45309"
            : (rule.Channel == CleanupExecutionChannel.OfficialSystemApi ? "#0078D4" : "#0F7B4B");

        Title = $"清理明细 · {rule.DisplayName}";
        RuleTitleText.Text = rule.DisplayName;
        RuleSummaryText.Text = $"{scanResult.StatusNote}（双击或点击「定位」可在资源管理器中只读核实文件）";

        ApplyTierBadge(rule.Tier);

        Loaded += CleanupDetailListWindow_OnLoaded;
        Closed += (_, _) =>
        {
            _filterCts?.Cancel();
            _filterCts?.Dispose();
        };
    }

    private void ApplyTierBadge(CleanupRecommendationTier tier)
    {
        (string text, string bg, string border, string fg) = tier switch
        {
            CleanupRecommendationTier.Recommended => ("建议清理", "#ECFDF5", "#A7F3D0", "#0F7B4B"),
            CleanupRecommendationTier.OnDemand => ("按需清理", "#EBF5FF", "#BFDBFE", "#0078D4"),
            _ => ("谨慎清理", "#FFFBEB", "#FDE68A", "#B45309")
        };

        TierBadgeText.Text = text;
        TierBadgeBorder.Background = (Brush)new BrushConverter().ConvertFromString(bg)!;
        TierBadgeBorder.BorderBrush = (Brush)new BrushConverter().ConvertFromString(border)!;
        TierBadgeText.Foreground = (Brush)new BrushConverter().ConvertFromString(fg)!;
    }

    private async void CleanupDetailListWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        EmptyOverlay.Visibility = Visibility.Visible;
        EmptyOverlayText.Text = "正在后台整理完整文件明细…";

        try
        {
            CleanupDetailListRow[] built = await Task.Run(() => BuildAllDetailRows(_scanResult, _accentFg));
            _allRows = built;
            ApplyFilterAndSortAsync(immediate: true);
        }
        catch (Exception ex)
        {
            EmptyOverlay.Visibility = Visibility.Visible;
            EmptyOverlayText.Text = $"加载明细失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 从扫描结果投影完整候选明细（通道 A 优先读取全量 VerificationExpectations，最多 5 万条；其他规则读取 PreviewItems）。
    /// </summary>
    public static CleanupDetailListRow[] BuildAllDetailRows(CleanupRuleScanResult scanResult, string accentFg)
    {
        int expectationCount = 0;
        foreach (CleanupDirectoryExpectation dirExp in scanResult.VerificationExpectations)
        {
            expectationCount += dirExp.ExpectedFiles.Count;
        }

        if (expectationCount > 0)
        {
            string kindTag = scanResult.Rule.Channel == CleanupExecutionChannel.OfficialSystemApi ? "官方接口" : "隔离区";
            var rows = new CleanupDetailListRow[expectationCount];
            int idx = 0;
            foreach (CleanupDirectoryExpectation dirExp in scanResult.VerificationExpectations)
            {
                foreach (CleanupFileExpectation file in dirExp.ExpectedFiles)
                {
                    string fullPath = file.FullPath;
                    string fileName = Path.GetFileName(fullPath);
                    if (string.IsNullOrEmpty(fileName)) fileName = fullPath;
                    string dirPath = Path.GetDirectoryName(fullPath) ?? string.Empty;

                    rows[idx++] = new CleanupDetailListRow
                    {
                        FileName = fileName,
                        DirectoryPath = dirPath,
                        FullPath = fullPath,
                        SizeBytes = file.ExpectedLogicalBytes,
                        KindTag = kindTag,
                        TypeAndSizeText = file.ExpectedLogicalBytes > 0
                            ? $"{kindTag} · {FileStatsService.FormatBytes(file.ExpectedLogicalBytes)}"
                            : kindTag,
                        AccentFg = accentFg
                    };
                }
            }
            return rows;
        }

        // 回退使用 PreviewItems（如 pnputil 旧驱动、DISM、Windows.old 等）
        var previewRows = new CleanupDetailListRow[scanResult.PreviewItems.Length];
        for (int i = 0; i < scanResult.PreviewItems.Length; i++)
        {
            CleanupPreviewItem p = scanResult.PreviewItems[i];
            string dirPath = string.IsNullOrWhiteSpace(p.FullPath)
                ? string.Empty
                : (Path.GetDirectoryName(p.FullPath) ?? p.FullPath);

            previewRows[i] = new CleanupDetailListRow
            {
                FileName = p.DisplayName,
                DirectoryPath = dirPath,
                FullPath = p.FullPath,
                SizeBytes = p.SizeBytes,
                KindTag = p.KindTag,
                TypeAndSizeText = p.SizeBytes > 0
                    ? $"{p.KindTag} · {FileStatsService.FormatBytes(p.SizeBytes)}"
                    : p.KindTag,
                AccentFg = accentFg
            };
        }
        return previewRows;
    }

    /// <summary>
    /// 纯内存高性能过滤与排序（支持空格多关键词 AND 匹配，可在后台线程与单元测试直接调用）。
    /// </summary>
    public static (CleanupDetailListRow[] Filtered, long MatchedBytes) FilterAndSortRows(
        CleanupDetailListRow[] source,
        string? query,
        CleanupDetailSortMode sortMode,
        CancellationToken ct = default)
    {
        string[] tokens = string.IsNullOrWhiteSpace(query)
            ? []
            : query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<CleanupDetailListRow> matched;
        long matchedBytes = 0;

        if (tokens.Length == 0)
        {
            matched = new List<CleanupDetailListRow>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                CleanupDetailListRow row = source[i];
                matched.Add(row);
                matchedBytes += row.SizeBytes;
            }
        }
        else
        {
            matched = new List<CleanupDetailListRow>(Math.Min(source.Length, 256));
            for (int i = 0; i < source.Length; i++)
            {
                if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                CleanupDetailListRow row = source[i];
                bool allHit = true;
                for (int t = 0; t < tokens.Length; t++)
                {
                    string tok = tokens[t];
                    if (!row.FileName.Contains(tok, StringComparison.OrdinalIgnoreCase)
                        && !row.FullPath.Contains(tok, StringComparison.OrdinalIgnoreCase)
                        && !row.KindTag.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    {
                        allHit = false;
                        break;
                    }
                }
                if (allHit)
                {
                    matched.Add(row);
                    matchedBytes += row.SizeBytes;
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        switch (sortMode)
        {
            case CleanupDetailSortMode.NameAscending:
                matched.Sort((a, b) =>
                {
                    int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
                    return cmp != 0 ? cmp : b.SizeBytes.CompareTo(a.SizeBytes);
                });
                break;
            case CleanupDetailSortMode.PathAscending:
                matched.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
                break;
            default:
                matched.Sort((a, b) =>
                {
                    int cmp = b.SizeBytes.CompareTo(a.SizeBytes);
                    return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
                });
                break;
        }

        return (matched.ToArray(), matchedBytes);
    }

    private async void ApplyFilterAndSortAsync(bool immediate)
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        CancellationToken ct = cts.Token;

        string query = SearchTextBox.Text ?? string.Empty;
        CleanupDetailSortMode sortMode = _sortMode;
        CleanupDetailListRow[] snapshot = _allRows;

        try
        {
            if (!immediate)
            {
                await Task.Delay(180, ct);
            }

            (CleanupDetailListRow[] filtered, long matchedBytes) = await Task.Run(
                () => FilterAndSortRows(snapshot, query, sortMode, ct), ct);

            if (ct.IsCancellationRequested) return;

            DetailListView.ItemsSource = filtered;
            if (filtered.Length == 0)
            {
                EmptyOverlay.Visibility = Visibility.Visible;
                EmptyOverlayText.Text = string.IsNullOrWhiteSpace(query)
                    ? "该清理规则当前无匹配明细。"
                    : $"未找到包含「{query.Trim()}」的文件或路径。";
            }
            else
            {
                EmptyOverlay.Visibility = Visibility.Collapsed;
            }

            FooterStatsText.Text = string.IsNullOrWhiteSpace(query)
                ? $"共 {filtered.Length:N0} 项候选明细 · 合计 {FileStatsService.FormatBytes(matchedBytes)}（只读预览，未执行任何修改）"
                : $"搜索命中 {filtered.Length:N0} / {snapshot.Length:N0} 项 · 命中体积 {FileStatsService.FormatBytes(matchedBytes)}";
        }
        catch (OperationCanceledException)
        {
            // 防抖或新输入触发取消，直接忽略
        }
    }

    private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_isInitialized)
        {
            ApplyFilterAndSortAsync(immediate: false);
        }
    }

    private void ClearSearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
    }

    private void SortSizeButton_OnClick(object sender, RoutedEventArgs e) => SetSortMode(CleanupDetailSortMode.SizeDescending);
    private void SortNameButton_OnClick(object sender, RoutedEventArgs e) => SetSortMode(CleanupDetailSortMode.NameAscending);
    private void SortPathButton_OnClick(object sender, RoutedEventArgs e) => SetSortMode(CleanupDetailSortMode.PathAscending);

    private void SetSortMode(CleanupDetailSortMode mode)
    {
        if (_sortMode == mode) return;
        _sortMode = mode;
        SortSizeButton.Tag = mode == CleanupDetailSortMode.SizeDescending ? "Active" : null;
        SortNameButton.Tag = mode == CleanupDetailSortMode.NameAscending ? "Active" : null;
        SortPathButton.Tag = mode == CleanupDetailSortMode.PathAscending ? "Active" : null;
        ApplyFilterAndSortAsync(immediate: true);
    }

    private void LocateRowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            OpenPathInExplorer(path);
        }
    }

    private void DetailListView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailListView.SelectedItem is CleanupDetailListRow row)
        {
            OpenPathInExplorer(row.FullPath);
        }
    }

    private static void OpenPathInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            string target = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
            if (!Directory.Exists(target))
            {
                CustomMessageBox.ShowWarning($"文件或目录已不存在：{path}", "无法定位");
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CustomMessageBox.ShowWarning($"无法打开资源管理器：{ex.Message}", "提示");
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
