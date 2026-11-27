using System.Windows;
using MigrationCore.Models.Cleanup;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// 隔离区操作结果弹窗（单批永久删除、批量永久删除、一键恢复的结构化回执与友善诊断提示）。
/// </summary>
public partial class QuarantineOperationResultWindow : Window
{
    public QuarantineOperationResultViewModel ViewModel { get; }

    public QuarantineOperationResultWindow(QuarantineOperationResultViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public static void ShowSingleDelete(QuarantineDeleteResult result, string batchDisplayTitle, string? quarantineDirectoryPath)
    {
        QuarantineOperationResultWindow? win = null;
        var vm = QuarantineOperationResultViewModel.FromSingleDelete(
            result: result,
            batchDisplayTitle: batchDisplayTitle,
            quarantineDirectoryPath: quarantineDirectoryPath,
            closeWindowCallback: () => win?.Close());
        win = new QuarantineOperationResultWindow(vm);
        ShowOwned(win);
    }

    public static void ShowBatchDelete(
        bool allSucceeded,
        bool wasCancelled,
        int totalSelectedBatches,
        int fullyDeletedBatches,
        int partialOrFailedBatches,
        long freedBytes,
        string summaryMessage,
        IReadOnlyList<string> details)
    {
        QuarantineOperationResultWindow? win = null;
        var vm = QuarantineOperationResultViewModel.FromBatchDelete(
            allSucceeded: allSucceeded,
            wasCancelled: wasCancelled,
            totalSelectedBatches: totalSelectedBatches,
            fullyDeletedBatches: fullyDeletedBatches,
            partialOrFailedBatches: partialOrFailedBatches,
            freedBytes: freedBytes,
            summaryMessage: summaryMessage,
            details: details,
            closeWindowCallback: () => win?.Close());
        win = new QuarantineOperationResultWindow(vm);
        ShowOwned(win);
    }

    public static void ShowRestore(QuarantineRestoreResult result, string batchDisplayTitle, string? quarantineDirectoryPath)
    {
        QuarantineOperationResultWindow? win = null;
        var vm = QuarantineOperationResultViewModel.FromRestore(
            result: result,
            batchDisplayTitle: batchDisplayTitle,
            quarantineDirectoryPath: quarantineDirectoryPath,
            closeWindowCallback: () => win?.Close());
        win = new QuarantineOperationResultWindow(vm);
        ShowOwned(win);
    }

    private static void ShowOwned(Window win)
    {
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;
        if (owner is not null && owner != win && owner.IsLoaded)
        {
            win.Owner = owner;
        }

        win.ShowDialog();
    }
}
