using MigrationCore.Models;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 垃圾清理「永久排除区」策略引擎（永不提供勾选入口，不可被用户开关、JSON 配置或 AI 建议解锁）。
/// 保护范围（对齐设计方案 7.3 / 9.1 / 9.2）：
/// 1. 所有迁移原始备份、待恢复副本、活动事务目录与工具自身目录（绝不因名称含 .bak/.tmp 或时间较久而误判）；
/// 2. 活动 Windows 核心文件、启动/恢复/卷元数据、隔离区自身；
/// 3. 浏览器登录凭据/Cookies/会话/书签/历史与密钥目录（.ssh/.gnupg 等）；
/// 4. 用户个人文档/下载/桌面/图片/视频/音乐/模型库/游戏存档；
/// 5. 整棵 AppData 容器根；
/// 6. 云同步占位文件、重解析点/符号链接/硬链接（对规则外 AI 疑似残留强制硬拦截）。
/// </summary>
public static class CleanupExclusionPolicy
{
    public static class ReasonCodes
    {
        public const string MigrationBackupOrTransaction = "CLEANUP_EXCLUDED_MIGRATION_BACKUP";
        public const string SystemCoreOrProtected = "CLEANUP_EXCLUDED_SYSTEM_CORE";
        public const string BrowserCredentialsOrProfile = "CLEANUP_EXCLUDED_BROWSER_CREDENTIALS";
        public const string PersonalDocumentsOrData = "CLEANUP_EXCLUDED_PERSONAL_DATA";
        public const string AppDataOrUserRoot = "CLEANUP_EXCLUDED_CONTAINER_ROOT";
        public const string SpecialFsStructure = "CLEANUP_EXCLUDED_SPECIAL_FS";
        public const string ToolOrQuarantineSelf = "CLEANUP_EXCLUDED_TOOL_SELF";
        public const string InvalidPath = "CLEANUP_EXCLUDED_INVALID_PATH";
    }

    /// <summary>
    /// 浏览器与凭据敏感文件名（永不清理，即使出现在自定义扫描或缓存混存目录中）。
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenSet<string> ProtectedCredentialAndBrowserFileNames =
        ProtectedPathVocabulary.CreateIgnoreCaseSet(
        [
            "Login Data",
            "Login Data-journal",
            "Login Data For Account",
            "Cookies",
            "Cookies-journal",
            "Network Persistent State",
            "Web Data",
            "Web Data-journal",
            "Bookmarks",
            "Bookmarks.bak",
            "History",
            "History-journal",
            "Favicons",
            "Local State",
            "Preferences",
            "Secure Preferences",
            "Top Sites",
            "Visited Links",
            "places.sqlite",
            "logins.json",
            "key4.db",
            "key3.db",
            "cert9.db",
            "cookies.sqlite",
            "formhistory.sqlite",
            "id_rsa",
            "id_ed25519",
            "known_hosts",
            "credentials"
        ]);

    /// <summary>
    /// 浏览器/凭据敏感目录段名（命中即永久排除；共享密钥/证书段委托 ProtectedPathVocabulary 单一事实源）。
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenSet<string> ProtectedCredentialSegments =
        ProtectedPathVocabulary.CreateIgnoreCaseSet(
        [
            "Session Storage",
            "Local Storage",
            "IndexedDB",
            "Sync Data",
            "Sync Extension Settings",
            "Extension Cookies",
            "Network",
            "WebStorage",
            "Sessions",
            ".aws",
            ".kube",
            ".azure",
            "Credentials",
            "Vault",
            "Protect",
            "Crypto",
            "Keychains",
            .. ProtectedPathVocabulary.SharedKeyAndCertificateSegments
        ]);

    /// <summary>
    /// 常见个人数据、模型库、云同步与游戏存档目录段名（针对规则外 AI 疑似残留与泛化扫描硬拦截）。
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenSet<string> ProtectedPersonalAndModelSegments =
        ProtectedPathVocabulary.CreateIgnoreCaseSet(
        [
            "Documents",
            "My Documents",
            "Desktop",
            "Downloads",
            "Pictures",
            "My Pictures",
            "Videos",
            "My Videos",
            "Music",
            "My Music",
            "Saved Games",
            "WeChat Files",
            "Tencent Files",
            "xwechat_files",
            "models",
            "checkpoints",
            "loras",
            "SaveGames",
            "saves",
            .. ProtectedPathVocabulary.CloudSyncSegmentNames
        ]);

