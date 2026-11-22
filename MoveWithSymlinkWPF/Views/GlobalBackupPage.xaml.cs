using System.Windows.Controls;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class GlobalBackupPage : UserControl
{
    public GlobalBackupPage()
    {
        InitializeComponent();
        DataContext = new GlobalBackupViewModel();
    }
}
