using System.IO;
using System.Windows;
using MigrationCore.Models;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.Helpers;

public static class UnverifiedExitConfirmation
{
    /// <summary>
    /// 筛选所有仍存在真实存活主体（未删除备份或源/目标目录）且尚未完成用户验证的事务。
    /// 按 TransactionId 去重，不再按 SourcePath 丢弃同源历史未清理备份，避免二次迁移或还原后遮蔽旧备份。
    /// </summary>
    public static MigrationTransactionRecord[] SelectPendingUnverified(
        IEnumerable<MigrationTransactionRecord> records,
        Func<string, bool>? directoryExists = null)
    {
        Func<string, bool> exists = directoryExists ?? Directory.Exists;
        return records
            .DistinctBy(record => record.TransactionId)
            .Where(record => record.Stage == MigrationStage.AwaitingUserVerification
                && record.UserVerifiedAtUtc is null
                && HasLiveSubject(record, exists))
            .OrderByDescending(record => record.UpdatedAtUtc)
            .ToArray();
    }

    public static bool Confirm(IEnumerable<MigrationTransactionRecord> records, string action)
    {
        var pending = SelectPendingUnverified(records);
        if (pending.Length == 0) return true;

        string folders = string.Join("\n", pending.Take(5).Select(record =>
            $"• {Path.GetFileName(Path.TrimEndingDirectorySeparator(record.SourcePath))}  ({record.SourcePath})"));
        if (pending.Length > 5) folders += $"\n• 另有 {pending.Length - 5} 条迁移/还原备份记录尚未验证";
        string message = $"还有 {pending.Length} 条迁移/还原记录尚未确认实际使用验证：\n\n{folders}\n\n"
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

    private static bool HasLiveSubject(MigrationTransactionRecord record, Func<string, bool> exists)
    {
        bool hasLiveBackup = record.BackupPath is not null
            && record.BackupState is not (BackupState.Deleted or BackupState.Recycled)
            && exists(record.BackupPath);
        if (hasLiveBackup) return true;
        return record.BackupPath is null && (exists(record.SourcePath) || exists(record.TargetPath));
    }
}
