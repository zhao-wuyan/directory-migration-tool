using Microsoft.Win32;
using MigrationCore.Models.Safety;

namespace MigrationCore.Services.Safety;

/// <summary>
/// 多来源应用目录发现与规则匹配服务（实现 ISafetyApplicationMatcher）。
/// 综合 HKLM/HKCU 32+64 位 Uninstall 键、Known Folders 与受审查内置规则发现软件归属；
/// 目录发现仅提供证据与归属说明，绝不绕过 L0 硬拒绝，也绝不因存在 exe/卸载项而自动授予 L1。
/// </summary>
public sealed class ApplicationCatalog : ISafetyApplicationMatcher
{
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IReadOnlyList<ApplicationRuleDefinition> _rules;
    private readonly IReadOnlyList<InstalledSoftwareEvidence> _installedSoftware;
    private readonly IReadOnlyList<(string Label, string Path)> _knownPersonalFolders;
    private readonly Dictionary<string, List<InstalledSoftwareEvidence>> _matchedRootsByRuleId;

    private static readonly Lazy<ApplicationCatalog> SharedInstance = new(() =>
    {
        var catalog = new ApplicationCatalog();
        MigrationSafetyPolicy.RegisterDefaultApplicationMatcher(catalog);
        return catalog;
    });

    public static ApplicationCatalog Shared => SharedInstance.Value;

    public string RuleCatalogVersion => BuiltInApplicationRules.RuleCatalogVersion;
    public IReadOnlyList<ApplicationRuleDefinition> Rules => _rules;
    public IReadOnlyList<InstalledSoftwareEvidence> InstalledSoftware => _installedSoftware;

    /// <summary>
    /// 生产构造：自动采集本机 HKLM/HKCU 32+64 位 Uninstall 与 Known Folders。
    /// </summary>
    public ApplicationCatalog()
        : this(BuiltInApplicationRules.All, DiscoverInstalledSoftwareFromRegistry(), DiscoverKnownPersonalFolders())
    {
    }

    /// <summary>
    /// 可测试构造：允许注入模拟注册表安装证据与 Known Folders，实现 100% 离线确定性测试。
    /// </summary>
    public ApplicationCatalog(
        IReadOnlyList<ApplicationRuleDefinition> rules,
        IReadOnlyList<InstalledSoftwareEvidence> installedSoftware,
        IReadOnlyList<(string Label, string Path)>? knownPersonalFolders = null)
    {
        _rules = rules;
        _installedSoftware = installedSoftware;
        _knownPersonalFolders = knownPersonalFolders ?? [];
        _matchedRootsByRuleId = BuildRuleRootIndex(rules, installedSoftware);
    }

    public static void RegisterWithSafetyPolicy()
    {
        MigrationSafetyPolicy.RegisterDefaultApplicationMatcher(Shared);
    }

