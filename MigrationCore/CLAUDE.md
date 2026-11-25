# MigrationCore - 核心业务逻辑类库

> 模块路径：[根目录](../CLAUDE.md) > **MigrationCore**
> 最后更新：2025-12-15 01:06:52

---

## 变更记录 (Changelog)

### 2025-12-15
- 初始化模块文档
- 记录核心服务和数据模型

---

## 模块职责

**MigrationCore** 是一个无状态的 .NET 类库，提供目录迁移、还原、修复、扫描等核心业务逻辑。该模块不依赖任何 UI 框架，可被 WPF GUI、CLI 工具或其他应用程序引用。

### 核心功能
1. **迁移服务 (MigrationService)**：执行目录迁移流程（扫描、复制、创建符号链接）
2. **可逆迁移服务 (ReversibleMigrationService)**：支持还原操作（从符号链接恢复为真实目录）
3. **修复服务 (RepairService)**：基于现有目标目录重建符号链接，无需复制数据
4. **路径验证 (PathValidator)**：检测系统关键目录、云盘目录，防止误操作
5. **符号链接助手 (SymbolicLinkHelper)**：跨注册表视图（x64/x86）检测和操作符号链接
6. **文件统计服务 (FileStatsService)**：扫描目录并统计文件数量、大小、大文件数
7. **配置加载器 (QuickMigrateConfigLoader)**：解析一键迁移配置文件（quick-migrate.json）
8. **注册表定位服务 (RegistryLocatorService)**：从注册表解析应用安装路径
9. **回收站助手 (RecycleBinHelper)**：安全删除文件到回收站
10. **文件锁检测 (HandleHelper, SimpleFileLockDetector)**：检测文件被占用情况

---

## 入口与启动

### 项目文件
- **文件**: `MigrationCore.csproj`
- **目标框架**: `net10.0-windows10.0.19041.0`
- **输出类型**: 类库（.dll）
- **平台**: x64, x86, AnyCPU

### 依赖包
```xml
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
<PackageReference Include="Microsoft.VisualBasic" Version="10.3.0" />
```

### 使用示例

```csharp
using MigrationCore.Services;
using MigrationCore.Models;

// 1. 扫描源目录
var fileStatsService = new FileStatsService();
var stats = await fileStatsService.ScanDirectoryAsync(
    sourcePath: @"C:\Users\YourName\Pictures",
    largeFileThresholdMB: 1024,
    progress: new Progress<string>(msg => Console.WriteLine(msg)),
    cancellationToken: CancellationToken.None
);

Console.WriteLine($"文件数: {stats.TotalFiles}");
Console.WriteLine($"总大小: {stats.TotalSizeBytes / 1024 / 1024} MB");

// 2. 执行迁移
var migrationService = new MigrationService();
var config = new MigrationConfig
{
    SourcePath = @"C:\Users\YourName\Pictures",
    TargetPath = @"D:\Data\Pictures",
    LargeFileThresholdMB = 1024,
    RobocopyThreads = 8
};

var result = await migrationService.MigrateAsync(
    config,
    progress: new Progress<MigrationProgress>(p =>
    {
        Console.WriteLine($"{p.Phase}: {p.ProgressPercent}%");
    }),
    cancellationToken: CancellationToken.None
);

if (result.IsSuccess)
{
    Console.WriteLine("迁移成功！");
}
else
{
    Console.WriteLine($"迁移失败: {result.ErrorMessage}");
}
```

---

## 对外接口

### 核心服务类

#### 1. MigrationService
**职责**: 执行完整的迁移流程

**公共方法**:
- `Task<MigrationResult> MigrateAsync(MigrationConfig config, IProgress<MigrationProgress> progress, CancellationToken ct)`
  - 执行迁移流程：扫描 → 复制 → 创建符号链接 → 健康检查 → 清理备份

#### 2. ReversibleMigrationService
**职责**: 支持还原操作（从符号链接恢复为真实目录）

**公共方法**:
- `Task<MigrationResult> RestoreAsync(MigrationConfig config, IProgress<MigrationProgress> progress, CancellationToken ct, bool keepTarget = false)`
  - 执行还原流程：验证 → 复制回源 → 删除符号链接 → 健康检查 → 清理标记
  - `keepTarget`: 是否保留目标目录数据

#### 3. RepairService
**职责**: 修复模式 - 基于现有目标目录重建符号链接

**公共方法**:
- `Task<MigrationResult> RepairAsync(MigrationConfig config, IProgress<MigrationProgress> progress, CancellationToken ct)`
  - 执行修复流程：验证目标存在 → 备份源（如果非空）→ 创建符号链接 → 验证

#### 4. PathValidator
**职责**: 路径安全验证

