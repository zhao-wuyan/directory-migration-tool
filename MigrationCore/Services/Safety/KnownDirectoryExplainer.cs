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
/// 常见 Windows 系统目录、个人 Known Folders 与高频软件/开发目录的只读中文说明库。
/// 仅提供客观用途与迁移参考建议，帮助用户自主判断，不改变 L0–L3 安全准入等级。
/// </summary>
public static class KnownDirectoryExplainer
{
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

        // 1. Windows 顶级系统目录（含隐藏更新/恢复/元数据目录与卷根受保护系统目录）
        if (parts.Length == 1)
        {
            if (Eq(leaf, "Windows"))
                return new("Windows 操作系统核心", "存放操作系统内核、驱动、系统库（System32/WinSxS）及系统服务。", "严禁迁移；若需释放空间，请等待三期官方 DISM 组件清理或使用 Windows 磁盘清理。");
            if (Eq(leaf, "Windows.old"))
                return new("旧版 Windows 系统备份目录（Windows.old）", "大版本升级后由 Windows 保留的旧系统备份（通常 10 天后系统自动清理），常占用 15–30 GB。", "严禁迁移；确认无需回退旧版系统后，请使用 Windows「磁盘清理（清理系统文件 → 以前的 Windows 安装）」或系统存储感知安全清理（后续三期垃圾清理也将纳入支持）。", DirectoryHintLevel.Info);
            if (Eq(leaf, "$WinREAgent"))
                return new("Windows 恢复环境更新缓存（$WinREAgent）", "Windows 专属隐藏系统目录，由系统更新与 WinRE 恢复环境维护服务创建，用于存放更新暂存与回滚备份文件。", "严禁通过符号链接迁移（会导致系统更新或恢复环境异常）；待 Windows 更新完成且系统稳定重启后，可通过 Windows「磁盘清理（清理系统文件）」或后续三期垃圾清理安全释放。");
            if (Eq(leaf, "$WINDOWS.~BT") || Eq(leaf, "$Windows.~WS") || Eq(leaf, "$Windows.~LS")
                || Eq(leaf, "$WINDOWS.~Q") || Eq(leaf, "$GetCurrent") || Eq(leaf, "$INPLACE.~TR"))
                return new("Windows 系统升级与更新临时目录", "Windows 专属隐藏系统目录，用于存放大版本升级或功能更新期间的安装包、临时解压文件与回滚数据。", "严禁迁移；升级完成且确认无需回退旧版本后，请使用 Windows「磁盘清理（清理系统文件 → 以前的 Windows 安装 / 临时 Windows 安装文件）」或后续三期垃圾清理释放。");
            if (Eq(leaf, "$SysReset"))
                return new("Windows 重置此电脑暂存与日志目录（$SysReset）", "Windows 专属隐藏系统目录，记录“重置此电脑”流程的配置、回滚数据与诊断日志。", "严禁迁移；确认系统已稳定运行且无需排查重置问题后，可通过系统清理或后续三期垃圾清理处理。");
            if (Eq(leaf, "System Volume Information"))
                return new("Windows 卷系统信息目录", "Windows 专属隐藏保护目录，存放卷影副本、系统还原点、BitLocker 及索引元数据。", "严禁迁移或手动删除；如需减少还原点占用，请在 Windows「系统保护」设置中调整最大磁盘使用量。");
            if (Eq(leaf, "$Recycle.Bin") || Eq(leaf, "RECYCLER") || Eq(leaf, "Recycled"))
                return new("Windows 回收站元数据目录", "Windows 专属隐藏系统目录，存放当前磁盘分区中已删除但尚未清空的回收站文件。", "严禁迁移；如需释放空间，请直接右键桌面「回收站」执行清空。");
            if (Eq(leaf, "Recovery") || Eq(leaf, "Boot") || Eq(leaf, "EFI"))
                return new("Windows 启动与系统恢复目录", "Windows 专属隐藏系统目录，存放操作系统引导配置（BCD）或 WinRE 恢复镜像。", "严禁迁移或删除，否则将导致系统无法启动或无法进入恢复环境。");
            if (Eq(leaf, "Config.Msi"))
                return new("Windows Installer 事务回滚目录（Config.Msi）", "Windows 专属隐藏系统目录，由 MSI 安装服务在软件安装/卸载期间用于记录回滚脚本与备份文件。", "严禁迁移；正常安装完成后由系统自动清理，若确认无安装程序运行且残留占用较大，可交由后续垃圾清理处理。");
            if (Eq(leaf, "MSOCache"))
                return new("Microsoft Office 本地安装源缓存（MSOCache）", "Office 安装程序留在卷根的隐藏本地安装源缓存，用于修复或增量安装组件。", "不建议符号链接迁移；若无需离线修复 Office，可通过 Windows「磁盘清理」或后续垃圾清理安全释放。");
            if (Eq(leaf, "Documents and Settings"))
                return new("Windows 旧版路径兼容联接点", "Windows 专属隐藏系统联接点（指向 Users），仅为兼容旧版软件而保留。", "严禁迁移或删除，实际不包含独立文件占用。");
            if (Eq(leaf, "OneDriveTemp"))
                return new("OneDrive 卷根隐藏临时缓存（OneDriveTemp）", "Windows 内置 OneDrive 同步客户端在卷根创建的隐藏系统临时目录，用于暂存同步与校验文件。", "严禁迁移；退出 OneDrive 后若仍有残留，可交由后续垃圾清理处理。");
            if (Eq(leaf, "WUDownloadCache") || Eq(leaf, "DeliveryOptimization"))
                return new("Windows 更新与传递优化缓存目录", "Windows 更新服务或传递优化功能在卷根创建的隐藏系统下载缓存目录。", "严禁迁移；可通过 Windows「设置 → 系统 → 存储 → 临时文件」或后续垃圾清理安全释放。");
            if (Eq(leaf, "WindowsApps") || Eq(leaf, "ModifiableWindowsApps") || Eq(leaf, "WpSystem"))
                return new("Microsoft Store / UWP 应用系统容器", "Windows 应用商店与 Xbox 游戏在该磁盘创建的受保护安装与数据容器，绑定 AppX 系统权限。", "严禁使用符号链接迁移；如需更换盘符，请前往 Windows「设置 → 应用 → 已安装的应用」中使用系统自带的「移动」功能。", DirectoryHintLevel.Warning);
            if (Eq(leaf, "PerfLogs"))
                return new("Windows 性能日志目录（PerfLogs）", "Windows 性能监视器默认日志存放目录，通常为空。", "属于系统默认目录，无需迁移。");
            if (leaf.Length == 9 && leaf.StartsWith("FOUND.", StringComparison.OrdinalIgnoreCase)
                && char.IsAsciiDigit(leaf[6]) && char.IsAsciiDigit(leaf[7]) && char.IsAsciiDigit(leaf[8]))
                return new("Windows 磁盘修复恢复目录（chkdsk）", "Windows 磁盘检查（chkdsk）修复文件系统异常时自动生成的隐藏恢复碎片目录。", "严禁迁移；确认无需恢复其中的 .chk 碎片文件后，可交由系统或后续垃圾清理删除。");
            if (Eq(leaf, "Users"))
                return new("Windows 用户账户总目录", "包含所有本地用户的个人文件、桌面、文档以及 AppData 软件配置。", "不可整体迁移，请展开进入具体用户目录后再挑选大体积数据子目录。");
            if (Eq(leaf, "Program Files") || Eq(leaf, "Program Files (x86)"))
                return new("系统级软件安装根目录", "Windows 默认的 64 位 / 32 位桌面程序安装根目录，关联大量系统服务与快捷方式。", "不可整体迁移；请展开进入具体软件目录，优先只迁移模型、素材或游戏库等纯数据子目录。");
            if (Eq(leaf, "ProgramData"))
                return new("全机共享程序数据目录", "存放所有用户共用的软件配置、授权信息、系统更新与后台服务数据。", "不可整体迁移；其中 Microsoft / Package Cache 等属系统保护区。");
            if (Eq(leaf, "Games") || Eq(leaf, "WeGameApps"))
                return new("游戏安装与资源库目录", "通常用于存放大型客户端游戏本体、资源包与更新文件，往往占据数十至数百 GB。", "完全退出游戏及对应启动器后，通常适合迁移到空间充足的磁盘。", DirectoryHintLevel.Info);
        }

