using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 文件统计服务
/// </summary>
public class FileStatsService
{
    /// <summary>
    /// 扫描目录并获取统计信息
    /// </summary>
    public static async Task<FileStats> ScanDirectoryAsync(string directoryPath, long largeFileThresholdBytes, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var stats = new FileStats();

        if (!Directory.Exists(directoryPath))
            return stats;

        await Task.Run(() =>
        {
            try
            {
                var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories);
                
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var fileInfo = new FileInfo(file);
                        stats.TotalBytes += fileInfo.Length;
                        stats.TotalFiles++;

                        if (fileInfo.Length >= largeFileThresholdBytes)
                        {
                            stats.LargeFiles++;
                        }

                        // 每100个文件报告一次进度
                        if (stats.TotalFiles % 100 == 0)
                        {
                            progress?.Report($"已扫描 {stats.TotalFiles} 个文件...");
                        }
                    }
                    catch
                    {
                        // 忽略无法访问的文件
                    }
                }
            }
            catch
            {
                // 忽略枚举错误
            }
        }, cancellationToken);

        return stats;
    }

    /// <summary>
    /// 获取目录大小（字节）
    /// </summary>
    public static long GetDirectorySize(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        long totalSize = 0;

        try
        {
            var files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    totalSize += new FileInfo(file).Length;
                }
                catch
                {
                    // 忽略无法访问的文件
                }
            }
        }
        catch
        {
            // 忽略枚举错误
        }

        return totalSize;
    }

    /// <summary>
    /// 格式化字节数为可读字符串
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB", "PB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:N2} {sizes[order]}";
    }

    /// <summary>
    /// 格式化速度为可读字符串
    /// </summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        return FormatBytes((long)bytesPerSecond) + "/s";
    }
}

