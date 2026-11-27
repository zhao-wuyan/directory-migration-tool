# 目录迁移工具 (Directory Migration Tool) · WPF 版本

> 当前版本：v1.1.7

一个使用符号链接透明迁移大型目录的 Windows 工具，支持 PowerShell CLI 和 WPF GUI 两种方式。

## 功能特性

- ✅ **透明迁移**: 使用目录符号链接 (`mklink /D`)，应用程序无感知
- ✅ **可靠复制**: 使用 `robocopy` 镜像复制，保留权限、时间戳和属性
- ✅ **实时进度**: 显示复制进度、速度、预计剩余时间
- ✅ **安全回滚**: 出错自动回滚至原始状态
- ✅ **路径验证**: 阻止迁移系统关键目录
- ✅ **磁盘检查**: 验证目标磁盘空间是否充足
- ✅ **大文件统计**: 统计超过阈值的大文件数量
- ✅ **一键迁移模式**: 基于 `quick-migrate.json` 的批量迁移与还原
- ✅ **可逆迁移（还原功能）**: 将符号链接一键还原为真实目录
- ✅ **修复模式**: 基于现有目标目录重建符号链接（不复制数据）
- ✅ **应用内用户指南**: 完整的帮助系统，包含使用步骤、常见问题和最佳实践
- ✅ **现代化界面**: 统一的设计语言，流畅的动画效果，优秀的用户体验
- ✅ **磁盘扫描模式**: 只读扫描 NTFS 原始 MFT，按占用查看目录树和大软件目录，勾选后批量迁移
- ✅ **统一四级安全准入**: 手动、一键、扫描共用一套 L0–L3 策略，系统和高危目录硬拒绝，普通目录需人工确认
- ✅ **目录用途说明**: 系统目录、个人文件夹和常见软件目录附带中文用途与迁移建议
- ✅ **硬链接感知**: 目录内部闭环的硬链接在目标盘无损重建，指向外部的硬链接被拦截并提示
- ✅ **内置 AI 助手**: 自带模型或本地模型，只读分析脱敏后的目录元数据，建议须人工采纳
- ✅ **全局备份管理**: 集中查看迁移与还原备份，验证后经双确认处理

## 系统要求

- Windows 10/11 (x64)
- **.NET 10.0 (LTS) 环境**（官方下载：[Microsoft .NET 10.0 下载页](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)）：
  - **完整版（自包含发布）**：无需安装 .NET
  - **轻量版（框架依赖）**：需要 **.NET 10.0 Desktop Runtime (x64)**（`winget install Microsoft.DotNet.DesktopRuntime.10`）
  - **从源码构建 / 开发调试（`dotnet run`、`mise run dev`、`publish.ps1`）**：需要 **.NET 10.0 SDK (x64)**（`winget install Microsoft.DotNet.SDK.10`）
- 管理员权限（或启用开发者模式）
- 本地固定 NTFS 卷（迁移的源盘和目标盘都必须是；ReFS 卷仅支持只读扫描浏览）

## 使用方式

### 方式一: PowerShell CLI

```powershell
.\MoveWithSymlink.ps1 -Source "C:\Users\YourName\Pictures" -Target "D:\Data\Pictures"
```

可选参数:
- `-LargeFileThresholdMB`: 大文件阈值（默认 1024MB）
- `-RobocopyThreads`: 复制并行线程数（默认 8）
- `-SampleMilliseconds`: 进度采样间隔（默认 1000ms）
- `-Repair`: 修复模式（基于现有目标目录重建符号链接，不复制数据）

**修复模式示例:**
```powershell
# 当目标目录已存在但源路径的符号链接丢失或被破坏时使用
.\MoveWithSymlink.ps1 -Source "C:\Users\YourName\Pictures" -Target "D:\Data\Pictures" -Repair
```

### 方式二: WPF GUI

1) 直接运行（推荐，自动申请管理员权限）

