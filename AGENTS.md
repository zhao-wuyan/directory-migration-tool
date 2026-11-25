# AGENTS.md

This file provides guidance to agents when working with code in this repository.

## 项目概述

这是一个用于Windows系统的目录迁移工具，使用符号链接技术实现目录迁移。项目采用.NET 10.0 + WPF技术栈，支持手动迁移和一键迁移两种模式。

## 构建和运行命令

### 运行应用
- `.\run.ps1` - 智能选择最佳可用版本运行（推荐）
- `cd MoveWithSymlinkWPF && dotnet run -c Release` - 直接运行项目

### 构建和发布
- `.\publish.ps1` - 发布自包含版本（默认，70-100MB）
- `.\publish.ps1 -Mode lite` - 发布框架依赖版本（需.NET 10.0运行时）
- `.\publish.ps1 -Mode both` - 同时发布两个版本
- `.\publish.ps1 -Mode debug -s` - 发布带控制台调试版本（不增加版本号）
- `.\publish.ps1 -s` - 发布但不增加版本号

### 清理和测试
- `.\clean-build.ps1` - 清理所有构建文件
- `.\clean-build.ps1 -KeepPublish` - 保留发布文件
- `.\test\test-publish.ps1` - 对比两种发布模式

## 项目架构和关键组件

### 核心项目结构
- `MigrationCore/` - 核心业务逻辑库
  - `Models/` - 数据模型（MigrationConfig、MigrationProgress等）
  - `Services/` - 核心服务（MigrationService、SymbolicLinkHelper等）
- `MoveWithSymlinkWPF/` - WPF用户界面
  - `ViewModels/` - MVVM视图模型
  - `Views/` - 用户界面
  - `Services/` - UI相关服务

### 关键服务类
- `MigrationService` - 手动迁移模式（6阶段流程）
- `ReversibleMigrationService` - 一键迁移模式
- `CopyOperationExecutor` - 文件复制操作执行器（使用Robocopy）
- `SymbolicLinkHelper` - 符号链接创建和检测
- `MigrationWorkflowHelper` - 通用工作流程辅助方法

## 重要项目特定约定

### 版本管理
- 版本号存储在 `version.json` 中，发布时自动递增patch版本
- `MoveWithSymlinkWPF.csproj` 中的版本号会与 `version.json` 同步

### 文件复制策略
- 使用Robocopy进行文件复制，支持多线程和进度监控
- 复制进度映射到总进度的10%-90%范围
- 使用指数移动平均算法平滑显示复制速度

### 符号链接处理
- 优先使用P/Invoke创建符号链接，失败时回退到cmd mklink命令
- 符号链接创建前会备份原目录（时间戳命名）
- 使用ReparsePoint属性检测符号链接

### 调试约定
- Debug模式下使用控制台输出详细信息
- 生产发布版本禁用调试符号以减少体积
- 可发布带控制台的调试版本进行问题排查

## 关键配置文件

- `version.json` - 版本号管理
- `quick-migrate.json` - 一键迁移配置（作为嵌入资源）
- `MoveWithSymlinkWPF/Properties/PublishProfiles/` - 发布配置文件

## 发布配置

- 自包含版本：包含完整.NET 10.0运行时，单文件发布
- 框架依赖版本：需要系统安装.NET 10.0 Desktop Runtime
- 发布输出目录：`MoveWithSymlinkWPF/bin/publish/`

## 代码风格注意点

- 启用Nullable引用类型和ImplicitUsings
- 使用CommunityToolkit.Mvvm实现MVVM模式
- 中文注释和UI文本为主
- 使用条件编译指令 `#if DEBUG` 包围调试代码

## 重要测试注意事项

- 应用需要管理员权限才能创建符号链接
- 测试符号链接功能时注意检查开发者模式设置
- 复制大文件时注意Robocopy线程数配置（默认8线程）

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **move-folder** (2219 symbols, 3917 relationships, 192 execution flows).

> Index stale? Run `node .gitnexus/run.cjs analyze --index-only`. No `.gitnexus/run.cjs`? Bootstrap with `npx`, `bunx`, or `pnpm dlx`; e.g. `bunx gitnexus@latest analyze` (#1939).
> On query/context/impact/cypher object results, read staleness.status and branch/lastCommit. Re-analyze only for behind or diverged — current is clone HEAD, not main.

## Always Do

- **MUST run impact before editing.** Use `impact({target: "symbolName", direction: "upstream"})` or `node .gitnexus/run.cjs impact "symbolName" --direction upstream --repo .`; report callers/processes/risk. Never substitute grep for graph analysis.
- **MUST analyze changes before commit.** Run `detect_changes({scope: "all"})` or `node .gitnexus/run.cjs detect-changes --scope all --repo .`. `partial: true` or `truncated: true` is not a clean check — a zero means unseen, not unaffected; re-run it. For regression review, fetch target branch "main" from its remote; compute its merge base with the reviewed HEAD and pass that SHA to `detect_changes({scope: "compare", base_ref: "<merge-base SHA>"})` or `node .gitnexus/run.cjs detect-changes --scope compare --base-ref "<merge-base SHA>" --repo .`.
- MUST warn on HIGH/CRITICAL `risk` pre-edit; never use `riskSharedAxes` to waive a HIGH/CRITICAL `risk` warning. Compare File/symbol: MCP File omits axes; Graph-RAG expands File.
- **MUST treat `risk: UNKNOWN` as unresolved, not as low.** An empty caller set is not evidence the symbol is unused — it can also mean the callers are not resolvable by the index (plain-object property access, dynamic dispatch, cross-language calls). `impact` pairs `UNKNOWN` with a `riskNote` saying so. Confirm with a text search before treating the symbol as safe to change or delete; do not proceed on the strength of a zero.
- **MUST use `query({search_query: "concept"})` for concepts/flows, `context({name: "symbolName"})` for a named symbol, or `impact` for blast radius, on read-only callers, dependencies, imports, or execution flow.** Graph first; text search only for empty/`UNKNOWN`/literals.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method before MCP/CLI impact analysis.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis, and never read `UNKNOWN` as an all-clear — it means the walk could not answer, which is the one verdict that requires confirming by other means.
- NEVER rename symbols with find-and-replace — use `rename` with `dry_run: true`, review semantic spans and coverage, then apply with `dry_run: false` and its `plan_token`.
- NEVER commit before MCP/CLI graph change analysis.

## Resources

| Resource | Use for |
| --- | --- |
| `gitnexus://repo/move-folder/context` | Codebase overview, check index freshness |
| `gitnexus://repo/move-folder/clusters` | All functional areas |
| `gitnexus://repo/move-folder/processes` | All execution flows |
| `gitnexus://repo/move-folder/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
| --- | --- |
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
