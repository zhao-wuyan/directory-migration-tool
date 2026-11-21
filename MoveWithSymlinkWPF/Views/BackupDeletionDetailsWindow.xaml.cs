using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MigrationCore.Models;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// 虚拟化备份处理明细弹窗（支持数万条逐项记录流畅浏览、筛选与检索）
/// </summary>
public partial class BackupDeletionDetailsWindow : Window
{
    private const int MaxCopyLines = 20000;

    private static readonly Brush GreenBg = Freeze(new SolidColorBrush(Color.FromRgb(220, 252, 231)));
    private static readonly Brush GreenBorder = Freeze(new SolidColorBrush(Color.FromRgb(134, 239, 172)));
    private static readonly Brush GreenFg = Freeze(new SolidColorBrush(Color.FromRgb(22, 101, 52)));

    private static readonly Brush RedBg = Freeze(new SolidColorBrush(Color.FromRgb(254, 226, 226)));
    private static readonly Brush RedBorder = Freeze(new SolidColorBrush(Color.FromRgb(252, 165, 165)));
    private static readonly Brush RedFg = Freeze(new SolidColorBrush(Color.FromRgb(153, 27, 27)));

    private static readonly Brush AmberBg = Freeze(new SolidColorBrush(Color.FromRgb(254, 243, 199)));
    private static readonly Brush AmberBorder = Freeze(new SolidColorBrush(Color.FromRgb(252, 211, 77)));
    private static readonly Brush AmberFg = Freeze(new SolidColorBrush(Color.FromRgb(146, 64, 14)));

    private static readonly Brush GrayBg = Freeze(new SolidColorBrush(Color.FromRgb(243, 244, 246)));
    private static readonly Brush GrayBorder = Freeze(new SolidColorBrush(Color.FromRgb(209, 213, 219)));
    private static readonly Brush GrayFg = Freeze(new SolidColorBrush(Color.FromRgb(55, 65, 81)));

    private readonly BackupDeletionResult _result;
    private readonly List<DeletionItemRow> _allRows;
    private List<DeletionItemRow> _filteredRows;
    private bool _initialized;

    public BackupDeletionDetailsWindow(string taskTitle, string? backupPath, BackupDeletionResult result)
    {
        InitializeComponent();
        _result = result;

        TaskTitleText.Text = string.IsNullOrWhiteSpace(taskTitle) ? "备份处理明细" : taskTitle;
        BackupPathText.Text = string.IsNullOrWhiteSpace(backupPath) ? "备份路径：未记录" : $"备份路径：{backupPath}";
        BackupPathText.ToolTip = backupPath;
        CompletedTimeText.Text = result.CompletedAtUtc is { } time
            ? $"完成时间：{time.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
            : "完成时间：未完成";

        ConfigureStatusBadge(result.Status);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            OverallErrorText.Text = result.Error;
            OverallErrorBanner.Visibility = Visibility.Visible;
        }

        _allRows = new List<DeletionItemRow>(result.Items.Length);
        int failedCount = 0;
        int succeededCount = 0;
        int retainedCount = 0;

        foreach (var item in result.Items)
        {
            var row = DeletionItemRow.From(item);
            _allRows.Add(row);
            if (item.Outcome is BackupItemOutcome.Failed or BackupItemOutcome.Unsupported) failedCount++;
            else if (item.Outcome is BackupItemOutcome.Deleted or BackupItemOutcome.Recycled) succeededCount++;
            else retainedCount++;
        }

        FilterAllRadio.Content = $"全部 {_allRows.Count}";
        FilterFailedRadio.Content = $"失败 {failedCount}";
        FilterSucceededRadio.Content = $"已处理 {succeededCount}";
        FilterRetainedRadio.Content = $"已保留 {retainedCount}";

