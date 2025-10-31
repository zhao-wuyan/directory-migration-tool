using System.Reflection;
using System.Text.Json;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 一键迁移配置加载器
/// </summary>
public static class QuickMigrateConfigLoader
{
    private const string ConfigFileName = "quick-migrate.json";
    private const string EmbeddedResourceName = "quick-migrate.json";

    /// <summary>
    /// 加载配置文件
    /// </summary>
    /// <param name="configPath">配置文件路径（可选，默认为可执行文件同目录）</param>
    /// <returns>配置对象，如果文件不存在则返回 null</returns>
    public static QuickMigrateConfig? LoadConfig(string? configPath = null)
    {
        try
        {
            string json;

            if (string.IsNullOrEmpty(configPath))
            {
                // 默认路径：可执行文件同目录
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                configPath = Path.Combine(exeDir, ConfigFileName);
            }

            // 优先从外部文件加载（允许用户自定义）
            if (File.Exists(configPath))
            {
                json = File.ReadAllText(configPath);
            }
            else
            {
                // 从嵌入资源加载默认配置
                json = LoadEmbeddedConfig();
                if (json == null)
                {
                    return null;
                }
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            var config = JsonSerializer.Deserialize<QuickMigrateConfig>(json, options);
            return config;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从嵌入资源加载配置
    /// </summary>
    private static string? LoadEmbeddedConfig()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var resourceName = EmbeddedResourceName;

            // 尝试查找资源
            var allResources = assembly.GetManifestResourceNames();
            var matchedResource = allResources.FirstOrDefault(r => r.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

            if (matchedResource == null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(matchedResource);
            if (stream == null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存配置文件
    /// </summary>
    public static bool SaveConfig(QuickMigrateConfig config, string? configPath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(configPath))
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                configPath = Path.Combine(exeDir, ConfigFileName);
            }

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };

            string json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 创建示例配置文件
    /// </summary>
    public static QuickMigrateConfig CreateExampleConfig()
    {
        return new QuickMigrateConfig
        {
            Defaults = new QuickMigrateDefaults
            {
                LargeFileThresholdMB = 1024,
                RobocopyThreads = 8,
                TargetStrategy = "unified",
                UnifiedTargetRoot = "E:\\Migrated",
                RestoreKeepTarget = false
            },
            Profiles = new List<QuickMigrateProfile>
            {
                new QuickMigrateProfile
                {
                    Id = "ExampleApp",
                    Name = "示例应用",
                    Locator = new QuickMigrateLocator
                    {
                        Type = "registryDisplayIcon",
                        Hive = "HKCU",
                        KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ExampleApp",
                        ValueName = "DisplayIcon"
                    },
                    Items = new List<QuickMigrateProfileItem>
                    {
                        new QuickMigrateProfileItem
                        {
                            DisplayName = "项目目录",
                            RelativePath = "Projects",
                            Enabled = true
                        },
                        new QuickMigrateProfileItem
                        {
                            DisplayName = "模板目录",
                            RelativePath = @"Assets\Templates",
                            Enabled = false
                        }
                    }
                }
            },
            StandaloneSources = new List<QuickMigrateStandaloneSource>
            {
                new QuickMigrateStandaloneSource
                {
                    DisplayName = "自定义资源库",
                    AbsolutePath = @"D:\MyResources",
                    Enabled = true
                }
            }
        };
    }
}