        // 2. 用户配置目录（Users\<User>\...）
        if (parts.Length >= 2 && Eq(parts[0], "Users"))
        {
            if (parts.Length == 2)
                return new("用户个人配置根目录", "包含当前用户的桌面、文档、注册表配置单元（NTUSER.DAT）与全部软件数据。", "不可整体迁移，否则会导致系统登录异常；请展开挑选具体的个人数据或软件数据目录。");

            string sub1 = parts[2];

            // 个人 Known Folders（精准匹配根目录）
            if (parts.Length == 3)
            {
                if (Eq(sub1, "Desktop"))
                    return new("Windows 桌面目录", "存放电脑桌面上显示的所有文件、文件夹与快捷方式。", "影响范围较广，且可能绑定 OneDrive 同步。更建议右键桌面文件夹「属性 → 位置」使用 Windows 原生移动；若确需迁移，请先确认未开启云同步。", DirectoryHintLevel.Warning);
                if (Eq(sub1, "Documents"))
                    return new("Windows 我的文档", "存放个人文档，同时大量软件和游戏会把配置文件、存档、聊天文件（如微信/QQ）默认存在这里。", "建议优先使用 Windows 右键「属性 → 位置」原生移动，或展开本目录，仅迁移其中占空间最大的单个子目录（如大项目、游戏素材），降低影响面。", DirectoryHintLevel.Info);
                if (Eq(sub1, "Downloads"))
                    return new("Windows 下载目录", "浏览器、网盘和下载工具默认保存安装包与下载文件的位置，最容易日积月累占满 C 盘。", "对程序路径依赖低，非常适合先手动清理旧安装包，或迁移到空间充足的磁盘（也支持右键「属性 → 位置」原生移动）。", DirectoryHintLevel.Info);
                if (Eq(sub1, "Videos"))
                    return new("Windows 视频目录", "存放录屏、剪辑工程、电影与个人视频素材。", "属于典型的大体积个人媒体数据，对系统无强依赖，非常适合迁移释放空间（也支持 Windows「属性 → 位置」原生移动）。", DirectoryHintLevel.Info);
                if (Eq(sub1, "Pictures"))
                    return new("Windows 图片目录", "存放截图、照片、设计素材及相册缓存。", "纯个人媒体文件，未开启 OneDrive 同步时适合迁移释放空间（也支持 Windows「属性 → 位置」原生移动）。", DirectoryHintLevel.Info);
                if (Eq(sub1, "Music"))
                    return new("Windows 音乐目录", "存放本地音频文件与音乐软件默认媒体库。", "纯个人媒体文件，退出音乐播放器后适合迁移（也支持 Windows「属性 → 位置」原生移动）。", DirectoryHintLevel.Info);
                if (Eq(sub1, "Saved Games"))
                    return new("Windows 游戏存档目录", "存放单机游戏进度存档与配置文件。", "通常体积不大但极其珍贵，非必要不建议迁移；如需迁移请先退出游戏并确认备份。", DirectoryHintLevel.Warning);
                if (Eq(sub1, "Favorites") || Eq(sub1, "Contacts") || Eq(sub1, "Links") || Eq(sub1, "Searches"))
                    return new("Windows 个人系统辅助目录", "存放资源管理器快速访问链接、收藏夹或搜索配置。", "体积通常极小且与系统壳层集成，不建议迁移。");
                if (Eq(sub1, "AppData"))
                    return new("用户软件数据总容器（AppData）", "Windows 隐藏目录，存放当前用户所有软件的本地配置、缓存、数据库、登录态与用户级安装程序。", "不可整体迁移；请展开 Local / Roaming 选择具体占空间的大软件数据目录。");
            }

            // AppData 子层级
            if (Eq(sub1, "AppData"))
            {
                if (parts.Length == 4 && Eq(parts[3], "Local"))
                    return new("本地应用数据容器（AppData\\Local）", "存放仅限本机使用的软件缓存、索引、模型、虚拟磁盘与用户级安装程序，通常体积最大。", "不可整体迁移，请展开挑选具体占空间的应用目录。");
                if (parts.Length == 4 && Eq(parts[3], "Roaming"))
                    return new("漫游应用配置容器（AppData\\Roaming）", "存放软件核心用户配置、插件、快捷键与账密会话，通常体积较小但非常关键。", "不可整体迁移；一般无需迁移，仅在个别软件（如大体积聊天/插件数据）占用过高时单独处理。");
                if (parts.Length == 4 && Eq(parts[3], "LocalLow"))
                    return new("低权限应用数据容器（AppData\\LocalLow）", "存放低完整性进程（如浏览器沙箱、Unity 游戏存档/日志）的本地数据。", "不可整体迁移；展开后可按需处理具体大体积游戏数据目录。");
                if (parts.Length == 5 && Eq(parts[3], "Local") && Eq(parts[4], "Programs"))
                    return new("用户级软件安装根目录（AppData\\Local\\Programs）", "Windows 官方用户级程序安装目录（等价于个人版 Program Files），存放 VS Code、Python、IDE、NovaStudio 等程序本体。", "影响全部用户级已装软件，禁止整体迁移；请展开进入具体软件目录，仅迁移模型/素材/输出等业务数据子目录。", DirectoryHintLevel.Warning);
                if (parts.Length == 5 && Eq(parts[3], "Local") && Eq(parts[4], "Temp"))
                    return new("当前用户临时文件目录（AppData\\Local\\Temp）", "安装程序解压包、运行中软件产生的临时缓存文件，长期未清理可能占用数 GB。", "无需迁移；建议关闭正在运行的软件后使用 Windows「磁盘清理」或系统存储感知安全清理。", DirectoryHintLevel.Info);
                if (parts.Length == 5 && Eq(parts[3], "Local") && Eq(parts[4], "Packages"))
                    return new("Microsoft Store / UWP 应用数据目录", "存放 Windows 应用商店程序（如终端、照片、微信 UWP、Xbox 应用）的沙箱数据与配置。", "与 Windows AppX 权限深度绑定，严禁迁移。");
                if (parts.Length == 5 && Eq(parts[3], "Local") && Eq(parts[4], "Microsoft"))
                    return new("Microsoft 本地系统与应用数据目录", "存放 Edge 浏览器、Windows 通知、凭据缓存、输入法及 Office 本地数据。", "与系统组件耦合紧密，严禁整体迁移。");
                if (parts.Length == 5 && Eq(parts[3], "Local") && (Eq(parts[4], "CrashDumps") || Eq(parts[4], "D3DSCache")))
                    return new("系统转储 / 显卡着色器缓存目录", "存放程序崩溃日志（CrashDumps）或 DirectX 着色器编译缓存（D3DSCache），均属于可重建系统缓存。", "属于系统保护区且无需迁移；如需释放空间，可使用 Windows 磁盘清理清理 DirectX 着色器缓存。");
                if (parts.Length == 6 && Eq(parts[3], "Local") && Eq(parts[4], "Programs") && Eq(parts[5], "Common"))
                    return new("用户级软件共享运行库目录", "多个用户级安装程序共用的组件与运行库目录。", "影响范围跨多个软件，禁止迁移。", DirectoryHintLevel.Warning);
            }
        }

