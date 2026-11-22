using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MigrationCore.Models.Safety;

namespace MigrationCore.Services.Safety;

/// <summary>
/// 策略环境快照：支持生产环境自动解析 Windows Known Folders 与动态系统卷，也支持单元测试注入虚拟系统卷夹具。
/// </summary>
public sealed record SafetyPolicyEnvironment(
    string[] WindowsDirectories,
    string[] ProgramFilesDirectories,
    string[] ProgramDataDirectories,
    string[] UsersRoots,
    string[] UserProfileRoots,
    string[] AppDataRoots,
    string[] ToolAndStateDirectories,
    Func<string, bool>? IsFixedNtfsVolume = null)
{
    public static SafetyPolicyEnvironment CreateDefault()
    {
        var windowsDirs = CollectValid(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var programFilesDirs = CollectValid(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        var programDataDirs = CollectValid(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userProfileRoots = CollectValid(userProfile);
        var usersRoots = CollectValid(
            string.IsNullOrWhiteSpace(userProfile) ? null : Path.GetDirectoryName(userProfile));

        var appDataRoots = CollectValid(
            string.IsNullOrWhiteSpace(userProfile) ? null : Path.Combine(userProfile, "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            string.IsNullOrWhiteSpace(userProfile) ? null : Path.Combine(userProfile, "AppData", "LocalLow"));

        var toolDirs = CollectValid(
            AppContext.BaseDirectory,
            MigrationRuntime.TransactionDirectory);

        return new SafetyPolicyEnvironment(
            windowsDirs,
            programFilesDirs,
            programDataDirs,
            usersRoots,
            userProfileRoots,
            appDataRoots,
            toolDirs);
    }

    private static string[] CollectValid(params string?[] candidates)
    {
        var list = new List<string>();
        foreach (string? raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                string normalized = PathIdentityService.Normalize(raw.TrimEnd('\\'));
                if (!list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    list.Add(normalized);
            }
            catch
            {
                // 忽略个别不可用的系统环境变量路径，其余已解析路径仍生效
            }
        }
        return list.ToArray();
    }
}

/// <summary>
/// 统一 4 级迁移安全策略引擎（L0 硬拒绝 / L1 推荐迁移 / L2 原生引导 / L3 人工风险确认）。
/// 手动模式、一键模式、磁盘扫描与批量迁移均委托至本策略底座，消除入口双标。
/// </summary>
public static partial class MigrationSafetyPolicy
{
    public const string CurrentPolicyVersion = "2026.11.22-phase1";

    private const FileAttributes UnsupportedFsAttributes =
        FileAttributes.ReparsePoint |
        FileAttributes.SparseFile |
        FileAttributes.Compressed |
        FileAttributes.Encrypted |
        FileAttributes.Offline;

    private static readonly string[] VolumeRootHardDeniedNames =
    [
        "System Volume Information",
        "$Recycle.Bin",
        "Boot",
        "Recovery",
        "EFI",
        "Windows.old",
        ".MoveFolderQuarantine"
    ];

    private static readonly string[] ProgramFilesBlockedSubtrees =
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
    ];

    private static readonly string[] ProgramDataBlockedSubtrees =
    [
        "Microsoft",
        "Package Cache",
        "WindowsHolographicDevices",
        "USOPrivate",
        "USOShared",
        "ssh"
    ];

    private static readonly string[] AppDataLocalBlockedSubtrees =
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
    ];

    private static readonly string[] HighRiskPathSegments =
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
        "SystemCertificates",
        ".ssh",
        ".gnupg"
    ];

    private static readonly string[] CloudSyncSegmentNames =
    [
        "OneDrive",
        "Dropbox",
        "Google Drive",
        "iCloudDrive"
    ];

    private static ISafetyApplicationMatcher? _defaultMatcher;

    /// <summary>
    /// 注册默认应用规则匹配器（由工作包 C ApplicationCatalog 注册）。
    /// </summary>
    public static void RegisterDefaultApplicationMatcher(ISafetyApplicationMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        _defaultMatcher = matcher;
    }

    public static ISafetyApplicationMatcher? DefaultApplicationMatcher => _defaultMatcher;

    /// <summary>
    /// 评估 L0 物理硬禁区（对新迁移、还原、修复及目标路径一律强制生效；L0 永不可被任何配置或确认绕过）。
    /// </summary>
    public static SafetyPolicyDecision EvaluateL0HardDeny(
        string path,
        bool allowLeafReparseForRestore = false,
        bool checkFilesystem = true,
        SafetyPolicyEnvironment? environment = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return HardDeny(SafetyReasonCodes.L0InvalidOrUnresolvedPath, "路径不能为空。");
        }

        string full;
        try
        {
            full = PathIdentityService.Normalize(path);
        }
        catch (Exception ex)
        {
            return HardDeny(SafetyReasonCodes.L0InvalidOrUnresolvedPath, $"路径无效：{ex.Message}");
        }

        var env = environment ?? SafetyPolicyEnvironment.CreateDefault();

        // 1. 驱动器根目录
        if (full.Length == 3)
        {
            return HardDeny(SafetyReasonCodes.L0DriveRoot, "不允许操作驱动器根目录。");
        }

        // 2. 卷根级保留、启动、恢复、回收站、隔离区与旧系统目录（不硬编码盘符，适用任意卷）
        string driveRoot = full[..3];
        foreach (string reserved in VolumeRootHardDeniedNames)
        {
            string reservedFull = Path.Combine(driveRoot, reserved);
            if (PathIdentityService.IsWithin(full, reservedFull))
            {
                string code = reserved.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                    || reserved.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                    ? SafetyReasonCodes.L0VolumeMetadata
                    : reserved.Equals(".MoveFolderQuarantine", StringComparison.OrdinalIgnoreCase)
                        ? SafetyReasonCodes.L0SelfOrActiveTransaction
                        : SafetyReasonCodes.L0BootRecovery;

                string message = reserved.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                    ? "不允许操作卷系统信息目录。"
                    : $"不允许操作系统关键目录：{reservedFull}";
                return HardDeny(code, message);
            }
        }

        // 3. 当前与已识别 Windows 操作系统目录（组件边界比较，C:\WindowsTools 不会被 C:\Windows 误判）
        foreach (string winDir in env.WindowsDirectories)
        {
            if (PathIdentityService.IsWithin(full, winDir))
            {
                return HardDeny(SafetyReasonCodes.L0OsCore, $"不允许操作系统关键目录：{winDir}");
            }
        }

        // 跨盘多系统安装兜底检测（如非当前系统盘存在 <Drive>:\Windows 且包含 System32，或路径正好是 <Drive>:\Windows\System32 等）
        string driveWindows = Path.Combine(driveRoot, "Windows");
        if (PathIdentityService.IsWithin(full, driveWindows)
            && !env.WindowsDirectories.Any(w => w.Equals(driveWindows, StringComparison.OrdinalIgnoreCase)))
        {
            bool looksLikeOtherWindows = IsKnownWindowsCoreSubpath(full, driveWindows)
                || (checkFilesystem && Directory.Exists(Path.Combine(driveWindows, "System32")));
            if (looksLikeOtherWindows)
            {
                return HardDeny(SafetyReasonCodes.L0OsCore, $"不允许操作系统关键目录：{driveWindows}");
            }
        }

        // 4. 工具自身目录、事务状态目录及事务保留备份/staging/probe 目录（双向重叠拦截，禁止迁移包含工具本体的父目录）
        foreach (string toolDir in env.ToolAndStateDirectories)
        {
            if (PathIdentityService.IsWithin(full, toolDir) || PathIdentityService.IsWithin(toolDir, full))
            {
                return HardDeny(SafetyReasonCodes.L0SelfOrActiveTransaction, $"不允许操作工具自身、事务状态目录或包含它们的父目录：{toolDir}");
            }
        }

        if (ContainsTransactionArtifactSegment(full))
        {
            return HardDeny(SafetyReasonCodes.L0SelfOrActiveTransaction, "不允许将迁移备份、staging 或活动事务目录作为源/目标。");
        }

        // 5. 系统容器根本身（仅精确拦截容器根，不阻塞其下合法第三方应用子目录）
        foreach (string container in EnumerateContainerRoots(env))
        {
            if (full.Equals(container, StringComparison.OrdinalIgnoreCase))
            {
                return HardDeny(SafetyReasonCodes.L0SystemContainerRoot, $"不允许操作系统关键目录：{container}（系统容器根目录不可整体迁移）");
            }
        }

        // 5b. 任意用户目录（含 Default、Public 及其他本地用户）的 Profile 根、AppData 根及 Microsoft/Packages/Temp 受保护子树
        SafetyPolicyDecision? anyUserBlock = CheckAnyUserProfileHardDeny(full, env);
        if (anyUserBlock is not null)
        {
            return anyUserBlock;
        }

        // 6. Windows 与系统组件受保护子树
        foreach (string pf in env.ProgramFilesDirectories)
        {
            foreach (string sub in ProgramFilesBlockedSubtrees)
            {
                string blocked = Path.Combine(pf, sub);
                if (PathIdentityService.IsWithin(full, blocked))
                {
                    return HardDeny(SafetyReasonCodes.L0WindowsComponent, $"不允许操作系统关键目录：{blocked}");
                }
            }
        }

        foreach (string pd in env.ProgramDataDirectories)
        {
            foreach (string sub in ProgramDataBlockedSubtrees)
            {
                string blocked = Path.Combine(pd, sub);
                if (PathIdentityService.IsWithin(full, blocked))
                {
                    return HardDeny(SafetyReasonCodes.L0WindowsComponent, $"不允许操作系统关键目录：{blocked}");
                }
            }
        }

        foreach (string profile in env.UserProfileRoots)
        {
            foreach (string sub in AppDataLocalBlockedSubtrees)
            {
                string blocked = Path.Combine(profile, "AppData", "Local", sub);
                if (PathIdentityService.IsWithin(full, blocked))
                {
                    return HardDeny(SafetyReasonCodes.L0WindowsComponent, $"不允许操作系统关键目录：{blocked}");
                }
            }

            string userProgramsCommon = Path.Combine(profile, "AppData", "Local", "Programs", "Common");
            if (PathIdentityService.IsWithin(full, userProgramsCommon))
            {
                return HardDeny(SafetyReasonCodes.L0WindowsComponent, $"不允许操作系统公共组件目录：{userProgramsCommon}");
            }

            string[] otherProfileBlocked =
            [
                Path.Combine(profile, "AppData", "Roaming", "Microsoft"),
                Path.Combine(profile, "AppData", "LocalLow", "Microsoft")
            ];
            foreach (string blocked in otherProfileBlocked)
            {
                if (PathIdentityService.IsWithin(full, blocked))
                {
                    return HardDeny(SafetyReasonCodes.L0WindowsComponent, $"不允许操作系统关键目录：{blocked}");
                }
            }
        }

        // 7. 高危安全软件 / EDR / 驱动 / 凭据密钥目录（按路径组件边界匹配）
        string[] segments = full[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (HighRiskPathSegments.Any(hr => segment.Equals(hr, StringComparison.OrdinalIgnoreCase)))
            {
                return HardDeny(SafetyReasonCodes.L0HighRiskSecurityOrDriver, $"不允许操作高危安全软件、驱动或凭据目录：{segment}");
            }
        }

        // 8. 实时文件系统边界检查（卷类型、祖先重解析点、叶特殊属性）
        if (checkFilesystem)
        {
            var fsError = CheckFilesystemBoundary(full, allowLeafReparseForRestore, env);
            if (fsError is not null)
            {
                return fsError;
            }
        }

        return new SafetyPolicyDecision(
            MigrationAdmissionLevel.ManualRiskConfirm,
            IsAllowedForNewMigration: true,
            IsAllowedForRestoreOrRepair: true,
            ReasonCodes: [SafetyReasonCodes.L3PersonalOrUnlistedConfirm],
            PolicyVersion: CurrentPolicyVersion,
            Summary: "已通过 L0 物理硬禁区校验。");
    }

    /// <summary>
    /// 评估源目录的完整 4 级准入结论（L0 硬拒绝 → 应用规则 L1/L2 → 云盘 L2 → L3 人工风险确认）。
    /// </summary>
    public static SafetyPolicyDecision EvaluateSource(
        string path,
        ISafetyApplicationMatcher? matcher = null,
        SafetyEvaluationContext context = SafetyEvaluationContext.NewMigration,
        bool checkFilesystem = true,
        SafetyPolicyEnvironment? environment = null)
    {
        bool allowLeafReparse = context == SafetyEvaluationContext.RestoreOrRepair;
        SafetyPolicyDecision l0 = EvaluateL0HardDeny(path, allowLeafReparse, checkFilesystem, environment);
        if (l0.IsHardDenied)
        {
            return l0;
        }

        string full = PathIdentityService.Normalize(path);
        var env = environment ?? SafetyPolicyEnvironment.CreateDefault();

        // 对新迁移源，检查目录存在性、可读性（失败封闭）
        if (checkFilesystem && context == SafetyEvaluationContext.NewMigration)
        {
            if (!Directory.Exists(full))
            {
                return HardDeny(SafetyReasonCodes.L0InvalidOrUnresolvedPath, "源目录不存在。");
            }

            try
            {
                using var enumerator = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
                _ = enumerator.MoveNext();
            }
            catch (UnauthorizedAccessException)
            {
                return HardDeny(SafetyReasonCodes.L0AccessDeniedOrUnverified, "无权访问源目录，按安全默认拒绝迁移。");
            }
            catch (Exception ex)
            {
                return HardDeny(SafetyReasonCodes.L0AccessDeniedOrUnverified, $"无法核实源目录可读性：{ex.Message}");
            }
        }

        // 还原或修复场景：只强制 L0 物理硬禁区，不因应用允许列表阻断用户恢复原状
        if (context == SafetyEvaluationContext.RestoreOrRepair)
        {
            return new SafetyPolicyDecision(
                MigrationAdmissionLevel.VerifiedEligible,
                IsAllowedForNewMigration: false,
                IsAllowedForRestoreOrRepair: true,
                ReasonCodes: [SafetyReasonCodes.L1VerifiedAppRule],
                PolicyVersion: CurrentPolicyVersion,
                Summary: "还原/修复专用校验通过 L0 物理硬禁区检查。");
        }

        // 应用规则库判定（L1 / L2 / L0 覆盖）
        ISafetyApplicationMatcher? activeMatcher = matcher ?? _defaultMatcher;
        SafetyApplicationMatch? appMatch = activeMatcher?.Match(full);
        if (appMatch is not null)
        {
            if (appMatch.RecommendedLevel == MigrationAdmissionLevel.HardDenied)
            {
                return HardDeny(
                    SafetyReasonCodes.L0HighRiskSecurityOrDriver,
                    appMatch.Warnings.FirstOrDefault() ?? $"应用规则禁止迁移该目录：{appMatch.DisplayName}");
            }

            if (appMatch.RecommendedLevel == MigrationAdmissionLevel.VendorNativeRecommended)
            {
                return new SafetyPolicyDecision(
                    MigrationAdmissionLevel.VendorNativeRecommended,
                    IsAllowedForNewMigration: false,
                    IsAllowedForRestoreOrRepair: true,
                    ReasonCodes: [SafetyReasonCodes.L2VendorNativeGuidance],
                    PolicyVersion: CurrentPolicyVersion,
                    Summary: $"{appMatch.DisplayName} 建议使用厂商原生迁移方案，默认不推荐符号链接迁移。",
                    MatchedApplicationId: appMatch.ApplicationId,
                    MatchedApplicationName: appMatch.DisplayName,
                    NativeGuidance: appMatch.NativeGuidance,
                    Warnings: appMatch.Warnings,
                    Evidence: appMatch.Evidence,
                    RequiredStoppedProcesses: appMatch.RequiredStoppedProcesses);
            }

            if (appMatch.RecommendedLevel == MigrationAdmissionLevel.VerifiedEligible)
            {
                return new SafetyPolicyDecision(
                    MigrationAdmissionLevel.VerifiedEligible,
                    IsAllowedForNewMigration: true,
                    IsAllowedForRestoreOrRepair: true,
                    ReasonCodes: [SafetyReasonCodes.L1VerifiedAppRule],
                    PolicyVersion: CurrentPolicyVersion,
                    Summary: $"命中已验证应用业务数据规则：{appMatch.DisplayName}。",
                    MatchedApplicationId: appMatch.ApplicationId,
                    MatchedApplicationName: appMatch.DisplayName,
                    Warnings: appMatch.Warnings,
                    Evidence: appMatch.Evidence,
                    RequiredStoppedProcesses: appMatch.RequiredStoppedProcesses);
            }
        }

        // 云同步目录内置兜底：归入 L2 原生引导
        string? cloudSegment = DetectCloudSyncSegment(full);
        if (cloudSegment is not null)
        {
            return new SafetyPolicyDecision(
                MigrationAdmissionLevel.VendorNativeRecommended,
                IsAllowedForNewMigration: false,
                IsAllowedForRestoreOrRepair: true,
                ReasonCodes: [SafetyReasonCodes.L2VendorNativeGuidance],
                PolicyVersion: CurrentPolicyVersion,
                Summary: $"检测到云盘同步目录（{cloudSegment}），建议使用客户端自带“更改文件夹位置”功能迁移。",
                MatchedApplicationId: "cloud-sync-" + cloudSegment.ToLowerInvariant().Replace(" ", "-"),
                MatchedApplicationName: cloudSegment,
                NativeGuidance: $"请在 {cloudSegment} 客户端设置中暂停/取消链接并更改本地同步目录位置，避免符号链接引发云端误删或冲突。",
                Warnings: [$"警告: 该目录可能位于云盘同步文件夹（{cloudSegment}），迁移可能导致同步冲突"]);
        }

        // L3 人工风险确认：位于非 L0 区的普通个人数据目录或未收录第三方目录
        var warnings = new List<string>
        {
            "该目录为普通个人数据或未收录第三方目录，未经官方兼容性实测，请确认无系统服务依赖后再勾选迁移。"
        };

        if (env.ProgramFilesDirectories.Any(pf => PathIdentityService.IsWithin(full, pf)))
        {
            warnings.Add("该目录位于 Program Files 下且未命中 L1 业务数据规则；整体迁移程序本体可能导致权限、更新或服务异常，建议仅迁移独立业务数据子目录。");
        }

        return new SafetyPolicyDecision(
            MigrationAdmissionLevel.ManualRiskConfirm,
            IsAllowedForNewMigration: true,
            IsAllowedForRestoreOrRepair: true,
            ReasonCodes: [SafetyReasonCodes.L3PersonalOrUnlistedConfirm],
            PolicyVersion: CurrentPolicyVersion,
            Summary: "位于非系统禁区的普通个人数据或未收录目录（L3 人工风险确认）。",
            Warnings: warnings.ToArray());
    }

    /// <summary>
    /// 评估目标路径是否合法（不得落入 L0 硬禁区，已有祖先不得包含重解析点，所在卷必须为可用固定本地 NTFS）。
    /// 注意：为兼容 Restore/Repair 反向传入已存在的符号链接源路径，本方法允许叶节点已存在为链接，
    /// 新迁移目标为空/非链接由 PathValidator.IsTargetDirectoryEmpty 与 MigrationWorkflowHelper 独立核验。
    /// </summary>
    public static SafetyPolicyDecision EvaluateTarget(
        string path,
        bool checkFilesystem = true,
        SafetyPolicyEnvironment? environment = null)
    {
        return EvaluateL0HardDeny(path, allowLeafReparseForRestore: true, checkFilesystem: checkFilesystem, environment: environment);
    }

    /// <summary>
    /// 快速判断某已规范化路径是否属于扫描/迁移的 L0 硬排除区（不执行昂贵的磁盘逐项枚举，适合扫描树渲染使用）。
    /// </summary>
    public static bool IsPathExcludedFromScanMigration(string normalizedPath, SafetyPolicyEnvironment? environment = null)
    {
        return EvaluateL0HardDeny(normalizedPath, allowLeafReparseForRestore: false, checkFilesystem: false, environment: environment).IsHardDenied;
    }

    private static SafetyPolicyDecision? CheckFilesystemBoundary(
        string fullPath,
        bool allowLeafReparse,
        SafetyPolicyEnvironment env)
    {
        string driveRoot = fullPath[..3];

        // 1. 卷能力校验：初始迁移仅支持本地固定 NTFS 卷
        if (env.IsFixedNtfsVolume is not null)
        {
            if (!env.IsFixedNtfsVolume(driveRoot))
            {
                return HardDeny(SafetyReasonCodes.L0UnsupportedVolume, $"目标或源驱动器不是受支持的本地固定 NTFS 卷：{driveRoot}");
            }
        }
        else
        {
            try
            {
                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady)
                {
                    return HardDeny(SafetyReasonCodes.L0UnsupportedVolume, $"驱动器不可用：{driveRoot}");
                }
                if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    return HardDeny(SafetyReasonCodes.L0UnsupportedVolume, $"初始迁移仅支持本地固定 NTFS 卷（当前：{drive.DriveType} / {drive.DriveFormat}）。");
                }
            }
            catch (Exception ex)
            {
                return HardDeny(SafetyReasonCodes.L0UnsupportedVolume, $"无法核实驱动器状态：{ex.Message}");
            }
        }

        // 2. 逐级校验已存在祖先不得为重解析点（直接调 Win32 GetFileAttributesW，禁止用 Directory.Exists 静默吞权限拒绝或悬空链接）
        for (int end = 3; end < fullPath.Length;)
        {
            string ancestor = fullPath[..end];
            if (TryGetNativeAttributes(ancestor, out FileAttributes attr, out int win32Err))
            {
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    return HardDeny(SafetyReasonCodes.L0SpecialFsStructure, $"祖先路径包含重解析点/符号链接，禁止绕过：{ancestor}");
                }
                if ((attr & FileAttributes.Directory) == 0)
                {
                    return HardDeny(SafetyReasonCodes.L0InvalidOrUnresolvedPath, $"祖先路径不是目录：{ancestor}");
                }
            }
            else if (win32Err is not (2 or 3)) // 非 ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND 一律失败封闭
            {
                return HardDeny(SafetyReasonCodes.L0AccessDeniedOrUnverified, $"无法核实祖先路径属性（Win32 错误 {win32Err}）：{ancestor}");
            }

            int next = fullPath.IndexOf('\\', end + 1);
            if (next < 0) break;
            end = next;
        }

        // 3. 校验叶对象属性（仅 ERROR_FILE_NOT_FOUND/ERROR_PATH_NOT_FOUND 视作尚不存在；其他错误失败封闭）
        if (TryGetNativeAttributes(fullPath, out FileAttributes leafAttr, out int leafErr))
        {
            FileAttributes forbidden = allowLeafReparse
                ? UnsupportedFsAttributes & ~FileAttributes.ReparsePoint
                : UnsupportedFsAttributes;

            if ((leafAttr & forbidden) != 0)
            {
                return HardDeny(
                    SafetyReasonCodes.L0SpecialFsStructure,
                    $"路径包含尚未支持安全迁移的特殊文件系统属性（{leafAttr & forbidden}）：{fullPath}");
            }
        }
        else if (leafErr is not (2 or 3))
        {
            return HardDeny(SafetyReasonCodes.L0AccessDeniedOrUnverified, $"无权读取或核实路径属性（Win32 错误 {leafErr}）：{fullPath}");
        }

        return null;
    }

    private static SafetyPolicyDecision? CheckAnyUserProfileHardDeny(string fullPath, SafetyPolicyEnvironment env)
    {
        foreach (string usersRoot in env.UsersRoots)
        {
            if (!PathIdentityService.IsWithin(fullPath, usersRoot) || fullPath.Equals(usersRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rel = fullPath[(usersRoot.Length + 1)..];
            string[] parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            // <UsersRoot>\<AnyUser> 根本身不可整体迁移
            if (parts.Length == 1)
            {
                return HardDeny(SafetyReasonCodes.L0SystemContainerRoot, $"不允许操作系统关键目录：{fullPath}（用户配置文件根目录不可整体迁移）");
            }

            if (parts[1].Equals("AppData", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2)
                {
                    return HardDeny(SafetyReasonCodes.L0SystemContainerRoot, $"不允许操作系统关键目录：{fullPath}（AppData 容器根目录不可整体迁移）");
                }

                bool isAppDataBucket = parts[2].Equals("Local", StringComparison.OrdinalIgnoreCase)
                    || parts[2].Equals("Roaming", StringComparison.OrdinalIgnoreCase)
                    || parts[2].Equals("LocalLow", StringComparison.OrdinalIgnoreCase);

                if (isAppDataBucket && parts.Length == 3)
                {
                    return HardDeny(SafetyReasonCodes.L0SystemContainerRoot, $"不允许操作系统关键目录：{fullPath}");
                }

                if (isAppDataBucket && parts.Length >= 4)
                {
                    // <Users>\<User>\AppData\Local\Programs 是 Windows 官方用户级程序安装根目录（对应 Program Files），
                    // 整体迁移会影响所有用户级安装软件（VS Code / Python / IDE 等），仅禁止根本身，允许展开迁移其下具体数据子目录
                    if (parts[2].Equals("Local", StringComparison.OrdinalIgnoreCase)
                        && parts[3].Equals("Programs", StringComparison.OrdinalIgnoreCase))
                    {
                        if (parts.Length == 4)
                        {
                            return HardDeny(SafetyReasonCodes.L0SystemContainerRoot, $"不允许整体迁移用户级程序安装根目录：{fullPath}（请展开选择具体软件的数据子目录）");
                        }
                        if (parts.Length >= 5 && parts[4].Equals("Common", StringComparison.OrdinalIgnoreCase))
                        {
                            return HardDeny(SafetyReasonCodes.L0WindowsComponent, $"不允许操作系统公共组件目录：{fullPath}");
                        }
                    }

                    if (parts[3].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                        || (parts[2].Equals("Local", StringComparison.OrdinalIgnoreCase)
                            && AppDataLocalBlockedSubtrees.Any(s => parts[3].Equals(s, StringComparison.OrdinalIgnoreCase))))
                    {
                        return HardDeny(SafetyReasonCodes.L0WindowsComponent, $"不允许操作系统关键目录：{fullPath}");
                    }
                }
            }
        }
        return null;
    }

    private static bool TryGetNativeAttributes(string fullPath, out FileAttributes attributes, out int errorCode)
    {
        uint raw = GetFileAttributesW(@"\\?\" + fullPath);
        if (raw == 0xFFFFFFFFU)
        {
            errorCode = Marshal.GetLastWin32Error();
            attributes = default;
            return false;
        }
        errorCode = 0;
        attributes = (FileAttributes)raw;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    private static IEnumerable<string> EnumerateContainerRoots(SafetyPolicyEnvironment env)
    {
        foreach (string p in env.ProgramFilesDirectories) yield return p;
        foreach (string p in env.ProgramDataDirectories) yield return p;
        foreach (string p in env.UsersRoots) yield return p;
        foreach (string p in env.UserProfileRoots)
        {
            yield return p;
            yield return Path.Combine(p, "AppData", "Local", "Programs");
        }
        foreach (string p in env.AppDataRoots) yield return p;
        foreach (string u in env.UsersRoots) yield return Path.Combine(u, "Public");
    }

    private static bool IsKnownWindowsCoreSubpath(string fullPath, string driveWindows)
    {
        if (fullPath.Equals(driveWindows, StringComparison.OrdinalIgnoreCase)) return true;
        string[] coreSubs = ["System32", "SysWOW64", "WinSxS", "Installer", "SoftwareDistribution", "servicing", "Boot"];
        return coreSubs.Any(sub => PathIdentityService.IsWithin(fullPath, Path.Combine(driveWindows, sub)));
    }

    private static bool ContainsTransactionArtifactSegment(string fullPath)
    {
        foreach (string segment in fullPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TransactionArtifactRegex().IsMatch(segment))
                return true;
        }
        return false;
    }

    private static string? DetectCloudSyncSegment(string fullPath)
    {
        foreach (string segment in fullPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string cloud in CloudSyncSegmentNames)
            {
                if (segment.Equals(cloud, StringComparison.OrdinalIgnoreCase)
                    || segment.StartsWith(cloud + " -", StringComparison.OrdinalIgnoreCase))
                {
                    return cloud;
                }
            }
        }
        return null;
    }

    private static SafetyPolicyDecision HardDeny(string reasonCode, string summary) => new(
        MigrationAdmissionLevel.HardDenied,
        IsAllowedForNewMigration: false,
        IsAllowedForRestoreOrRepair: false,
        ReasonCodes: [reasonCode],
        PolicyVersion: CurrentPolicyVersion,
        Summary: summary);

    [GeneratedRegex(@"(\.bak_|\.copy_|\.restore_|_lock_test_)[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex TransactionArtifactRegex();
}
