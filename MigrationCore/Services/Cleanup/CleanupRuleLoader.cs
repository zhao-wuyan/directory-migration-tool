using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationCore.Models.Cleanup;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 轻量化声明式垃圾清理规则加载器（零外部依赖，从内嵌 JSON 资源加载并展开家族模板，启动期执行严格安全边界校验）。
/// </summary>
internal static class CleanupRuleLoader
{
    public const string EmbeddedResourceName = "MigrationCore.Rules.cleanup-rules.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static (string Version, IReadOnlyList<CleanupRuleDefinition> Rules) LoadEmbeddedRules()
    {
        Assembly asm = typeof(CleanupRuleLoader).Assembly;
        using Stream stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Missing embedded cleanup rules resource: {EmbeddedResourceName}");

        CleanupRuleCatalogDocument doc = JsonSerializer.Deserialize<CleanupRuleCatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize cleanup rules from {EmbeddedResourceName}");

        if (string.IsNullOrWhiteSpace(doc.Version))
            throw new InvalidOperationException("Cleanup rules manifest must declare a non-empty version.");

        Dictionary<string, string> groups = new(doc.Groups ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var result = new List<CleanupRuleDefinition>(doc.Rules?.Count ?? 0);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CleanupRuleEntryDto entry in doc.Rules ?? [])
        {
            CleanupRuleDefinition rule = MaterializeRule(entry, doc.Version, groups);
            ValidateRuleSafetyInvariants(rule, seenIds);
            result.Add(rule);
        }

        return (doc.Version, result);
    }

    private static CleanupRuleDefinition MaterializeRule(
        CleanupRuleEntryDto dto,
        string version,
        IReadOnlyDictionary<string, string> groups)
    {
        if (string.IsNullOrWhiteSpace(dto.RuleId))
            throw new InvalidOperationException("Cleanup rule entry is missing ruleId.");

        (string? groupId, string? groupTitle) = ResolveGroup(dto.GroupId, groups);

        if (!string.IsNullOrWhiteSpace(dto.Template))
        {
            return dto.Template.Trim().ToLowerInvariant() switch
            {
                "chromium-browser" => ExpandChromiumBrowser(dto, version, groupId, groupTitle),
                "vscode-family" => ExpandVsCodeFamily(dto, version, groupId, groupTitle),
                "jetbrains-ide" => ExpandJetBrainsIde(dto, version, groupId, groupTitle),
                "electron-app" => ExpandElectronApp(dto, version, groupId, groupTitle),
                _ => throw new InvalidOperationException($"Unknown cleanup rule template '{dto.Template}' for rule '{dto.RuleId}'.")
            };
        }

        CleanupPathRuleSpec[] specs = (dto.TargetSpecs ?? []).Select(ToPathSpec).ToArray();
        CleanupCautionReason? caution = dto.CautionReason is null
            ? null
            : new CleanupCautionReason(
                OneLineImpact: dto.CautionReason.OneLineImpact,
                HoverSummary: dto.CautionReason.HoverSummary,
                RiskCause: dto.CautionReason.RiskCause,
                PossibleImpact: dto.CautionReason.PossibleImpact,
                ProtectionStrategy: dto.CautionReason.ProtectionStrategy,
                RequiresPhraseConfirmation: dto.CautionReason.RequiresPhraseConfirmation ?? true,
                ConfirmationPhrase: string.IsNullOrWhiteSpace(dto.CautionReason.ConfirmationPhrase) ? "确认清理" : dto.CautionReason.ConfirmationPhrase);

        return new CleanupRuleDefinition(
            RuleId: dto.RuleId,
            Version: version,
            DisplayName: dto.DisplayName ?? throw new InvalidOperationException($"Rule '{dto.RuleId}' missing displayName."),
            ShortDescription: dto.ShortDescription ?? string.Empty,
            Category: dto.Category ?? throw new InvalidOperationException($"Rule '{dto.RuleId}' missing category."),
            Tier: dto.Tier ?? throw new InvalidOperationException($"Rule '{dto.RuleId}' missing tier."),
            Channel: dto.Channel ?? CleanupExecutionChannel.FileQuarantine,
            OfficialApiKind: dto.OfficialApiKind ?? CleanupOfficialApiKind.None,
            TargetSpecs: specs,
            RequiredStoppedProcesses: dto.RequiredStoppedProcesses ?? [],
            MinAgeHours: dto.MinAgeHours ?? 0,
            CostAndReclaimNote: dto.CostAndReclaimNote ?? string.Empty,
            ChannelBadgeText: dto.ChannelBadgeText ?? "同卷隔离可恢复",
            IsRebuildable: dto.IsRebuildable ?? true,
            ForceSameVolumeQuarantineOnly: dto.ForceSameVolumeQuarantineOnly ?? false,
            CautionReason: caution,
            IsBrowserCacheRule: dto.IsBrowserCacheRule ?? false,
            GroupId: groupId,
            GroupDisplayName: groupTitle);
    }

