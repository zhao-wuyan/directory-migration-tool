using System.IO;
using System.Windows;
using MigrationCore.Models;
using MigrationCore.Services;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class BackupConfirmationWindow : Window
{
    public BackupConfirmationWindow(BackupConfirmationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SetWindow(this);
        Loaded += (_, _) => CancelButton.Focus();
    }

    public static async Task<BackupDeletionResult?> ShowDeletionAsync(Guid transactionId, BackupDeletionMode mode, CancellationToken ct)
    {
        if (mode == BackupDeletionMode.Recycle)
        {
            CustomMessageBox.ShowWarning(RecycleBinHelper.UnsupportedReason, "备份未处理");
            return null;
        }
        var service = new BackupManagementService();
        BackupDeletionRequest request = await Task.Run(() => service.CreateDeletionRequestAsync(transactionId, mode, ct));
        var record = await new MigrationTransactionStore().LoadAsync(transactionId, ct) ?? throw new IOException("确认前事务记录缺失。");
        foreach (ConfirmationStep step in new[] { ConfirmationStep.First, ConfirmationStep.Second })
        {
            ct.ThrowIfCancellationRequested();
            var viewModel = new BackupConfirmationViewModel(record, request, step);
            var window = new BackupConfirmationWindow(viewModel) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() != true) return null;
            request = await Task.Run(() => service.ConfirmDeletionAsync(request.RequestId, step, viewModel.ConfirmationText, ct));
        }
        return await service.DeleteAsync(request.RequestId, ct);
    }
}
