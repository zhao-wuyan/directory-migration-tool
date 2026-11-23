using MigrationCore.Models.Safety;

namespace MigrationCore.Services.Safety;

/// <summary>
/// 版本化内置应用迁移规则库（受审查的白名单业务数据规则与 L2 厂商原生引导规则）。
/// 规则仅授权经过明确验证的业务/素材/模型子目录，程序本体与共享运行环境保留原位。
/// </summary>
public static class BuiltInApplicationRules
{
    public const string RuleCatalogVersion = "2026.11.22-rules-v1";

    public static IReadOnlyList<ApplicationRuleDefinition> All { get; } =
    [
        // NovaStudio（智玲同学）：重点迁移模型、输出、知识库、数据库等业务数据；程序本体与共享运行环境保留原位
        new ApplicationRuleDefinition(
            ApplicationId: "novastudio",
            DisplayName: "NovaStudio（智玲同学）",
            RuleVersion: RuleCatalogVersion,
            Level: MigrationAdmissionLevel.VerifiedEligible,
            InstallDetectionNames: ["NovaStudio", "智玲同学"],
            PublisherHints: ["Nova", "LingLong", "XingHe", "玲珑"],
            RelativeAllowedDataPaths:
            [
                @"comfyui\models",
                @"comfyui\output",
                @"comfyui\input",
                "models",
                "output",
                "outputs",
                "knowledge",
                "database",
                "db",
                "workspace"
            ],
            RelativeForbiddenSubpaths:
            [
                "bin",
                "runtime",
                "python",
                "python_embeded",
                "venv",
                ".venv",
                "resources",
                "locales"
            ],
            PathContainsMarkers: ["NovaStudio"],
            RequiredStoppedProcessNames: ["NovaStudio", "ComfyUI"],
            AssociatedServiceNames: [],
            RequireProcessStopped: true,
            NativeGuidance: null,
            VerificationNotes: "迁移后请启动 NovaStudio 加载已迁移模型并执行一次生成/写入测试，确认模型列表与输出正常后再决定备份处理。"),

        // NovaMax：重点迁移模型、输出、知识库、数据库等业务数据
        new ApplicationRuleDefinition(
            ApplicationId: "novamax",
            DisplayName: "NovaMax",
            RuleVersion: RuleCatalogVersion,
            Level: MigrationAdmissionLevel.VerifiedEligible,
            InstallDetectionNames: ["NovaMax"],
            PublisherHints: ["Nova", "LingLong", "XingHe", "玲珑"],
            RelativeAllowedDataPaths:
            [
                "models",
                "output",
                "outputs",
                "knowledge",
                "database",
                "db",
                "workspace",
                @"comfyui\models",
                @"comfyui\output"
            ],
            RelativeForbiddenSubpaths:
            [
                "bin",
                "runtime",
                "python",
                "python_embeded",
                "venv",
                ".venv",
                "resources"
            ],
            PathContainsMarkers: ["NovaMax"],
            RequiredStoppedProcessNames: ["NovaMax"],
            AssociatedServiceNames: [],
            RequireProcessStopped: true,
            NativeGuidance: null,
            VerificationNotes: "迁移后请启动 NovaMax 验证模型加载、知识库检索与数据写入，再处理原始备份。"),

        // ComfyUI 独立目录
        new ApplicationRuleDefinition(
            ApplicationId: "comfyui",
            DisplayName: "ComfyUI",
            RuleVersion: RuleCatalogVersion,
            Level: MigrationAdmissionLevel.VerifiedEligible,
            InstallDetectionNames: ["ComfyUI"],
            PublisherHints: ["Comfy", "ComfyUI"],
            RelativeAllowedDataPaths:
            [
                "models",
                "output",
                "input"
            ],
            RelativeForbiddenSubpaths:
            [
                "python_embeded",
                "venv",
                ".venv",
                "comfy"
            ],
            PathContainsMarkers: ["ComfyUI"],
            RequiredStoppedProcessNames: ["ComfyUI"],
            AssociatedServiceNames: [],
            RequireProcessStopped: true,
            NativeGuidance: null,
            VerificationNotes: "迁移后启动 ComfyUI 验证 Checkpoint/LoRA 读取与出图保存。"),

        // Steam 游戏库数据子目录
        new ApplicationRuleDefinition(
            ApplicationId: "steam-library",
            DisplayName: "Steam 游戏库",
            RuleVersion: RuleCatalogVersion,
            Level: MigrationAdmissionLevel.VerifiedEligible,
            InstallDetectionNames: ["Steam"],
            PublisherHints: ["Valve"],
            RelativeAllowedDataPaths:
            [
                @"steamapps\common",
                @"steamapps\workshop"
            ],
            RelativeForbiddenSubpaths:
            [
                "bin",
                "package",
                "config",
                "userdata"
            ],
            PathContainsMarkers: ["SteamLibrary", "Steam"],
            RequiredStoppedProcessNames: ["steam", "steamwebhelper"],
            AssociatedServiceNames: ["Steam Client Service"],
            RequireProcessStopped: true,
            NativeGuidance: null,
            VerificationNotes: "请先完全退出 Steam；迁移后启动 Steam 校验游戏文件完整性与启动正常。"),

        // Blender 资源与渲染输出目录
        new ApplicationRuleDefinition(
            ApplicationId: "blender",
            DisplayName: "Blender",
            RuleVersion: RuleCatalogVersion,
            Level: MigrationAdmissionLevel.VerifiedEligible,
            InstallDetectionNames: ["Blender"],
            PublisherHints: ["Blender Foundation"],
            RelativeAllowedDataPaths:
            [
                "assets",
                "render_outputs",
                "projects"
            ],
            RelativeForbiddenSubpaths:
            [
                "python",
                "lib",
                "scripts"
            ],
            PathContainsMarkers: ["Blender"],
            RequiredStoppedProcessNames: ["blender"],
            AssociatedServiceNames: [],
            RequireProcessStopped: true,
            NativeGuidance: null,
            VerificationNotes: "迁移后打开 Blender 资产库确认材质/模型引用正常。"),

        // Docker Desktop / WSL2：L2 厂商原生引导（严禁硬建符号链接或代停容器）
        new ApplicationRuleDefinition(
            ApplicationId: "docker-desktop-wsl",
            DisplayName: "Docker Desktop / WSL2 虚拟磁盘",
            RuleVersion: RuleCatalogVersion,
            Level: MigrationAdmissionLevel.VendorNativeRecommended,
            InstallDetectionNames: ["Docker Desktop"],
            PublisherHints: ["Docker"],
            RelativeAllowedDataPaths:
            [
                @"Docker\wsl",
                @"Docker\wsl\disk",
                @"Docker\wsl\data",
                @"Docker\wsl\main",
                @"DockerDesktop\vm-data",
                @"DockerDesktop\vms"
            ],
            RelativeForbiddenSubpaths: [],
            PathContainsMarkers:
            [
                @"AppData\Local\Docker",
                @"AppData\Local\wsl",
                @"Docker\wsl",
                @"ProgramData\DockerDesktop",
                @"DockerDesktop\vm-data",
                @"DockerDesktop\vms",
                "DockerDesktopWSL",
                "docker-desktop-data"
            ],
            RequiredStoppedProcessNames: ["Docker Desktop", "com.docker.backend"],
            AssociatedServiceNames: ["com.docker.service"],
            RequireProcessStopped: true,
            NativeGuidance: "Docker Desktop 的 WSL2 虚拟磁盘（ext4.vhdx）不建议硬建符号链接，可能导致镜像损坏或启动失败。请使用 Docker Desktop 官方迁移方式：1) 先手动停止容器并退出 Docker Desktop；2) 打开 Docker Desktop → Settings → Resources → Disk image location 修改数据目录，或使用官方 `wsl --export` / `wsl --import` 命令迁移；3) 确认容器正常后再清理旧镜像文件。",
            VerificationNotes: "使用 Docker 原生迁移功能并保留备份镜像后再移除旧目录。"),

        // OneDrive / 云同步目录：L2 厂商原生引导
        new ApplicationRuleDefinition(
            ApplicationId: "onedrive-sync",
            DisplayName: "OneDrive 云同步目录",
            RuleVersion: RuleCatalogVersion,
            Level: MigrationAdmissionLevel.VendorNativeRecommended,
            InstallDetectionNames: ["Microsoft OneDrive", "OneDrive"],
            PublisherHints: ["Microsoft"],
            RelativeAllowedDataPaths: [],
            RelativeForbiddenSubpaths: [],
            PathContainsMarkers: ["OneDrive"],
            RequiredStoppedProcessNames: ["OneDrive"],
            AssociatedServiceNames: [],
            RequireProcessStopped: true,
            NativeGuidance: "云同步目录（含 Files On-Demand 云占位文件）直接建符号链接可能引发云端误删或同步死循环。请在 OneDrive 客户端设置中取消链接此电脑，移动本地文件夹后再重新登录并指定新位置。",
            VerificationNotes: "请通过 OneDrive 客户端原生设置更改本地同步目录。")
    ];
}