**公共方法**:
- `bool IsDangerousPath(string path)`: 检查是否为系统关键目录
- `bool IsCloudSyncedPath(string path)`: 检查是否为云盘同步目录
- `(bool IsValid, string Message) ValidateMigrationPaths(string sourcePath, string targetPath)`: 综合验证源和目标路径

**危险路径列表**:
```csharp
C:\Windows, C:\Program Files, C:\Program Files (x86),
C:\ProgramData\Microsoft, C:\System Volume Information,
C:\$Recycle.Bin, C:\Recovery
```

#### 5. SymbolicLinkHelper
**职责**: 符号链接检测和操作

**公共方法**:
- `bool IsSymbolicLink(string path)`: 检查路径是否为符号链接
- `string? GetSymbolicLinkTarget(string path)`: 获取符号链接指向的目标路径
- `bool CreateSymbolicLink(string linkPath, string targetPath)`: 创建目录符号链接（需管理员权限）

**特性**:
- 支持跨注册表视图检测（HKCU、HKLM、x64、x86）
- 自动处理路径规范化（去除尾部斜杠）

#### 6. FileStatsService
**职责**: 目录扫描和统计

**公共方法**:
- `Task<FileStats> ScanDirectoryAsync(string sourcePath, int largeFileThresholdMB, IProgress<string> progress, CancellationToken ct)`
  - 扫描目录并统计文件数、总大小、大文件数

**返回结构**:
```csharp
public class FileStats
{
    public int TotalFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public int LargeFilesCount { get; set; }
}
```

#### 7. QuickMigrateConfigLoader
**职责**: 解析一键迁移配置文件

**公共方法**:
- `(bool Success, QuickMigrateConfig? Config, string ErrorMessage) LoadConfig(string jsonContent)`
  - 解析 JSON 配置并验证结构

**配置结构**:
```json
{
  "defaults": {
    "largeFileThresholdMB": 1024,
    "robocopyThreads": 8,
    "targetStrategy": "unified",
    "unifiedTargetRoot": "D:\\Migrated",
    "restoreKeepTarget": false
  },
  "profiles": [
    {
      "id": "NovaStudio",
      "name": "智玲同学",
      "locator": {
        "type": "registryDisplayIcon",
        "hive": "HKCU",
        "keyPath": "Software\\...",
        "valueName": "DisplayIcon"
      },
      "items": [
        {
          "displayName": "comfyui模型目录",
          "relativePath": "comfyui\\models",
          "enabled": true
        }
      ]
    }
  ],
  "standaloneSources": [
    {
      "displayName": "模型管理",
      "absolutePath": "C:\\LingLong\\.llmodels",
      "enabled": true
    }
  ]
}
```

#### 8. RegistryLocatorService
**职责**: 从注册表解析应用安装路径

**公共方法**:
- `string? LocatePath(string hive, string keyPath, string valueName, RegistryView view = RegistryView.Default)`
  - 从注册表读取路径（支持 DisplayIcon 自动提取目录）

---

## 关键依赖与配置

### 内部依赖
- **CopyOperationExecutor**: 封装 robocopy 命令行调用
- **MigrationWorkflowHelper**: 迁移流程的通用辅助方法
- **MigrationStateDetector**: 检测迁移状态（未迁移/已迁移/可修复）

### 外部工具依赖
1. **robocopy**: Windows 内置工具，用于文件复制
   - 参数: `/MIR /MT:8 /R:3 /W:5 /NP /NDL /NC /BYTES /TEE`
2. **mklink**: Windows 内置命令，用于创建符号链接
   - 参数: `mklink /D "源路径" "目标路径"`
3. **handle64.exe**: Sysinternals 工具，用于检测文件锁（可选）

### 配置参数
```csharp
public class MigrationConfig
{
    public string SourcePath { get; set; }           // 源目录路径
    public string TargetPath { get; set; }           // 目标目录路径
    public int LargeFileThresholdMB { get; set; }    // 大文件阈值（MB）
    public int RobocopyThreads { get; set; }         // robocopy 线程数
    public int SampleMilliseconds { get; set; }      // 进度采样间隔（ms）
}
```

---

## 数据模型

### 核心模型类

#### 1. FileStats
**文件**: `Models/FileStats.cs`

```csharp
public class FileStats
{
    public int TotalFiles { get; set; }         // 总文件数
    public long TotalSizeBytes { get; set; }    // 总大小（字节）
    public int LargeFilesCount { get; set; }    // 大文件数量
}
```

#### 2. MigrationConfig
**文件**: `Models/MigrationConfig.cs`

```csharp
public class MigrationConfig
{
    public string SourcePath { get; set; }
    public string TargetPath { get; set; }
    public int LargeFileThresholdMB { get; set; } = 1024;
    public int RobocopyThreads { get; set; } = 8;
    public int SampleMilliseconds { get; set; } = 1000;
}
```