    private static (string? GroupId, string? GroupTitle) ResolveGroup(string? rawGroupId, IReadOnlyDictionary<string, string> groups)
    {
        if (string.IsNullOrWhiteSpace(rawGroupId))
            return (null, null);

        if (!groups.TryGetValue(rawGroupId, out string? title) || string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException($"Cleanup rule references unknown groupId '{rawGroupId}'.");

        return (rawGroupId, title);
    }

    private static CleanupPathRuleSpec ToPathSpec(CleanupPathRuleSpecDto dto) =>
        new(
            RootKind: dto.RootKind,
            RelativeSubPath: dto.RelativeSubPath ?? string.Empty,
            IncludeFilePatterns: dto.IncludeFilePatterns is { Length: > 0 } ? dto.IncludeFilePatterns : ["*"],
            Recursive: dto.Recursive ?? true,
            ExcludePathSegments: dto.ExcludePathSegments,
            ExcludeFileNames: dto.ExcludeFileNames,
            CustomRootOverride: dto.CustomRootOverride,
            MinAgeHoursOverride: dto.MinAgeHoursOverride);

    private static CleanupRuleDefinition ExpandChromiumBrowser(
        CleanupRuleEntryDto dto, string version, string? groupId, string? groupTitle)
    {
        string softwareName = RequireField(dto.SoftwareName, dto.RuleId, "softwareName");
        string userDataSubPath = RequireField(dto.UserDataSubPath, dto.RuleId, "userDataSubPath");
        CleanupKnownRootKind rootKind = dto.RootKind ?? CleanupKnownRootKind.LocalAppData;

        return new CleanupRuleDefinition(
            RuleId: dto.RuleId,
            Version: version,
            DisplayName: $"{softwareName} 网页与 GPU 缓存",
            ShortDescription: $"仅清 {softwareName}（含多配置文件）的 Cache / Code Cache / GPUCache / ShaderCache，永久排除 Cookies、登录凭据、书签与历史记录。",
            Category: CleanupCategory.SoftwareAndDeveloperCache,
            Tier: CleanupRecommendationTier.Recommended,
            Channel: CleanupExecutionChannel.FileQuarantine,
            OfficialApiKind: CleanupOfficialApiKind.None,
            TargetSpecs:
            [
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Default\Cache\Cache_Data", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Default\Code Cache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Default\GPUCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Default\DawnGraphiteCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Default\DawnWebGPUCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Profile *\Cache\Cache_Data", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Profile *\Code Cache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Profile *\GPUCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Profile *\DawnGraphiteCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\Profile *\DawnWebGPUCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\GrShaderCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\GraphiteDawnCache", ["*"], Recursive: true),
                new CleanupPathRuleSpec(rootKind, $@"{userDataSubPath}\ShaderCache", ["*"], Recursive: true)
            ],
            RequiredStoppedProcesses: dto.RequiredStoppedProcesses ?? [],
            MinAgeHours: 0,
            CostAndReclaimNote: $"清理前需先关闭 {softwareName}（工具不强杀浏览器）；绝不触碰密码、Cookie、登录态、书签与扩展数据。",
            ChannelBadgeText: "同卷隔离可恢复",
            IsRebuildable: true,
            IsBrowserCacheRule: true,
            GroupId: groupId,
            GroupDisplayName: groupTitle);
    }

    private static CleanupRuleDefinition ExpandVsCodeFamily(
        CleanupRuleEntryDto dto, string version, string? groupId, string? groupTitle)
    {
        string softwareName = RequireField(dto.SoftwareName, dto.RuleId, "softwareName");
        string[] folders = dto.RoamingFolders is { Length: > 0 }
            ? dto.RoamingFolders
            : throw new InvalidOperationException($"Rule '{dto.RuleId}' missing roamingFolders.");

        var specs = new List<CleanupPathRuleSpec>();
        foreach (string folder in folders)
        {
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\Cache", ["*"], Recursive: true));
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\CachedData", ["*"], Recursive: true));
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\CachedExtensionVSIXs", ["*"], Recursive: true));
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\Code Cache", ["*"], Recursive: true));
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\GPUCache", ["*"], Recursive: true));
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\DawnGraphiteCache", ["*"], Recursive: true));
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\DawnWebGPUCache", ["*"], Recursive: true));
            specs.Add(new CleanupPathRuleSpec(CleanupKnownRootKind.RoamingAppData, $@"{folder}\logs", ["*"], Recursive: true, MinAgeHoursOverride: 24));
        }

        if (dto.ExtraSpecs is { Length: > 0 })
        {
            specs.AddRange(dto.ExtraSpecs.Select(ToPathSpec));
        }

        return new CleanupRuleDefinition(
            RuleId: dto.RuleId,
            Version: version,
            DisplayName: $"{softwareName} 缓存与旧日志",
            ShortDescription: $"仅清 {softwareName} 的 Cache / CachedData / CachedExtensionVSIXs / GPUCache 与超过 24h 的旧日志；不影响代码、设置与已装插件。",
            Category: CleanupCategory.SoftwareAndDeveloperCache,
            Tier: CleanupRecommendationTier.Recommended,
            Channel: CleanupExecutionChannel.FileQuarantine,
            OfficialApiKind: CleanupOfficialApiKind.None,
            TargetSpecs: specs.ToArray(),
            RequiredStoppedProcesses: dto.RequiredStoppedProcesses ?? [],
            MinAgeHours: 0,
            CostAndReclaimNote: $"清理前需关闭 {softwareName}；仅处理可重建缓存与超过 24h 的旧日志，保留全部用户设置、工作区状态与已安装插件。",
            ChannelBadgeText: "同卷隔离可恢复",
            IsRebuildable: true,
            GroupId: groupId,
            GroupDisplayName: groupTitle);
    }

    private static CleanupRuleDefinition ExpandJetBrainsIde(
        CleanupRuleEntryDto dto, string version, string? groupId, string? groupTitle)
    {
        string ideName = RequireField(dto.SoftwareName, dto.RuleId, "softwareName");
        string folderPattern = RequireField(dto.FolderPattern, dto.RuleId, "folderPattern");

        return new CleanupRuleDefinition(
            RuleId: dto.RuleId,
            Version: version,
            DisplayName: $"{ideName} 索引缓存与日志",
            ShortDescription: $"清理 LocalAppData\\JetBrains\\{folderPattern} 下的 caches / index / compile-server / tmp 与旧日志；不影响代码、配置与插件。",
            Category: CleanupCategory.SoftwareAndDeveloperCache,
            Tier: CleanupRecommendationTier.OnDemand,
            Channel: CleanupExecutionChannel.FileQuarantine,
            OfficialApiKind: CleanupOfficialApiKind.None,
            TargetSpecs:
            [
                new CleanupPathRuleSpec(CleanupKnownRootKind.LocalAppData, $@"JetBrains\{folderPattern}\caches", ["*"], Recursive: true),
                new CleanupPathRuleSpec(CleanupKnownRootKind.LocalAppData, $@"JetBrains\{folderPattern}\index", ["*"], Recursive: true),
                new CleanupPathRuleSpec(CleanupKnownRootKind.LocalAppData, $@"JetBrains\{folderPattern}\compile-server", ["*"], Recursive: true),
                new CleanupPathRuleSpec(CleanupKnownRootKind.LocalAppData, $@"JetBrains\{folderPattern}\tmp", ["*"], Recursive: true),
                new CleanupPathRuleSpec(CleanupKnownRootKind.LocalAppData, $@"JetBrains\{folderPattern}\log", ["*"], Recursive: true, MinAgeHoursOverride: 24),
                new CleanupPathRuleSpec(CleanupKnownRootKind.JetBrainsCustomSystem, "caches", ["*"], Recursive: true, CustomRootOverride: folderPattern),
                new CleanupPathRuleSpec(CleanupKnownRootKind.JetBrainsCustomSystem, "index", ["*"], Recursive: true, CustomRootOverride: folderPattern),
                new CleanupPathRuleSpec(CleanupKnownRootKind.JetBrainsCustomSystem, "compile-server", ["*"], Recursive: true, CustomRootOverride: folderPattern),
                new CleanupPathRuleSpec(CleanupKnownRootKind.JetBrainsCustomSystem, "tmp", ["*"], Recursive: true, CustomRootOverride: folderPattern),
                new CleanupPathRuleSpec(CleanupKnownRootKind.JetBrainsCustomSystem, "log", ["*"], Recursive: true, CustomRootOverride: folderPattern, MinAgeHoursOverride: 24)
            ],
            RequiredStoppedProcesses: dto.RequiredStoppedProcesses ?? [],
            MinAgeHours: 0,
            CostAndReclaimNote: $"清理前需关闭 {ideName}；仅清理 LocalAppData 缓存与索引，绝不触碰 Roaming 配置和插件；下次打开项目需重新建立索引。",
            ChannelBadgeText: "同卷隔离可恢复",
            IsRebuildable: true,
            GroupId: groupId,
            GroupDisplayName: groupTitle);
    }

    private static CleanupRuleDefinition ExpandElectronApp(
        CleanupRuleEntryDto dto, string version, string? groupId, string? groupTitle)
    {
        string softwareName = RequireField(dto.SoftwareName, dto.RuleId, "softwareName");
        string roamingFolder = RequireField(dto.RoamingFolder, dto.RuleId, "roamingFolder");
        bool includeLogs = dto.IncludeLogs ?? false;

        var specs = new List<CleanupPathRuleSpec>
        {
            new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\Cache", ["*"], Recursive: true),
            new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\Code Cache", ["*"], Recursive: true),
            new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\GPUCache", ["*"], Recursive: true),
            new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\GrShaderCache", ["*"], Recursive: true),
            new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\DawnCache", ["*"], Recursive: true),
            new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\DawnGraphiteCache", ["*"], Recursive: true),
            new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\DawnWebGPUCache", ["*"], Recursive: true)
        };
        if (includeLogs)
        {
            specs.Add(new(CleanupKnownRootKind.RoamingAppData, $@"{roamingFolder}\logs", ["*"], Recursive: true, MinAgeHoursOverride: 24));
        }

        return new CleanupRuleDefinition(
            RuleId: dto.RuleId,
            Version: version,
            DisplayName: includeLogs ? $"{softwareName} 网页缓存与旧日志" : $"{softwareName} 网页与 GPU 缓存",
            ShortDescription: $"仅清 {softwareName} 的 Cache / Code Cache / GPUCache" + (includeLogs ? " 与超过 24h 的旧日志" : string.Empty) + "，绝不触碰登录凭据、Local Storage 与业务数据。",
            Category: CleanupCategory.SoftwareAndDeveloperCache,
            Tier: CleanupRecommendationTier.Recommended,
            Channel: CleanupExecutionChannel.FileQuarantine,
            OfficialApiKind: CleanupOfficialApiKind.None,
            TargetSpecs: specs.ToArray(),
            RequiredStoppedProcesses: dto.RequiredStoppedProcesses ?? [],
            MinAgeHours: 0,
            CostAndReclaimNote: $"清理前需退出 {softwareName}；仅清可重建渲染缓存，保留全部账号配置与本地数据。",
            ChannelBadgeText: "同卷隔离可恢复",
            IsRebuildable: true,
            GroupId: groupId,
            GroupDisplayName: groupTitle);
    }

    private static void ValidateRuleSafetyInvariants(CleanupRuleDefinition rule, HashSet<string> seenIds)
    {
        if (!seenIds.Add(rule.RuleId))
            throw new InvalidOperationException($"Duplicate cleanup ruleId detected: '{rule.RuleId}'.");

        if (rule.Category == CleanupCategory.SoftwareAndDeveloperCache
            && (string.IsNullOrWhiteSpace(rule.GroupId) || string.IsNullOrWhiteSpace(rule.GroupDisplayName)))
        {
            throw new InvalidOperationException($"Software cleanup rule '{rule.RuleId}' must belong to a valid group.");
        }

        if (rule.RuleId.StartsWith("browser-", StringComparison.OrdinalIgnoreCase) && !rule.IsBrowserCacheRule)
        {
            throw new InvalidOperationException($"Browser rule '{rule.RuleId}' must enable IsBrowserCacheRule protection.");
        }

        if (rule.Tier == CleanupRecommendationTier.Caution && rule.CautionReason is null)
        {
            throw new InvalidOperationException($"Caution tier rule '{rule.RuleId}' must define CautionReason.");
        }

        foreach (CleanupPathRuleSpec spec in rule.TargetSpecs)
        {
            if (Path.IsPathRooted(spec.RelativeSubPath) || spec.RelativeSubPath.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cleanup rule '{rule.RuleId}' contains unsafe relativeSubPath '{spec.RelativeSubPath}'.");
            }
        }
    }

    private static string RequireField(string? value, string ruleId, string fieldName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Cleanup rule '{ruleId}' missing required template field '{fieldName}'.");

    private sealed class CleanupRuleCatalogDocument
    {
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, string>? Groups { get; set; }
        public List<CleanupRuleEntryDto>? Rules { get; set; }
    }

    private sealed class CleanupRuleEntryDto
    {
        public string RuleId { get; set; } = string.Empty;
        public string? GroupId { get; set; }
        public string? Template { get; set; }
        public string? SoftwareName { get; set; }
        public CleanupKnownRootKind? RootKind { get; set; }
        public string? UserDataSubPath { get; set; }
        public string[]? RoamingFolders { get; set; }
        public string? RoamingFolder { get; set; }
        public string? FolderPattern { get; set; }
        public bool? IncludeLogs { get; set; }
        public CleanupPathRuleSpecDto[]? ExtraSpecs { get; set; }

        public string? DisplayName { get; set; }
        public string? ShortDescription { get; set; }
        public CleanupCategory? Category { get; set; }
        public CleanupRecommendationTier? Tier { get; set; }
        public CleanupExecutionChannel? Channel { get; set; }
        public CleanupOfficialApiKind? OfficialApiKind { get; set; }
        public CleanupPathRuleSpecDto[]? TargetSpecs { get; set; }
        public string[]? RequiredStoppedProcesses { get; set; }
        public int? MinAgeHours { get; set; }
        public string? CostAndReclaimNote { get; set; }
        public string? ChannelBadgeText { get; set; }
        public bool? IsRebuildable { get; set; }
        public bool? ForceSameVolumeQuarantineOnly { get; set; }
        public CleanupCautionReasonDto? CautionReason { get; set; }
        public bool? IsBrowserCacheRule { get; set; }
    }

    private sealed class CleanupPathRuleSpecDto
    {
        public CleanupKnownRootKind RootKind { get; set; }
        public string? RelativeSubPath { get; set; }
        public string[]? IncludeFilePatterns { get; set; }
        public bool? Recursive { get; set; }
        public string[]? ExcludePathSegments { get; set; }
        public string[]? ExcludeFileNames { get; set; }
        public string? CustomRootOverride { get; set; }
        public int? MinAgeHoursOverride { get; set; }
    }

    private sealed class CleanupCautionReasonDto
    {
        public string OneLineImpact { get; set; } = string.Empty;
        public string HoverSummary { get; set; } = string.Empty;
        public string RiskCause { get; set; } = string.Empty;
        public string PossibleImpact { get; set; } = string.Empty;
        public string ProtectionStrategy { get; set; } = string.Empty;
        public bool? RequiresPhraseConfirmation { get; set; }
        public string? ConfirmationPhrase { get; set; }
    }
}
