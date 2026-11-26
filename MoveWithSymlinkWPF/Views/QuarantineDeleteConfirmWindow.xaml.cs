using System.Windows;
using System.Windows.Controls;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MigrationCore.Services.Cleanup;

namespace MoveWithSymlinkWPF.Views;

public partial class QuarantineDeleteConfirmWindow : Window
{
    private readonly string _expectedPhrase = QuarantineStore.DeleteConfirmationPhrase;

    public string EnteredPhrase => PhraseInputBox.Text.Trim();

    private sealed record BatchPreviewRow(
        string DisplayTitle,
        string MetaText,
        string SizeText,
        Visibility AiBadgeVisibility);

    public QuarantineDeleteConfirmWindow(QuarantineBatchRecord batch)
        : this([batch])
    {
    }

    public QuarantineDeleteConfirmWindow(IReadOnlyList<QuarantineBatchRecord> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0)
        {
            throw new ArgumentException("至少需要指定 1 个待删除的隔离批次。", nameof(batches));
        }

        InitializeComponent();
        ExpectedPhraseText.Text = _expectedPhrase;

        int aiCount = batches.Count(b => b.IsFromAiSuspectedResidual);
        if (aiCount > 0)
        {
            AiResidualWarningCard.Visibility = Visibility.Visible;
            AiResidualWarningText.Text = batches.Count == 1
                ? "注意：该批次属于「AI 疑似残留」目录，请再次确认相关软件与个人数据均正常运行后再永久删除。"
                : $"注意：本次所选包含 {aiCount} 批「AI 疑似残留」目录，请再次确认相关软件与数据正常后再执行批量永久删除。";
        }

        if (batches.Count == 1)
        {
            QuarantineBatchRecord batch = batches[0];
            BatchTitleText.Text = batch.DisplayTitle;
            BatchSourceText.Text = $"原路径映射：{batch.SourceDisplaySummary}";
            BatchMetaText.Text = $"批次：{batch.BatchId} · 所在卷：{batch.DriveRoot} · 共 {batch.ItemCount:N0} 项 · 占用 {FileStatsService.FormatBytes(batch.TotalBytes)}";
        }
        else
        {
            long totalBytes = batches.Sum(b => b.TotalBytes);
            int totalItems = batches.Sum(b => b.ItemCount);

            Title = "二次确认 · 批量永久删除隔离项";
            DialogHeaderText.Text = $"批量永久删除 {batches.Count} 个隔离区批次（不可恢复）";
            WarningBannerText.Text = $"此操作将从隔离区逐批永久删除以下 {batches.Count} 个冻结批次（预计释放 {FileStatsService.FormatBytes(totalBytes)} 物理空间），删除后均无法再从隔离区恢复。取消将完整保留所有隔离文件。";
            SingleBatchCard.Visibility = Visibility.Collapsed;
            MultiBatchCard.Visibility = Visibility.Visible;
            MultiBatchSummaryText.Text = $"待删批次清单（共 {batches.Count} 批 · {totalItems:N0} 项 · 预计释放 {FileStatsService.FormatBytes(totalBytes)}）：";
            MultiBatchItemsList.ItemsSource = batches.Select(b => new BatchPreviewRow(
                DisplayTitle: b.DisplayTitle,
                MetaText: $"卷 {b.DriveRoot.TrimEnd('\\')} · {b.ItemCount:N0} 项 · {b.SourceDisplaySummary}",
                SizeText: FileStatsService.FormatBytes(b.TotalBytes),
                AiBadgeVisibility: b.IsFromAiSuspectedResidual ? Visibility.Visible : Visibility.Collapsed)).ToArray();
            DeleteButton.Content = $"确认永久删除（{batches.Count} 批）";
        }
    }

    private void PhraseInputBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        DeleteButton.IsEnabled = string.Equals(PhraseInputBox.Text.Trim(), _expectedPhrase, StringComparison.Ordinal);
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(PhraseInputBox.Text.Trim(), _expectedPhrase, StringComparison.Ordinal)) return;
        DialogResult = true;
        Close();
    }
}