```powershell
.\MoveWithSymlinkWPF\bin\publish\win-x64\目录迁移工具.exe
```

2) 使用启动脚本

```powershell
.\run.ps1
```

3) 构建/发布

```powershell
# 发布完整版（包含运行时，约 70+ MB）
.\publish.ps1

# 发布轻量版（框架依赖，约 20+ MB，需要系统安装 .NET 10.0）
.\publish.ps1 -Mode lite

# 同时发布两个版本
.\publish.ps1 -Mode both
```

详细发布说明请查看：[docs/发布版本说明.md](docs/发布版本说明.md)

### 一键迁移模式（WPF）

- 在可执行文件同目录放置 `quick-migrate.json`
- 启动应用，点击顶部“⏩ 一键迁移”进入批量迁移页
- 选择“统一目标目录”或“分任务目录”策略后开始执行
- 支持“未迁移/已迁移”状态分组、失败不阻塞后续、单任务还原与备份清理
- 详情见：`docs/v1.1/快速上手指南.md`

### 磁盘扫描模式（WPF）

- 点击顶部“磁盘扫描”，在磁盘卡片上点击“开始扫描”。扫描全程只读，不改卷数据，也不改文件权限。
- NTFS 卷直接读取原始 MFT，失败时自动降级为低占用并发目录遍历（SSD 多线程、HDD 单线程，均为后台低 I/O 优先级）。ReFS 卷（含 Dev Drive）只能只读浏览，不能作为迁移的源盘或目标盘。
- 在“目录树”和“大软件目录”两个视图里按占用查看目录。行内蓝色图标是目录用途说明，琥珀色感叹号是迁移提醒。
- 勾选目录即加入待迁移清单，勾选本身不执行迁移。L0 目录禁止迁移，L2 目录不可勾选并显示官方迁移步骤，L3 目录需先勾选风险确认。
- 在“待迁移清单与安全预检”里设置统一目标根目录和核对级别（快速核对或完整 SHA-256），预检会复核目录身份、进程占用和目标空间。
- 队列串行执行，单项失败即暂停，不会删除源目录和已完成的备份。迁移完成后到“备份管理”验证软件正常，再手动处理源盘备份。

### AI 助手（WPF）

- 点击顶部“AI 助手”，或使用扫描页右侧的“AI 分析助手”抽屉。
- 模型自带（BYOK）：内置 DeepSeek、通义千问、OpenAI、本地 Ollama、LM Studio 预设，也可填写任意 OpenAI 兼容端点。API Key 用 Windows DPAPI 按当前用户加密保存。
- AI 只能通过只读工具读取脱敏后的目录元数据（用户名替换为 `<USER>`），不读取文件内容，也不能执行迁移、清理或删除。扫描完成后脱敏快照会共享给 AI，可随时“撤销共享”。
- 每次工具调用实际返回给模型的脱敏 JSON，可在“AI 工具返回数据明细”窗口查看。
- AI 给出的迁移建议先经安全策略逐条复核，再由你人工采纳进待迁移清单。采纳不等于执行，执行前仍要经过安全预检。
- 使用云端模型前需确认隐私提示；用本地模型时数据不出本机。

### 备份管理（WPF）

- 点击顶部“备份管理”集中查看手动、一键、扫描三种模式的迁移记录、还原记录和中断待恢复项；有未验证的备份时按钮上显示角标。
- 备份按卷 GUID 和文件 ID 绑定身份，不靠文件名猜测。
- 点“验证与处理备份”进入验证窗口，处理备份仍需双确认。“处理明细”弹窗可按结果筛选、搜索和复制逐项记录。

## GUI 使用向导

### 📖 应用内帮助
- 点击主界面右上角的 **[❓ 使用说明]** 按钮，可查看详细的用户指南
- 用户指南包含：
  - 软件概述和核心优势
  - 手动模式使用步骤
  - 一键迁移模式说明
  - 还原模式和修复模式详解
  - 常见问题解答（FAQ）