    /// <summary>
    /// 获取按规则匹配与多来源发现的软件安装根列表（供扫描页大软件视图与诊断详情展示）。
    /// </summary>
    public IReadOnlyList<DiscoveredApplicationRoot> GetDiscoveredApplications()
    {
        var result = new List<DiscoveredApplicationRoot>();

        foreach (ApplicationRuleDefinition rule in _rules)
        {
            if (!_matchedRootsByRuleId.TryGetValue(rule.ApplicationId, out var evidences))
            {
                continue;
            }

            foreach (var group in evidences.GroupBy(e => e.InstallRoot, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                var candidatePaths = rule.RelativeAllowedDataPaths
                    .Select(rel => Path.Combine(first.InstallRoot, rel))
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                result.Add(new DiscoveredApplicationRoot(
                    rule.ApplicationId,
                    rule.DisplayName,
                    first.InstallRoot,
                    first.DisplayVersion,
                    first.Publisher,
                    group.Select(g => g.Source).Distinct().ToArray(),
                    IsRuleVerified: rule.Level == MigrationAdmissionLevel.VerifiedEligible,
                    RecommendedLevel: rule.Level,
                    CandidateDataPaths: candidatePaths,
                    NativeGuidance: rule.NativeGuidance));
            }
        }

        return result;
    }

    /// <summary>
    /// 实现 ISafetyApplicationMatcher：针对任意已规范化路径执行规则匹配。
    /// 优先级：L2 原生引导规则优先 -> L1 精确业务数据子目录匹配 -> 禁止子目录/安装根不授予 L1（交由上层按 L3 人工确认处理）。
    /// </summary>
    public SafetyApplicationMatch? Match(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        string path;
        try
        {
            path = PathIdentityService.Normalize(normalizedPath);
        }
        catch
        {
            return null;
        }

        // 1. 先检查 L2 厂商原生引导规则（例如 Docker Desktop / WSL2、OneDrive），确保结构性高风险目录不被误降级
        foreach (ApplicationRuleDefinition rule in _rules.Where(r => r.Level == MigrationAdmissionLevel.VendorNativeRecommended))
        {
            if (MatchesL2Rule(path, rule, out string[] evidence))
            {
                return new SafetyApplicationMatch(
                    rule.ApplicationId,
                    rule.DisplayName,
                    MigrationAdmissionLevel.VendorNativeRecommended,
                    rule.NativeGuidance,
                    evidence,
                    [$"{rule.DisplayName} 存在结构性风险，推荐使用官方原生迁移方案。"],
                    rule.RequiredStoppedProcessNames);
            }
        }

        // 2. 再检查 L1 已验证应用业务数据子目录（优先基于注册表安装根精确相对路径，其次受限路径模式匹配）
        foreach (ApplicationRuleDefinition rule in _rules.Where(r => r.Level == MigrationAdmissionLevel.VerifiedEligible))
        {
            if (MatchesL1AllowedDataSubdir(path, rule, out string[] evidence))
            {
                return new SafetyApplicationMatch(
                    rule.ApplicationId,
                    rule.DisplayName,
                    MigrationAdmissionLevel.VerifiedEligible,
                    NativeGuidance: null,
                    Evidence: evidence,
                    Warnings: [rule.VerificationNotes],
                    RequiredStoppedProcesses: rule.RequiredStoppedProcessNames);
            }
        }

        // 3. 命中规则软件但落在程序本体或禁止子目录时，不颁发 L1，返回 null 交由上层按 L3 人工风险确认处理
        return null;
    }

    /// <summary>
    /// 查找路径归属的只读标签（用于扫描树与热点聚合展示，涵盖已收录软件、已安装未收录软件和 Known Folders）。
    /// </summary>
    public string? IdentifyAttributionLabel(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath)) return null;
        string path;
        try { path = PathIdentityService.Normalize(normalizedPath); }
        catch { return null; }

        SafetyApplicationMatch? ruleMatch = Match(path);
        if (ruleMatch is not null)
        {
            return ruleMatch.DisplayName;
        }

        // 检查是否落在已发现的已安装软件目录下（仅展示归属，不授予 L1）
        InstalledSoftwareEvidence? longestSoftware = null;
        foreach (InstalledSoftwareEvidence sw in _installedSoftware)
        {
            if (PathIdentityService.IsWithin(path, sw.InstallRoot)
                && (longestSoftware is null || sw.InstallRoot.Length > longestSoftware.InstallRoot.Length))
            {
                longestSoftware = sw;
            }
        }
        if (longestSoftware is not null)
        {
            return longestSoftware.DisplayName;
        }

        // 检查 Known Folders（个人数据目录归属）
        foreach (var (label, folderPath) in _knownPersonalFolders)
        {
            if (PathIdentityService.IsWithin(path, folderPath))
            {
                return $"个人目录 · {label}";
            }
        }

