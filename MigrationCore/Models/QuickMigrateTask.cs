namespace MigrationCore.Models;

/// <summary>
/// 一键迁移任务
/// </summary>
public class QuickMigrateTask
{
    /// <summary>
    /// 任务唯一 ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 显示名称
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 所属 Profile 名称（可选，独立源为空）
    /// </summary>
    public string? ProfileName { get; set; }

    /// <summary>
    /// 源目录路径
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 子路径
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// 目标目录路径
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// 任务状态
    /// </summary>
    public QuickMigrateTaskStatus Status { get; set; } = QuickMigrateTaskStatus.Pending;

    /// <summary>
    /// 迁移状态
    /// </summary>
    public MigrationState MigrationState { get; set; } = MigrationState.Pending;

    /// <summary>
    /// 当前阶段（1-6）
    /// </summary>
    public int CurrentPhase { get; set; } = 0;

    /// <summary>
    /// 进度百分比（0-100）
    /// </summary>
    public double ProgressPercent { get; set; } = 0;

    /// <summary>
    /// 状态消息
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// 错误消息（如果失败）
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 是否是待恢复任务（复制中断等）
    /// </summary>
    public bool IsResumable { get; set; } = false;

    /// <summary>
    /// 迁移完成时间
    /// </summary>
    public DateTime? MigratedAt { get; set; }

    /// <summary>
    /// 备份路径（如果存在）
    /// </summary>
    public string? BackupPath { get; set; }

    /// <summary>
    /// 符号链接目标（实际解析出来的）
    /// </summary>
    public string? SymlinkTarget { get; set; }
}

/// <summary>
/// 一键迁移任务状态
/// </summary>
public enum QuickMigrateTaskStatus
{
    /// <summary>
    /// 待执行
    /// </summary>
    Pending,

    /// <summary>
    /// 执行中
    /// </summary>
    InProgress,

    /// <summary>
    /// 已完成
    /// </summary>
    Completed,

    /// <summary>
    /// 失败
    /// </summary>
    Failed,

    /// <summary>
    /// 已跳过
    /// </summary>
    Skipped
}

/// <summary>
/// 迁移状态
/// </summary>
public enum MigrationState
{
    /// <summary>
    /// 待迁移（未迁移）
    /// </summary>
    Pending,

    /// <summary>
    /// 已迁移
    /// </summary>
    Migrated,

    /// <summary>
    /// 不一致/异常（符号链接存在但目标缺失等）
    /// </summary>
    Inconsistent,

    /// <summary>
    /// 待清理（已迁移但存在备份）
    /// </summary>
    NeedsCleanup,

    /// <summary>
    /// 待完成（复制完成但未创建符号链接）
    /// </summary>
    NeedsCompletion
}

/// <summary>
/// 迁移模式
/// </summary>
public enum MigrationMode
{
    /// <summary>
    /// 正向迁移（源 → 目标）
    /// </summary>
    Migrate,

    /// <summary>
    /// 还原（目标 → 源）
    /// </summary>
    Restore
}