### 步骤 1: 选择路径
- 选择源目录（要迁移的目录）
- 选择目标目录（迁移到的位置）
- 配置大文件阈值和复制线程数
- **修复模式**: 当检测到可修复条件时（目标存在但源符号链接丢失/错误），会显示"修复链接"按钮

### 步骤 2: 扫描分析
- 扫描源目录统计文件数量和大小
- 检查目标磁盘可用空间
- 显示大文件（超过阈值）数量

### 步骤 3: 执行迁移
- 实时显示复制进度和速度
- 查看详细日志输出
- 可随时取消操作

### 步骤 4: 完成
- 查看迁移结果，点击“验证迁移并管理备份…”打开验证窗口，核对原路径、目标位置、实际链接、核对级别及目录属性。
- **优先启动文件夹所属软件，验证关键数据能否正常使用**；打开备份或查看属性不能替代使用验证。勾选实际验证后再确认，基础核对不证明逐字节一致。
- 在验证窗口内选择保留备份或处理备份；处理仍需独立双确认，回收不会追加 Windows 原生删除确认窗口。永久删除第二次确认输入原目录名，不输入备份随机后缀。
- 默认移入回收站，由 Windows Shell 判断当前磁盘是否支持回收、容量是否允许；不使用固定的 GB 阈值。过大、回收站禁用或回收失败时保留未回收项目，不自动改为永久删除，结果区记录实际逐项状态。
- 移入回收站不会立即释放原磁盘空间；清空回收站后才释放相应空间。回收站项目也可能被 Windows 容量策略或“存储感知”清理，不能替代长期备份。
- 未确认验证时，点击“完成”、关闭验证窗口或退出应用会询问是否暂不验证并保留备份；选择“是”才继续原操作，不会记为已验证或自动删除备份。

## 迁移流程

1. **路径验证**: 检查源/目标路径合法性，验证权限
2. **扫描分析**: 统计文件数量、总大小、大文件数
3. **复制文件**: 使用 robocopy 多线程镜像复制
4. **创建链接**: 将源目录重命名为备份，创建符号链接指向目标
5. **健康检查**: 验证符号链接可访问性
6. **用户验收与备份处理**：技术完成后保留原始备份；用户在验证窗口确认实际使用正常，再自行决定保留备份或经过双确认处理备份。

## 安全机制

### 统一四级安全准入

手动模式、一键迁移和磁盘扫描共用同一套策略，结论分四级，L0 优先于任何允许：

| 等级 | 目录类型 | 处理 |
| --- | --- | --- |
| L0 硬拒绝 | 系统核心、启动与恢复目录、卷元数据、高危安全软件与驱动目录、工具自身与活动事务目录、非本地固定 NTFS 卷 | 一律拦截，任何配置或确认都不能绕过 |
| L1 推荐迁移 | 命中内置已验证规则的常用软件数据目录 | 直接放行 |
| L2 原生引导 | 有官方迁移方式或直接建链有结构性风险的软件，如 Docker/WSL2 和云盘同步目录 | 磁盘扫描中不可勾选，显示官方迁移步骤；手动与一键模式给出警告后放行 |
| L3 人工确认 | 不属于 L0 的普通个人数据目录和未收录的软件目录 | 放行；磁盘扫描中需先勾选风险确认 |

### 阻止迁移的目录
- `C:\Windows`
- `C:\Program Files`
- `C:\Program Files (x86)`
- `C:\ProgramData\Microsoft`
- 任意卷根下的 `System Volume Information`、`$Recycle.Bin`、`Boot`、`Recovery`、`EFI`、`Windows.old`
- 驱动器根目录，以及系统容器根目录：`C:\Users`、各用户配置目录本身、`AppData` 及其 `Local` / `Roaming` / `LocalLow` 根、`AppData\Local\Programs` 根
- `Program Files`、`ProgramData` 和用户 `AppData` 下的系统组件子树，如 `WindowsApps`、`Common Files`、`Microsoft`、`Packages`、`Temp`
- 高危安全软件、驱动与凭据目录，如 `Windows Defender`、`DriverStore`、`.ssh`、`.gnupg`
- 工具自身目录、事务状态目录，以及迁移产生的备份和 staging 目录
- 无权访问或无法核实状态的目录
- 其他系统关键目录

