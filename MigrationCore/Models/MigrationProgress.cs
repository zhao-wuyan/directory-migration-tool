namespace MigrationCore.Models;

/// <summary>
/// 迁移进度信息
/// </summary>
public class MigrationProgress
{
    /// <summary>
    /// 完成百分比（0-100）
    /// </summary>
    public double PercentComplete { get; set; }

    /// <summary>
    /// 已复制字节数
    /// </summary>
    public long CopiedBytes { get; set; }

    /// <summary>
    /// 总字节数
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// 当前速度（字节/秒）
    /// </summary>
    public double SpeedBytesPerSecond { get; set; }

    /// <summary>
    /// 预计剩余时间
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; set; }

    /// <summary>
    /// 状态消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 当前阶段（1-6）
    /// </summary>
    public int CurrentPhase { get; set; }

    /// <summary>
    /// 阶段描述
    /// </summary>
    public string PhaseDescription { get; set; } = string.Empty;
}

