using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationCore.Models.Safety;

namespace MigrationCore.Services.Safety;

/// <summary>
/// 列表行内悬浮提示图标级别：
/// None = 不显示行内图标（保持列表清爽）；
/// Info = 蓝色信息图标（常见个人/开发/模型目录，提供用途说明与迁移参考）；
/// Warning = 琥珀黄感叹号（工具自身保护、原生引导或谨慎风险目录，提醒用户注意特殊原因/避坑）。
/// </summary>
public enum DirectoryHintLevel
{
    None,
    Info,
    Warning
}

public sealed record DirectoryExplanation(
    string RoleTitle,
    string Purpose,
    string MigrationAdvice,
    DirectoryHintLevel HintLevel = DirectoryHintLevel.None);

/// <summary>
/// 常见 Windows 系统目录、个人 Known Folders 与高频软件/开发目录的只读中文说明库（表驱动，从内嵌 JSON 规则加载）。
/// 仅提供客观用途与迁移参考建议，帮助用户自主判断，不改变 L0–L3 安全准入等级。
/// </summary>
public static class KnownDirectoryExplainer
{
    private const string EmbeddedResourceName = "MigrationCore.Rules.directory-explanations.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly CompiledExplanationTable Table = LoadTable();

    public static string FormatHoverToolTip(DirectoryExplanation explanation) =>
        $"{explanation.RoleTitle}\n{explanation.Purpose}\n建议：{explanation.MigrationAdvice}";

