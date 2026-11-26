using System.Collections.Frozen;

namespace MigrationCore.Services.Safety;

/// <summary>
/// 迁移安全策略（L0–L3）与垃圾清理永久排除区共享的路径敏感词表单一事实源。
/// 全部使用 .NET 原生 FrozenSet&lt;string&gt;（O(1) 零分配查询），编译期固定不可被外部配置篡改。
/// </summary>
internal static class ProtectedPathVocabulary
{
    public const string QuarantineRootDirectoryName = ".MoveFolderQuarantine";

    /// <summary>
    /// 云盘同步目录段名（迁移归入 L2 原生引导，垃圾清理 AI 疑似残留通道强制硬拦截）。
    /// </summary>
    public static FrozenSet<string> CloudSyncSegmentNames { get; } = CreateIgnoreCaseSet(
    [
        "OneDrive",
        "Dropbox",
        "Google Drive",
        "iCloudDrive"
    ]);

    /// <summary>
    /// 密钥、证书与高危凭据目录段名（L0 迁移禁区与垃圾清理永久排除区共享核心子集）。
    /// </summary>
    public static FrozenSet<string> SharedKeyAndCertificateSegments { get; } = CreateIgnoreCaseSet(
    [
        ".ssh",
        ".gnupg",
        "SystemCertificates"
    ]);

    /// <summary>
    /// 卷根下 Windows 专属且默认隐藏的系统/更新/恢复/元数据目录（强制 L0 硬拒绝并在扫描视图隐藏）。
    /// </summary>
    public static FrozenSet<string> HiddenSystemVolumeRootNames { get; } = CreateIgnoreCaseSet(
    [
        "System Volume Information",
        "$Recycle.Bin",
        "RECYCLER",
        "Recycled",
        "$Extend",
        "Boot",
        "Recovery",
        "EFI",
        "$WinREAgent",
        "$WINDOWS.~BT",
        "$Windows.~WS",
        "$Windows.~LS",
        "$WINDOWS.~Q",
        "$SysReset",
        "$GetCurrent",
        "$INPLACE.~TR",
        "Config.Msi",
        "Documents and Settings",
        "MSOCache",
        "OneDriveTemp",
        "WUDownloadCache",
        "DeliveryOptimization",
        QuarantineRootDirectoryName
    ]);

    /// <summary>
    /// 卷根下可见（非隐藏）但同样强制 L0 硬拒绝的 Windows 系统目录。
    /// </summary>
    public static FrozenSet<string> VisibleSystemVolumeRootHardDeniedNames { get; } = CreateIgnoreCaseSet(
    [
        "Windows.old",
        "WindowsApps",
        "ModifiableWindowsApps",
        "WpSystem",
        "PerfLogs"
    ]);

    public static FrozenSet<string> VolumeMetadataRootNames { get; } = CreateIgnoreCaseSet(
    [
        "System Volume Information",
        "$Recycle.Bin",
        "RECYCLER",
        "Recycled",
        "$Extend"
    ]);

    public static FrozenSet<string> WindowsComponentRootNames { get; } = CreateIgnoreCaseSet(
    [
        "Config.Msi",
        "MSOCache",
        "OneDriveTemp",
        "WUDownloadCache",
        "DeliveryOptimization",
        "WindowsApps",
        "ModifiableWindowsApps",
        "WpSystem",
        "PerfLogs"
    ]);

    public static FrozenSet<string> ProgramFilesBlockedSubtrees { get; } = CreateIgnoreCaseSet(
    [
        "WindowsApps",
        "Common Files",
        "Windows Defender",
        "Windows Defender Advanced Threat Protection",
        "WindowsPowerShell",
        "Windows NT",
        "Windows Mail",
        "Windows Media Player",
        "Windows Photo Viewer",
        "Windows Portable Devices",
        "Windows Security",
        "Windows Sidebar",
        "Microsoft",
        "Reference Assemblies",
        "MSBuild",
        "ModifiableWindowsApps",
        "Internet Explorer",
        "Uninstall Information"
    ]);

    public static FrozenSet<string> ProgramDataBlockedSubtrees { get; } = CreateIgnoreCaseSet(
    [
        "Microsoft",
        "Package Cache",
        "WindowsHolographicDevices",
        "USOPrivate",
        "USOShared",
        "ssh"
    ]);

    public static FrozenSet<string> AppDataLocalBlockedSubtrees { get; } = CreateIgnoreCaseSet(
    [
        "Microsoft",
        "Packages",
        "Temp",
        "VirtualStore",
        "Comms",
        "ConnectedDevicesPlatform",
        "Publishers",
        "Diagnostics",
        "ElevatedDiagnostics",
        "PeerDistRepub",
        "CrashDumps",
        "D3DSCache"
    ]);

    public static FrozenSet<string> HighRiskSecurityAndDriverSegments { get; } = CreateIgnoreCaseSet(
    [
        "Windows Defender",
        "CrowdStrike",
        "CarbonBlack",
        "SentinelOne",
        "Kaspersky",
        "Kaspersky Lab",
        "Bitdefender",
        "Huorong",
        "360Safe",
        "ESET",
        "Symantec",
        "McAfee",
        "Malwarebytes",
        "Sophos",
        "DriverStore",
        .. SharedKeyAndCertificateSegments
    ]);

    public static FrozenSet<string> CreateIgnoreCaseSet(IEnumerable<string> values) =>
        values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
