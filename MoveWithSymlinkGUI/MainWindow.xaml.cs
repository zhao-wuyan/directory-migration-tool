using Microsoft.UI.Xaml;
using MoveWithSymlinkGUI.ViewModels;

namespace MoveWithSymlinkGUI;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();
        ViewModel = new MainViewModel();
        (this.Content as FrameworkElement)!.DataContext = ViewModel;
        
        // Set window size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
    }
}

