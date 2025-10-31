using MoveWithSymlinkWPF.ViewModels;
using System.Windows.Controls;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// QuickMigratePage.xaml 的交互逻辑
/// </summary>
public partial class QuickMigratePage : UserControl
{
    public QuickMigrateViewModel ViewModel { get; }

    public QuickMigratePage()
    {
        InitializeComponent();
        ViewModel = new QuickMigrateViewModel();
        DataContext = ViewModel;
    }
}