### 警告提示的目录
- OneDrive / Dropbox / Google Drive / iCloud Drive 等云盘同步目录：归为 L2。手动与一键模式给出警告后放行；磁盘扫描中不可勾选，提示改用客户端自带的更改位置功能

### 目录权限保留
- 迁移和还原时，复制根目录会冻结源目录的 Owner、Group、访问规则与审计规则，保留每条规则的内容、顺序和来源标志，避免被目标父目录的权限替换。
- 仅复制根目录停止继承目标父目录；子目录和文件仍保留各自的继承规则。原目录及原始备份的权限不修改。
- ACL 核对不会跳过：根目录按权限快照核对，源目录变化、规则增删、权限掩码或允许/拒绝顺序变化仍会阻止操作。已有事务摘要的默认计算方式不变。

### 回滚策略
- 尚未切换原路径时失败：显示“未切换原路径，无需回滚”，不宣称执行过恢复。
- 已核对并完成回滚：显示“已核对并恢复操作前状态”；还原操作回滚后可能恢复的是原符号链接。
- 无法确认恢复完成：保留相关数据并提示核对实际位置，不宣称已恢复，也不自动删除保留副本。

### 硬链接处理
- 磁盘扫描按 MFT 文件号判断目录内的硬链接是否全部闭环在本目录内，目录树用图标标出。
- 闭环在目录内部的硬链接可以迁移：复制时只复制一份实体文件，再在目标盘重建其余硬链接，不多占空间。
- 指向目录外部的硬链接会被拦截并给出外部关联路径示例。强行迁移会断开共享关系，源盘对应空间也释放不了。
- 无法核实归属的硬链接，以及扫描不完整的目录，同样不允许加入迁移清单。
- 迁移后的核对会比较目标盘硬链接组的文件 ID，不一致判为不匹配。

## v1.1 更新摘要

### v1.1.6 – v1.1.7 (2026-11)
`fet/scan-model` 分支在 v1.1.5 之后新增的内容。

- **磁盘扫描模式**：只读解析 NTFS 原始 MFT 生成紧凑 `ScanSnapshot`，失败自动降级为低占用并发目录遍历；按 SSD/HDD 限制并发并降低后台 I/O 优先级。ReFS（含 Dev Drive）支持只读浏览，迁移暂未开放。
- **目录树与大软件目录视图**：大软件目录分“直达列表”和“按路径”两种视角，互不嵌套、无重复计数；目录树默认只显示文件夹，可切换“显示文件”。
- **目录用途说明**：`KnownDirectoryExplainer` 为系统目录、个人文件夹和常见软件、开发、AI 模型目录提供中文用途与迁移建议。
- **统一四级安全准入**：`PathValidator` 收敛委托 `MigrationSafetyPolicy`，手动、一键、扫描三个入口标准一致；新增应用目录归属识别（注册表 + Known Folders）和版本化内置规则库。
- **硬链接处理**：按 MFT FileId 区分内部闭环与指向外部的硬链接。内部闭环的目录可迁移，复制时去重后用 `CreateHardLinkW` 在目标盘重建，不产生空间膨胀；指向外部或无法核实的仍拦截。校验阶段新增硬链接拓扑核对。
- **批量迁移**：计划冻结（`PlanRevision` + `PlanDigest`）、只读进程预检、保守容量核算和串行执行队列，失败即暂停，永不自动删除备份。
- **内置 AI 助手**：支持 BYOK 与本地模型，凭据用 DPAPI 保存；通过共享只读工具底座 `IAiAnalysisToolset` 分析脱敏元数据，零执行权限。建议进入 `AiProposalInbox`，经策略复核后由用户人工采纳。带调用超时、复读检测、隐私清洗和只读载荷明细窗口。
- **全局备份管理中心**：集中收口手动、一键、扫描与还原的全部事务，备份以 `VolumeGuid + FileId` 绑定身份。
- **还原改为独立事务**：还原完成后把原目标目录改名为 `.bak_<事务ID>` 保留为还原备份，验证后再经双确认清理；`restoreKeepTarget` 配置项已移除。
- **一键迁移页重做**：下划线 Tab + 卡片布局，验证状态与备份状态徽章，“已迁移”页签的“全部 / 待验证 / 保留备份 / 异常”筛选，备份处理明细弹窗，错误详情浮窗和可自动展开的日志抽屉。
- **统一错误分类**：`ErrorTypeClassifier` 统一失败提示的图标、配色、文案和解决方案；新增应用图标并统一到主窗口与各对话框。
- **稳定性修复**：历史事务不再误拦备份清理，还原后更换目标盘不再被旧事务卡死，修复入口条件前后端对齐，回收站校验由 O(N²) 降为 O(N)。
- **测试**：新增 `test/MigrationSafetyHarness` 与 `test/vhd`（双虚拟磁盘安全验收环境）和 `test/Phase1UnitTests`。

