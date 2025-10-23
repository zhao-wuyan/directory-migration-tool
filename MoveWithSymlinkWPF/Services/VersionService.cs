using System.IO;
using System.Text.Json;
using System.Reflection;

namespace MoveWithSymlinkWPF.Services;

/// <summary>
/// 版本管理服务
/// </summary>
public static class VersionService
{
    private static string? _cachedVersion;

    /// <summary>
    /// 版本信息数据模型
    /// </summary>
    private class VersionData
    {
        public int major { get; set; }
        public int minor { get; set; }
        public int patch { get; set; }
        public string description { get; set; } = string.Empty;
    }

    /// <summary>
    /// 获取当前应用程序版本号
    /// </summary>
    /// <returns>版本号字符串，格式为 "v1.0.2"</returns>
    public static string GetVersion()
    {
        if (_cachedVersion != null)
        {
            return _cachedVersion;
        }

        try
        {
            // 尝试从 version.json 读取版本号
            string? versionJsonPath = FindVersionJsonPath();
            
            if (versionJsonPath != null && File.Exists(versionJsonPath))
            {
                string jsonContent = File.ReadAllText(versionJsonPath);
                var versionData = JsonSerializer.Deserialize<VersionData>(jsonContent);
                
                if (versionData != null)
                {
                    _cachedVersion = $"v{versionData.major}.{versionData.minor}.{versionData.patch}";
                    return _cachedVersion;
                }
            }
        }
        catch
        {
            // 如果读取 version.json 失败，继续尝试从程序集获取
        }

        // 回退方案：从程序集版本获取
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyVersion = assembly.GetName().Version;
            
            if (assemblyVersion != null)
            {
                _cachedVersion = $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
                return _cachedVersion;
            }
        }
        catch
        {
            // 如果程序集版本也获取失败，使用默认版本
        }

        // 最终回退
        _cachedVersion = "v1.0.0";
        return _cachedVersion;
    }

    /// <summary>
    /// 查找 version.json 文件路径
    /// </summary>
    private static string? FindVersionJsonPath()
    {
        // 1. 尝试从程序所在目录查找
        string exePath = AppDomain.CurrentDomain.BaseDirectory;
        string versionJsonInExeDir = Path.Combine(exePath, "version.json");
        if (File.Exists(versionJsonInExeDir))
        {
            return versionJsonInExeDir;
        }

        // 2. 尝试从项目根目录查找（开发时）
        string? projectRoot = FindProjectRoot(exePath);
        if (projectRoot != null)
        {
            string versionJsonInProjectRoot = Path.Combine(projectRoot, "version.json");
            if (File.Exists(versionJsonInProjectRoot))
            {
                return versionJsonInProjectRoot;
            }
        }

        return null;
    }

    /// <summary>
    /// 查找项目根目录
    /// </summary>
    private static string? FindProjectRoot(string startPath)
    {
        DirectoryInfo? directory = new DirectoryInfo(startPath);
        
        // 向上查找，最多查找5层
        int maxLevels = 5;
        int currentLevel = 0;
        
        while (directory != null && currentLevel < maxLevels)
        {
            // 检查是否存在 .sln 文件或 version.json
            if (directory.GetFiles("*.sln").Length > 0 || 
                directory.GetFiles("version.json").Length > 0)
            {
                return directory.FullName;
            }
            
            directory = directory.Parent;
            currentLevel++;
        }
        
        return null;
    }

    /// <summary>
    /// 获取详细版本信息（包含描述）
    /// </summary>
    public static (string version, string description) GetDetailedVersion()
    {
        string version = GetVersion();
        string description = string.Empty;

        try
        {
            string? versionJsonPath = FindVersionJsonPath();
            
            if (versionJsonPath != null && File.Exists(versionJsonPath))
            {
                string jsonContent = File.ReadAllText(versionJsonPath);
                var versionData = JsonSerializer.Deserialize<VersionData>(jsonContent);
                
                if (versionData != null)
                {
                    description = versionData.description;
                }
            }
        }
        catch
        {
            // 忽略错误
        }

        return (version, description);
    }
}

