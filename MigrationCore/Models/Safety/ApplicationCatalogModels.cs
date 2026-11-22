namespace MigrationCore.Models.Safety;

/// <summary>
/// 应用目录归属证据来源（发现仅用于归属说明，不构成迁移或清理许可）。
/// </summary>
public enum ApplicationEvidenceSource
{
    RegistryUninstall64,
    RegistryUninstall32,
    RegistryCurrentUser,
    KnownFolder,
    BuiltInLocator,
    DiskHeuristic
}

/// <summary>
/// 经人工审查的版本化应用目录规则定义（L1 推荐迁移或 L2 厂商原生引导）。
/// </summary>
public sealed record ApplicationRuleDefinition(
    string ApplicationId,
    string DisplayName,
    string RuleVersion,
    MigrationAdmissionLevel Level,
    string[] InstallDetectionNames,
    string[] PublisherHints,
    string[] RelativeAllowedDataPaths,
    string[] RelativeForbiddenSubpaths,
    string[] PathContainsMarkers,
    string[] RequiredStoppedProcessNames,
    string[] AssociatedServiceNames,
    bool RequireProcessStopped,
    string? NativeGuidance,
    string VerificationNotes);

/// <summary>
/// 从 Windows 多来源（注册表 32/64 位 Uninstall、Known Folders 等）采集的原始软件安装证据。
/// </summary>
public sealed record InstalledSoftwareEvidence(
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string InstallRoot,
    ApplicationEvidenceSource Source);

/// <summary>
/// 目录发现结果：仅表示软件归属候选与对应策略建议，不绕过 L0 硬拒绝。
/// </summary>
public sealed record DiscoveredApplicationRoot(
    string ApplicationId,
    string DisplayName,
    string RootPath,
    string? MatchedVersion,
    string? Publisher,
    ApplicationEvidenceSource[] EvidenceSources,
    bool IsRuleVerified,
    MigrationAdmissionLevel RecommendedLevel,
    string[] CandidateDataPaths,
    string? NativeGuidance);
