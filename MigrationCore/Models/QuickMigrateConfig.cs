namespace MigrationCore.Models;

/// <summary>
/// 一键迁移配置
/// </summary>
public class QuickMigrateConfig
{
    /// <summary>
    /// 默认配置
    /// </summary>
    public QuickMigrateDefaults Defaults { get; set; } = new();

    /// <summary>
    /// 软件 Profile 列表
    /// </summary>
    public List<QuickMigrateProfile> Profiles { get; set; } = new();

    /// <summary>
    /// 独立源目录列表
    /// </summary>
    public List<QuickMigrateStandaloneSource> StandaloneSources { get; set; } = new();
}

/// <summary>
/// 默认配置参数
/// </summary>
public class QuickMigrateDefaults
{
    /// <summary>
    /// 大文件阈值（MB）
    /// </summary>
    public int LargeFileThresholdMB { get; set; } = 1024;

    /// <summary>
    /// Robocopy 线程数
    /// </summary>
    public int RobocopyThreads { get; set; } = 8;

    /// <summary>
    /// 目标策略：unified（统一）或 perTask（分任务）
    /// </summary>
    public string TargetStrategy { get; set; } = "unified";

    /// <summary>
    /// 统一目标根目录（当 targetStrategy 为 unified 时使用）
    /// </summary>
    public string UnifiedTargetRoot { get; set; } = string.Empty;

    /// <summary>
    /// 还原时是否保留目标数据（默认 false，会删除）
    /// </summary>
    public bool RestoreKeepTarget { get; set; } = false;
}

/// <summary>
/// 软件 Profile 配置
/// </summary>
public class QuickMigrateProfile
{
    /// <summary>
    /// Profile ID（唯一标识）
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Profile 显示名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 定位器配置
    /// </summary>
    public QuickMigrateLocator Locator { get; set; } = new();

    /// <summary>
    /// 待迁移项列表
    /// </summary>
    public List<QuickMigrateProfileItem> Items { get; set; } = new();
}

/// <summary>
/// 定位器配置
/// </summary>
public class QuickMigrateLocator
{
    /// <summary>
    /// 定位器类型：registryDisplayIcon
    /// </summary>
    public string Type { get; set; } = "registryDisplayIcon";

    /// <summary>
    /// 注册表 Hive：HKCU、HKLM
    /// </summary>
    public string Hive { get; set; } = "HKCU";

    /// <summary>
    /// 注册表键路径
    /// </summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>
    /// 注册表值名
    /// </summary>
    public string ValueName { get; set; } = "DisplayIcon";
}

/// <summary>
/// Profile 内的待迁移项
/// </summary>
public class QuickMigrateProfileItem
{
    /// <summary>
    /// 显示名称
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 相对于安装根目录的路径
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 独立源目录配置
/// </summary>
public class QuickMigrateStandaloneSource
{
    /// <summary>
    /// 显示名称
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 绝对路径
    /// </summary>
    public string AbsolutePath { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;
}


