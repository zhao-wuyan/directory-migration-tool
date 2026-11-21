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
    [NotifyPropertyChangedFor(nameof(DisplayMigratedTarget))]
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
    public bool ShowMigratedWarningBanner =>
        MigrationState == Models.MigrationState.Inconsistent
        || IsTransactionAbnormal
        || (!HasTransaction && HasBackupPath);
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
    public string DisplayMigratedTarget => !string.IsNullOrWhiteSpace(SymlinkTarget) ? SymlinkTarget : Transaction?.TargetPath ?? string.Empty;
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
    [NotifyPropertyChangedFor(nameof(DisplayMigratedTarget))]
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
    /// 驱动器不存在或不可用
    /// </summary>
    DriveUnavailable,

    /// <summary>
    /// 路径或目录无效
    /// </summary>
    PathInvalid,

    /// <summary>
    /// 目标目录非空或已存在冲突
    /// </summary>
    TargetConflict,

    /// <summary>
    /// 符号链接或重解析点错误
    /// </summary>
    SymlinkError,

    /// <summary>
    /// 事务状态或目录身份不匹配
    /// </summary>
    TransactionMismatch,

    /// <summary>
    /// 数据核对或清单校验失败
    /// </summary>
    VerificationFailed,

    /// <summary>
    /// 文件复制过程失败
    /// </summary>
    CopyFailed,

    /// <summary>
    /// 未知错误
    /// </summary>
    Unknown
}

/// <summary>
/// 统一错误消息分类器（ViewModel 与 UI 转换器共用，避免规则漂移）
/// </summary>
public static class ErrorTypeClassifier
{
    public static ErrorType Classify(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return ErrorType.None;

        // 仅用主错误内容分类，避免被追加的“相关/保留位置”路径文本误触发关键词
        string primary = ExtractPrimaryMessage(errorMessage);
        string lower = primary.ToLowerInvariant();

        // 1. 文件占用（需先于权限检查，因为 Windows 英文占用报错包含 "cannot access ... being used by another process"）
        if (ContainsAny(lower,
            "占用", "被另一进程使用", "正在被", "不能锁定", "sharing violation",
            "being used by another process", "in use", "file is locked", "lock"))
            return ErrorType.FileInUse;

        // 2. 磁盘空间不足（需先于驱动器与复制失败，例如“实际复制目的卷空间不足，尚未创建 staging”）
        if (ContainsAny(lower,
            "空间不足", "磁盘已满", "disk full", "not enough space", "insufficient space", "no space"))
            return ErrorType.DiskSpace;

        // 3. 驱动器不可用 / 不存在
        if (ContainsAny(lower,
            "驱动器不可用", "驱动器不存在", "找不到指定的驱动器", "设备未就绪", "盘符无效", "本地卷", "卷 guid", "卷身份",
            "drive not ready", "device is not ready", "not ready", "could not find drive", "cannot find the drive"))
            return ErrorType.DriveUnavailable;

        // 4. 目标目录冲突（非空 / 已存在不得覆盖）
        if (ContainsAny(lower,
            "目标非空", "目标目录已存在且不为空", "路径已存在", "不得覆盖", "归属不明", "源出现新内容"))
            return ErrorType.TargetConflict;

        // 5. 符号链接 / 重解析点错误
        if (ContainsAny(lower,
            "符号链接", "mklink", "重解析", "reparse", "不是已登记的链接", "不是普通实体目录",
            "必须是普通实体目录", "新建对象不是链接", "恢复结果不是链接", "链接目标"))
            return ErrorType.SymlinkError;

        // 6. 数据核对 / 清单 / 元数据校验失败
        if (ContainsAny(lower,
            "核对失败", "核对基线", "无法核对", "核对", "清单", "大小写歧义",
            "长度已经变化", "长度变化", "内容已变化", "元数据或内容已变化", "ads", "acl"))
            return ErrorType.VerificationFailed;

        // 7. 事务状态 / 目录身份冲突
        if (ContainsAny(lower,
            "事务", "路径身份", "实体身份", "身份不符", "身份匹配", "不能认领", "不能按目录名认领",
            "不能猜测归属", "revision", "备份处理尚未结束", "备份意图", "不能跳过备份", "删除请求"))
            return ErrorType.TransactionMismatch;

        // 8. 文件复制过程失败
        if (ContainsAny(lower,
            "robocopy", "staging", "退出码", "复制失败", "复制进程"))
            return ErrorType.CopyFailed;

        // 9. 系统保护目录或系统级进程/环境错误（避免泛匹配 "system"/"process" 误伤 Windows 路径或占用报错）
        if (ContainsAny(lower,
            "系统关键目录", "卷系统信息", "系统错误", "严重", "无法创建本任务", "进程集",
            "system error", "critical", "job"))
            return ErrorType.System;

        // 10. 权限不足
        if (ContainsAny(lower,
            "权限", "无权", "拒绝访问", "管理员", "安全描述符", "恢复令牌",
            "access denied", "access to the path", "unauthorized", "permission", "denied"))
            return ErrorType.Permission;

        // 11. 网络错误
        if (ContainsAny(lower,
            "网络", "连接", "network", "connection", "unc"))
            return ErrorType.Network;

        // 12. 路径或目录无效 / 不存在
        if (ContainsAny(lower,
            "目录不存在", "路径不存在", "路径无效", "路径不能为空", "不能相同", "不能位于",
            "互相包含", "驱动器根目录", "规范绝对路径", "无效路径", "保留的 dos", "cmd 元字符",
            "父目录", "父路径缺失", "不是目录", "找不到", "cannot find the path", "cannot find the file",
            "could not find", "does not exist", "not found"))
            return ErrorType.PathInvalid;

        return ErrorType.Unknown;
    }

    public static string ExtractPrimaryMessage(string errorMessage)
    {
        int marker = errorMessage.IndexOf("\n相关/保留位置：", StringComparison.Ordinal);
        string head = marker >= 0 ? errorMessage[..marker] : errorMessage;
        return head.Trim();
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (string keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
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