#### 3. MigrationProgress
**文件**: `Models/MigrationProgress.cs`

```csharp
public class MigrationProgress
{
    public string Phase { get; set; }              // 当前阶段
    public string Message { get; set; }            // 进度消息
    public int ProgressPercent { get; set; }       // 百分比（0-100）
    public string DetailedLog { get; set; }        // 详细日志
}
```

#### 4. MigrationResult
**文件**: `Models/MigrationResult.cs`

```csharp
public class MigrationResult
{
    public bool IsSuccess { get; set; }            // 是否成功
    public string ErrorMessage { get; set; }       // 错误消息
    public string DetailedLog { get; set; }        // 详细日志
    public bool WasRolledBack { get; set; }        // 是否已回滚
}
```

#### 5. QuickMigrateConfig
**文件**: `Models/QuickMigrateConfig.cs`

包含一键迁移配置的完整结构定义：
- `Defaults`: 默认配置（阈值、线程数、目标策略）
- `Profiles`: 基于注册表定位的应用配置列表
- `StandaloneSources`: 独立的绝对路径迁移任务

#### 6. QuickMigrateTask
**文件**: `Models/QuickMigrateTask.cs`

```csharp
public class QuickMigrateTask
{
    public string TaskId { get; set; }             // 任务唯一标识
    public string DisplayName { get; set; }        // 显示名称
    public string SourcePath { get; set; }         // 源路径
    public string TargetPath { get; set; }         // 目标路径
    public bool IsEnabled { get; set; }            // 是否启用
    public string Status { get; set; }             // 状态（未迁移/已迁移）
}
```

---

## 测试与质量

### 当前状态
- **单元测试**: 无
- **集成测试**: 通过 WPF GUI 和 PowerShell CLI 手动测试
- **代码覆盖率**: 未测量

### 测试建议
1. **路径验证测试**: 验证 `PathValidator` 的危险路径检测逻辑
2. **符号链接测试**: 测试 `SymbolicLinkHelper` 的跨视图兼容性
3. **配置解析测试**: 测试 `QuickMigrateConfigLoader` 的 JSON 解析和错误处理
4. **模拟测试**: 使用临时目录模拟迁移流程（无需真实 robocopy）

---

## 常见问题 (FAQ)

### Q1: 为什么需要管理员权限？
**A**: 创建目录符号链接（`mklink /D`）默认需要管理员权限。可通过启用 Windows 开发者模式绕过此限制。

### Q2: 支持哪些文件系统？
**A**: 建议使用 NTFS 文件系统。FAT32 不支持符号链接。

### Q3: 如何处理大文件？
**A**: `FileStatsService` 会统计超过阈值的大文件数量，但不会特殊处理。robocopy 会自动处理大文件复制。

### Q4: 修复模式和还原模式有什么区别？
**A**:
- **修复模式**: 目标目录已存在，重建符号链接，不复制数据
- **还原模式**: 将数据从目标复制回源，删除符号链接，恢复为真实目录

### Q5: 如何取消正在执行的迁移？
**A**: 传入 `CancellationToken` 并调用 `Cancel()`。服务会在安全点停止并自动回滚。

---

## 相关文件清单

```
MigrationCore/
├── Models/                                # 数据模型
│   ├── FileStats.cs
│   ├── MigrationConfig.cs
│   ├── MigrationProgress.cs
│   ├── MigrationResult.cs
│   ├── QuickMigrateConfig.cs
│   └── QuickMigrateTask.cs
├── Services/                              # 业务服务
│   ├── CopyOperationExecutor.cs           # robocopy 执行器
│   ├── FileStatsService.cs                # 目录扫描服务
│   ├── HandleHelper.cs                    # 文件锁检测（Sysinternals handle）
│   ├── MigrationService.cs                # 迁移服务
│   ├── MigrationStateDetector.cs          # 状态检测服务
│   ├── MigrationWorkflowHelper.cs         # 流程辅助方法
│   ├── PathValidator.cs                   # 路径验证服务
│   ├── QuickMigrateConfigLoader.cs        # 配置加载器
│   ├── RecycleBinHelper.cs                # 回收站助手
│   ├── RegistryLocatorService.cs          # 注册表定位服务
│   ├── RepairService.cs                   # 修复服务
│   ├── ReversibleMigrationService.cs      # 可逆迁移服务
│   ├── SimpleFileLockDetector.cs          # 简单文件锁检测
│   └── SymbolicLinkHelper.cs              # 符号链接助手
└── MigrationCore.csproj                   # 项目文件
```

---

**最后更新**: 2025-12-15 01:06:52
**文档版本**: 1.0.0
**模块版本**: 与根项目同步 v1.1.5
