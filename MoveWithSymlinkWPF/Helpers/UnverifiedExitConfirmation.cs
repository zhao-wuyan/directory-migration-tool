using System.IO;
using System.Windows;
using MigrationCore.Models;

namespace MoveWithSymlinkWPF.Helpers;

public static class UnverifiedExitConfirmation
{
    public static bool Confirm(IEnumerable<MigrationTransactionRecord> records, string action)
    {
        var pending = records.Where(record => record.Stage == MigrationStage.AwaitingUserVerification
                && record.UserVerifiedAtUtc is null)
            .DistinctBy(record => record.TransactionId).ToArray();
        if (pending.Length == 0) return true;

        string folders = string.Join("\n", pending.Take(5).Select(record =>
            $"• {Path.GetFileName(Path.TrimEndingDirectorySeparator(record.SourcePath))}\n  {record.SourcePath}"));
        if (pending.Length > 5) folders += $"\n另有 {pending.Length - 5} 个目录尚未验证。";
        string message = $"还有 {pending.Length} 个目录尚未确认使用验证。\n\n{folders}\n\n"
            + "建议先启动文件夹所属软件，确认能正常使用，再决定是否处理备份。\n\n"
            + $"是否暂不验证并保留备份，继续{action}？\n\n"
            + "是：继续原操作，不删除备份，也不会标记为已验证。\n否：留在当前界面。";
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current.MainWindow;
        // 默认选择“否”，关闭提示框也不能视为同意。
        return MessageBox.Show(owner, message, "尚未验证：保留备份继续？", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
