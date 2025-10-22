namespace MigrationCore.Services;

/// <summary>
/// 路径验证服务
/// </summary>
public static class PathValidator
{
    private static readonly string[] BlockedPaths = new[]
    {
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData\Microsoft",
        @"C:\System Volume Information"
    };

    private static readonly string[] WarningPaths = new[]
    {
        @"OneDrive",
        @"Dropbox",
        @"Google Drive",
        @"iCloudDrive"
    };

    /// <summary>
    /// 验证源路径是否合法
    /// </summary>
    public static (bool IsValid, string? Error, string? Warning) ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return (false, "源路径不能为空", null);

        if (!Directory.Exists(sourcePath))
            return (false, "源目录不存在", null);

        // 检查是否为阻止的系统目录
        string fullPath = Path.GetFullPath(sourcePath);
        foreach (var blocked in BlockedPaths)
        {
            if (fullPath.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"不允许迁移系统关键目录: {blocked}", null);
            }
        }

        // 检查是否为云盘同步目录
        string? warning = null;
        foreach (var warningPath in WarningPaths)
        {
            if (fullPath.Contains(warningPath, StringComparison.OrdinalIgnoreCase))
            {
                warning = $"警告: 该目录可能位于云盘同步文件夹（{warningPath}），迁移可能导致同步冲突";
                break;
            }
        }

        return (true, null, warning);
    }

    /// <summary>
    /// 验证目标路径是否合法
    /// </summary>
    public static (bool IsValid, string? Error) ValidateTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "目标路径不能为空");

        try
        {
            string fullPath = Path.GetFullPath(targetPath);
            string? parentDir = Path.GetDirectoryName(fullPath);
            
            if (string.IsNullOrEmpty(parentDir))
                return (false, "无法解析目标路径的父目录");

            // 检查父目录是否存在或可创建
            if (!Directory.Exists(parentDir))
            {
                DriveInfo? drive = new DriveInfo(Path.GetPathRoot(parentDir) ?? "C:\\");
                if (!drive.IsReady)
                    return (false, "目标驱动器不可用");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"目标路径无效: {ex.Message}");
        }
    }

    /// <summary>
    /// 验证源和目标的关系
    /// </summary>
    public static (bool IsValid, string? Error) ValidatePathRelation(string sourcePath, string targetPath)
    {
        try
        {
            string srcFull = Path.GetFullPath(sourcePath).TrimEnd('\\');
            string dstFull = Path.GetFullPath(targetPath).TrimEnd('\\');

            // 不能相同
            if (srcFull.Equals(dstFull, StringComparison.OrdinalIgnoreCase))
                return (false, "源路径和目标路径不能相同");

            // 目标不能在源内部
            if (dstFull.StartsWith(srcFull + "\\", StringComparison.OrdinalIgnoreCase))
                return (false, "目标路径不能位于源目录内部");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"路径关系验证失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查是否有管理员权限
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查磁盘空间是否足够
    /// </summary>
    public static (bool IsSufficient, long Available, long Required) CheckDiskSpace(string targetPath, long requiredBytes)
    {
        try
        {
            string root = Path.GetPathRoot(targetPath) ?? "C:\\";
            var driveInfo = new DriveInfo(root);
            
            if (!driveInfo.IsReady)
                return (false, 0, requiredBytes);

            long available = driveInfo.AvailableFreeSpace;
            // 预留 10% 余量
            long required = (long)(requiredBytes * 1.1);
            
            return (available >= required, available, required);
        }
        catch
        {
            return (false, 0, requiredBytes);
        }
    }
}

