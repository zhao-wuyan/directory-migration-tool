using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 清理规则根目录定位器策略接口（每个跨盘或标准根定位器独立封装，消除巨型 switch）。
/// </summary>
internal interface ICleanupRootPathStrategy
{
    bool SkipSubPathWhenAlreadyLeaf { get; }
    IReadOnlyList<string> ResolveRoots(
        CleanupPathRuleSpec spec,
        IReadOnlyList<string> protectedTxPaths,
        CleanupRootResolutionCache cache);
}

/// <summary>
/// 单次扫描会话级定位缓存：避免 ~80 条规则重复读取注册表、.npmrc、settings.xml、libraryfolders.vdf、环境变量与已迁移符号链接现场校验。
/// </summary>
internal sealed class CleanupRootResolutionCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _verifiedMigrationLinks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetOrAdd(string key, Func<IReadOnlyList<string>> factory) =>
        _entries.GetOrAdd(key, _ => factory());

    public bool GetOrAddMigrationLinkVerification(string sourcePath, string targetPath, Func<bool> verifier) =>
        _verifiedMigrationLinks.GetOrAdd($"{sourcePath}|{targetPath}", _ => verifier());
}

/// <summary>
/// 受限清理根目录解析与有界安全展开器（策略注册表 + 单次扫描缓存 + 已登记符号链接安全重映射）。
/// </summary>
internal sealed partial class CleanupRootPathResolver
{
    private const int MaxWildcardMatchesPerSegment = 64;
    private const int MaxExpandedDirsPerSpec = 128;

    private readonly SafetyPolicyEnvironment? _policyEnvironment;
    private readonly Lazy<string[]> _normalizedForbiddenCrossRoots;
    private readonly Lazy<string[]> _normalizedPersonalFolders;
    private readonly Dictionary<CleanupKnownRootKind, ICleanupRootPathStrategy> _strategies;

