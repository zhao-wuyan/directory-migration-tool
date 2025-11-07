# 目录迁移工具 (Directory Migration Tool) · WPF 版本

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

## 系统要求

- Windows 10/11 (x64)
- .NET 8.0 Desktop Runtime（仅框架依赖版本需要）
- 管理员权限（或启用开发者模式）
- 目标推荐 NTFS 文件系统

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

# 发布轻量版（框架依赖，约 20+ MB，需要系统安装 .NET 8.0）
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

## GUI 使用向导

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
- 查看迁移结果
- 验证符号链接是否创建成功

## 迁移流程

1. **路径验证**: 检查源/目标路径合法性，验证权限
2. **扫描分析**: 统计文件数量、总大小、大文件数
3. **复制文件**: 使用 robocopy 多线程镜像复制
4. **创建链接**: 将源目录重命名为备份，创建符号链接指向目标
5. **健康检查**: 验证符号链接可访问性
6. **清理备份**: 验证通过后删除备份目录

## 安全机制

### 阻止迁移的目录
- `C:\Windows`
- `C:\Program Files`
- `C:\Program Files (x86)`
- `C:\ProgramData\Microsoft`
- 其他系统关键目录

### 警告提示的目录
- OneDrive/Dropbox/Google Drive 等云盘同步目录

### 回滚策略
- 任何步骤失败自动回滚
- 删除已创建的符号链接
- 还原原始目录

## v1.1 更新摘要

- 新增一键迁移模式：读取 `quick-migrate.json` 自动扫描任务，按“未迁移/已迁移”分组展示；顺序执行，单项失败不阻塞；底部统一日志；支持中断续传（`robocopy /Z /ZB`）。
- 手动模式新增还原功能：选择符号链接源目录自动进入“还原模式”（橙色标签），目标路径只读；执行对称的 6 阶段反向流程；完成后可选择删除或保留目标数据（`restoreKeepTarget`）。
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
- **.NET 8.0** 平台
- **WPF** 框架（支持单文件自包含发布）
- **MVVM 架构** (CommunityToolkit.Mvvm)
- **核心库分离** (MigrationCore) 便于复用

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
│   ├── Converters/             # XAML 转换器
│   │   └── BooleanConverters.cs
│   ├── Views/
│   │   ├── QuickMigratePage.xaml
│   │   └── QuickMigratePage.xaml.cs
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
2. **NTFS 文件系统**: 目标路径建议使用 NTFS，以保留完整文件属性
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
- Visual Studio 2022 或更高版本
- .NET 8.0 SDK

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

- 为什么 EXE 文件较大？自包含发布包含 .NET 运行时与依赖，通常 60–80MB。
- 没有安装 .NET 可以运行吗？可以，自包含发布无需预装 .NET。
- 是否需要管理员权限？是。应用含 UAC 清单，启动时会自动申请管理员权限。
- 修复模式和还原模式有什么区别？修复模式不复制数据，只重建符号链接；还原模式会将数据从目标复制回源，并删除符号链接。
- 双击 EXE 与运行 `run.ps1` 有何区别？功能相同，脚本提供额外提示与错误处理，直接双击更简便。
