using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 迁移流程的公共帮助类，封装符号链接创建、验证、备份清理等通用逻辑
/// 
/// 该类提取自 MigrationService 和 ReversibleMigrationService 的重复代码，
/// 将符号链接操作、备份管理、目录扫描等通用流程抽取为静态方法。
/// 
/// 主要功能：
/// - 创建符号链接并备份源目录
/// - 验证符号链接有效性
/// - 清理备份目录
/// - 回滚迁移操作（删除符号链接、恢复备份）
/// - 目录扫描与结果报告
/// 
/// 使用此类可确保 MigrationService 和 ReversibleMigrationService 
/// 使用完全一致的符号链接和备份逻辑，便于维护和测试。
/// </summary>
public static class MigrationWorkflowHelper
{
    /// <summary>
    /// 创建符号链接（包含备份源目录的操作）
    /// </summary>
    /// <param name="sourcePath">源路径</param>
    /// <param name="targetPath">目标路径</param>
    /// <param name="logProgress">日志进度回调</param>
    /// <returns>备份路径</returns>
    public static async Task<string> CreateSymbolicLinkWithBackupAsync(
        string sourcePath,
        string targetPath,
        IProgress<string>? logProgress = null)
    {
        return await Task.Run(() =>
        {
            // 创建备份
            string parent = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("无法解析源目录父路径");
            string name = Path.GetFileName(sourcePath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupPath = Path.Combine(parent, $"{name}.bak_{timestamp}");

            logProgress?.Report($"备份源目录到: {backupPath}");
            Directory.Move(sourcePath, backupPath);

            // 创建符号链接
            logProgress?.Report($"创建符号链接: {sourcePath} -> {targetPath}");

            // 优先使用 P/Invoke 方法
            bool success = SymbolicLinkHelper.CreateDirectorySymbolicLink(sourcePath, targetPath);

            if (!success)
            {
                // 备选: 使用 cmd mklink
                logProgress?.Report("P/Invoke 失败，尝试使用 cmd mklink...");
                success = SymbolicLinkHelper.CreateSymbolicLinkViaCmdAsync(sourcePath, targetPath, out string error);

                if (!success)
                {
                    throw new InvalidOperationException($"创建符号链接失败: {error}");
                }
            }

            if (!Directory.Exists(sourcePath))
            {
                throw new InvalidOperationException("符号链接创建后无法访问");
            }

            return backupPath;
        });
    }

    /// <summary>
    /// 验证符号链接是否正确创建
    /// </summary>
    public static async Task VerifySymbolicLinkAsync(
        string symlinkPath,
        IProgress<string>? logProgress = null)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("验证符号链接...");

            if (!SymbolicLinkHelper.IsSymbolicLink(symlinkPath))
            {
                throw new InvalidOperationException("创建的对象不是符号链接（重解析点）");
            }

            if (!Directory.Exists(symlinkPath))
            {
                throw new InvalidOperationException("符号链接无法访问");
            }

            logProgress?.Report("✅ 符号链接验证成功");
        });
    }

    /// <summary>
    /// 清理备份目录
    /// </summary>
    public static async Task CleanupBackupAsync(
        string? backupPath,
        IProgress<string>? logProgress = null)
    {
        if (string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath))
            return;

        await Task.Run(() =>
        {
            try
            {
                logProgress?.Report($"清理备份目录: {backupPath}");
                Directory.Delete(backupPath, true);
                logProgress?.Report("✅ 备份已清理");
            }
            catch (Exception ex)
            {
                logProgress?.Report($"⚠️ 清理备份失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 回滚迁移操作（删除符号链接，恢复备份）
    /// </summary>
    public static async Task RollbackMigrationAsync(
        string sourcePath,
        string targetPath,
        string? backupPath,
        IProgress<string>? logProgress = null)
    {
        if (string.IsNullOrEmpty(backupPath))
            return;

        await Task.Run(() =>
        {
            logProgress?.Report("开始回滚...");

            try
            {
                // 删除符号链接
                if (Directory.Exists(sourcePath))
                {
                    if (SymbolicLinkHelper.IsSymbolicLink(sourcePath))
                    {
                        Directory.Delete(sourcePath, false);
                    }
                }

                // 还原备份
                if (Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, sourcePath);
                    logProgress?.Report("✅ 已回滚至迁移前状态");
                }

                // 清理迁移标记
                MigrationStateDetector.DeleteMigrateMarkers(targetPath);
            }
            catch (Exception ex)
            {
                logProgress?.Report($"❌ 回滚失败: {ex.Message}");
                throw;
            }
        });
    }

    /// <summary>
    /// 扫描目录并获取统计信息
    /// </summary>
    public static async Task<FileStats> ScanDirectoryAsync(
        string directoryPath,
        long largeFileThresholdBytes,
        IProgress<string>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        logProgress?.Report($"正在扫描: {directoryPath}");

        var scanProgress = new Progress<string>(msg => logProgress?.Report(msg));
        var stats = await FileStatsService.ScanDirectoryAsync(directoryPath, largeFileThresholdBytes, scanProgress, cancellationToken);

        return stats;
    }

    /// <summary>
    /// 报告扫描结果
    /// </summary>
    public static void ReportScanResults(
        FileStats stats,
        int largeFileThresholdMB,
        IProgress<string>? logProgress = null)
    {
        logProgress?.Report($"总文件数: {stats.TotalFiles}");
        logProgress?.Report($"总大小: {FileStatsService.FormatBytes(stats.TotalBytes)}");
        logProgress?.Report($"大文件 (≥{largeFileThresholdMB}MB): {stats.LargeFiles} 个");
    }
}

