using System.Windows;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MoveWithSymlinkWPF.Controls;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class CleanupConfirmWindow : Window
{
    private static readonly System.Windows.Media.Brush DisabledRadioBrush = CreateFrozenBrush(148, 163, 184);

    private readonly IReadOnlyList<CleanupRuleItemViewModel> _selectedItems;
    private readonly int _directEligibleCount;
    private readonly int _nonDirectChannelACount;
    private readonly bool _isInitialized;
    private CleanupConfirmationPlan _plan;
    private bool _confirmed;

    public CleanupConfirmationPlan ConfirmationPlan => _plan;

    public CleanupFileAction SelectedFileAction =>
        DirectDeleteRadio.IsChecked == true && DirectDeleteRadio.IsEnabled
            ? CleanupFileAction.DirectPermanentDelete
            : RecycleBinRadio.IsChecked == true && RecycleBinRadio.IsEnabled
                ? CleanupFileAction.RecycleBin
                : CleanupFileAction.QuarantineSameVolume;

    public string? EnteredCautionPhrase => _plan.HasCautionItems && _confirmed ? _plan.ExpectedCautionPhrase : null;

    public CleanupConfirmWindow(IReadOnlyList<CleanupRuleItemViewModel> selectedItems)
    {
        _selectedItems = selectedItems;
        _directEligibleCount = selectedItems.Count(i => i.Rule.SupportsDirectPermanentDelete);
        _nonDirectChannelACount = selectedItems.Count(i => i.Channel == CleanupExecutionChannel.FileQuarantine && !i.Rule.SupportsDirectPermanentDelete);

        InitializeComponent();

        SelectedItemsList.ItemsSource = selectedItems;
        long totalBytes = selectedItems.Sum(i => i.TotalBytes);
        string sizeLabel = totalBytes > 0 ? FileStatsService.FormatBytes(totalBytes) : "含官方系统接口项";
        ScopeHeaderText.Text = $"已选 {selectedItems.Count} 项清理规则（{sizeLabel}）：";

        bool hasChannelA = selectedItems.Any(i => i.Channel == CleanupExecutionChannel.FileQuarantine);
        bool hasChannelB = selectedItems.Any(i => i.Channel == CleanupExecutionChannel.OfficialSystemApi);
        bool forceQuarantineOnly = selectedItems.Any(i => i.Rule.ForceSameVolumeQuarantineOnly);

        ChannelAOptionsCard.Visibility = hasChannelA ? Visibility.Visible : Visibility.Collapsed;
        ChannelBNoticeCard.Visibility = hasChannelB ? Visibility.Visible : Visibility.Collapsed;

        if (_directEligibleCount > 0)
        {
            DirectDeleteRadio.IsEnabled = true;
            DirectDeleteRadio.Content = _nonDirectChannelACount > 0
                ? "直接永久删除 1 档推荐项（立即释放空间；所选 2/3 档项仍自动进入同卷隔离区）"
                : "直接永久删除（仅 1 档可重建缓存 · 立即释放物理空间，不可恢复）";
        }
        else
        {
            DirectDeleteRadio.IsEnabled = false;
            DirectDeleteRadio.Content = "直接永久删除（仅对 1 档可重建推荐清理项开放；本次未勾选 1 档推荐项）";
            DirectDeleteRadio.Foreground = DisabledRadioBrush;
        }

        if (forceQuarantineOnly)
        {
            QuarantineRadio.IsChecked = true;
            RecycleBinRadio.IsEnabled = false;
            QuarantineForcedNote.Visibility = Visibility.Visible;
        }

        _plan = ConfirmationPolicy.EvaluateCleanupConfirm(selectedItems, SelectedFileAction);

        if (_plan.HasCautionItems)
        {
            CleanupRuleItemViewModel[] cautionItems = selectedItems
                .Where(i => i.IsCaution && i.Rule.CautionReason is { RequiresPhraseConfirmation: true })
                .ToArray();

            CautionImpactSummary.Text = string.Join("\n", cautionItems.Select(c => $"• {c.DisplayName}：{c.Rule.CautionReason!.OneLineImpact}"));
            CautionConfirmCard.Visibility = Visibility.Visible;

            if (_plan.RequiresCheckbox)
            {
                CautionHeaderText.Text = "⚠ 包含 3 档不可恢复「谨慎清理」项 — 高危确认";
                CautionStandardHintText.Visibility = Visibility.Collapsed;
                CautionAckCheckBox.Visibility = Visibility.Visible;
            }
            else
            {
                CautionHeaderText.Text = "⚠ 包含 3 档「谨慎清理」项 — 长按确认执行";
                CautionStandardHintText.Visibility = Visibility.Visible;
                CautionStandardHintText.Text = $"防误触确认：按住右下角按钮 {_plan.HoldSeconds:0.0} 秒即可确认执行，中途松手随时取消。";
                CautionAckCheckBox.Visibility = Visibility.Collapsed;
            }
        }

        _isInitialized = true;
        RefreshConfirmationState();
    }

    private void FileActionRadio_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        RefreshConfirmationState();
    }

    private void RefreshConfirmationState()
    {
        _plan = ConfirmationPolicy.EvaluateCleanupConfirm(_selectedItems, SelectedFileAction);
        ConfirmHoldButton.CancelHold();

        if (_plan.HasDirectDeleteTargets)
        {
            DirectDeleteNoticeBox.Visibility = Visibility.Visible;
            DirectDeleteNoticeText.Text = _nonDirectChannelACount > 0
                ? $"⚠ 分档执行：本次 {_directEligibleCount} 项 1 档推荐缓存将直接永久删除并立即释放空间（不可恢复）；其余 {_nonDirectChannelACount} 项 2/3 档项仍自动进入同卷隔离区保留恢复机会。按住右下角按钮 {_plan.HoldSeconds:0.0} 秒确认执行。"
                : $"⚠ 不可恢复：本次 {_directEligibleCount} 项 1 档推荐缓存将直接永久删除并立即释放物理空间，不经过隔离区与回收站。按住右下角按钮 {_plan.HoldSeconds:0.0} 秒确认执行。";
        }
        else
        {
            DirectDeleteNoticeBox.Visibility = Visibility.Collapsed;
            DirectDeleteNoticeText.Text = string.Empty;
        }

        if (_plan.Tier == ConfirmationTier.HighRiskCheckboxAndHold)
        {
            ConfirmHoldButton.IsHoldRequired = true;
            ConfirmHoldButton.HoldDurationSeconds = _plan.HoldSeconds;
            ConfirmHoldButton.Tone = HoldButtonTone.Danger;
            ConfirmHoldButton.Label = $"按住 {_plan.HoldSeconds:0.0} 秒确认清理";
            ConfirmHoldButton.IsEnabled = CautionAckCheckBox.IsChecked == true;
        }
        else if (_plan.Tier == ConfirmationTier.StandardHold)
        {
            ConfirmHoldButton.IsHoldRequired = true;
            ConfirmHoldButton.HoldDurationSeconds = _plan.HoldSeconds;
            ConfirmHoldButton.Tone = _plan.HasDirectDeleteTargets ? HoldButtonTone.Danger : HoldButtonTone.Warning;
            ConfirmHoldButton.Label = _plan.HasDirectDeleteTargets && !_plan.HasCautionItems
                ? $"按住 {_plan.HoldSeconds:0.0} 秒确认直接删除"
                : $"按住 {_plan.HoldSeconds:0.0} 秒确认清理";
            ConfirmHoldButton.IsEnabled = true;
        }
        else
        {
            ConfirmHoldButton.IsHoldRequired = false;
            ConfirmHoldButton.Tone = HoldButtonTone.Primary;
            ConfirmHoldButton.Label = "确认执行清理";
            ConfirmHoldButton.IsEnabled = true;
        }
    }

    private void CautionAckCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_plan.RequiresCheckbox) return;
        ConfirmHoldButton.IsEnabled = CautionAckCheckBox.IsChecked == true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _confirmed = false;
        DialogResult = false;
        Close();
    }

    private void ConfirmHoldButton_OnConfirmed(object? sender, EventArgs e)
    {
        if (_plan.RequiresCheckbox && CautionAckCheckBox.IsChecked != true)
        {
            return;
        }
        _confirmed = true;
        DialogResult = true;
        Close();
    }

    private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
