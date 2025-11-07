using MoveWithSymlinkWPF.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MoveWithSymlinkWPF.Views;

public partial class FolderPickerWindow : Window
{
    public FolderPickerViewModel ViewModel { get; }

    public FolderPickerWindow()
    {
        InitializeComponent();
        ViewModel = new FolderPickerViewModel();
        DataContext = ViewModel;
        ViewModel.SetWindow(this);
    }

    /// <summary>
    /// 获取选择的路径
    /// </summary>
    public string? SelectedPath => ViewModel.DialogResult ? ViewModel.SelectedPath : null;

    /// <summary>
    /// ListBox 双击事件处理
    /// </summary>
    private void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is FolderItem folderItem)
        {
            ViewModel.ItemDoubleClickCommand.Execute(folderItem);
        }
    }
}

