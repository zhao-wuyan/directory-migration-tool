using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 迁移状态检测服务
/// </summary>
public static class MigrationStateDetector
{
    private const string MigrateLockFile = ".xinghe-migrate.lock";
    private const string MigrateDoneFile = ".xinghe-migrate.done";
    private const string RestoreLockFile = ".xinghe-reduction.lock";
    private const string RestoreDoneFile = ".xinghe-reduction.done";

    /// <summary>
    /// 检测任务的迁移状态
    /// </summary>
    /// <param name="task">任务</param>
    /// <returns>更新后的任务</returns>
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
                    task.StatusMessage = record.UserVerifiedAtUtc is null ? "技术动作完成，备份保留，待用户验证" : "用户已验证，备份处理仍需独立授权";
                }
                else
                {
                    task.MigrationState = MigrationState.Inconsistent;
                    task.StatusMessage = $"待恢复：{record.Stage}；{record.LastError}";
                }
                return task;
            }

            // 旧目录名和标记只能提示候选，不构成恢复或删除授权。
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

    /// <summary>
    /// 查找备份路径
    /// </summary>
    /// <param name="sourcePath">源路径</param>
    /// <returns>备份路径，如果不存在返回 null</returns>
    public static string? FindBackupPath(string sourcePath)
    {
        try
        {
            string? parentDir = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                return null;

            string sourceName = Path.GetFileName(sourcePath);
            string backupPrefix = $"{sourceName}.bak_";

            var backups = Directory.GetDirectories(parentDir, $"{backupPrefix}*")
                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                .ToList();

            return backups.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 创建迁移锁文件
    /// </summary>
    /// <param name="targetPath">目标路径</param>
    /// <param name="sourcePath">源路径</param>
    public static void CreateMigrateLockFile(string targetPath, string sourcePath)
    {
        try
        {
            // 删除可能存在的还原标记
            DeleteRestoreMarkers(targetPath);

            // 删除可能存在的旧迁移标记（包括 .lock 和 .done）
            DeleteMigrateMarkers(targetPath);

            string lockFilePath = Path.Combine(targetPath, MigrateLockFile);
            string content = $"SourcePath: {sourcePath}\nStartTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            File.WriteAllText(lockFilePath, content);
            File.SetAttributes(lockFilePath, FileAttributes.Hidden);
        }
        catch
        {
            // 忽略错误
        }
    }

    /// <summary>
    /// 创建迁移完成文件
    /// </summary>
    /// <param name="targetPath">目标路径</param>
    public static void CreateMigrateDoneFile(string targetPath)
    {
        try
        {
            string doneFilePath = Path.Combine(targetPath, MigrateDoneFile);
            string content = $"CompletedTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            File.WriteAllText(doneFilePath, content);
            File.SetAttributes(doneFilePath, FileAttributes.Hidden);
        }
        catch
        {
            // 忽略错误
        }
    }

    /// <summary>
    /// 删除迁移标记文件
    /// </summary>
    /// <param name="targetPath">目标路径</param>
    public static void DeleteMigrateMarkers(string targetPath)
    {
        try
        {
            string lockFilePath = Path.Combine(targetPath, MigrateLockFile);
            if (File.Exists(lockFilePath))
            {
                File.SetAttributes(lockFilePath, FileAttributes.Normal);
                File.Delete(lockFilePath);
            }

            string doneFilePath = Path.Combine(targetPath, MigrateDoneFile);
            if (File.Exists(doneFilePath))
            {
                File.SetAttributes(doneFilePath, FileAttributes.Normal);
                File.Delete(doneFilePath);
            }
        }
        catch
        {
            // 忽略错误
        }
    }

    /// <summary>
    /// 创建还原锁文件
    /// </summary>
    /// <param name="sourcePath">源路径（当前为符号链接）</param>
    /// <param name="targetPath">目标路径（实际数据位置）</param>
    public static void CreateRestoreLockFile(string sourcePath, string targetPath)
    {
        try
        {
            // 删除可能存在的旧还原标记
            DeleteRestoreMarkers(targetPath);
            
            // 在目标路径创建还原锁文件
            string lockFilePath = Path.Combine(targetPath, RestoreLockFile);
            string content = $"SourcePath: {sourcePath}\nStartTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            File.WriteAllText(lockFilePath, content);
            File.SetAttributes(lockFilePath, FileAttributes.Hidden);
        }
        catch
        {
            // 忽略错误
        }
    }

    /// <summary>
    /// 创建还原完成文件
    /// </summary>
    /// <param name="sourcePath">源路径（还原后的实际数据位置）</param>
    public static void CreateRestoreDoneFile(string sourcePath)
    {
        try
        {
            string doneFilePath = Path.Combine(sourcePath, RestoreDoneFile);
            string content = $"CompletedTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            File.WriteAllText(doneFilePath, content);
            File.SetAttributes(doneFilePath, FileAttributes.Hidden);
        }
        catch
        {
            // 忽略错误
        }
    }

    /// <summary>
    /// 删除还原标记文件
    /// </summary>
    /// <param name="path">路径</param>
    public static void DeleteRestoreMarkers(string path)
    {
        try
        {
            string lockFilePath = Path.Combine(path, RestoreLockFile);
            if (File.Exists(lockFilePath))
            {
                File.SetAttributes(lockFilePath, FileAttributes.Normal);
                File.Delete(lockFilePath);
            }

            string doneFilePath = Path.Combine(path, RestoreDoneFile);
            if (File.Exists(doneFilePath))
            {
                File.SetAttributes(doneFilePath, FileAttributes.Normal);
                File.Delete(doneFilePath);
            }
        }
        catch
        {
            // 忽略错误
        }
    }
}


