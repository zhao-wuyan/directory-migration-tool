using System.Windows;
using System.Windows.Controls;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views.Cleanup;

public partial class CleanupQuarantineView : UserControl
{
    public CleanupQuarantineView()
    {
        InitializeComponent();
    }

    private void SelectAllQuarantineCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CleanupViewModel vm && sender is CheckBox cb)
        {
            vm.ToggleSelectAllQuarantineBatchesCommand.Execute(null);
            cb.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        }
        e.Handled = true;
    }
}
