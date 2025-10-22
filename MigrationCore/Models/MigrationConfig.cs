namespace MigrationCore.Models;

/// <summary>
/// 迁移配置参数
/// </summary>
public class MigrationConfig
{
    /// <summary>
    /// 源目录路径
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 目标目录路径
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// 大文件阈值（MB）
    /// </summary>
    public int LargeFileThresholdMB { get; set; } = 1024;

    /// <summary>
    /// Robocopy 并行线程数
    /// </summary>
    public int RobocopyThreads { get; set; } = 8;

    /// <summary>
    /// 进度采样间隔（毫秒）
    /// </summary>
    public int SampleMilliseconds { get; set; } = 1000;
}

