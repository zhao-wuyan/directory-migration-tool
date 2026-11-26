using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationCore.Models.Safety;

namespace MigrationCore.Services.Safety;

/// <summary>
/// 版本化内置应用迁移规则库（受审查的白名单业务数据规则与 L2 厂商原生引导规则）。
/// 规则数据维护在内嵌 Resources/Rules/application-rules.json 中，随应用版本固定并经加载期校验。
/// </summary>
public static class BuiltInApplicationRules
{
    public const string RuleCatalogVersion = "2026.11.22-rules-v1";
    private const string EmbeddedResourceName = "MigrationCore.Rules.application-rules.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static IReadOnlyList<ApplicationRuleDefinition> All { get; } = LoadEmbeddedRules();

    private static IReadOnlyList<ApplicationRuleDefinition> LoadEmbeddedRules()
    {
        Assembly asm = typeof(BuiltInApplicationRules).Assembly;
        using Stream stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Missing embedded application rules resource: {EmbeddedResourceName}");

        ApplicationRuleCatalogDocument doc = JsonSerializer.Deserialize<ApplicationRuleCatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize application rules from {EmbeddedResourceName}");

        string version = string.IsNullOrWhiteSpace(doc.Version) ? RuleCatalogVersion : doc.Version;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<ApplicationRuleDefinition>(doc.Rules?.Count ?? 0);

        foreach (ApplicationRuleEntryDto dto in doc.Rules ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.ApplicationId) || !seen.Add(dto.ApplicationId))
                throw new InvalidOperationException($"Invalid or duplicate applicationId '{dto.ApplicationId}'.");

            rules.Add(new ApplicationRuleDefinition(
                ApplicationId: dto.ApplicationId,
                DisplayName: dto.DisplayName,
                RuleVersion: version,
                Level: dto.Level,
                InstallDetectionNames: dto.InstallDetectionNames ?? [],
                PublisherHints: dto.PublisherHints ?? [],
                RelativeAllowedDataPaths: dto.RelativeAllowedDataPaths ?? [],
                RelativeForbiddenSubpaths: dto.RelativeForbiddenSubpaths ?? [],
                PathContainsMarkers: dto.PathContainsMarkers ?? [],
                RequiredStoppedProcessNames: dto.RequiredStoppedProcessNames ?? [],
                AssociatedServiceNames: dto.AssociatedServiceNames ?? [],
                RequireProcessStopped: dto.RequireProcessStopped ?? true,
                NativeGuidance: dto.NativeGuidance,
                VerificationNotes: dto.VerificationNotes ?? string.Empty));
        }

        return rules;
    }

    private sealed class ApplicationRuleCatalogDocument
    {
        public string Version { get; set; } = string.Empty;
        public List<ApplicationRuleEntryDto>? Rules { get; set; }
    }

    private sealed class ApplicationRuleEntryDto
    {
        public string ApplicationId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public MigrationAdmissionLevel Level { get; set; }
        public string[]? InstallDetectionNames { get; set; }
        public string[]? PublisherHints { get; set; }
        public string[]? RelativeAllowedDataPaths { get; set; }
        public string[]? RelativeForbiddenSubpaths { get; set; }
        public string[]? PathContainsMarkers { get; set; }
        public string[]? RequiredStoppedProcessNames { get; set; }
        public string[]? AssociatedServiceNames { get; set; }
        public bool? RequireProcessStopped { get; set; }
        public string? NativeGuidance { get; set; }
        public string? VerificationNotes { get; set; }
    }
}
