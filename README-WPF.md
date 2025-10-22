# 🎉 目录迁移工具 - WPF 单文件版本

## ✅ 已完成！单文件独立可执行文件

恭喜！您的应用程序已成功转换为 **WPF** 版本，并打包为**单文件独立可执行文件**！

---

## 📦 生成的文件

### 主程序
- **文件**: `MoveWithSymlinkWPF.exe`
- **位置**: `MoveWithSymlinkWPF\bin\publish\win-x64\`
- **大小**: 74.46 MB
- **类型**: 单文件独立可执行文件

### 特性
✅ **完全自包含** - 包含 .NET 8.0 运行时  
✅ **单个文件** - 无需额外的 DLL 文件  
✅ **无需安装** - 直接运行，无需安装 .NET  
✅ **跨机器可用** - 可复制到其他 Windows 10/11 (x64) 电脑直接运行  

---

## 🚀 如何使用

### 方法 1：直接运行
```powershell
.\MoveWithSymlinkWPF\bin\publish\win-x64\MoveWithSymlinkWPF.exe
```

### 方法 2：使用发布脚本
```powershell
# 重新发布（如果修改了代码）
.\publish-wpf.ps1
```

### 方法 3：分发给其他用户
只需复制 `MoveWithSymlinkWPF.exe` 这一个文件，其他用户可直接运行！

---

## 🎯 应用程序功能

### 界面流程

**步骤 1 - 选择路径**
- 选择源目录（要迁移的目录）
- 选择目标目录（迁移到的位置）
- 配置高级选项（大文件阈值、线程数）
- 自动验证路径有效性

**步骤 2 - 扫描分析**
- 扫描源目录所有文件
- 统计文件数量和大小
- 识别大文件
- 检查目标磁盘空间

**步骤 3 - 执行迁移**
- 阶段 1: 创建备份标记
- 阶段 2: 使用 Robocopy 复制大文件
- 阶段 3: 验证大文件
- 阶段 4: 复制小文件
- 阶段 5: 完整性验证
- 阶段 6: 创建符号链接
- 实时显示进度和日志

**步骤 4 - 完成**
- 显示迁移结果
- 查看详细统计信息

### 核心功能
- ✅ 智能文件分类（大文件/小文件）
- ✅ 多线程并行复制
- ✅ 完整性验证
- ✅ 自动创建符号链接
- ✅ 错误处理和自动回滚
- ✅ 实时进度和日志

---

## 💻 系统要求

### 操作系统
- Windows 10 版本 1809 (Build 17763) 或更高
- Windows 11（推荐）
- 仅支持 x64 架构

### 权限要求
创建符号链接需要以下之一：
- **管理员权限**（推荐）
- 启用 **Windows 开发者模式**

如何启用开发者模式：
```
设置 → 更新和安全 → 开发者选项 → 开发人员模式
```

---

## 🔧 重新构建/发布

如果您需要修改代码后重新发布：

### 快速发布
```powershell
.\publish-wpf.ps1
```

### 手动步骤
```powershell
# 1. 构建项目
dotnet build MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj -c Release

# 2. 发布为单文件
dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj -p:PublishProfile=win-x64 -c Release

# 3. 输出位置
# MoveWithSymlinkWPF\bin\publish\win-x64\MoveWithSymlinkWPF.exe
```

---

## 📂 项目结构

```
moveFloder/
├── MigrationCore/              # 核心迁移逻辑库
│   ├── Models/                 # 数据模型
│   └── Services/               # 服务类
│       ├── FileStatsService.cs
│       ├── MigrationService.cs
│       ├── PathValidator.cs
│       └── SymbolicLinkHelper.cs
│
├── MoveWithSymlinkWPF/         # ✨ WPF GUI 应用
│   ├── ViewModels/             # MVVM ViewModel
│   │   └── MainViewModel.cs
│   ├── Converters/             # 值转换器
│   │   └── BooleanConverters.cs
│   ├── MainWindow.xaml         # 主界面
│   └── Properties/
│       └── PublishProfiles/
│           └── win-x64.pubxml  # 发布配置
│
├── MoveWithSymlink.ps1         # PowerShell 脚本版本
├── publish-wpf.ps1             # WPF 发布脚本
└── WPF版本说明.md              # 详细说明
```

---

## 🆚 版本对比

| 版本 | 文件形式 | 大小 | UI 框架 | 推荐场景 |
|------|---------|------|---------|----------|
| **WPF GUI** | 单文件 EXE | ~75 MB | WPF | ⭐ 分发给其他用户 |
| WinUI 3 GUI | 多文件 | ~65 MB | WinUI 3 | 最现代化 UI |
| PowerShell | 单文件脚本 | <1 MB | 命令行 | 技术用户/自用 |

**推荐使用 WPF 版本**用于分发，因为它是真正的单文件 EXE！

---

## ❓ 常见问题

### Q: 为什么文件这么大（75MB）？
A: 因为包含了完整的 .NET 8.0 运行时和所有依赖库，这样用户无需安装任何东西就能运行。

### Q: 可以在没有安装 .NET 的电脑上运行吗？
A: 可以！这就是"自包含"的意义，所有需要的运行时都已打包在 EXE 中。

### Q: 为什么要转换为 WPF？
A: WinUI 3 对单文件发布有限制，WPF 是成熟稳定的框架，完美支持单文件打包。

### Q: 功能有区别吗？
A: 没有！所有版本共享相同的 `MigrationCore` 核心库，功能完全相同。

### Q: 运行时需要管理员权限吗？
A: 只有在创建符号链接时需要管理员权限，或者启用 Windows 开发者模式。

---

## 📝 技术栈

- **UI 框架**: WPF (Windows Presentation Foundation)
- **MVVM 工具包**: CommunityToolkit.Mvvm 8.2.2
- **运行时**: .NET 8.0
- **核心逻辑**: 自定义 MigrationCore 库
- **文件操作**: Robocopy + Windows 符号链接 API

---

## 🎊 成功！

您现在拥有一个：
- ✅ 功能完整的目录迁移工具
- ✅ 现代化的 WPF 图形界面
- ✅ 真正的单文件独立可执行程序
- ✅ 可以分发给任何 Windows 10/11 x64 用户

**立即使用**: 双击 `MoveWithSymlinkWPF.exe` 即可开始！

---

**开发时间**: 2025-10-22  
**版本**: 1.0.0  
**框架**: .NET 8.0 + WPF

