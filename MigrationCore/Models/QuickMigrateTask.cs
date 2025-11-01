using CommunityToolkit.Mvvm.ComponentModel;

namespace MigrationCore.Models;

/// <summary>
/// 一键迁移任务
/// </summary>
public partial class QuickMigrateTask : ObservableObject
{
    /// <summary>
    /// 任务唯一 ID
    /// </summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    /// <summary>
    /// 显示名称
    /// </summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// 所属 Profile 名称（可选，独立源为空）
    /// </summary>
    [ObservableProperty]
    private string? _profileName;

    /// <summary>
    /// 源目录路径
    /// </summary>
    [ObservableProperty]
    private string _sourcePath = string.Empty;

    /// <summary>
    /// 子路径
    /// </summary>
    [ObservableProperty]
    private string _relativePath = string.Empty;

    /// <summary>
    /// 目标目录路径
    /// </summary>
    [ObservableProperty]
    private string _targetPath = string.Empty;

    /// <summary>
    /// 任务状态
    /// </summary>
    [ObservableProperty]
    private QuickMigrateTaskStatus _status = QuickMigrateTaskStatus.Pending;

    /// <summary>
    /// 迁移状态
    /// </summary>
    [ObservableProperty]
    private MigrationState _migrationState = MigrationState.Pending;

    /// <summary>
    /// 当前阶段（1-6）
    /// </summary>
    [ObservableProperty]
    private int _currentPhase = 0;

    /// <summary>
    /// 进度百分比（0-100）
    /// </summary>
    [ObservableProperty]
    private double _progressPercent = 0;

    /// <summary>
    /// 状态消息
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 错误消息（如果失败）
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// 是否是待恢复任务（复制中断等）
    /// </summary>
    [ObservableProperty]
    private bool _isResumable = false;

    /// <summary>
    /// 迁移完成时间
    /// </summary>
    [ObservableProperty]
    private DateTime? _migratedAt;

    /// <summary>
    /// 备份路径（如果存在）
    /// </summary>
    [ObservableProperty]
    private string? _backupPath;

    /// <summary>
    /// 符号链接目标（实际解析出来的）
    /// </summary>
    [ObservableProperty]
    private string? _symlinkTarget;

    /// <summary>
    /// 是否被选中（用于批量迁移）
    /// </summary>
    [ObservableProperty]
    private bool _isSelected = false;
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


