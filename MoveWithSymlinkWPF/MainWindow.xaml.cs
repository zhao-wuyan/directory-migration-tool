using MoveWithSymlinkWPF.ViewModels;
using System.Windows;

namespace MoveWithSymlinkWPF;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        Closing += (_, args) =>
        {
            if (!ViewModel.IsOperationBusy) return;
            args.Cancel = true;
            ViewModel.CancelCurrentOperations();
        };
    }
}
