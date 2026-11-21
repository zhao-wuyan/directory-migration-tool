using System.ComponentModel;
using System.Windows;
using MigrationCore.Models;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class MigrationVerificationWindow : Window
{
    private readonly MigrationVerificationViewModel _viewModel;
    private bool _allowClose;

    private MigrationVerificationWindow(Guid transactionId)
    {
        InitializeComponent();
        _viewModel = new MigrationVerificationViewModel(transactionId);
        DataContext = _viewModel;
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(1, workArea.Width);
        MaxHeight = Math.Max(1, workArea.Height);
        MinWidth = Math.Min(MinWidth, MaxWidth);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    // 调用方持有迁移互斥状态；此处必须由 UI 线程进入，并保持模态直至关闭。
    public static Task ShowForTransactionAsync(Guid transactionId)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var window = new MigrationVerificationWindow(transactionId) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsBusy) await _viewModel.LoadAsync();
    }

    private async void ManageBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanManageBackup) return;
        var mode = _viewModel.BackupModeIndex == 1 ? BackupDeletionMode.Recycle : BackupDeletionMode.Permanent;
        _viewModel.IsBusy = true;
        _viewModel.ErrorText = "";
        try
        {
            // 服务重新核验事务、身份与内容；界面勾选不能授予删除权限。
            var result = await BackupConfirmationWindow.ShowDeletionAsync(_viewModel.Record!.TransactionId,
                mode, CancellationToken.None, owner: this);
            if (result is null) _viewModel.ShowDeletionCancelled();
            else _viewModel.ShowDeletionResult(result);
        }
        catch (Exception ex) { _viewModel.ErrorText = "处理备份失败：" + ex.Message; }
        finally
        {
            try { await _viewModel.ReloadRecordAsync(); }
            catch (Exception ex) { _viewModel.ErrorText += "\n刷新事务记录失败：" + ex.Message; }
            _viewModel.IsBusy = false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_viewModel.IsBusy) return;
        _viewModel.IsBusy = true;
        bool close = false;
        try
        {
            // 必须重新加载，不使用打开弹框时的过期确认状态。
            await _viewModel.ReloadRecordAsync();
            close = UnverifiedExitConfirmation.Confirm([_viewModel.Record!], "关闭迁移验证窗口");
        }
        catch (Exception ex)
        {
            _viewModel.ErrorText = "无法读取最新验证状态，暂不关闭以避免跳过确认：" + ex.Message;
        }
        finally { _viewModel.IsBusy = false; }
        if (close)
        {
            _allowClose = true;
            // 即使存储任务同步完成，也不能在首次 Closing 回调内再次关闭。
            await Dispatcher.InvokeAsync(Close);
        }
    }
}