    public CleanupRootPathResolver(SafetyPolicyEnvironment? policyEnvironment = null)
    {
        _policyEnvironment = policyEnvironment;
        _normalizedForbiddenCrossRoots = new Lazy<string[]>(BuildNormalizedForbiddenCrossRoots);
        _normalizedPersonalFolders = new Lazy<string[]>(BuildNormalizedPersonalFolders);
        _strategies = new Dictionary<CleanupKnownRootKind, ICleanupRootPathStrategy>
        {
            [CleanupKnownRootKind.CustomTestRoot] = new DelegateRootStrategy(
                skipLeaf: false,
                (spec, _, _) => string.IsNullOrWhiteSpace(spec.CustomRootOverride) ? [] : [spec.CustomRootOverride]),
            [CleanupKnownRootKind.EnvironmentVariable] = new DelegateRootStrategy(
                skipLeaf: true,
                (spec, tx, cache) => string.IsNullOrWhiteSpace(spec.CustomRootOverride)
                    ? []
                    : cache.GetOrAdd($"env:{spec.CustomRootOverride}", () => ResolveEnvironmentVariableRoots(spec.CustomRootOverride, tx))),
            [CleanupKnownRootKind.SteamLibraries] = new DelegateRootStrategy(
                skipLeaf: false,
                (_, tx, cache) => cache.GetOrAdd("steam", () => ResolveSteamLibraryRoots(tx))),
            [CleanupKnownRootKind.JetBrainsCustomSystem] = new DelegateRootStrategy(
                skipLeaf: false,
                (spec, tx, cache) => string.IsNullOrWhiteSpace(spec.CustomRootOverride)
                    ? []
                    : cache.GetOrAdd($"jb:{spec.CustomRootOverride}", () => ResolveJetBrainsCustomSystemRoots(spec.CustomRootOverride, tx))),
            [CleanupKnownRootKind.NpmConfigCache] = new DelegateRootStrategy(
                skipLeaf: true,
                (_, tx, cache) => cache.GetOrAdd("npmrc", () => ResolveNpmrcCacheRoots(tx))),
            [CleanupKnownRootKind.MavenCustomRepository] = new DelegateRootStrategy(
                skipLeaf: false,
                (_, tx, cache) => cache.GetOrAdd("maven", () => ResolveMavenCustomRepoRoots(tx))),
            [CleanupKnownRootKind.UserTemp] = StandardFolderStrategy(static () => Path.GetTempPath()),
            [CleanupKnownRootKind.WindowsTemp] = StandardFolderStrategy(static () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")),
            [CleanupKnownRootKind.LocalAppData] = StandardFolderStrategy(static () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            [CleanupKnownRootKind.RoamingAppData] = StandardFolderStrategy(static () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            [CleanupKnownRootKind.ProgramData] = StandardFolderStrategy(static () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            [CleanupKnownRootKind.WindowsDirectory] = StandardFolderStrategy(static () => Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            [CleanupKnownRootKind.UserProfile] = StandardFolderStrategy(static () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            [CleanupKnownRootKind.SystemDriveRoot] = StandardFolderStrategy(static () => Path.GetPathRoot(Environment.SystemDirectory))
        };
    }

    /// <summary>
    /// 纯内存前缀剪枝：判断常规规则 TargetSpec 是否有可能命中任何已完成迁移的源目录（避免对无关规则触发磁盘通配符枚举）。
    /// </summary>
    public bool CanSpecIntersectCompletedMigrations(
        CleanupPathRuleSpec spec,
        IReadOnlyList<(string SourcePath, string TargetPath)> completedMigrations,
        CleanupRootResolutionCache? cache = null)
    {
        if (completedMigrations.Count == 0) return false;
        if (!_strategies.TryGetValue(spec.RootKind, out ICleanupRootPathStrategy? strategy))
        {
            return false;
        }

        // 仅对标准目录/测试根执行纯内存前缀裁剪；显式跨盘策略由调用方直接解析
        IReadOnlyList<string> rawRoots = strategy.ResolveRoots(spec, [], cache ?? new CleanupRootResolutionCache());
        if (rawRoots.Count == 0) return false;

        string rel = spec.RelativeSubPath ?? string.Empty;
        string[] segments = rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var staticSegs = new List<string>(segments.Length);
        bool hasWildcard = false;
        foreach (string seg in segments)
        {
            if (seg.Contains('*') || seg.Contains('?'))
            {
                hasWildcard = true;
                break;
            }
            if (seg == "." || seg == ".." || seg.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }
            staticSegs.Add(seg);
        }

        foreach (string rawRoot in rawRoots)
        {
            string prefixPath;
            try
            {
                string normRoot = PathIdentityService.Normalize(rawRoot.TrimEnd('\\'));
                prefixPath = staticSegs.Count == 0
                    ? normRoot
                    : PathIdentityService.Normalize(Path.Combine([normRoot, .. staticSegs]));
            }
            catch
            {
                continue;
            }

            foreach (var mig in completedMigrations)
            {
                if (PathIdentityService.IsWithin(prefixPath, mig.SourcePath))
                {
                    return true;
                }
                if (hasWildcard && PathIdentityService.IsWithin(mig.SourcePath, prefixPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public IReadOnlyList<string> ResolveRuleTargetDirectories(
        CleanupPathRuleSpec spec,
        IReadOnlyList<string> protectedTxPaths,
        IReadOnlyList<(string SourcePath, string TargetPath)> completedMigrations,
        CleanupRootResolutionCache? cache = null)
    {
        if (!_strategies.TryGetValue(spec.RootKind, out ICleanupRootPathStrategy? strategy))
        {
            return [];
        }

        CleanupRootResolutionCache activeCache = cache ?? new CleanupRootResolutionCache();
        IReadOnlyList<string> rawRoots = strategy.ResolveRoots(spec, protectedTxPaths, activeCache);
        if (rawRoots.Count == 0) return [];

        var resolved = new List<string>();
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in rawRoots)
        {
            string effectiveSubPath = spec.RelativeSubPath;
            if (strategy.SkipSubPathWhenAlreadyLeaf
                && !string.IsNullOrWhiteSpace(effectiveSubPath)
                && !effectiveSubPath.Contains('*')
                && !effectiveSubPath.Contains('?')
                && root.TrimEnd('\\', '/').EndsWith(effectiveSubPath.Trim('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                effectiveSubPath = string.Empty;
            }

            foreach (string expanded in ExpandSafeRelativeDirectoriesWithMigrationRemap(root, effectiveSubPath, completedMigrations, protectedTxPaths, activeCache))
            {
                if (dedup.Add(expanded))
                {
                    resolved.Add(expanded);
                }
            }
        }

        return resolved;
    }

    private IReadOnlyList<string> ExpandSafeRelativeDirectoriesWithMigrationRemap(
        string root,
        string relativeSubPath,
        IReadOnlyList<(string SourcePath, string TargetPath)> completedMigrations,
        IReadOnlyList<string> protectedTxPaths,
        CleanupRootResolutionCache cache)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 先按常规安全规则展开（无通配或目录存在）
        foreach (string dir in ExpandSafeRelativeDirectories(root, relativeSubPath))
        {
            string finalDir = dir;
            if (completedMigrations.Count > 0 && TryRemapRegisteredMigrationPath(dir, completedMigrations, protectedTxPaths, cache, out string remapped))
            {
                finalDir = remapped;
            }
            if (seen.Add(finalDir))
            {
                results.Add(finalDir);
            }
        }

        // 2. 若含通配符且中间父目录已被本工具迁移为符号链接，则先定位到目标盘真实目录再展开剩余相对段
        if (completedMigrations.Count > 0 && (relativeSubPath.Contains('*') || relativeSubPath.Contains('?')))
        {
            string normRoot;
            try
            {
                normRoot = PathIdentityService.Normalize(root.TrimEnd('\\'));
            }
            catch
            {
                return results;
            }

            string[] segs = relativeSubPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            for (int split = 0; split < segs.Length; split++)
            {
                if (segs[split].Contains('*') || segs[split].Contains('?')) break;
                string prefixPath = PathIdentityService.Normalize(Path.Combine([normRoot, .. segs[..(split + 1)]]));
                if (TryRemapRegisteredMigrationPath(prefixPath, completedMigrations, protectedTxPaths, cache, out string remappedPrefix))
                {
                    string remaining = split + 1 < segs.Length ? string.Join('\\', segs[(split + 1)..]) : string.Empty;
                    foreach (string dir in ExpandSafeRelativeDirectories(remappedPrefix, remaining))
                    {
                        if (seen.Add(dir))
                        {
                            results.Add(dir);
                        }
                    }
                }
            }
        }

        return results;
    }

    private bool TryRemapRegisteredMigrationPath(
        string candidatePath,
        IReadOnlyList<(string SourcePath, string TargetPath)> completedMigrations,
        IReadOnlyList<string> protectedTxPaths,
        CleanupRootResolutionCache cache,
        out string remappedPath)
    {
        remappedPath = string.Empty;
        if (completedMigrations.Count == 0 || string.IsNullOrWhiteSpace(candidatePath)) return false;

        string normalized;
        try
        {
            normalized = PathIdentityService.Normalize(candidatePath);
        }
        catch
        {
            return false;
        }

        (string SourcePath, string TargetPath)? bestMatch = null;
        foreach (var mig in completedMigrations)
        {
            if (PathIdentityService.IsWithin(normalized, mig.SourcePath))
            {
                if (bestMatch is null || mig.SourcePath.Length > bestMatch.Value.SourcePath.Length)
                {
                    bestMatch = mig;
                }
            }
        }

        if (bestMatch is null) return false;
        var (src, dst) = bestMatch.Value;

        bool linkVerified = cache.GetOrAddMigrationLinkVerification(src, dst, () =>
        {
            try
            {
                string extSrc = QuarantineStore.ToExtendedPath(src);
                if (!Directory.Exists(extSrc)) return false;
                if ((File.GetAttributes(extSrc) & FileAttributes.ReparsePoint) == 0) return false;

                FileSystemInfo? resolvedLink = Directory.ResolveLinkTarget(src, returnFinalTarget: true);
                if (resolvedLink is null) return false;
                string liveTarget = PathIdentityService.Normalize(resolvedLink.FullName);
                return liveTarget.Equals(dst, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
        if (!linkVerified) return false;

        string relative = Path.GetRelativePath(src, normalized);
        string mapped = relative == "."
            ? dst
            : PathIdentityService.Normalize(Path.Combine(dst, relative));

        if (!PathIdentityService.IsWithin(mapped, dst)) return false;

        CleanupExclusionVerdict verdict = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(mapped, protectedTxPaths, _policyEnvironment);
        if (verdict.IsExcluded) return false;

        remappedPath = mapped;
        return true;
    }

    internal static IReadOnlyList<string> ExpandSafeRelativeDirectories(string root, string relativeSubPath)
    {
        string normRoot;
        try
        {
            normRoot = PathIdentityService.Normalize(root.TrimEnd('\\'));
        }
        catch
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(relativeSubPath))
        {
            return [normRoot];
        }

        if (Path.IsPathRooted(relativeSubPath))
        {
            return [];
        }

        string[] segments = relativeSubPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == "." || seg == ".." || seg.Contains("..", StringComparison.Ordinal))
            {
                return [];
            }
        }

        if (!relativeSubPath.Contains('*') && !relativeSubPath.Contains('?'))
        {
            string combined = PathIdentityService.Normalize(Path.Combine(normRoot, relativeSubPath));
            return PathIdentityService.IsWithin(combined, normRoot) ? [combined] : [];
        }

        var currentDirs = new List<string> { normRoot };
        foreach (string seg in segments)
        {
            var nextDirs = new List<string>();
            bool hasWildcard = seg.Contains('*') || seg.Contains('?');

            foreach (string baseDir in currentDirs)
            {
                if (nextDirs.Count >= MaxExpandedDirsPerSpec) break;
                string extendedBase = QuarantineStore.ToExtendedPath(baseDir);
                if (!Directory.Exists(extendedBase)) continue;

                try
                {
                    if ((File.GetAttributes(extendedBase) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (!hasWildcard)
                {
                    string candidate = PathIdentityService.Normalize(Path.Combine(baseDir, seg));
                    if (PathIdentityService.IsWithin(candidate, normRoot))
                    {
                        string extCand = QuarantineStore.ToExtendedPath(candidate);
                        if (Directory.Exists(extCand))
                        {
                            try
                            {
                                if ((File.GetAttributes(extCand) & FileAttributes.ReparsePoint) == 0)
                                {
                                    nextDirs.Add(candidate);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                else
                {
                    int segMatches = 0;
                    try
                    {
                        foreach (string sub in Directory.EnumerateDirectories(extendedBase))
                        {
                            if (segMatches >= MaxWildcardMatchesPerSegment || nextDirs.Count >= MaxExpandedDirsPerSpec) break;
                            string cleanSub = sub.StartsWith(@"\\?\", StringComparison.Ordinal) ? sub[4..] : sub;
                            string name = Path.GetFileName(cleanSub);
                            if (!FileSystemName.MatchesSimpleExpression(seg, name, ignoreCase: true)) continue;

                            try
                            {
                                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch
                            {
                                continue;
                            }

                            string normalizedSub = PathIdentityService.Normalize(cleanSub);
                            if (PathIdentityService.IsWithin(normalizedSub, normRoot))
                            {
                                nextDirs.Add(normalizedSub);
                                segMatches++;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            currentDirs = nextDirs;
            if (currentDirs.Count == 0) break;
        }

        return currentDirs;
    }

    private IReadOnlyList<string> ResolveEnvironmentVariableRoots(string envVarName, IReadOnlyList<string> protectedTxPaths)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EnvironmentVariableTarget[] targets =
        [
            EnvironmentVariableTarget.Process,
            EnvironmentVariableTarget.User,
            EnvironmentVariableTarget.Machine
        ];

        foreach (EnvironmentVariableTarget target in targets)
        {
            string? raw;
            try
            {
                raw = Environment.GetEnvironmentVariable(envVarName, target);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw)) continue;

            foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string expanded = Environment.ExpandEnvironmentVariables(part).Trim('"');
                if (TryValidateSafeCrossDriveRoot(expanded, protectedTxPaths, out string safePath) && seen.Add(safePath))
                {
                    results.Add(safePath);
                }
            }
        }

        return results;
    }

    private IReadOnlyList<string> ResolveSteamLibraryRoots(IReadOnlyList<string> protectedTxPaths)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidateSteamRoots = new List<string>();
        try
        {
            using RegistryKey? hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (hkcu?.GetValue("SteamPath") is string p1 && !string.IsNullOrWhiteSpace(p1))
                candidateSteamRoots.Add(p1.Replace('/', '\\'));
        }
        catch { }

        try
        {
            using RegistryKey? hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (hklm?.GetValue("InstallPath") is string p2 && !string.IsNullOrWhiteSpace(p2))
                candidateSteamRoots.Add(p2.Replace('/', '\\'));
        }
        catch { }

        foreach (string steamRoot in candidateSteamRoots)
        {
            if (TryValidateSafeCrossDriveRoot(steamRoot, protectedTxPaths, out string safeSteam) && seen.Add(safeSteam))
            {
                results.Add(safeSteam);

                string vdfPath = Path.Combine(safeSteam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    try
                    {
                        string content = File.ReadAllText(vdfPath);
                        foreach (Match m in SteamLibraryPathRegex().Matches(content))
                        {
                            string rawLib = m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\');
                            if (TryValidateSafeCrossDriveRoot(rawLib, protectedTxPaths, out string safeLib) && seen.Add(safeLib))
                            {
                                results.Add(safeLib);
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        return results;
    }

    private IReadOnlyList<string> ResolveJetBrainsCustomSystemRoots(string folderPattern, IReadOnlyList<string> protectedTxPaths)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roaming)) return results;

        string jbRoaming = Path.Combine(roaming, "JetBrains");
        if (!Directory.Exists(jbRoaming)) return results;

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(jbRoaming))
            {
                string name = Path.GetFileName(dir);
                if (!FileSystemName.MatchesSimpleExpression(folderPattern, name, ignoreCase: true)) continue;

                string propFile = Path.Combine(dir, "idea.properties");
                if (!File.Exists(propFile)) continue;

                foreach (string line in File.ReadLines(propFile))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith('#') || !trimmed.StartsWith("idea.system.path", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int eq = trimmed.IndexOf('=');
                    if (eq < 0) continue;
                    string rawVal = trimmed[(eq + 1)..].Trim().Trim('"')
                        .Replace("${user.home}", userHome, StringComparison.OrdinalIgnoreCase)
                        .Replace('/', '\\');

                    if (TryValidateSafeCrossDriveRoot(rawVal, protectedTxPaths, out string safePath) && seen.Add(safePath))
                    {
                        results.Add(safePath);
                    }
                }
            }
        }
        catch { }

        return results;
    }

    private IReadOnlyList<string> ResolveNpmrcCacheRoots(IReadOnlyList<string> protectedTxPaths)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(userProfile, ".npmrc"),
            Path.Combine(roaming, "npm", "etc", "npmrc")
        ];

        foreach (string rcPath in candidates)
        {
            if (string.IsNullOrWhiteSpace(rcPath) || !File.Exists(rcPath)) continue;
            try
            {
                foreach (string line in File.ReadLines(rcPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith('#') || trimmed.StartsWith(';')) continue;
                    if (!trimmed.StartsWith("cache", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = trimmed.IndexOf('=');
                    if (eq < 0) continue;
                    string key = trimmed[..eq].Trim();
                    if (!key.Equals("cache", StringComparison.OrdinalIgnoreCase)) continue;

                    string val = Environment.ExpandEnvironmentVariables(trimmed[(eq + 1)..].Trim().Trim('"', '\''));
                    if (TryValidateSafeCrossDriveRoot(val, protectedTxPaths, out string safePath) && seen.Add(safePath))
                    {
                        results.Add(safePath);
                    }
                }
            }
            catch { }
        }

        return results;
    }

    private IReadOnlyList<string> ResolveMavenCustomRepoRoots(IReadOnlyList<string> protectedTxPaths)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string settingsPath = Path.Combine(userProfile, ".m2", "settings.xml");
        if (!File.Exists(settingsPath)) return results;

        try
        {
            string xml = File.ReadAllText(settingsPath);
            string stripped = XmlCommentRegex().Replace(xml, string.Empty);
            Match m = MavenLocalRepoRegex().Match(stripped);
            if (m.Success)
            {
                string val = Environment.ExpandEnvironmentVariables(m.Groups[1].Value.Trim())
                    .Replace("${user.home}", userProfile, StringComparison.OrdinalIgnoreCase);
                if (TryValidateSafeCrossDriveRoot(val, protectedTxPaths, out string safePath) && seen.Add(safePath))
                {
                    results.Add(safePath);
                }
            }
        }
        catch { }

        return results;
    }

    private bool TryValidateSafeCrossDriveRoot(string rawPath, IReadOnlyList<string> protectedTxPaths, out string safePath)
    {
        safePath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathRooted(rawPath)) return false;

        string normalized;
        try
        {
            normalized = PathIdentityService.Normalize(rawPath.TrimEnd('\\', '/'));
        }
        catch
        {
            return false;
        }

        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(root) || normalized.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string ext = QuarantineStore.ToExtendedPath(normalized);
        if (!Directory.Exists(ext)) return false;

        try
        {
            if ((File.GetAttributes(ext) & FileAttributes.ReparsePoint) != 0) return false;
        }
        catch
        {
            return false;
        }

        CleanupExclusionVerdict ruleVerdict = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(normalized, protectedTxPaths, _policyEnvironment);
        if (ruleVerdict.IsExcluded) return false;

        foreach (string normForbidden in _normalizedForbiddenCrossRoots.Value)
        {
            if (normalized.Equals(normForbidden, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (string normPf in _normalizedPersonalFolders.Value)
        {
            if (PathIdentityService.IsWithin(normalized, normPf)) return false;
        }

        safePath = normalized;
        return true;
    }

    private string[] BuildNormalizedForbiddenCrossRoots()
    {
        SafetyPolicyEnvironment env = _policyEnvironment ?? SafetyPolicyEnvironment.CreateDefault();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string forbidden in env.WindowsDirectories
            .Concat(env.ProgramFilesDirectories)
            .Concat(env.UsersRoots)
            .Concat(env.UserProfileRoots))
        {
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            try { set.Add(PathIdentityService.Normalize(forbidden)); } catch { }
        }
        return set.ToArray();
    }

    private static string[] BuildNormalizedPersonalFolders()
    {
        string[] raw =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        ];
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pf in raw)
        {
            if (string.IsNullOrWhiteSpace(pf)) continue;
            try { set.Add(PathIdentityService.Normalize(pf)); } catch { }
        }
        return set.ToArray();
    }

    private static ICleanupRootPathStrategy StandardFolderStrategy(Func<string?> getter) =>
        new DelegateRootStrategy(
            skipLeaf: false,
            (_, _, _) =>
            {
                string? path = getter();
                return string.IsNullOrWhiteSpace(path) ? [] : [path];
            });

    private sealed class DelegateRootStrategy(
        bool skipLeaf,
        Func<CleanupPathRuleSpec, IReadOnlyList<string>, CleanupRootResolutionCache, IReadOnlyList<string>> resolver) : ICleanupRootPathStrategy
    {
        public bool SkipSubPathWhenAlreadyLeaf => skipLeaf;

        public IReadOnlyList<string> ResolveRoots(
            CleanupPathRuleSpec spec,
            IReadOnlyList<string> protectedTxPaths,
            CleanupRootResolutionCache cache) =>
            resolver(spec, protectedTxPaths, cache);
    }

    [GeneratedRegex(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPathRegex();

    [GeneratedRegex(@"<localRepository>\s*([^<]+?)\s*</localRepository>", RegexOptions.IgnoreCase)]
    private static partial Regex MavenLocalRepoRegex();

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex XmlCommentRegex();
}
