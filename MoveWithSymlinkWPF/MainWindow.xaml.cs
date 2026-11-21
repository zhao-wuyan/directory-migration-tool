using MoveWithSymlinkWPF.ViewModels;
using System.Windows;

namespace MoveWithSymlinkWPF;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private bool _closeApproved;
    private bool _checkingClose;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        Closing += async (_, args) =>
        {
            if (_closeApproved) return;
            args.Cancel = true;
            if (_checkingClose) return;
            if (ViewModel.IsOperationBusy)
            {
                ViewModel.CancelCurrentOperations();
                return;
            }
            _checkingClose = true;
            try
            {
                if (!await ViewModel.ConfirmCloseAsync()) return;
                _closeApproved = true;
                await Dispatcher.InvokeAsync(Close);
            }
            finally { _checkingClose = false; }
        };
    }
}
