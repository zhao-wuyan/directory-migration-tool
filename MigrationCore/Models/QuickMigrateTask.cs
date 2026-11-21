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
    [NotifyPropertyChangedFor(nameof(CanVerifyData))]
    [NotifyPropertyChangedFor(nameof(CanManageBackup))]
    [NotifyPropertyChangedFor(nameof(IsTransactionAbnormal))]
    [NotifyPropertyChangedFor(nameof(ShowMigratedWarningBanner))]
    [NotifyPropertyChangedFor(nameof(VerificationBadgeText))]
    [NotifyPropertyChangedFor(nameof(HasVerificationBadge))]
    private MigrationState _migrationState = MigrationState.Pending;

    /// <summary>
    /// 当前阶段（1-6）
    /// </summary>
    [ObservableProperty]
    private int _currentPhase = 0;

    /// <summary>
    /// 阶段描述
    /// </summary>
    [ObservableProperty]
    private string _phaseDescription = string.Empty;

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
    /// 是否显示进度条（执行中时为 true）
    /// </summary>
    [ObservableProperty]
    private bool _showProgress = false;

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
    [NotifyPropertyChangedFor(nameof(DisplayMigratedAt))]
    [NotifyPropertyChangedFor(nameof(HasDisplayMigratedAt))]
    private DateTime? _migratedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransactionId))]
    [NotifyPropertyChangedFor(nameof(HasTransaction))]
    [NotifyPropertyChangedFor(nameof(BackupState))]
    [NotifyPropertyChangedFor(nameof(VerificationSummary))]
    [NotifyPropertyChangedFor(nameof(BackupStatusText))]
    [NotifyPropertyChangedFor(nameof(VerificationText))]
    [NotifyPropertyChangedFor(nameof(TransactionStatusText))]
    [NotifyPropertyChangedFor(nameof(CanVerifyData))]
    [NotifyPropertyChangedFor(nameof(CanManageBackup))]
    [NotifyPropertyChangedFor(nameof(DeletionDetails))]
    [NotifyPropertyChangedFor(nameof(HasDeletionDetails))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingUserVerification))]
    [NotifyPropertyChangedFor(nameof(IsUserVerified))]
    [NotifyPropertyChangedFor(nameof(IsTransactionAbnormal))]
    [NotifyPropertyChangedFor(nameof(HasRetainedBackup))]
    [NotifyPropertyChangedFor(nameof(IsBackupCleaned))]
    [NotifyPropertyChangedFor(nameof(ShowPendingVerificationEntry))]
    [NotifyPropertyChangedFor(nameof(ShowPendingHistoryNotice))]
    [NotifyPropertyChangedFor(nameof(ShowMigratedWarningBanner))]
    [NotifyPropertyChangedFor(nameof(VerificationBadgeText))]
    [NotifyPropertyChangedFor(nameof(HasVerificationBadge))]
    [NotifyPropertyChangedFor(nameof(BackupShortBadgeText))]
    [NotifyPropertyChangedFor(nameof(HasBackupShortBadge))]
    [NotifyPropertyChangedFor(nameof(VerifyActionButtonText))]
    [NotifyPropertyChangedFor(nameof(DisplayMigratedAt))]
    [NotifyPropertyChangedFor(nameof(HasDisplayMigratedAt))]
    [NotifyPropertyChangedFor(nameof(BackupPathLabel))]
    private MigrationTransactionRecord? _transaction;

    public Guid? TransactionId => Transaction?.TransactionId;
    public bool HasTransaction => Transaction is not null;
    public bool HasBackupPath => !string.IsNullOrWhiteSpace(BackupPath);
    public BackupState? BackupState => Transaction?.BackupPath is null ? null : Transaction.BackupState;
    public VerificationSummary? VerificationSummary => Transaction?.VerificationSummary;
    public string VerificationText => VerificationSummary?.Description ?? "尚未完成核对";
    public string TransactionStatusText => Transaction is null ? "无可信事务；不能认领或删除候选备份"
        : Transaction.BackupDeletionInProgress ? "备份处理未确认结束，禁止重试，请核对实际位置"
        : Transaction.Stage != MigrationStage.AwaitingUserVerification ? $"待恢复/未完成：{Transaction.Stage}"
        : Transaction.UserVerifiedAtUtc is null ? "技术完成，待用户验证" : "用户已验证；处理备份仍需独立双确认";
    public string BackupStatusText => BackupState switch
    {
        Models.BackupState.RetainedUnverified => "原件备份保留，待用户验证",
        Models.BackupState.UserVerified => "用户已验证，备份仍保留",
        Models.BackupState.DeleteRequested => "备份保留；处理请求尚需有效确认",
        Models.BackupState.Recycled => "已核实移入回收站",
        Models.BackupState.Deleted => "已永久删除",
        Models.BackupState.PartiallyDeleted => "部分删除/处理中，不能作为完整备份",
        Models.BackupState.Missing => "备份缺失，请人工核对",
        Models.BackupState.IdentityChanged => "备份身份变化，禁止处理",
        Models.BackupState.DeleteFailed => "处理未完成；查看逐项结果及实际目录",
        _ => BackupPath is null ? "未创建原始备份" : "未确认的备份候选，仅可查看"
    };
    public bool CanVerifyData => MigrationState != Models.MigrationState.Inconsistent
        && Transaction is { Stage: MigrationStage.AwaitingUserVerification, BackupDeletionInProgress: false, UserVerifiedAtUtc: null };
    public bool CanManageBackup => Transaction is { Stage: MigrationStage.AwaitingUserVerification, BackupDeletionInProgress: false,
        UserVerifiedAtUtc: not null, BackupPath: not null, BackupIdentity: not null, BackupManifestDigest: not null }
        && Transaction.BackupState is not (Models.BackupState.Deleted or Models.BackupState.Recycled or Models.BackupState.PartiallyDeleted
            or Models.BackupState.Missing or Models.BackupState.IdentityChanged) && MigrationState != Models.MigrationState.Inconsistent;
    public string DeletionDetails => Transaction?.LastDeletionResult is not { } result ? ""
        : $"{result.Status}：{result.Error}\n" + string.Join("\n", result.Items.Select(i => $"{i.Outcome}：{i.Path} {i.Error}"));
    public bool HasDeletionDetails => !string.IsNullOrWhiteSpace(DeletionDetails);
    public bool IsAwaitingUserVerification => Transaction is { Stage: MigrationStage.AwaitingUserVerification, BackupDeletionInProgress: false, UserVerifiedAtUtc: null };
    public bool IsUserVerified => Transaction is { Stage: MigrationStage.AwaitingUserVerification, BackupDeletionInProgress: false, UserVerifiedAtUtc: not null };
    public bool IsTransactionAbnormal => Transaction is not null && (Transaction.BackupDeletionInProgress || Transaction.Stage != MigrationStage.AwaitingUserVerification || MigrationState == Models.MigrationState.Inconsistent);
    public bool HasRetainedBackup => BackupState is Models.BackupState.RetainedUnverified or Models.BackupState.UserVerified
        or Models.BackupState.DeleteRequested or Models.BackupState.DeleteFailed;
    public bool IsBackupCleaned => BackupState is Models.BackupState.Recycled or Models.BackupState.Deleted;
    public bool ShowPendingVerificationEntry => Transaction is { Mode: MigrationMode.Restore, Stage: MigrationStage.AwaitingUserVerification }
        && (Transaction.UserVerifiedAtUtc is null || HasRetainedBackup || Transaction.BackupDeletionInProgress);
    public bool ShowPendingHistoryNotice => !ShowPendingVerificationEntry && Transaction is { Stage: MigrationStage.Cancelled or MigrationStage.Failed };
    public bool ShowMigratedWarningBanner => !HasTransaction && (MigrationState == Models.MigrationState.Inconsistent || HasBackupPath);
    public string VerificationBadgeText => Transaction is null
        ? (MigrationState == Models.MigrationState.Inconsistent ? "无事务记录" : "")
        : Transaction.BackupDeletionInProgress ? "备份处理中"
        : Transaction.Stage != MigrationStage.AwaitingUserVerification ? $"待恢复：{Transaction.Stage}"
        : Transaction.UserVerifiedAtUtc is null
            ? (Transaction.Mode == MigrationMode.Restore ? "还原待验证" : "待用户验证")
            : (Transaction.Mode == MigrationMode.Restore ? "还原已验证" : "已验证");
    public bool HasVerificationBadge => !string.IsNullOrEmpty(VerificationBadgeText);
    public string BackupShortBadgeText => BackupState switch
    {
        Models.BackupState.RetainedUnverified => Transaction?.Mode == MigrationMode.Restore ? "还原备份保留" : "原始备份保留",
        Models.BackupState.UserVerified => Transaction?.Mode == MigrationMode.Restore ? "还原备份保留" : "原始备份保留",
        Models.BackupState.DeleteRequested => "备份待双确认",
        Models.BackupState.Recycled => "已移入回收站",
        Models.BackupState.Deleted => "已永久删除",
        Models.BackupState.PartiallyDeleted => "部分删除",
        Models.BackupState.Missing => "备份缺失",
        Models.BackupState.IdentityChanged => "备份身份变化",
        Models.BackupState.DeleteFailed => "备份处理未完成",
        _ => HasBackupPath ? "候选备份（仅查看）" : ""
    };
    public bool HasBackupShortBadge => !string.IsNullOrEmpty(BackupShortBadgeText);
    public string VerifyActionButtonText => Transaction is null ? ""
        : Transaction.UserVerifiedAtUtc is null
            ? (Transaction.Mode == MigrationMode.Restore ? "验证还原并管理备份…" : "验证并管理备份…")
            : HasRetainedBackup
                ? (Transaction.Mode == MigrationMode.Restore ? "管理还原备份…" : "管理备份…")
                : "查看验证记录";
    public DateTime? DisplayMigratedAt => MigratedAt ?? (Transaction is { Mode: not MigrationMode.Restore } ? Transaction.UpdatedAtUtc.LocalDateTime : null);
    public bool HasDisplayMigratedAt => DisplayMigratedAt.HasValue;
    public string BackupPathLabel => Transaction?.Mode == MigrationMode.Restore ? "还原备份:" : "原始备份:";

    /// <summary>
    /// 备份路径（如果存在）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupPath))]
    [NotifyPropertyChangedFor(nameof(BackupStatusText))]
    [NotifyPropertyChangedFor(nameof(BackupShortBadgeText))]
    [NotifyPropertyChangedFor(nameof(HasBackupShortBadge))]
    [NotifyPropertyChangedFor(nameof(ShowMigratedWarningBanner))]
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

    /// <summary>
    /// 错误类型
    /// </summary>
    [ObservableProperty]
    private ErrorType _errorType = ErrorType.None;

    /// <summary>
    /// 是否显示错误详情
    /// </summary>
    [ObservableProperty]
    private bool _showErrorDetails = false;
}

/// <summary>
/// 错误类型枚举
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// 无错误
    /// </summary>
    None,

    /// <summary>
    /// 权限错误
    /// </summary>
    Permission,

    /// <summary>
    /// 磁盘空间不足
    /// </summary>
    DiskSpace,

    /// <summary>
    /// 文件被占用
    /// </summary>
    FileInUse,

    /// <summary>
    /// 网络错误
    /// </summary>
    Network,

    /// <summary>
    /// 系统错误
    /// </summary>
    System,

    /// <summary>
    /// 未知错误
    /// </summary>
    Unknown
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
    Restore,

    /// <summary>
    /// 修复（基于现有目标重建符号链接，不复制数据）
    /// </summary>
    Repair
}


