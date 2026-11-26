using System.Collections.Frozen;
using MigrationCore.Models.Cleanup;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 内置声明式垃圾清理规则库（由内嵌 JSON 规则清单经 CleanupRuleLoader 模板展开与安全边界校验后加载）。
/// 设计原则：
/// 1. 软件与开发者缓存按单款软件/单个包管理器独立拆条，绝不聚合多个软件；
/// 2. 浏览器类规则强制开启 IsBrowserCacheRule 守卫，仅清可重建缓存目录，永久排除 Cookies/凭据/书签/历史/会话；
/// 3. 通讯与创作类软件仅清内嵌网页缓存、媒体缩略图与 >24h 运行日志，绝不触碰聊天记录、工程文件与个人文档；
/// 4. 包管理器类规则统一归为 2 档「按需清理」，明确告知离线构建/重下成本。
/// </summary>
public static class BuiltInCleanupRules
{
    public const string CurrentRuleVersion = "2026.11-phase3-v2";

    public const string GroupBrowsersId = "browsers";
    public const string GroupBrowsersTitle = "浏览器缓存";

    public const string GroupIdeId = "ide";
    public const string GroupIdeTitle = "编辑器、IDE 与开发工具";

    public const string GroupDevPackagesId = "dev-packages";
    public const string GroupDevPackagesTitle = "开发者包管理器与构建缓存";

    public const string GroupChatId = "chat";
    public const string GroupChatTitle = "社交与办公协同缓存";

    public const string GroupCreativeId = "creative-productivity";
    public const string GroupCreativeTitle = "创作、效率与网盘客户端";

    public const string GroupGamingMediaId = "gaming-media";
    public const string GroupGamingMediaTitle = "游戏平台与影音客户端";

    private static readonly (string Version, IReadOnlyList<CleanupRuleDefinition> Rules) Loaded =
        CleanupRuleLoader.LoadEmbeddedRules();

    private static readonly FrozenDictionary<string, CleanupRuleDefinition> RulesById =
        Loaded.Rules.ToFrozenDictionary(r => r.RuleId, StringComparer.OrdinalIgnoreCase);

    public static string LoadedManifestVersion => Loaded.Version;

    public static IReadOnlyList<CleanupRuleDefinition> All => Loaded.Rules;

    public static CleanupRuleDefinition? FindById(string? ruleId) =>
        string.IsNullOrWhiteSpace(ruleId)
            ? null
            : RulesById.GetValueOrDefault(ruleId.Trim());
}
