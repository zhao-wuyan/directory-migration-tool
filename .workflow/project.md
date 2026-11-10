# Project: 目录迁移工具

## What This Is

面向 Windows 用户的目录迁移工具，通过 Robocopy 复制数据并在原路径建立目录符号链接，保持应用原有访问路径。已有 C# WPF GUI 和 PowerShell CLI，支持手动迁移、一键迁移、还原和链接修复。

## Core Value

安全地迁移目录并保持原路径可用；复制、链接创建、回滚与备份清理必须保护用户数据。

## Requirements

### Validated

以下为 README 与现有项目结构记录的既有能力，不代表本次重新运行验证：

- [x] 手动迁移：路径验证、扫描、复制、链接创建与健康检查。
- [x] 基于 quick-migrate.json 的批量迁移与还原。
- [x] 使用现有目标目录修复源路径符号链接。
- [x] 复制进度展示，以及自包含／框架依赖发布。

### Active

- [x] 完成现有代码库的 Maestro 本地知识接入与初始化校验，不新增业务功能。

### Out of Scope

- 业务重构、功能开发与 roadmap 划分：用户本次仅请求 init。
- 执行真实目录迁移、还原、修复或删除备份：初始化不触碰用户数据。
- 本次 Git 提交：用户选择本地知识接入。

## Context

现有解决方案为 MoveWithSymlink.sln。MigrationCore 承载业务模型和服务；MoveWithSymlinkWPF 承载 MVVM 界面。README、两个 csproj 和项目指导提供初始化证据；版本以 version.json 及发布脚本为准，不从 README 推断当前版本。

### 初始化选择

| # | Decision | Choice | Source | Confidence |
|---|----------|--------|--------|------------|
| 1 | 项目类型 | 现有代码库接入 | code：现有 sln、csproj、README | high |
| 2 | 技术栈 | C#、.NET 8、WPF、CommunityToolkit.Mvvm | code：两个 csproj | high |
| 3 | 目录组织 | 保留 MigrationCore 与 MoveWithSymlinkWPF，不移动文件 | code：现有分层；init 边界 | high |
| 4 | 工作流偏好 | 建立代码索引；research、reflection、自动同步开启；commit_docs 关闭 | user：本地知识接入 | high |
| 5 | 初始化范围 | 配置、现有约定、领域词汇；不运行迁移、不规划新增功能 | user：init；选择项说明 | high |

## Constraints

- **平台**：Windows，现有目标框架 net8.0-windows10.0.19041.0，UI 最低平台版本 10.0.17763.0。
- **文件安全**：迁移及符号链接操作涉及管理员权限和用户数据，初始化仅写 .workflow/。
- **兼容性**：保留现有 CLI、WPF、配置与发布行为。
- **工作流**：后续 roadmap 与 plan 保持确认门禁；本次不自动创建里程碑。

## Tech Stack

- **Language**: C#、XAML、PowerShell。
- **Framework**: .NET 8、WPF、CommunityToolkit.Mvvm 8.2.2、Microsoft.Xaml.Behaviors.Wpf 1.1.122。
- **Database**: 当前 csproj 未声明数据库依赖；配置使用 JSON。

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| 本地知识接入，关闭本次文档提交 | 用户明确选择，保留现有代码 | 已配置 |
| 沿用现有技术栈与目录布局 | 初始化不承担架构或业务变更 | 已采用 |

## Stakeholders

- 使用目录迁移工具的 Windows 用户。
- 维护 C# WPF、核心迁移服务及 PowerShell 发布流程的开发者。

---
*Last updated: 2026-10-25 after initialization*