设计文档（含尚未实现的规划）：[扫描模式与安全迁移设计方案](docs/扫描模式与安全迁移及垃圾清理设计方案.md)、[安全底座分批实现计划](docs/安全底座分批实现计划.md)、[ReFS 符号链接迁移支持规划](docs/ReFS符号链接迁移支持规划.md)。

### v1.1.4 (2025-12-14)
- **🎨 UI现代化设计**：统一全局按钮样式，添加阴影和悬停动画效果；改进Tab控件设计，增加渐变背景和过渡动画；优化文本渲染清晰度和界面层次感。
- **📖 应用内用户指南**：新增完整的用户指南窗口（[UserGuideWindow.xaml](MoveWithSymlinkWPF/Views/UserGuideWindow.xaml)），提供软件概述、使用步骤、常见问题等帮助内容；资源化文本管理（[UserGuideTexts.xaml](MoveWithSymlinkWPF/Resources/UserGuideTexts.xaml)），便于维护和本地化。
- **⚡ 界面交互优化**：去除冗余复制按钮，优化Tab切换体验；调整边框圆角、间距和视觉元素，提升整体专业感。

### v1.1.0 核心功能
- 新增一键迁移模式：读取 `quick-migrate.json` 自动扫描任务，按"未迁移/已迁移"分组展示；顺序执行，单项失败不阻塞；底部统一日志；支持中断续传（`robocopy /Z /ZB`）。
- 手动模式新增还原功能：选择符号链接源目录自动进入"还原模式"（橙色标签），目标路径只读；执行对称的 6 阶段反向流程；完成后可选择删除或保留目标数据（`restoreKeepTarget`）。
- 标记文件与状态管理：引入 `.xinghe-migrate.lock/.done` 与 `.xinghe-reduction.lock/.done`；修复还原后标记文件残留问题，迁移前/还原后清理源目录标记；目录非空检查忽略标记文件。
- 符号链接选择与对话框修复：处理系统对话框自动解析符号链接的问题，支持反向查找并提示使用符号链接路径进入还原模式；建议优先直接选择符号链接路径本身。
- 兼容性与限制：支持 HKCU/HKLM、x64/x86 注册表视图；非管理员提示启用开发者模式；暂不支持网络路径（UNC）。
- 新增与变更文件（节选）：`MoveWithSymlinkWPF/Views/QuickMigratePage.xaml`、`ViewModels/QuickMigrateViewModel.cs`、核心 `ReversibleMigrationService`、`quick-migrate.json` 示例配置。
- 深入文档：`docs/v1.1/快速上手指南.md`、`docs/v1.1/v1.1实现说明.md`、`docs/v1.1/手动模式还原功能使用说明.md`、`docs/v1.1/符号链接选择说明.md`。

