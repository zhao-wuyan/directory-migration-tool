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
    public static QuickMigrateTask DetectMigrationState(QuickMigrateTask task)
    {
        string sourcePath = task.SourcePath;
        string? targetPath = task.TargetPath;

        // 检查源路径是否是符号链接
        bool isSymlink = SymbolicLinkHelper.IsSymbolicLink(sourcePath);

        if (isSymlink)
        {
            // 源是符号链接，表示已迁移
            task.MigrationState = MigrationState.Migrated;

            // 尝试获取符号链接目标
            try
            {
                var dirInfo = new DirectoryInfo(sourcePath);
                if (dirInfo.Exists && dirInfo.LinkTarget != null)
                {
                    task.SymlinkTarget = dirInfo.LinkTarget;
                    
                    // 如果任务没有设置目标路径，从符号链接读取
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        task.TargetPath = dirInfo.LinkTarget;
                        targetPath = dirInfo.LinkTarget;
                    }
                }
            }
            catch
            {
                // 忽略错误
            }

            // 检查目标是否存在
            if (!string.IsNullOrEmpty(targetPath) && !Directory.Exists(targetPath))
            {
                task.MigrationState = MigrationState.Inconsistent;
                task.StatusMessage = "符号链接存在但目标缺失";
            }

            // 检查是否有备份需要清理
            var backupPath = FindBackupPath(sourcePath);
            if (backupPath != null)
            {
                task.MigrationState = MigrationState.NeedsCleanup;
                task.BackupPath = backupPath;
                task.StatusMessage = "已迁移，但存在备份待清理";
            }
        }
        else if (Directory.Exists(sourcePath))
        {
            // 源是普通目录
            task.MigrationState = MigrationState.Pending;

            // 检查是否是中断的迁移
            if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
            {
                // 检查目标目录中的标记文件
                bool hasLockFile = File.Exists(Path.Combine(targetPath, MigrateLockFile));
                bool hasDoneFile = File.Exists(Path.Combine(targetPath, MigrateDoneFile));

                if (hasLockFile && !hasDoneFile)
                {
                    // 复制中断
                    task.IsResumable = true;
                    task.StatusMessage = "复制未完成，可恢复";
                }
                else if (hasDoneFile)
                {
                    // 复制完成但未创建符号链接
                    var backupPath = FindBackupPath(sourcePath);
                    if (backupPath != null)
                    {
                        task.MigrationState = MigrationState.NeedsCompletion;
                        task.BackupPath = backupPath;
                        task.StatusMessage = "复制完成，待创建符号链接";
                    }
                }
            }

            // 检查是否有备份但源未被替换为符号链接
            var backup = FindBackupPath(sourcePath);
            if (backup != null && !string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
            {
                task.MigrationState = MigrationState.NeedsCompletion;
                task.BackupPath = backup;
                task.StatusMessage = "已备份且数据已复制，待创建符号链接";
            }
        }
        else
        {
            // 源不存在
            // 检查是否有备份
            var backupPath = FindBackupPath(sourcePath);
            if (backupPath != null)
            {
                if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
                {
                    task.MigrationState = MigrationState.NeedsCompletion;
                    task.BackupPath = backupPath;
                    task.StatusMessage = "源已备份，待创建符号链接";
                }
            }
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