        return null;
    }

    private bool MatchesL2Rule(string path, ApplicationRuleDefinition rule, out string[] evidence)
    {
        var ev = new List<string>();

        if (_matchedRootsByRuleId.TryGetValue(rule.ApplicationId, out var roots))
        {
            foreach (InstalledSoftwareEvidence sw in roots)
            {
                if (PathIdentityService.IsWithin(path, sw.InstallRoot))
                {
                    ev.Add($"注册表证据({sw.Source}): {sw.DisplayName} @ {sw.InstallRoot}");
                    evidence = ev.ToArray();
                    return true;
                }
            }
        }

        foreach (string marker in rule.PathContainsMarkers)
        {
            if (ContainsPathComponentSequence(path, marker))
            {
                ev.Add($"内置规则 {rule.ApplicationId}@{rule.RuleVersion} 命中特征路径段：{marker}");
                evidence = ev.ToArray();
                return true;
            }
        }

        evidence = [];
        return false;
    }

    private bool MatchesL1AllowedDataSubdir(string path, ApplicationRuleDefinition rule, out string[] evidence)
    {
        var ev = new List<string>();

        // A. 基于已发现安装根进行精确相对子目录匹配
        if (_matchedRootsByRuleId.TryGetValue(rule.ApplicationId, out var roots))
        {
            foreach (InstalledSoftwareEvidence sw in roots)
            {
                if (!PathIdentityService.IsWithin(path, sw.InstallRoot)) continue;

                // 若恰好是安装根本身，明确不授 L1（程序本体保留原位）
                if (path.Equals(sw.InstallRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(sw.InstallRoot, path);
                if (IsRelativeForbidden(relative, rule.RelativeForbiddenSubpaths))
                {
                    continue;
                }

                foreach (string allowedRel in rule.RelativeAllowedDataPaths)
                {
                    if (IsRelativeEqualOrWithin(relative, allowedRel))
                    {
                        ev.Add($"注册表证据({sw.Source}): {sw.DisplayName}");
                        ev.Add($"命中规则 {rule.ApplicationId}@{rule.RuleVersion} 允许业务数据子目录：{allowedRel}");
                        evidence = ev.ToArray();
                        return true;
                    }
                }
            }
        }

        // B. 便携版/自定义安装目录：要求同时命中应用特征路径段 + 明确允许的数据子目录段，且不命中禁止子目录段
        foreach (string marker in rule.PathContainsMarkers)
        {
            if (!TryGetRelativeAfterMarker(path, marker, out string relativeAfterMarker))
            {
                continue;
            }

            // marker 自身（即安装根）不给 L1，仅当存在允许的业务数据子目录时放行
            if (string.IsNullOrEmpty(relativeAfterMarker))
            {
                continue;
            }

            if (IsRelativeForbidden(relativeAfterMarker, rule.RelativeForbiddenSubpaths))
            {
                continue;
            }

            foreach (string allowedRel in rule.RelativeAllowedDataPaths)
            {
                if (IsRelativeEqualOrWithin(relativeAfterMarker, allowedRel))
                {
                    ev.Add($"便携/自定义安装路径特征命中：{marker}");
                    ev.Add($"命中规则 {rule.ApplicationId}@{rule.RuleVersion} 允许业务数据子目录：{allowedRel}");
                    evidence = ev.ToArray();
                    return true;
                }
            }
        }

        evidence = [];
        return false;
    }

    private static bool IsRelativeForbidden(string relativePath, string[] forbiddenSubpaths)
    {
        foreach (string forbidden in forbiddenSubpaths)
        {
            if (IsRelativeEqualOrWithin(relativePath, forbidden))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsRelativeEqualOrWithin(string relativePath, string targetRel)
    {
        string r = relativePath.Trim('\\');
        string t = targetRel.Trim('\\');
        return r.Equals(t, StringComparison.OrdinalIgnoreCase)
            || r.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPathComponentSequence(string fullPath, string sequence)
    {
        string[] pathSegs = fullPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string[] seqSegs = sequence.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (seqSegs.Length == 0 || pathSegs.Length < seqSegs.Length) return false;

        for (int i = 0; i <= pathSegs.Length - seqSegs.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < seqSegs.Length; j++)
            {
                if (!pathSegs[i + j].Equals(seqSegs[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static bool TryGetRelativeAfterMarker(string fullPath, string marker, out string relativeAfter)
    {
        string[] pathSegs = fullPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string[] markerSegs = marker.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (markerSegs.Length == 0 || pathSegs.Length < markerSegs.Length)
        {
            relativeAfter = string.Empty;
            return false;
        }

        for (int i = 0; i <= pathSegs.Length - markerSegs.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < markerSegs.Length; j++)
            {
                if (!pathSegs[i + j].Equals(markerSegs[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                int afterStart = i + markerSegs.Length;
                relativeAfter = afterStart >= pathSegs.Length
                    ? string.Empty
                    : string.Join('\\', pathSegs[afterStart..]);
                return true;
            }
        }

        relativeAfter = string.Empty;
        return false;
    }

    private static Dictionary<string, List<InstalledSoftwareEvidence>> BuildRuleRootIndex(
        IReadOnlyList<ApplicationRuleDefinition> rules,
        IReadOnlyList<InstalledSoftwareEvidence> softwareList)
    {
        var index = new Dictionary<string, List<InstalledSoftwareEvidence>>(StringComparer.OrdinalIgnoreCase);

        foreach (ApplicationRuleDefinition rule in rules)
        {
            foreach (InstalledSoftwareEvidence sw in softwareList)
            {
                bool nameMatched = rule.InstallDetectionNames.Any(n =>
                    sw.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (!nameMatched) continue;

                // 若规则声明了 PublisherHints 且注册表有 Publisher 字段，则交叉核对发布者降低伪造卸载项误认风险
                if (rule.PublisherHints.Length > 0 && !string.IsNullOrWhiteSpace(sw.Publisher))
                {
                    bool pubMatched = rule.PublisherHints.Any(p =>
                        sw.Publisher.Contains(p, StringComparison.OrdinalIgnoreCase));
                    if (!pubMatched) continue;
                }

                if (!index.TryGetValue(rule.ApplicationId, out var list))
                {
                    list = [];
                    index[rule.ApplicationId] = list;
                }
                list.Add(sw);
            }
        }

        return index;
    }

    private static IReadOnlyList<InstalledSoftwareEvidence> DiscoverInstalledSoftwareFromRegistry()
    {
        var results = new List<InstalledSoftwareEvidence>();
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry64, ApplicationEvidenceSource.RegistryUninstall64, results);
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry32, ApplicationEvidenceSource.RegistryUninstall32, results);
        ReadUninstallHive(RegistryHive.CurrentUser, RegistryView.Registry64, ApplicationEvidenceSource.RegistryCurrentUser, results);
        ReadUninstallHive(RegistryHive.CurrentUser, RegistryView.Registry32, ApplicationEvidenceSource.RegistryCurrentUser, results);
        return results;
    }

    private static void ReadUninstallHive(
        RegistryHive hive,
        RegistryView view,
        ApplicationEvidenceSource source,
        List<InstalledSoftwareEvidence> results)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallSubKey);
            if (uninstall is null) return;

            foreach (string subKeyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? appKey = uninstall.OpenSubKey(subKeyName);
                    if (appKey is null) continue;

                    string? displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    string? installLocation = CleanRegistryPath(appKey.GetValue("InstallLocation")?.ToString());
                    if (string.IsNullOrWhiteSpace(installLocation))
                    {
                        string? iconPath = CleanRegistryPath(appKey.GetValue("DisplayIcon")?.ToString());
                        if (!string.IsNullOrWhiteSpace(iconPath) && iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            installLocation = Path.GetDirectoryName(iconPath);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(installLocation)) continue;

                    string normalizedRoot;
                    try { normalizedRoot = PathIdentityService.Normalize(installLocation); }
                    catch { continue; }

                    // 排除系统容器根本身被某些劣质卸载项写为 InstallLocation（例如 C:\Program Files）
                    if (MigrationSafetyPolicy.IsPathExcludedFromScanMigration(normalizedRoot))
                    {
                        continue;
                    }

                    results.Add(new InstalledSoftwareEvidence(
                        displayName,
                        appKey.GetValue("DisplayVersion")?.ToString()?.Trim(),
                        appKey.GetValue("Publisher")?.ToString()?.Trim(),
                        normalizedRoot,
                        source));
                }
                catch
                {
                    // 单个坏键不影响其余软件发现
                }
            }
        }
        catch
        {
            // 注册表访问受限时保守跳过，不抛异常中断扫描
        }
    }

    private static IReadOnlyList<(string Label, string Path)> DiscoverKnownPersonalFolders()
    {
        var list = new List<(string, string)>();
        TryAdd("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        TryAdd("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        TryAdd("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        TryAdd("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        TryAdd("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            TryAdd("下载", Path.Combine(profile, "Downloads"));
        }
        return list;

        void TryAdd(string label, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            try
            {
                string norm = PathIdentityService.Normalize(raw);
                list.Add((label, norm));
            }
            catch { }
        }
    }

    private static string? CleanRegistryPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2)
        {
            s = s[1..^1];
        }
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _))
        {
            s = s[..comma];
        }
        return s.Trim().Trim('"');
    }
}