        // 若存在失败项且整体并非完全成功，默认直接定位到「失败」或保持全部方便排查
        _filteredRows = _allRows;
        _initialized = true;
        ApplyFilter();
    }

    public static void ShowDetails(string taskTitle, string? backupPath, BackupDeletionResult result, Window? owner = null)
    {
        var dialog = new BackupDeletionDetailsWindow(taskTitle, backupPath, result)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        dialog.ShowDialog();
    }

    private void ConfigureStatusBadge(BackupDeletionStatus status)
    {
        (string text, Brush bg, Brush border, Brush fg) = status switch
        {
            BackupDeletionStatus.Recycled => ("已移入回收站", GreenBg, GreenBorder, GreenFg),
            BackupDeletionStatus.Deleted => ("已永久删除", GreenBg, GreenBorder, GreenFg),
            BackupDeletionStatus.Partial => ("部分处理", AmberBg, AmberBorder, AmberFg),
            BackupDeletionStatus.Failed => ("处理失败", RedBg, RedBorder, RedFg),
            BackupDeletionStatus.Unsupported => ("当前不支持", GrayBg, GrayBorder, GrayFg),
            _ => (status.ToString(), GrayBg, GrayBorder, GrayFg)
        };
        StatusBadgeText.Text = text;
        StatusBadgeBorder.Background = bg;
        StatusBadgeBorder.BorderBrush = border;
        StatusBadgeText.Foreground = fg;
    }

    private void FilterRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initialized) ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_initialized) ApplyFilter();
    }

    private void ApplyFilter()
    {
        string keyword = SearchBox.Text.Trim();
        bool hasKeyword = keyword.Length > 0;
        bool onlyFailed = FilterFailedRadio.IsChecked == true;
        bool onlySucceeded = FilterSucceededRadio.IsChecked == true;
        bool onlyRetained = FilterRetainedRadio.IsChecked == true;

        if (!hasKeyword && !onlyFailed && !onlySucceeded && !onlyRetained)
        {
            _filteredRows = _allRows;
        }
        else
        {
            var matched = new List<DeletionItemRow>();
            foreach (var row in _allRows)
            {
                if (onlyFailed && row.Outcome is not (BackupItemOutcome.Failed or BackupItemOutcome.Unsupported))
                    continue;
                if (onlySucceeded && row.Outcome is not (BackupItemOutcome.Deleted or BackupItemOutcome.Recycled))
                    continue;
                if (onlyRetained && row.Outcome != BackupItemOutcome.Retained)
                    continue;
                if (hasKeyword
                    && !row.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    && (row.Error is null || !row.Error.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    continue;
                matched.Add(row);
            }
            _filteredRows = matched;
        }

        ItemsListBox.ItemsSource = _filteredRows;
        EmptyHintText.Visibility = _filteredRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountSummaryText.Text = _filteredRows.Count == _allRows.Count
            ? $"共 {_allRows.Count} 项（已启用列表虚拟化）"
            : $"当前显示 {_filteredRows.Count} / {_allRows.Count} 项";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"任务：{TaskTitleText.Text}");
            sb.AppendLine($"结果：{StatusBadgeText.Text}");
            sb.AppendLine(CompletedTimeText.Text);
            sb.AppendLine(BackupPathText.Text);
            if (!string.IsNullOrWhiteSpace(_result.Error))
                sb.AppendLine($"错误：{_result.Error}");
            sb.AppendLine($"当前明细共 {_filteredRows.Count} 项：");

            int limit = Math.Min(_filteredRows.Count, MaxCopyLines);
            for (int i = 0; i < limit; i++)
            {
                var row = _filteredRows[i];
                sb.Append(row.OutcomeText).Append("：").Append(row.Path);
                if (!string.IsNullOrWhiteSpace(row.Error))
                    sb.Append("；").Append(row.Error);
                sb.AppendLine();
            }

            if (_filteredRows.Count > MaxCopyLines)
                sb.AppendLine($"...（超出 {MaxCopyLines} 行已截断，请按筛选条件缩小范围后复制）");

            Clipboard.SetDataObject(sb.ToString(), true);
            CopyButton.Content = "已复制";
        }
        catch
        {
            CopyButton.Content = "复制失败";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed class DeletionItemRow
    {
        public required BackupItemOutcome Outcome { get; init; }
        public required string OutcomeText { get; init; }
        public required string Path { get; init; }
        public string? Error { get; init; }
        public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(Error) ? Visibility.Collapsed : Visibility.Visible;
        public required Brush BadgeBackground { get; init; }
        public required Brush BadgeBorder { get; init; }
        public required Brush BadgeForeground { get; init; }

        public static DeletionItemRow From(BackupDeletionItem item)
        {
            (string label, Brush bg, Brush border, Brush fg) = item.Outcome switch
            {
                BackupItemOutcome.Recycled => ("已回收", GreenBg, GreenBorder, GreenFg),
                BackupItemOutcome.Deleted => ("已删除", GreenBg, GreenBorder, GreenFg),
                BackupItemOutcome.Failed => ("失败", RedBg, RedBorder, RedFg),
                BackupItemOutcome.Retained => ("已保留", AmberBg, AmberBorder, AmberFg),
                BackupItemOutcome.Unsupported => ("不支持", GrayBg, GrayBorder, GrayFg),
                _ => (item.Outcome.ToString(), GrayBg, GrayBorder, GrayFg)
            };

            return new DeletionItemRow
            {
                Outcome = item.Outcome,
                OutcomeText = label,
                Path = item.Path,
                Error = item.Error,
                BadgeBackground = bg,
                BadgeBorder = border,
                BadgeForeground = fg
            };
        }
    }
}
