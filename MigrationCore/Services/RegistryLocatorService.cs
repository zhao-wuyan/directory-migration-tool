using Microsoft.Win32;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 注册表定位服务
/// </summary>
public static class RegistryLocatorService
{
    /// <summary>
    /// 通过 DisplayIcon 定位安装根目录
    /// </summary>
    /// <param name="locator">定位器配置</param>
    /// <returns>安装根目录，如果定位失败返回 null</returns>
    public static string? LocateInstallRoot(QuickMigrateLocator locator)
    {
        if (locator.Type != "registryDisplayIcon")
        {
            return null;
        }

        try
        {
            RegistryKey? baseKey = locator.Hive.ToUpperInvariant() switch
            {
                "HKCU" => Registry.CurrentUser,
                "HKLM" => Registry.LocalMachine,
                "HKCR" => Registry.ClassesRoot,
                "HKU" => Registry.Users,
                "HKCC" => Registry.CurrentConfig,
                _ => null
            };

            if (baseKey == null)
                return null;

            using var key = baseKey.OpenSubKey(locator.KeyPath);
            if (key == null)
                return null;

            var value = key.GetValue(locator.ValueName);
            if (value == null)
                return null;

            string rawPath = value.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(rawPath))
                return null;

            // 清洗路径：去除包裹引号和末尾的 ",数字"
            string cleanedPath = CleanDisplayIconPath(rawPath);

            // 如果是 .exe 文件，取其所在目录
            if (cleanedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                cleanedPath = Path.GetDirectoryName(cleanedPath) ?? string.Empty;
            }

            // 验证目录是否存在
            if (Directory.Exists(cleanedPath))
            {
                return cleanedPath;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清洗 DisplayIcon 路径
    /// </summary>
    /// <param name="rawPath">原始路径</param>
    /// <returns>清洗后的路径</returns>
    private static string CleanDisplayIconPath(string rawPath)
    {
        // 去除首尾空白
        string path = rawPath.Trim();

        // 去除包裹的引号
        if (path.StartsWith("\"") && path.EndsWith("\""))
        {
            path = path.Substring(1, path.Length - 2);
        }

        // 去除末尾的 ",数字"（如 ",0"）
        int commaIndex = path.LastIndexOf(',');
        if (commaIndex > 0)
        {
            string afterComma = path.Substring(commaIndex + 1).Trim();
            if (int.TryParse(afterComma, out _))
            {
                path = path.Substring(0, commaIndex);
            }
        }

        return path;
    }

    /// <summary>
    /// 展开 Profile 到任务列表
    /// </summary>
    /// <param name="profile">Profile 配置</param>
    /// <param name="installRoot">安装根目录</param>
    /// <returns>任务列表</returns>
    public static List<QuickMigrateTask> ExpandProfileToTasks(QuickMigrateProfile profile, string installRoot)
    {
        var tasks = new List<QuickMigrateTask>();

        foreach (var item in profile.Items)
        {
            if (!item.Enabled)
                continue;

            string sourcePath = Path.Combine(installRoot, item.RelativePath);

            // 只有源目录存在时才添加任务
            if (Directory.Exists(sourcePath))
            {
                tasks.Add(new QuickMigrateTask
                {
                    DisplayName = $"{profile.Name} - {item.DisplayName}",
                    RelativePath = item.RelativePath,
                    ProfileName = profile.Name,
                    SourcePath = sourcePath,
                    Status = QuickMigrateTaskStatus.Pending
                });
            }
        }

        return tasks;
    }

    /// <summary>
    /// 展开独立源到任务列表
    /// </summary>
    /// <param name="standaloneSource">独立源配置</param>
    /// <returns>任务列表</returns>
    public static List<QuickMigrateTask> ExpandStandaloneSourceToTasks(QuickMigrateStandaloneSource standaloneSource)
    {
        var tasks = new List<QuickMigrateTask>();

        if (!standaloneSource.Enabled)
            return tasks;

        if (Directory.Exists(standaloneSource.AbsolutePath))
        {
            tasks.Add(new QuickMigrateTask
            {
                DisplayName = standaloneSource.DisplayName,
                RelativePath = Path.GetFileName(standaloneSource.AbsolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                ProfileName = null,
                SourcePath = standaloneSource.AbsolutePath,
                Status = QuickMigrateTaskStatus.Pending
            });
        }

        return tasks;
    }

    /// <summary>
    /// 去重任务列表（基于物理路径）
    /// </summary>
    /// <param name="tasks">任务列表</param>
    /// <returns>去重后的任务列表</returns>
    public static List<QuickMigrateTask> DeduplicateTasks(List<QuickMigrateTask> tasks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = new List<QuickMigrateTask>();

        foreach (var task in tasks)
        {
            try
            {
                string fullPath = Path.GetFullPath(task.SourcePath);
                if (seen.Add(fullPath))
                {
                    deduplicated.Add(task);
                }
            }
            catch
            {
                // 如果无法获取完整路径，保留任务
                deduplicated.Add(task);
            }
        }

        return deduplicated;
    }
}


