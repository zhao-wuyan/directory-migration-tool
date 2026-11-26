using System.Windows;
using System.Windows.Controls;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class CleanupConfirmWindow : Window
{
    private readonly bool _requiresPhrase;
    private readonly string _expectedPhrase = "确认清理";

    public CleanupFileAction SelectedFileAction =>
        RecycleBinRadio.IsChecked == true && RecycleBinRadio.IsEnabled
            ? CleanupFileAction.RecycleBin
            : CleanupFileAction.QuarantineSameVolume;

    public string? EnteredCautionPhrase => _requiresPhrase ? PhraseInputBox.Text.Trim() : null;

    public CleanupConfirmWindow(IReadOnlyList<CleanupRuleItemViewModel> selectedItems)
    {
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

        if (forceQuarantineOnly)
        {
            QuarantineRadio.IsChecked = true;
            RecycleBinRadio.IsEnabled = false;
            QuarantineForcedNote.Visibility = Visibility.Visible;
        }

        CleanupRuleItemViewModel[] cautionItems = selectedItems
            .Where(i => i.IsCaution && i.Rule.CautionReason is { RequiresPhraseConfirmation: true })
            .ToArray();

        if (cautionItems.Length > 0)
        {
            _requiresPhrase = true;
            _expectedPhrase = cautionItems[0].Rule.CautionReason!.ConfirmationPhrase;
            ExpectedPhraseText.Text = _expectedPhrase;
            CautionImpactSummary.Text = string.Join("\n", cautionItems.Select(c => $"• {c.DisplayName}：{c.Rule.CautionReason!.OneLineImpact}"));
            CautionPhraseCard.Visibility = Visibility.Visible;
            ConfirmButton.IsEnabled = false;
        }
    }

    private void PhraseInputBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_requiresPhrase) return;
        ConfirmButton.IsEnabled = string.Equals(PhraseInputBox.Text.Trim(), _expectedPhrase, StringComparison.Ordinal);
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_requiresPhrase && !string.Equals(PhraseInputBox.Text.Trim(), _expectedPhrase, StringComparison.Ordinal))
        {
            return;
        }
        DialogResult = true;
        Close();
    }
}
