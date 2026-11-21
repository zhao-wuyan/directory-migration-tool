using System.IO;
using System.Windows;
using MigrationCore.Models;
using MoveWithSymlinkWPF.Views;

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
            $"• {Path.GetFileName(Path.TrimEndingDirectorySeparator(record.SourcePath))}  ({record.SourcePath})"));
        if (pending.Length > 5) folders += $"\n• 另有 {pending.Length - 5} 个目录尚未验证";
        string message = $"还有 {pending.Length} 个目录尚未确认实际使用验证：\n\n{folders}\n\n"
            + "建议选择「否」留在当前界面，先启动文件夹所属软件确认功能正常，再决定是否处理备份。\n\n"
            + $"若选择「是」继续{action}，将保留原始备份，也不会标记为已验证。";
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current.MainWindow;
        // 默认高亮并聚焦“否”（建议留在当前界面）；关闭提示框或按 Esc 均不能视为同意。
        return MessageWindow.Show(
            message,
            title: "尚未验证：保留备份继续？",
            type: MessageType.Warning,
            buttons: MessageButtons.YesNo,
            owner: owner,
            defaultResult: MessageResult.No,
            yesText: "是，继续操作",
            noText: "否，留在当前界面（建议）") == MessageResult.Yes;
    }
}