    public static DirectoryExplanation? Explain(
        string fullPath,
        ApplicationCatalog? catalog = null,
        SafetyPolicyDecision? decision = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        // 0. 最高优先级：工具自身目录或活动事务保护（直白告知用户为何显示 L0，而非系统损坏）
        if (decision is not null && decision.ReasonCodes.Contains(SafetyReasonCodes.L0SelfOrActiveTransaction, StringComparer.OrdinalIgnoreCase))
        {
            return new DirectoryExplanation(
                "包含当前运行中的迁移工具或事务目录",
                "本目录是当前正在运行的迁移工具（或事务备份/状态目录）所在位置或其上级目录，并非 Windows 系统目录。",
                "为防止工具在执行中把运行中的自己搬走导致崩溃，禁止整体迁移本层；请展开选择其中的其他业务子目录，或将本工具移到其他盘后再迁移整个目录。",
                DirectoryHintLevel.Warning);
        }

        string normalized;
        try
        {
            normalized = PathIdentityService.Normalize(fullPath).TrimEnd('\\');
        }
        catch
        {
            return null;
        }

        if (normalized.Length <= 3)
        {
            return new DirectoryExplanation(
                "磁盘卷根目录",
                "整个磁盘分区的最顶层目录，包含系统卷元数据与全部文件夹。",
                "卷根目录不可整体迁移，请展开选择具体的业务或个人数据目录。");
        }

        string[] parts = normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        string leaf = parts[^1];
        string lowerFull = normalized.ToLowerInvariant();

        // 1. Windows 顶级系统目录（卷根直接子目录 O(1) 查表 + chkdsk FOUND.xxx 模式）
        if (parts.Length == 1)
        {
            if (Table.VolumeRoots.TryGetValue(leaf, out DirectoryExplanation? rootMatch))
                return rootMatch;

            if (leaf.Length == 9 && leaf.StartsWith("FOUND.", StringComparison.OrdinalIgnoreCase)
                && char.IsAsciiDigit(leaf[6]) && char.IsAsciiDigit(leaf[7]) && char.IsAsciiDigit(leaf[8]))
            {
                return new DirectoryExplanation(
                    "Windows 磁盘修复恢复目录（chkdsk）",
                    "Windows 磁盘检查（chkdsk）修复文件系统异常时自动生成的隐藏恢复碎片目录。",
                    "严禁迁移；确认无需恢复其中的 .chk 碎片文件后，可交由系统或手动清理删除。");
            }
        }

        // 2. 用户配置目录（Users\<User>\...）
        if (parts.Length >= 2 && parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 2)
            {
                return new DirectoryExplanation(
                    "用户个人配置根目录",
                    "包含当前用户的桌面、文档、注册表配置单元（NTUSER.DAT）与全部软件数据。",
                    "不可整体迁移，否则会导致系统登录异常；请展开挑选具体的个人数据或软件数据目录。");
            }

            string sub1 = parts[2];
            if (parts.Length == 3 && Table.UserProfileChildren.TryGetValue(sub1, out DirectoryExplanation? userChildMatch))
                return userChildMatch;

            if (sub1.Equals("AppData", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
            {
                string appDataRel = string.Join('\\', parts.AsSpan(3).ToArray());
                if (Table.AppDataRelatives.TryGetValue(appDataRel, out DirectoryExplanation? appDataMatch))
                    return appDataMatch;
            }
        }

        // 3. 高频软件、开发工具与 AI 模型目录（按配置优先级匹配）
        foreach (CompiledFeatureRule rule in Table.FeatureRules)
        {
            if (rule.Matches(leaf, lowerFull))
                return rule.Explanation;
        }

        // 4. 已匹配内置规则或注册表安装软件的通用说明
        if (catalog is not null)
        {
            SafetyApplicationMatch? match = catalog.Match(normalized);
            if (match is not null)
            {
                return match.RecommendedLevel == MigrationAdmissionLevel.VerifiedEligible
                    ? new DirectoryExplanation(
                        $"{match.DisplayName} · 已验证业务数据目录",
                        $"已通过内置规则识别为 {match.DisplayName} 的独立数据/素材/模型目录，与程序本体分离。",
                        "关闭对应软件后推荐迁移；迁移后保留原盘备份，请先启动软件确认加载正常再释放备份。",
                        DirectoryHintLevel.Info)
                    : new DirectoryExplanation(
                        $"{match.DisplayName} · 建议官方原生迁移",
                        $"已识别为 {match.DisplayName} 的专用存储目录，强建符号链接存在兼容性或服务重启风险。",
                        "请优先按下方「官方原生迁移方案」操作。",
                        DirectoryHintLevel.Warning);
            }

            string? attr = catalog.IdentifyAttributionLabel(normalized);
            if (!string.IsNullOrWhiteSpace(attr))
            {
                return new DirectoryExplanation(
                    $"{attr} 相关目录",
                    $"经本机已安装软件或路径特征识别，该目录归属于「{attr}」。",
                    "若该目录为程序安装本体，建议继续展开，优先仅迁移其中的模型、素材、缓存或输出等纯数据子目录；迁移前请先彻底退出该软件。");
            }
        }

        // 5. 兜底通用说明（针对用户自建目录）
        return new DirectoryExplanation(
            "普通本地目录（未收录特征）",
            "未命中系统保护区与内置软件规则，可能是你的个人资料、项目代码、绿色软件或未收录第三方应用目录。",
            "请点击右上角打开文件夹确认内容：纯资料/素材/模型目录确认无程序占用后可勾选 L3 风险确认迁移；若包含正在运行的服务、驱动或系统组件请勿迁移。");
    }

    private static CompiledExplanationTable LoadTable()
    {
        Assembly asm = typeof(KnownDirectoryExplainer).Assembly;
        using Stream stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Missing embedded explanation rules resource: {EmbeddedResourceName}");

        ExplanationDocument doc = JsonSerializer.Deserialize<ExplanationDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize explanation rules from {EmbeddedResourceName}");

        var volumeRoots = new Dictionary<string, DirectoryExplanation>(StringComparer.OrdinalIgnoreCase);
        var userChildren = new Dictionary<string, DirectoryExplanation>(StringComparer.OrdinalIgnoreCase);
        var appDataRelatives = new Dictionary<string, DirectoryExplanation>(StringComparer.OrdinalIgnoreCase);
        var featureRules = new List<CompiledFeatureRule>();

        foreach (ExplanationRuleDto dto in doc.Rules ?? [])
        {
            var explanation = new DirectoryExplanation(
                dto.RoleTitle,
                dto.Purpose,
                dto.MigrationAdvice,
                dto.HintLevel ?? DirectoryHintLevel.None);

            switch (dto.MatchKind)
            {
                case ExplanationMatchKind.VolumeRoot:
                    foreach (string name in dto.Names ?? []) volumeRoots[name] = explanation;
                    break;
                case ExplanationMatchKind.UserProfileChild:
                    foreach (string name in dto.Names ?? []) userChildren[name] = explanation;
                    break;
                case ExplanationMatchKind.AppDataRelative:
                    foreach (string name in dto.Names ?? []) appDataRelatives[name] = explanation;
                    break;
                case ExplanationMatchKind.PathContains:
                case ExplanationMatchKind.Leaf:
                case ExplanationMatchKind.LeafOrPrefix:
                case ExplanationMatchKind.LeafWithPathContains:
                    featureRules.Add(new CompiledFeatureRule(
                        dto.MatchKind,
                        (dto.Names ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                        dto.Names ?? [],
                        dto.Prefixes ?? [],
                        dto.RequiredPathContains?.ToLowerInvariant(),
                        explanation));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported explanation matchKind: {dto.MatchKind}");
            }
        }

        return new CompiledExplanationTable(
            volumeRoots.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            userChildren.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            appDataRelatives.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            featureRules.ToArray());
    }

    private enum ExplanationMatchKind
    {
        VolumeRoot,
        UserProfileChild,
        AppDataRelative,
        PathContains,
        Leaf,
        LeafOrPrefix,
        LeafWithPathContains
    }

    private sealed record CompiledExplanationTable(
        FrozenDictionary<string, DirectoryExplanation> VolumeRoots,
        FrozenDictionary<string, DirectoryExplanation> UserProfileChildren,
        FrozenDictionary<string, DirectoryExplanation> AppDataRelatives,
        CompiledFeatureRule[] FeatureRules);

    private sealed record CompiledFeatureRule(
        ExplanationMatchKind MatchKind,
        FrozenSet<string> ExactNames,
        string[] RawPatterns,
        string[] Prefixes,
        string? RequiredPathContainsLower,
        DirectoryExplanation Explanation)
    {
        public bool Matches(string leaf, string lowerFullPath) => MatchKind switch
        {
            ExplanationMatchKind.PathContains => RawPatterns.Any(p => lowerFullPath.Contains(p, StringComparison.OrdinalIgnoreCase)),
            ExplanationMatchKind.Leaf => ExactNames.Contains(leaf),
            ExplanationMatchKind.LeafOrPrefix => ExactNames.Contains(leaf)
                || Prefixes.Any(p => leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase)),
            ExplanationMatchKind.LeafWithPathContains => ExactNames.Contains(leaf)
                && !string.IsNullOrEmpty(RequiredPathContainsLower)
                && lowerFullPath.Contains(RequiredPathContainsLower, StringComparison.Ordinal),
            _ => false
        };
    }

    private sealed class ExplanationDocument
    {
        public string Version { get; set; } = string.Empty;
        public List<ExplanationRuleDto>? Rules { get; set; }
    }

    private sealed class ExplanationRuleDto
    {
        public ExplanationMatchKind MatchKind { get; set; }
        public string[]? Names { get; set; }
        public string[]? Prefixes { get; set; }
        public string? RequiredPathContains { get; set; }
        public string RoleTitle { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string MigrationAdvice { get; set; } = string.Empty;
        public DirectoryHintLevel? HintLevel { get; set; }
    }
}