    private static readonly System.Collections.Frozen.FrozenSet<string> AllowedBrowserCacheSegments =
        ProtectedPathVocabulary.CreateIgnoreCaseSet(
        [
            "Cache",
            "Cache_Data",
            "Code Cache",
            "GPUCache",
            "GrShaderCache",
            "ShaderCache",
            "DawnCache",
            "DawnGraphiteCache",
            "DawnWebGPUCache",
            "GraphiteDawnCache",
            "cache2",
            "startupCache",
            "jumpListCache",
            "thumbnails",
            "shader-cache"
        ]);

    /// <summary>
    /// 收集当前应用已登记的全部迁移事务路径（原路径、目标路径、备份路径），用于永久排除保护。
    /// </summary>
    public static IReadOnlyList<string> LoadProtectedTransactionPaths(string? stateDirectory = null)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var store = new MigrationTransactionStore(stateDirectory);
            IReadOnlyList<MigrationTransactionRecord> records = store.ReadAllAsync().GetAwaiter().GetResult();
            foreach (MigrationTransactionRecord r in records)
            {
                AddIfValid(protectedPaths, r.SourcePath);
                AddIfValid(protectedPaths, r.TargetPath);
                AddIfValid(protectedPaths, r.BackupPath);
            }
        }
        catch
        {
            // 状态库读取异常时仍保留静态后缀与目录名防线
        }

        AddIfValid(protectedPaths, MigrationRuntime.TransactionDirectory);
        AddIfValid(protectedPaths, AppContext.BaseDirectory);
        return protectedPaths.ToArray();
    }

    /// <summary>
    /// 为内置已审查规则加载受保护事务路径：
    /// - 所有迁移原始备份（BackupPath）、暂存/探测目录及活动中的事务目录永久排除；
    /// - 对已完成的迁移（Stage == AwaitingUserVerification 且非活动），允许内置规则追踪其符号链接目标下的可重建缓存子目录，
    ///   但其 BackupPath 仍绝对永久排除，绝不触碰任何原始备份。
    /// </summary>
    public static (IReadOnlyList<string> ProtectedPaths, IReadOnlyList<(string SourcePath, string TargetPath)> CompletedMigrations)
        LoadBuiltInRuleTransactionContext(string? stateDirectory = null)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = new List<(string SourcePath, string TargetPath)>();

        try
        {
            var store = new MigrationTransactionStore(stateDirectory);
            IReadOnlyList<MigrationTransactionRecord> records = store.ReadAllAsync().GetAwaiter().GetResult();
            foreach (MigrationTransactionRecord r in records)
            {
                // 备份、暂存、探测目录任何状态下均永久排除
                AddIfValid(protectedPaths, r.BackupPath);
                AddIfValid(protectedPaths, r.StagingPath);
                AddIfValid(protectedPaths, r.ProbePath);

                bool isCompletedLink = !r.IsActive
                    && r.Stage == MigrationStage.AwaitingUserVerification
                    && r.LinkIdentity is not null
                    && !string.IsNullOrWhiteSpace(r.SourcePath)
                    && !string.IsNullOrWhiteSpace(r.TargetPath);

                if (isCompletedLink)
                {
                    try
                    {
                        string normSrc = PathIdentityService.Normalize(r.SourcePath);
                        string normDst = PathIdentityService.Normalize(r.TargetPath);
                        if (!normSrc.Equals(normDst, StringComparison.OrdinalIgnoreCase))
                        {
                            completed.Add((normSrc, normDst));
                        }
                    }
                    catch
                    {
                        AddIfValid(protectedPaths, r.SourcePath);
                        AddIfValid(protectedPaths, r.TargetPath);
                    }
                }
                else
                {
                    AddIfValid(protectedPaths, r.SourcePath);
                    AddIfValid(protectedPaths, r.TargetPath);
                }
            }
        }
        catch
        {
            // 状态库读取异常时仍保留静态后缀与目录名防线
        }

        AddIfValid(protectedPaths, MigrationRuntime.TransactionDirectory);
        AddIfValid(protectedPaths, AppContext.BaseDirectory);
        return (protectedPaths.ToArray(), completed);
    }

    /// <summary>
    /// 针对单个文件/目录路径执行清理永久排除区检查（适用于通道 A 内置规则枚举过滤）。
    /// </summary>
    public static CleanupExclusionVerdict EvaluateFileForRuleCleanup(
        string fullPath,
        IReadOnlyCollection<string>? registeredTransactionPaths = null,
        SafetyPolicyEnvironment? environment = null,
        bool isKnownBrowserCacheRule = false)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return Exclude(ReasonCodes.InvalidPath, "无效路径", "清理路径不能为空。");
        }

        string normalized;
        try
        {
            normalized = PathIdentityService.Normalize(fullPath);
        }
        catch (Exception ex)
        {
            return Exclude(ReasonCodes.InvalidPath, "无效路径", $"路径规范化失败：{ex.Message}");
        }

        // 1. 工具自身、隔离区与迁移事务/备份目录（即使伪装为 .tmp/.bak 也永久排除）
        if (IsToolQuarantineOrBackupArtifact(normalized, registeredTransactionPaths, environment))
        {
            return Exclude(
                ReasonCodes.MigrationBackupOrTransaction,
                "迁移原始备份 / 事务保护",
                "命中「永久排除区（迁移原始备份、工具自身或隔离区）」；备份仅允许在全局备份管理中人工处理。");
        }

        // 2. 浏览器登录凭据 / Cookies / 会话 / 书签 / 历史 / 密钥目录（即便在浏览器缓存规则中也严禁误碰）
        string fileName = Path.GetFileName(normalized);
        if (ProtectedCredentialAndBrowserFileNames.Contains(fileName))
        {
            return Exclude(
                ReasonCodes.BrowserCredentialsOrProfile,
                "凭据 / 浏览器登录数据",
                $"文件「{fileName}」属于浏览器凭据、Cookies、书签或密钥保护项，永不清理。");
        }

        string[] segments = GetPathSegments(normalized);
        foreach (string seg in segments)
        {
            if (ProtectedCredentialSegments.Contains(seg))
            {
                return Exclude(
                    ReasonCodes.BrowserCredentialsOrProfile,
                    "凭据 / 浏览器会话",
                    $"路径段「{seg}」属于凭据、密钥或浏览器会话数据区，永不清理。");
            }
        }

        // 3. 浏览器缓存规则额外防护：仅允许 Cache/Cache_Data/Code Cache/GPUCache/GrShaderCache/ShaderCache 子树
        if (isKnownBrowserCacheRule && !IsWithinAllowedBrowserCacheSubtree(segments))
        {
            return Exclude(
                ReasonCodes.BrowserCredentialsOrProfile,
                "浏览器配置保护",
                "浏览器清理仅限定于可重建的 Cache / Code Cache / GPUCache 子目录，禁止触碰 Profile 其他数据。");
        }

        return Allow();
    }

    /// <summary>
    /// 针对规则库外「AI 疑似残留」目录执行最严格的永久排除区硬拦截（对齐设计方案 9.2 与画板 11）。
    /// 命中系统核心/容器根/迁移备份/个人文档/模型库/凭据/重解析点/硬链接/云占位/占用中一律硬拦截且永不提供勾选入口。
    /// </summary>
    public static CleanupExclusionVerdict EvaluateAiSuspectedResidualDirectory(
        string fullPath,
        ScanSnapshot? snapshot = null,
        int? nodeId = null,
        IReadOnlyCollection<string>? registeredTransactionPaths = null,
        SafetyPolicyEnvironment? environment = null,
        bool checkFilesystem = true)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return Exclude(ReasonCodes.InvalidPath, "无效路径", "目录路径为空。");
        }

        string normalized;
        try
        {
            normalized = PathIdentityService.Normalize(fullPath);
        }
        catch (Exception ex)
        {
            return Exclude(ReasonCodes.InvalidPath, "无效路径", $"路径无效：{ex.Message}");
        }

        // 1. 迁移原始备份、事务目录、工具本体与隔离区
        if (IsToolQuarantineOrBackupArtifact(normalized, registeredTransactionPaths, environment))
        {
            return Exclude(
                ReasonCodes.MigrationBackupOrTransaction,
                "迁移原始备份 / 事务目录",
                "命中「永久排除区（迁移原始备份 / 工具自身 / 隔离区）」；即使 AI 建议清理，核心安全策略也强制拒收且永不提供勾选入口。");
        }

        // 2. 复用统一 L0 物理硬禁区（系统核心、卷根、Program Files/Users/AppData 容器根、Windows 组件、杀软/EDR/驱动、重解析点/云占位）
        SafetyPolicyDecision l0 = MigrationSafetyPolicy.EvaluateL0HardDeny(
            normalized,
            allowLeafReparseForRestore: false,
            checkFilesystem: checkFilesystem,
            environment: environment);
        if (l0.IsHardDenied)
        {
            string category = l0.ReasonCodes.Contains(SafetyReasonCodes.L0SystemContainerRoot)
                ? "系统容器根 / 整棵 AppData"
                : l0.ReasonCodes.Contains(SafetyReasonCodes.L0SpecialFsStructure)
                    ? "重解析点 / 云占位 / 特殊结构"
                    : "系统核心 / 高危保护区";

            return Exclude(
                l0.ReasonCodes.Contains(SafetyReasonCodes.L0SystemContainerRoot)
                    ? ReasonCodes.AppDataOrUserRoot
                    : l0.ReasonCodes.Contains(SafetyReasonCodes.L0SpecialFsStructure)
                        ? ReasonCodes.SpecialFsStructure
                        : ReasonCodes.SystemCoreOrProtected,
                category,
                $"命中「永久排除区（{category}）」：{l0.Summary} 即使 AI 建议清理，核心安全策略也强制拒收且永不提供勾选入口。");
        }

        // 3. 个人 Known Folders（文档/桌面/下载/图片/视频/音乐）与凭据/模型/游戏存档/云同步目录硬拦截
        SafetyPolicyEnvironment env = environment ?? SafetyPolicyEnvironment.CreateDefault();
        foreach (string personalRoot in EnumeratePersonalKnownFolders(env))
        {
            if (PathIdentityService.IsWithin(normalized, personalRoot))
            {
                return Exclude(
                    ReasonCodes.PersonalDocumentsOrData,
                    "个人文档 / 个人数据区",
                    $"命中「永久排除区（个人文档 / 迁移备份）」：{personalRoot} 属于用户个人数据目录，AI 疑似残留通道严禁触碰。");
            }
        }

        string[] segments = GetPathSegments(normalized);
        foreach (string seg in segments)
        {
            if (ProtectedCredentialSegments.Contains(seg))
            {
                return Exclude(
                    ReasonCodes.BrowserCredentialsOrProfile,
                    "凭据 / 密钥 / 会话目录",
                    $"命中「永久排除区（凭据 / 密钥：{seg}）」，永不提供清理勾选入口。");
            }

            if (ProtectedPersonalAndModelSegments.Contains(seg))
            {
                return Exclude(
                    ReasonCodes.PersonalDocumentsOrData,
                    "个人文档 / 模型库 / 游戏存档",
                    $"命中「永久排除区（个人文档 / 模型库 / 存档：{seg}）」，AI 疑似残留通道强制拒收且永不提供勾选入口。");
            }
        }

        // 4. 浏览器 User Data 目录硬拦截（规则外 AI 不得建议清理浏览器配置目录）
        if (IsBrowserUserDataPath(segments))
        {
            return Exclude(
                ReasonCodes.BrowserCredentialsOrProfile,
                "浏览器用户配置与登录凭据",
                "命中「永久排除区（浏览器配置与登录凭据）」；浏览器缓存仅允许通过内置受审规则清理，AI 疑似残留通道禁止触碰。");
        }

        // 5. 若绑定了扫描快照节点，核查子树是否存在重解析点、硬链接、云占位或未完成节点
        if (snapshot is not null && nodeId is int nid && (uint)nid < (uint)snapshot.Nodes.Length)
        {
            ref readonly ScanNodeRecord node = ref snapshot.Nodes[nid];
            if (!node.IsDirectory || node.IsReparsePoint || node.IsCloudPlaceholder || node.IsOrphaned)
            {
                return Exclude(
                    ReasonCodes.SpecialFsStructure,
                    "特殊文件系统结构",
                    "目录为重解析点、云同步占位或孤立节点，命中永久排除区。");
            }

            SubtreeHazardReport hazards = snapshot.AnalyzeSubtreeHazards(nid, maxExternalSamples: 0);
            if (hazards.HasReparseDescendants || hazards.HasHardLinks || hazards.HasIncompleteNodes)
            {
                return Exclude(
                    ReasonCodes.SpecialFsStructure,
                    "子树含链接 / 硬链接 / 不可读节点",
                    "目录子树包含符号链接/联接点、硬链接或权限受限节点，按防误删底线强制拒收。");
            }
        }

        return Allow();
    }

    private static bool IsToolQuarantineOrBackupArtifact(
        string normalizedPath,
        IReadOnlyCollection<string>? registeredTransactionPaths,
        SafetyPolicyEnvironment? environment)
    {
        // 1. 任意卷的 .MoveFolderQuarantine 隔离区本身
        string[] segments = GetPathSegments(normalizedPath);
        if (segments.Length > 0 && segments[0].Equals(".MoveFolderQuarantine", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. 迁移备份/staging/probe 命名段（包括 .bak_、.movefolder_staging_、_backup_202 等）
        foreach (string seg in segments)
        {
            if (seg.Contains(".bak_", StringComparison.OrdinalIgnoreCase)
                || seg.Contains(".movefolder_staging_", StringComparison.OrdinalIgnoreCase)
                || seg.Contains(".movefolder_probe_", StringComparison.OrdinalIgnoreCase)
                || seg.Contains("MoveWithSymlinkWPF", StringComparison.OrdinalIgnoreCase)
                || seg.Contains("_backup_20", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 3. 工具与状态目录
        SafetyPolicyEnvironment env = environment ?? SafetyPolicyEnvironment.CreateDefault();
        foreach (string toolDir in env.ToolAndStateDirectories)
        {
            if (!string.IsNullOrWhiteSpace(toolDir)
                && (PathIdentityService.IsWithin(normalizedPath, toolDir) || PathIdentityService.IsWithin(toolDir, normalizedPath)))
            {
                return true;
            }
        }

        // 4. 事务库已登记的所有活跃源/目标/备份目录（双向防包含）
        if (registeredTransactionPaths is not null)
        {
            foreach (string txPath in registeredTransactionPaths)
            {
                if (!string.IsNullOrWhiteSpace(txPath)
                    && (PathIdentityService.IsWithin(normalizedPath, txPath) || PathIdentityService.IsWithin(txPath, normalizedPath)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumeratePersonalKnownFolders(SafetyPolicyEnvironment env)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void TryYield(HashSet<string> set, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                set.Add(PathIdentityService.Normalize(path.TrimEnd('\\')));
            }
            catch
            {
            }
        }

        // 生产环境 Known Folders
        TryYield(seen, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        TryYield(seen, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        TryYield(seen, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        TryYield(seen, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        TryYield(seen, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

        // 结合注入的 UserProfileRoots（兼顾单元测试虚拟夹具与多用户目录）
        string[] standardPersonalNames = ["Documents", "Desktop", "Downloads", "Pictures", "Videos", "Music", "Saved Games", "OneDrive"];
        foreach (string profile in env.UserProfileRoots)
        {
            foreach (string folder in standardPersonalNames)
            {
                TryYield(seen, Path.Combine(profile, folder));
            }
        }

        return seen;
    }

    private static bool IsWithinAllowedBrowserCacheSubtree(string[] segments)
    {
        foreach (string seg in segments)
        {
            if (AllowedBrowserCacheSegments.Contains(seg))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsBrowserUserDataPath(string[] segments)
    {
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("User Data", StringComparison.OrdinalIgnoreCase)
                || (segments[i].Equals("Firefox", StringComparison.OrdinalIgnoreCase)
                    && segments[i + 1].Equals("Profiles", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static string[] GetPathSegments(string normalizedPath) =>
        normalizedPath.Length <= 3
            ? []
            : normalizedPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private static void AddIfValid(HashSet<string> set, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            set.Add(PathIdentityService.Normalize(path));
        }
        catch
        {
        }
    }

    private static CleanupExclusionVerdict Allow() => new(false, null, null, null);

    private static CleanupExclusionVerdict Exclude(string code, string category, string message) =>
        new(true, code, category, message);
}