        // 3. 高频软件、开发工具与 AI 模型目录（按特征路径/叶名匹配）
        if (lowerFull.Contains(@"\appdata\local\docker") || lowerFull.Contains(@"\programdata\docker")
            || (Eq(leaf, "wsl") && lowerFull.Contains(@"\appdata\local\")))
            return new("Docker / WSL2 虚拟磁盘目录", "存放 Docker 容器镜像、卷数据或 WSL2 Linux 发行版的虚拟硬盘（ext4.vhdx），常占数十至数百 GB。", "不建议强建符号链接；请使用 Docker Desktop 设置中的 Disk image location 或 `wsl --export / --import` 原生迁移。", DirectoryHintLevel.Warning);

        if (Eq(leaf, "OneDrive") || leaf.StartsWith("OneDrive -", StringComparison.OrdinalIgnoreCase)
            || Eq(leaf, "iCloudDrive") || Eq(leaf, "Dropbox"))
            return new("云盘同步目录", "云端网盘本地同步文件夹，通常包含按需下载的云端占位文件与后台同步守护进程。", "直接建符号链接可能引发同步死循环或云端误删；请在对应网盘客户端设置中修改本地同步路径。", DirectoryHintLevel.Warning);

        if (Eq(leaf, "Google") && lowerFull.Contains(@"\appdata\local\")
            || Eq(leaf, "Mozilla") && lowerFull.Contains(@"\appdata\")
            || Eq(leaf, "BraveSoftware") && lowerFull.Contains(@"\appdata\local\"))
            return new("浏览器用户配置与缓存目录", "存放 Chrome / Firefox / Brave 等浏览器的书签、扩展、登录态及网页缓存。", "含加密登录态且浏览器常驻后台进程，一般不建议整体迁移；若体积过大建议先在浏览器内清理网页缓存。", DirectoryHintLevel.Warning);

        if (Eq(leaf, "Android") && lowerFull.Contains(@"\appdata\local\") || Eq(leaf, ".android"))
            return new("Android SDK / 模拟器镜像目录", "存放 Android 开发 SDK、系统镜像及 AVD 虚拟设备磁盘文件，常占用数十 GB。", "退出 Android Studio 与全部模拟器后可迁移；更推荐直接修改 `ANDROID_HOME` / `ANDROID_AVD_HOME` 环境变量原生转移。", DirectoryHintLevel.Info);

        if (Eq(leaf, ".conda") || Eq(leaf, "anaconda3") || Eq(leaf, "miniconda3"))
            return new("Python / Conda 虚拟环境目录", "存放 Conda 虚拟环境、Python 解释器及已下载的科学计算包缓存（pkgs），常占数十 GB。", "建议先执行 `conda clean --all` 释放包缓存；彻底退出终端与 IDE 后也可迁移整个目录，或在 `.condarc` 中更改 `envs_dirs` / `pkgs_dirs`。", DirectoryHintLevel.Info);

        if (Eq(leaf, "JetBrains"))
        {
            bool isLocal = lowerFull.Contains(@"\appdata\local\");
            return isLocal
                ? new("JetBrains IDE 本地索引与缓存", "存放 IDEA / PyCharm / WebStorm / Rider 等 IDE 的项目索引、编译缓存与下载的运行时。", "完全关闭所有 JetBrains IDE 后，可迁移到空间充足的磁盘（也可先在 IDE 中清理旧版本缓存）。", DirectoryHintLevel.Info)
                : new("JetBrains IDE 用户配置与插件", "存放 JetBrains IDE 的全局设置、代码风格、已装插件与登录配置。", "含核心配置，若体积不大建议保留原位；若因插件体积过大需迁移，请先关闭全部 IDE。", DirectoryHintLevel.Warning);
        }

        if (Eq(leaf, ".lmstudio") || Eq(leaf, ".ollama") || Eq(leaf, "huggingface") || Eq(leaf, "modelscope"))
            return new("本地 AI 模型权重与缓存目录", "存放本地大语言模型（GGUF / Safetensors）或开源模型缓存，往往占用数十至数百 GB。", "非常适合迁移到空间充足的大容量磁盘；迁移前请完全退出对应 AI 客户端（部分软件也支持在设置里直接更改模型存储路径）。", DirectoryHintLevel.Info);

        if (Eq(leaf, "npm-cache") || Eq(leaf, "pnpm-store") || Eq(leaf, "Yarn") || Eq(leaf, "pip") || Eq(leaf, "uv")
            || Eq(leaf, ".nuget") || Eq(leaf, ".gradle") || Eq(leaf, ".m2") || Eq(leaf, ".cargo"))
            return new("开发者包管理器缓存目录", "存放 Node.js / Python / .NET / Java / Rust 等开发依赖包的下载缓存，属于可重建数据。", "若开发工具未运行，可迁移到其他盘释放 C 盘空间，或直接通过包管理器命令清理/更改缓存目录环境变量。", DirectoryHintLevel.Info);

        if (Eq(leaf, "WeChat Files") || Eq(leaf, "xwechat_files") || Eq(leaf, "Tencent Files"))
            return new("微信 / QQ 本地聊天与文件目录", "存放聊天图片、视频、收发文件与本地消息数据库，常占数十 GB。", "优先推荐在微信/QQ「设置 → 文件管理」中点击「更改」使用官方迁移功能；如用本工具迁移，务必先彻底退出微信/QQ。", DirectoryHintLevel.Info);

        if (Eq(leaf, "Steam") || Eq(leaf, "SteamLibrary"))
            return new("Steam 游戏库目录", "存放 Steam 客户端或游戏安装资源文件。", "请先彻底退出 Steam；不建议迁移 Steam 客户端本体，优先只迁移 `steamapps\\common` 或 `steamapps\\workshop` 目录。", DirectoryHintLevel.Info);

        if (Eq(leaf, "WeGameApps") || Eq(leaf, "Epic Games") || Eq(leaf, "Battle.net"))
            return new("第三方游戏平台安装库", "存放大型网络或单机游戏客户端文件与更新资源。", "完全退出游戏及对应启动器后，通常可安全迁移到其他大容量磁盘。", DirectoryHintLevel.Info);

        if (Eq(leaf, "MoveWithSymlinkWPF-TestLab"))
            return new("本工具自动化测试实验目录", "运行 VHDX 安全验收测试时创建的临时虚拟磁盘镜像与测试证据目录，不包含你的个人业务数据。", "无需迁移；确认当前没有运行测试脚本后，可直接手工删除旧测试记录释放空间。", DirectoryHintLevel.Warning);

        if (Eq(leaf, "Microsoft VS Code") || Eq(leaf, ".vscode"))
            return Eq(leaf, ".vscode")
                ? new("VS Code 扩展与用户数据目录", "存放 Visual Studio Code 已安装的扩展插件及辅助配置。", "彻底关闭所有 VS Code 窗口后，如插件体积较大可谨慎迁移。", DirectoryHintLevel.Info)
                : new("VS Code 程序本体目录", "Visual Studio Code 编辑器可执行程序目录。", "程序本体通常仅数百 MB 至 1 GB，且涉及右键菜单与自动更新，一般不建议迁移。", DirectoryHintLevel.Warning);

        // 4. 已匹配内置规则或注册表安装软件的通用说明
        if (catalog is not null)
        {
            var match = catalog.Match(normalized);
            if (match is not null)
            {
                return match.RecommendedLevel == MigrationAdmissionLevel.VerifiedEligible
                    ? new($"{match.DisplayName} · 已验证业务数据目录", $"已通过内置规则识别为 {match.DisplayName} 的独立数据/素材/模型目录，与程序本体分离。", "关闭对应软件后推荐迁移；迁移后保留原盘备份，请先启动软件确认加载正常再释放备份。", DirectoryHintLevel.Info)
                    : new($"{match.DisplayName} · 建议官方原生迁移", $"已识别为 {match.DisplayName} 的专用存储目录，强建符号链接存在兼容性或服务重启风险。", "请优先按下方「官方原生迁移方案」操作。", DirectoryHintLevel.Warning);
            }

            string? attr = catalog.IdentifyAttributionLabel(normalized);
            if (!string.IsNullOrWhiteSpace(attr))
            {
                return new($"{attr} 相关目录", $"经本机已安装软件或路径特征识别，该目录归属于「{attr}」。", "若该目录为程序安装本体，建议继续展开，优先仅迁移其中的模型、素材、缓存或输出等纯数据子目录；迁移前请先彻底退出该软件。");
            }
        }

        // 5. 兜底通用说明（针对用户自建目录）
        return new DirectoryExplanation(
            "普通本地目录（未收录特征）",
            "未命中系统保护区与内置软件规则，可能是你的个人资料、项目代码、绿色软件或未收录第三方应用目录。",
            "请点击右上角打开文件夹确认内容：纯资料/素材/模型目录确认无程序占用后可勾选 L3 风险确认迁移；若包含正在运行的服务、驱动或系统组件请勿迁移。");
    }

    private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