## 技术架构

### PowerShell 版本
- 纯 PowerShell 5.1+ 脚本
- 使用 `robocopy` 进行文件复制
- 使用 `mklink` 创建符号链接

### C# GUI 版本（WPF）
- **.NET 10.0 (LTS)** 平台
- **WPF** 框架（支持单文件自包含发布）
- **MVVM 架构** (CommunityToolkit.Mvvm)
- **核心库分离** (MigrationCore) 便于复用
- **Microsoft.Extensions.AI** 接入内置 AI 助手（OpenAI 兼容端点与本地模型）

### 项目结构
```
moveFloder/
├── MigrationCore/              # 核心业务逻辑类库
│   ├── Models/                 # 数据模型
│   └── Services/               # 服务层
│       ├── FileStatsService.cs
│       ├── MigrationService.cs
│       ├── PathValidator.cs
│       ├── SymbolicLinkHelper.cs
│       ├── ReversibleMigrationService.cs
│       ├── MigrationStateDetector.cs
│       ├── QuickMigrateConfigLoader.cs
│       └── RegistryLocatorService.cs
│
├── MoveWithSymlinkWPF/         # WPF GUI 应用
│   ├── ViewModels/             # 视图模型
│   │   ├── MainViewModel.cs
│   │   └── QuickMigrateViewModel.cs
│   ├── Views/                  # 视图
│   │   ├── QuickMigratePage.xaml
│   │   ├── QuickMigratePage.xaml.cs
│   │   ├── UserGuideWindow.xaml         # 用户指南窗口
│   │   ├── UserGuideWindow.xaml.cs
│   │   ├── FolderPickerWindow.xaml
│   │   └── FolderPickerWindow.xaml.cs
│   ├── Resources/              # 资源文件
│   │   ├── UserGuideTexts.xaml          # 用户指南文本资源
│   │   └── README-使用说明资源管理.md
│   ├── Converters/             # XAML 转换器
│   │   └── BooleanConverters.cs
│   ├── MainWindow.xaml         # 主窗口
│   ├── App.xaml                # 应用程序
│   └── Properties/PublishProfiles/
│       ├── win-x64.pubxml
│       └── win-x64-fast.pubxml
│
├── MoveWithSymlink.ps1         # PowerShell CLI 版本
├── publish.ps1                 # 发布脚本（WPF）
├── quick-migrate.json          # 一键迁移配置（可选）
└── run.ps1                     # 启动脚本（WPF）
```

## 注意事项

1. **管理员权限**: 建议以管理员身份运行，或启用 Windows 开发者模式
2. **NTFS 文件系统**: WPF 版要求源盘和目标盘都是本地固定 NTFS 卷，否则按 L0 拒绝。ReFS（含 Dev Drive）、exFAT、FAT32、可移动盘和网络盘不能用于迁移；ReFS 仅支持只读扫描浏览。
3. **磁盘空间**: 确保目标磁盘有足够空间（需源目录大小 + 10% 余量）
4. **云盘目录**: 避免迁移正在同步的云盘目录，可能导致冲突
5. **长路径支持**: 建议启用 Windows 长路径支持（组策略或注册表）
6. **网络路径限制**: 暂不支持 UNC 网络路径

## 启用开发者模式 (可选)

如果不想以管理员身份运行，可以启用 Windows 开发者模式：

1. 打开 **设置** > **隐私和安全性** > **开发者选项**
2. 开启 **开发人员模式**

## 构建说明

