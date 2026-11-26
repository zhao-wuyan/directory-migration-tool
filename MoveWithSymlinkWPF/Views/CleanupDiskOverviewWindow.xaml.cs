using System.Windows;
using System.Windows.Controls;
using MigrationCore.Services;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public enum CleanupDiskSortMode
{
    Urgency = 0,
    FreeSpaceAscending = 1,
    DriveLetter = 2
}

public partial class CleanupDiskOverviewWindow : Window
{
    private readonly CleanupDiskOverviewItem[] _allDisks;
    private readonly Action? _onOpenQuarantine;
    private CleanupDiskSortMode _sortMode = CleanupDiskSortMode.Urgency;

    public CleanupDiskOverviewWindow(
        IEnumerable<CleanupDiskOverviewItem> disks,
        Action? onOpenQuarantine = null)
    {
        InitializeComponent();
        _allDisks = disks.ToArray();
        _onOpenQuarantine = onOpenQuarantine;

        long totalFree = _allDisks.Sum(d => d.FreeBytes);
        long totalCap = _allDisks.Sum(d => d.TotalBytes);
        int tightCount = _allDisks.Count(d => d.UsedPercent >= 85d);
        long totalQuarantine = _allDisks.Sum(d => d.QuarantineBytes);

        TotalSummaryBadge.Text = tightCount > 0
            ? $"共 {_allDisks.Length} 块固定磁盘 · {tightCount} 块紧张"
            : $"共 {_allDisks.Length} 块固定磁盘";

        FooterSummaryText.Text = totalQuarantine > 0
            ? $"总可用 {FileStatsService.FormatBytes(totalFree)} / {FileStatsService.FormatBytes(totalCap)} · 全盘隔离区暂存 {FileStatsService.FormatBytes(totalQuarantine)}"
            : $"总可用 {FileStatsService.FormatBytes(totalFree)} / {FileStatsService.FormatBytes(totalCap)}";

        if (totalQuarantine > 0 && _onOpenQuarantine is not null)
        {
            OpenQuarantineButton.Visibility = Visibility.Visible;
        }

        ApplyFilterAndSort();
    }

    public static CleanupDiskOverviewItem[] FilterAndSort(
        IReadOnlyList<CleanupDiskOverviewItem> allDisks,
        string? query,
        CleanupDiskSortMode sortMode)
    {
        IEnumerable<CleanupDiskOverviewItem> filtered = allDisks;
        if (!string.IsNullOrWhiteSpace(query))
        {
            string q = query.Trim();
            filtered = filtered.Where(d =>
                d.DriveLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
                || d.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                || d.VolumeLabel.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return sortMode switch
        {
            CleanupDiskSortMode.FreeSpaceAscending => filtered
                .OrderBy(d => d.FreeBytes)
                .ThenBy(d => d.DriveLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CleanupDiskSortMode.DriveLetter => filtered
                .OrderBy(d => d.DriveLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => filtered
                .OrderByDescending(d => d.IsSystemDrive)
                .ThenByDescending(d => d.UsedPercent >= 85d)
                .ThenByDescending(d => d.HasQuarantineData)
                .ThenByDescending(d => d.UsedPercent)
                .ThenBy(d => d.DriveLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private void ApplyFilterAndSort()
    {
        string? q = SearchTextBox.Text;
        ClearSearchButton.Visibility = string.IsNullOrEmpty(q) ? Visibility.Collapsed : Visibility.Visible;
        DiskListView.ItemsSource = FilterAndSort(_allDisks, q, _sortMode);
        UpdateSortButtonStyles();
    }

    private void UpdateSortButtonStyles()
    {
        var activeStyle = TryFindResource("FluentSoftPrimaryButtonStyle") as Style;
        var normalStyle = TryFindResource("FluentSecondaryButtonStyle") as Style;
        if (activeStyle is null || normalStyle is null) return;

        SortUrgencyButton.Style = _sortMode == CleanupDiskSortMode.Urgency ? activeStyle : normalStyle;
        SortFreeButton.Style = _sortMode == CleanupDiskSortMode.FreeSpaceAscending ? activeStyle : normalStyle;
        SortLetterButton.Style = _sortMode == CleanupDiskSortMode.DriveLetter ? activeStyle : normalStyle;
    }

    private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilterAndSort();

    private void ClearSearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
    }

    private void SortUrgencyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _sortMode = CleanupDiskSortMode.Urgency;
        ApplyFilterAndSort();
    }

    private void SortFreeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _sortMode = CleanupDiskSortMode.FreeSpaceAscending;
        ApplyFilterAndSort();
    }

    private void SortLetterButton_OnClick(object sender, RoutedEventArgs e)
    {
        _sortMode = CleanupDiskSortMode.DriveLetter;
        ApplyFilterAndSort();
    }

    private void OpenQuarantineButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
        _onOpenQuarantine?.Invoke();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
