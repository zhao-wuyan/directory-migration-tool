using MigrationCore.Models;

namespace MigrationCore.Services;

public class FileStatsService
{
    private static readonly string[] Sizes = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly EnumerationOptions Enumeration = new()
    {
        AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false
    };

    public static Task<FileStats> ScanDirectoryAsync(string directoryPath, long largeFileThresholdBytes,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var stats = new FileStats();
        foreach (FileInfo file in EnumerateFiles(directoryPath, cancellationToken))
        {
            long length = file.Length;
            stats.TotalBytes = checked(stats.TotalBytes + length);
            stats.TotalFiles++;
            if (length >= largeFileThresholdBytes) stats.LargeFiles++;
            if (stats.TotalFiles % 100 == 0) progress?.Report($"已扫描 {stats.TotalFiles} 个文件...");
        }
        return stats;
    }, cancellationToken);

    // 聚合数值仅用于进度估计，不表示完整性或已经落盘。
    public static long GetDirectorySize(string directoryPath)
    {
        long size = 0;
        foreach (FileInfo file in EnumerateFiles(directoryPath, CancellationToken.None)) size = checked(size + file.Length);
        return size;
    }

    /// <summary>返回逻辑长度估计，不是物理分配容量；沿用既有进度查询接口。</summary>
    public static long GetDirectorySizeOnDisk(string directoryPath)
    {
        long size = 0;
        foreach (FileInfo file in EnumerateFiles(directoryPath, CancellationToken.None)) size = checked(size + file.Length);
        return size;
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string path, CancellationToken ct)
    {
        path = PathIdentityService.Normalize(path);
        MigrationRuntime.AssertDataPath(path);
        var identity = new PathIdentityService();
        DirectoryIdentity root = identity.ReadDirectory(path, false);
        if (root.IsReparsePoint) throw new IOException($"不能统计未知重解析目录：{path}");
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out string? directory))
        {
            ct.ThrowIfCancellationRequested();
            if (identity.ReadDirectory(directory, false).IsReparsePoint) throw new IOException($"发现未知重解析目录：{directory}");
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Enumeration))
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"发现未知重解析点：{entry.FullName}");
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else yield return (FileInfo)entry;
            }
        }
        identity.RequireSame(path, root);
    }

    public static string FormatBytes(long bytes)
    {
        double length = bytes;
        int order = 0;
        while (length >= 1024 && order < Sizes.Length - 1) { order++; length /= 1024; }
        return $"{length:N2} {Sizes[order]}";
    }

    public static string FormatSpeed(double bytesPerSecond) => FormatBytes((long)bytesPerSecond) + "/s";
}
