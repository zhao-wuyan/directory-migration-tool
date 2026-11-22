namespace MigrationCore.Models.Safety;

/// <summary>
/// 统一 4 级迁移安全准入等级（L0 硬拒绝永远优先于任何允许）。
/// </summary>
public enum MigrationAdmissionLevel
{
    /// <summary>L0 硬拒绝：系统核心、启动/恢复、高危安全/驱动、特殊文件系统结构或工具自身事务目录，永远禁止迁移。</summary>
    HardDenied = 0,

    /// <summary>L1 推荐迁移：命中内置已验证规则的常用软件独立业务数据目录。</summary>
    VerifiedEligible = 1,

    /// <summary>L2 原生引导：存在官方迁移机制或直接建链有结构性风险的软件（如 Docker/WSL2、OneDrive）。</summary>
    VendorNativeRecommended = 2,

    /// <summary>L3 人工风险确认：位于非 L0 区的普通个人数据目录或未收录第三方软件独立目录，需用户知情确认。</summary>
    ManualRiskConfirm = 3
}

/// <summary>
/// 策略评估上下文：区分新发起迁移、还原/修复反向校验、目标路径与扫描预览。
/// </summary>
public enum SafetyEvaluationContext
{
    NewMigration,
    RestoreOrRepair,
    TargetPath,
    ScanPreview
}

/// <summary>
/// 稳定策略原因码（可供 UI、日志、测试及后续只读 AI 工具消费）。
/// </summary>
public static class SafetyReasonCodes
{
    public const string L0DriveRoot = "L0_DRIVE_ROOT";
    public const string L0OsCore = "L0_OS_CORE";
    public const string L0BootRecovery = "L0_BOOT_RECOVERY";
    public const string L0VolumeMetadata = "L0_VOLUME_METADATA";
    public const string L0SystemContainerRoot = "L0_SYSTEM_CONTAINER_ROOT";
    public const string L0WindowsComponent = "L0_WINDOWS_COMPONENT";
    public const string L0HighRiskSecurityOrDriver = "L0_HIGH_RISK_SECURITY_OR_DRIVER";
    public const string L0SpecialFsStructure = "L0_SPECIAL_FS_STRUCTURE";
    public const string L0SelfOrActiveTransaction = "L0_SELF_OR_ACTIVE_TRANSACTION";
    public const string L0AccessDeniedOrUnverified = "L0_ACCESS_DENIED_OR_UNVERIFIED";
    public const string L0InvalidOrUnresolvedPath = "L0_INVALID_OR_UNRESOLVED_PATH";
    public const string L0UnsupportedVolume = "L0_UNSUPPORTED_VOLUME";

    public const string L1VerifiedAppRule = "L1_VERIFIED_APP_RULE";
    public const string L2VendorNativeGuidance = "L2_VENDOR_NATIVE_GUIDANCE";
    public const string L3PersonalOrUnlistedConfirm = "L3_PERSONAL_OR_UNLISTED_CONFIRM";
}

/// <summary>
/// 应用规则库向安全策略返回的归属与建议匹配结果（发现不等于跳过 L0 硬拒绝）。
/// </summary>
public sealed record SafetyApplicationMatch(
    string ApplicationId,
    string DisplayName,
    MigrationAdmissionLevel RecommendedLevel,
    string? NativeGuidance,
    string[] Evidence,
    string[] Warnings,
    string[]? RequiredStoppedProcesses = null);

/// <summary>
/// 应用目录匹配扩展点，由工作包 C（ApplicationCatalog）实现并注册，避免核心安全层与应用目录发现循环耦合。
/// </summary>
public interface ISafetyApplicationMatcher
{
    SafetyApplicationMatch? Match(string normalizedPath);
}

/// <summary>
/// 统一安全策略判定结论。
/// </summary>
public sealed record SafetyPolicyDecision(
    MigrationAdmissionLevel Level,
    bool IsAllowedForNewMigration,
    bool IsAllowedForRestoreOrRepair,
    string[] ReasonCodes,
    string PolicyVersion,
    string Summary,
    string? MatchedApplicationId = null,
    string? MatchedApplicationName = null,
    string? NativeGuidance = null,
    string[]? Warnings = null,
    string[]? Evidence = null,
    string[]? RequiredStoppedProcesses = null)
{
    public bool IsHardDenied => Level == MigrationAdmissionLevel.HardDenied;
    public bool RequiresManualRiskConfirmation => Level == MigrationAdmissionLevel.ManualRiskConfirm;
}