### 前置要求
- [.NET 10.0 SDK (x64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)（也可执行 `winget install Microsoft.DotNet.SDK.10` 安装）
- Visual Studio 2022 或更高版本（可选，仅用 IDE 构建时需要）

### 使用 mise（推荐）

安装 [mise](https://mise.jdx.dev/) 和 .NET 10.0 SDK 后，可从仓库根目录或子目录执行以下任务。任务固定在仓库根目录运行，使用 Windows 自带的 `powershell.exe`，不要求额外安装 PowerShell 7。

| 命令 | 用途 |
| --- | --- |
| `mise run dev` | 从当前源码启动 WPF Debug 开发环境，显示调试控制台，不使用已有发布包 |
| `mise run package` | 打包自包含 Release 版本，包含 .NET 运行时 |
| `mise run package:lite` | 打包轻量 Release 版本，运行时需要 .NET 10.0 Desktop Runtime |
| `mise run package:both` | 同时打包自包含和轻量 Release 版本 |
| `mise run package:debug` | 打包带控制台的 Debug 版本，不递增版本号 |

首次运行若提示配置不受信任，先执行 `mise trust mise.toml`。开发时需要创建符号链接等管理员操作，请在管理员终端执行 `mise run dev`。

打包任务复用 `publish.ps1`：正式模式默认递增 patch 版本并同步项目文件；调试模式不递增。参数可通过 `--` 透传：

```powershell
# 使用当前版本打包，不递增版本号（仍会同步项目中的版本字段）
mise run package -- -s

# 查看发布脚本帮助，不执行打包
mise run package -- -Help

# 查看全部任务或预览将执行的命令
mise tasks ls
mise run --dry-run package:both
```

产物位于 `MoveWithSymlinkWPF/bin/publish/`：自包含版在 `win-x64/`，轻量版在 `win-x64-framework-dependent/`，调试版在 `win-x64-debug/`。

### 构建步骤

1. 克隆或下载项目
2. 打开 `MoveWithSymlink.sln`
3. 还原 NuGet 包
4. 构建解决方案 (Ctrl+Shift+B)

```bash
# 使用 CLI 构建
dotnet restore
dotnet build -c Release

# 运行 WPF GUI（开发调试）
cd MoveWithSymlinkWPF
dotnet run -c Release

# 发布为单文件（自包含）
dotnet publish MoveWithSymlinkWPF/MoveWithSymlinkWPF.csproj -p:PublishProfile=win-x64 -c Release
```

## 示例场景

### 场景 1: 迁移游戏目录
将 C 盘的大型游戏迁移到 D 盘，游戏启动器仍能正常识别路径

### 场景 2: 迁移用户数据
将 `C:\Users\YourName\Documents` 迁移到更大的磁盘

### 场景 3: 迁移开发环境缓存
将 `node_modules`、`.gradle` 等大型缓存目录迁移到其他磁盘

## 验收标准

迁移完成后验证:
- ✅ 原路径可正常访问（符号链接有效）
- ✅ 文件读写功能正常
- ✅ 目标目录大小与源目录一致
- ✅ 控制台/日志显示完整信息
- ✅ 失败时自动回滚，原路径保持可用

## 未来增强

- [ ] 过滤规则（忽略缓存/临时文件）
- [ ] 哈希校验模式（可选全量/抽样）
- [ ] 结构化日志导出 (JSON/CSV)
- [ ] 云盘集成检测与同步暂停
- [ ] 空闲时自动执行
- [ ] ReFS 卷符号链接迁移（当前仅支持只读扫描浏览，见 `docs/ReFS符号链接迁移支持规划.md`）

## 许可证

MIT License

## 贡献

欢迎提交 Issue 和 Pull Request！

---

**警告**: 此工具会移动大量文件并创建符号链接，使用前请确保已备份重要数据！


## 修复模式详解

### 什么时候需要修复？

当出现以下情况时，可以使用修复模式来重建符号链接：
- 符号链接被意外删除
- 符号链接被替换为普通目录
- 符号链接指向错误的目标
- 复制完成但未成功创建符号链接

### 修复模式的优势

- ✅ **不复制数据**: 基于现有目标目录直接重建符号链接，节省时间
- ✅ **自动备份**: 如果源路径是非空目录，会自动备份为 `.bak_时间戳`
- ✅ **备份标记**: 自动创建 `.xinghe-repair.info` 记录修复信息（时间戳、原始路径等）
- ✅ **安全删除**: 备份清理时移入回收站（仅限修复模式），可恢复
- ✅ **安全回滚**: 失败时自动还原到修复前状态
- ✅ **智能检测**: 自动识别符号链接状态并采取相应操作

### 使用修复模式

**PowerShell CLI:**
```powershell
.\MoveWithSymlink.ps1 -Source "C:\App\Data" -Target "D:\Backup\AppData" -Repair
```

**WPF GUI:**
- 手动模式：选择源路径和目标路径后，如果满足修复条件会显示"修复链接"按钮

### 修复流程

1. **验证路径**: 检查目标目录是否存在，路径关系是否合法
2. **处理源路径**:
   - 源不存在 → 直接创建符号链接
   - 源是符号链接 → 检查指向，错误则重建
   - 源是空目录 → 删除后创建符号链接
   - 源是非空目录 → 备份后创建符号链接，并创建 `.xinghe-repair.info` 标记文件
3. **验证链接**: 确认符号链接创建成功且可访问
4. **清理备份**: GUI 模式会询问是否将备份移入回收站（可恢复）

### 备份标记文件说明

修复模式创建备份时，会在备份目录中生成 `.xinghe-repair.info` 文件，记录以下信息：
- **修复时间戳**: 修复操作的执行时间
- **原始目录名称**: 源文件夹的原名
- **原始完整路径**: 源文件夹的完整路径
- **目标路径**: 符号链接指向的目标位置
- **备份路径**: 备份目录的完整路径
- **备份创建时间**: 备份创建的时间戳
- **修复模式**: 固定为 "SymbolicLinkRecreation"
- **备注信息**: 说明备份的创建方式

此标记文件永久保留，不影响任何逻辑判断，仅供用户日后查看和判断备份来源。

## 常见问题（WPF 版本）

- **如何获取帮助**？点击主界面右上角的 **[❓ 使用说明]** 按钮，查看完整的用户指南。
- 为什么 EXE 文件较大？自包含发布包含 .NET 运行时与依赖，通常 60–80MB。
- 没有安装 .NET 可以运行吗？完整版（自包含发布）无需预装 .NET；轻量版（框架依赖）需安装 [.NET 10.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)；从源码构建/运行需安装 [.NET 10.0 SDK (x64)](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)。
- 是否需要管理员权限？是。应用含 UAC 清单，启动时会自动申请管理员权限。
- 修复模式和还原模式有什么区别？修复模式不复制数据，只重建符号链接；还原模式会将数据从目标复制回源，并删除符号链接。
- 双击 EXE 与运行 `run.ps1` 有何区别？功能相同，脚本提供额外提示与错误处理，直接双击更简便。
- 磁盘扫描里有些目录为什么勾选不了？L0 目录禁止迁移；L2 目录建议用软件自带的迁移方式，点“详情弹窗”查看官方步骤；含外部硬链接或扫描不完整的目录也会被拦截，原因见弹出的提示。
- AI 助手会动我的文件吗？不会。AI 只能通过只读工具读取脱敏后的目录元数据，不能执行迁移、清理或删除。它的建议要先经安全策略复核，再由你人工采纳。

## 开源致谢

- 内置 AI 助手抽屉中的**思考等级（`reasoning_effort`）像素点阵能量滑块**，其交互体验与三阶段像素火焰动效灵感参考自开源项目 [dsh-effort-slider](https://github.com/2768651338/dsh-effort-slider)（BSD-3-Clause）及其上游 [dsh-ui-web](https://github.com/CAPTAIN1275/dsh-ui-web) `aurora` 皮肤（BSD-3-Clause），在此谨表感谢！本项目采用纯 C# .NET 10 原生 WPF（`WriteableBitmap` + `CompositionTarget.Rendering`）独立重写，与上述项目无隶属关系。

