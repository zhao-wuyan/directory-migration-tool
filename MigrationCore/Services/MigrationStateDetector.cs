using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>界面状态由事务和实际实体身份推导，旧目录名不构成授权。</summary>
public static class MigrationStateDetector
{
    public static QuickMigrateTask DetectMigrationState(QuickMigrateTask task, MigrationTransactionStore? store = null)
    {
        try
        {
            store ??= new MigrationTransactionStore();
            MigrationTransactionRecord? record = task.TransactionId is Guid id
                ? store.LoadAsync(id).GetAwaiter().GetResult() ?? throw new IOException("已登记事务缺失。")
                : store.ReadAllAsync().GetAwaiter().GetResult()
                    .Where(r => r.SourcePath.Equals(task.SourcePath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.UpdatedAtUtc).FirstOrDefault();
            task.Transaction = record;
            task.IsResumable = false;
            var identity = new PathIdentityService();
            if (record is not null)
            {
                task.BackupPath = record.BackupPath;
                task.Status = record.Stage == MigrationStage.Cancelled ? QuickMigrateTaskStatus.Skipped
                    : record.Stage == MigrationStage.AwaitingUserVerification ? QuickMigrateTaskStatus.Completed : QuickMigrateTaskStatus.Failed;
                task.ErrorMessage = record.LastError;
                if (record.BackupDeletionInProgress)
                {
                    task.MigrationState = MigrationState.Inconsistent;
                    task.StatusMessage = "备份处理未确认结束，按不完整副本保留审计，禁止重试";
                    return task;
                }
                if (!record.SourcePath.Equals(task.SourcePath, StringComparison.OrdinalIgnoreCase)
                    || !record.TargetPath.Equals(task.TargetPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("当前配置与已登记事务路径不同，不能认领另一目标。");
                if (record.TargetIdentity is not null) identity.RequireSame(record.TargetPath, record.TargetIdentity);
                if (record.BackupIdentity is not null && record.BackupState is not (BackupState.Recycled or BackupState.Deleted))
                    identity.RequireSame(record.BackupPath ?? throw new IOException("备份路径缺失。"), record.BackupIdentity);
                if (record.Stage == MigrationStage.AwaitingUserVerification)
                {
                    if (record.Mode != MigrationMode.Restore)
                    {
                        identity.RequireSame(record.SourcePath, record.LinkIdentity);
                        identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
                        task.SymlinkTarget = record.TargetPath;
                    }
                    else identity.RequireSame(record.SourcePath, record.StagingIdentity);
                    task.MigrationState = record.Mode == MigrationMode.Restore ? MigrationState.Pending : MigrationState.Migrated;
                    string backup = record.BackupPath is null ? "未创建原始备份" : $"备份状态：{record.BackupState}";
                    task.StatusMessage = record.UserVerifiedAtUtc is null
                        ? $"技术动作完成，待用户验证；{backup}" : $"用户已验证；{backup}；处理备份仍需独立授权";
                }
                else if (record.Stage is MigrationStage.Cancelled or MigrationStage.Failed
                    && identity.ReadIfExists(record.SourcePath) == record.SourceIdentity)
                {
                    task.MigrationState = MigrationState.Pending;
                    task.StatusMessage = $"{record.Stage}：源实体原位，已有副本保留";
                }
                else if (record.Stage is MigrationStage.Cancelled or MigrationStage.Failed && record.LinkIdentity is not null)
                {
                    identity.RequireSame(record.SourcePath, record.LinkIdentity);
                    identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
                    task.MigrationState = MigrationState.Migrated;
                    task.StatusMessage = $"{record.Stage}：链接与数据仍可用，副本保留";
                }
                else
                {
                    task.MigrationState = MigrationState.Inconsistent;
                    task.StatusMessage = $"待恢复：{record.Stage}；{record.LastError}";
                }
                return task;
            }
            string? candidate = FindBackupPath(task.SourcePath);
            task.BackupPath = candidate;
            DirectoryIdentity? source = identity.ReadIfExists(task.SourcePath);
            if (candidate is not null || source is null || source.IsReparsePoint)
            {
                task.MigrationState = MigrationState.Inconsistent;
                task.StatusMessage = candidate is null ? "缺少可靠事务记录，请核对实际数据位置" : "发现未确认备份候选，不能自动认领或删除";
            }
            else
            {
                task.MigrationState = MigrationState.Pending;
                task.StatusMessage = "待迁移";
            }
        }
        catch (Exception ex)
        {
            task.MigrationState = MigrationState.Inconsistent;
            task.IsResumable = false;
            task.StatusMessage = $"事务或路径身份无法确认：{ex.Message}";
        }
        return task;
    }

    public static string? FindBackupPath(string sourcePath)
    {
        string? parent = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return null;
        return Directory.EnumerateDirectories(parent, Path.GetFileName(sourcePath) + ".bak_*").FirstOrDefault();
    }
}
