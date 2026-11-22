using System.Windows.Controls;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class DiskScanPage : UserControl
{
    public DiskScanPage()
    {
        InitializeComponent();
        DataContext = new DiskScanViewModel();
    }
}
