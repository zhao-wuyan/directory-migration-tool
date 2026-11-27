using System.Windows;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MigrationCore.Services.Cleanup;
using MoveWithSymlinkWPF.Helpers;

namespace MoveWithSymlinkWPF.Views;

public partial class QuarantineDeleteConfirmWindow : Window
{
    private readonly string _expectedPhrase = QuarantineStore.DeleteConfirmationPhrase;
    private readonly QuarantineConfirmationPlan _plan;
    private bool _confirmed;

    public QuarantineConfirmationPlan ConfirmationPlan => _plan;

    public string EnteredPhrase => _confirmed ? _expectedPhrase : string.Empty;

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

        _plan = ConfirmationPolicy.EvaluateQuarantineDelete(batches);

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
        }

        DeleteHoldButton.HoldDurationSeconds = _plan.HoldSeconds;
        DeleteHoldButton.IsHoldRequired = true;

        if (_plan.RequiresCheckbox)
        {
            HighRiskConfirmCard.Visibility = Visibility.Visible;
            StandardHoldHintText.Visibility = Visibility.Collapsed;
            HighRiskReasonText.Text = $"高危确认（{string.Join(" · ", _plan.HighRiskReasons)}）：勾选下方确认后，按住右侧按钮 {_plan.HoldSeconds:0.0} 秒执行。";
            HighRiskAckCheckBox.Content = batches.Count == 1
                ? "我已核对以上隔离批次信息，确认永久删除且无法恢复"
                : $"我已核对以上 {batches.Count} 个待删批次，确认永久删除且均无法恢复";
            DeleteHoldButton.Label = batches.Count == 1
                ? $"按住 {_plan.HoldSeconds:0.0} 秒永久删除"
                : $"按住 {_plan.HoldSeconds:0.0} 秒删除（{batches.Count} 批）";
            DeleteHoldButton.IsEnabled = false;
        }
        else
        {
            HighRiskConfirmCard.Visibility = Visibility.Collapsed;
            StandardHoldHintText.Visibility = Visibility.Visible;
            StandardHoldHintText.Text = $"防误触确认：按住右侧红色按钮 {_plan.HoldSeconds:0.0} 秒即可永久删除，中途松手随时取消。";
            DeleteHoldButton.Label = $"按住 {_plan.HoldSeconds:0.0} 秒永久删除";
            DeleteHoldButton.IsEnabled = true;
        }
    }

    private void HighRiskAckCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_plan.RequiresCheckbox) return;
        DeleteHoldButton.IsEnabled = HighRiskAckCheckBox.IsChecked == true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _confirmed = false;
        DialogResult = false;
        Close();
    }

    private void DeleteHoldButton_OnConfirmed(object? sender, EventArgs e)
    {
        if (_plan.RequiresCheckbox && HighRiskAckCheckBox.IsChecked != true) return;
        _confirmed = true;
        DialogResult = true;
        Close();
    }
}
