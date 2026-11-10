---
title: "Architecture Constraints"
readMode: required
priority: high
category: arch
keywords:
  - architecture
  - module
  - layer
  - boundary
  - dependency
  - structure
---

# Architecture Constraints

## Module Structure

## Layer Boundaries

## Dependency Rules

## Technology Constraints

## Entries



<spec-entry category="arch" keywords="" date="2026-10-25" sid="S-20261025-rj2c" title="现有模块分层与迁移公共流程" sourceRef="MigrationCore/MigrationCore.csproj;MoveWithSymlinkWPF/MoveWithSymlinkWPF.csproj;MigrationCore/Services/MigrationService.cs:6-12">

### 现有模块分层与迁移公共流程

MigrationCore/Models 和 MigrationCore/Services 承载核心模型与业务，MoveWithSymlinkWPF 通过 ProjectReference 依赖 MigrationCore，界面采用 WPF 与 MVVM。手动 MigrationService 和可逆 ReversibleMigrationService 共享 CopyOperationExecutor 与 MigrationWorkflowHelper；新增迁移行为应复用现有公共流程，不建立第二套复制或链接逻辑。两个项目目标框架为 net8.0-windows10.0.19041.0。

</spec-entry>

<spec-entry category="arch" keywords="session-knowledge,constraint" date="2026-11-08" sid="S-20261108-d0c17b62a8de2f88" title="保留普通个人数据人工迁移能力，与受支持软件目录准入分离；不得借此豁免系统、高危或身份不明软件目录。" sourceRef="session:20261108-grill-scan-requirements:KDC-d0c17b62a8de2f88">

### 保留普通个人数据人工迁移能力，与受支持软件目录准入分离；不得借此豁免系统、高危或身份不明软件目录。

保留普通个人数据人工迁移能力，与受支持软件目录准入分离；不得借此豁免系统、高危或身份不明软件目录。

</spec-entry>

<spec-entry category="arch" keywords="session-knowledge,constraint" date="2026-11-08" sid="S-20261108-75026d022d18bc8c" title="常用软件目录须通过开源策略参考和 Windows 多来源证据广泛发现，并重点适配 NovaStudio、NovaMax、Docker；目录发现不构成迁移或清理授权。" sourceRef="session:20261108-grill-scan-requirements:KDC-75026d022d18bc8c">

### 常用软件目录须通过开源策略参考和 Windows 多来源证据广泛发现，并重点适配 NovaStudio、NovaMax、Docker；目录发现不构成迁移或清理授权。

常用软件目录须通过开源策略参考和 Windows 多来源证据广泛发现，并重点适配 NovaStudio、NovaMax、Docker；目录发现不构成迁移或清理授权。

</spec-entry>

<spec-entry category="arch" keywords="session-knowledge,constraint" date="2026-11-08" sid="S-20261108-453f8c79281e7cf9" title="Nova 系列重点迁移模型、输出、知识库、数据库等业务数据，程序本体和共享运行环境保留原位，不作为必须整体迁移目标。" sourceRef="session:20261108-grill-scan-requirements:KDC-453f8c79281e7cf9">

### Nova 系列重点迁移模型、输出、知识库、数据库等业务数据，程序本体和共享运行环境保留原位，不作为必须整体迁移目标。

Nova 系列重点迁移模型、输出、知识库、数据库等业务数据，程序本体和共享运行环境保留原位，不作为必须整体迁移目标。

</spec-entry>

<spec-entry category="arch" keywords="session-knowledge,constraint" date="2026-11-08" sid="S-20261108-0bbf6af15afde9fd" title="用户自行关闭关联应用和容器，工具仅检测、复核并阻断；不得代退出、强杀、停止或重启容器，也不得全局关闭 Docker。" sourceRef="session:20261108-grill-scan-requirements:KDC-0bbf6af15afde9fd">

### 用户自行关闭关联应用和容器，工具仅检测、复核并阻断；不得代退出、强杀、停止或重启容器，也不得全局关闭 Docker。

用户自行关闭关联应用和容器，工具仅检测、复核并阻断；不得代退出、强杀、停止或重启容器，也不得全局关闭 Docker。

</spec-entry>

<spec-entry category="arch" keywords="session-knowledge,constraint" date="2026-11-08" sid="S-20261108-554fb3bb4851785d" title="正式验收必须包含真实原始 MFT 主路径及正确性和性能验证；普通目录遍历只能明确标注为降级，不得替代 MFT 需求完成。" sourceRef="session:20261108-grill-scan-requirements:KDC-554fb3bb4851785d">

### 正式验收必须包含真实原始 MFT 主路径及正确性和性能验证；普通目录遍历只能明确标注为降级，不得替代 MFT 需求完成。

正式验收必须包含真实原始 MFT 主路径及正确性和性能验证；普通目录遍历只能明确标注为降级，不得替代 MFT 需求完成。

</spec-entry>

<spec-entry category="arch" keywords="session-knowledge,decision" date="2026-11-08" sid="S-20261108-85126cf0e5a84554" title="新扫描、旧手动及一键迁移统一默认执行相对路径清单、逐文件大小、必要元数据和复制结果快速核对；完整内容哈希为用户可选，不再对新扫描模式默认强制；基础核对不得宣称逐字节一致。" sourceRef="session:20261108-analyze-migration-safety:KDC-85126cf0e5a84554">

### 新扫描、旧手动及一键迁移统一默认执行相对路径清单、逐文件大小、必要元数据和复制结果快速核对；完整内容哈希为用户可选，不再对新扫描模式默认强制；基础核对不得宣称逐字节一致。

新扫描、旧手动及一键迁移统一默认执行相对路径清单、逐文件大小、必要元数据和复制结果快速核对；完整内容哈希为用户可选，不再对新扫描模式默认强制；基础核对不得宣称逐字节一致。

</spec-entry>

<spec-entry category="arch" keywords="session-knowledge,decision" date="2026-11-08" sid="S-20261108-9d5959799e99b2ec" title="保留重命名作为目录切换能力快检，失败后才查询 Handle；恢复原名失败时保留改名后的真实数据目录，停止迁移并报告实际位置，不得自动删除或覆盖。" sourceRef="session:20261108-analyze-migration-safety:KDC-9d5959799e99b2ec">

### 保留重命名作为目录切换能力快检，失败后才查询 Handle；恢复原名失败时保留改名后的真实数据目录，停止迁移并报告实际位置，不得自动删除或覆盖。

保留重命名作为目录切换能力快检，失败后才查询 Handle；恢复原名失败时保留改名后的真实数据目录，停止迁移并报告实际位置，不得自动删除或覆盖。

</spec-entry>
