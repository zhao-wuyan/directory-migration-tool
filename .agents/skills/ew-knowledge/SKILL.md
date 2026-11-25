---
name: ew-knowledge
description: Unified project knowledge governance — Knowledge Gate retrieval, working memory sync, Session closeout status and recovery, candidate governance, feedback, diagnostics, and migration.
argument-hint: "[intent — e.g. 'read <query>' | 'closeout <session-id>' | 'review' | 'publish' | 'migrate' | 'sync']"
session-mode: none
version: 0.3.0
---

# Evolve 知识治理

本 Skill 独立承担项目知识全生命周期治理，覆盖检索门禁、工作记忆、收尾沉淀、候选审查、Wiki 变更、历史迁移以及向 `workflow-evolve` / `ew-skill-design` 的分流。详细规范按需加载：

- [知识路由与规则](references/knowledge-route.md) — 统一 Wiki 模型（`mode: constraint|reference`）、ID 边界、`knowledge-reads/1.0` 回执、L3 初始化、日期/SHA 锚点、`.out-of-scope/` 治理与旧知识迁移分流标准。
- [收尾与候选晋升协议](references/closeout-protocol.md) — `Read → Refresh → Present → Ask → Execute → Verify` 收尾流程与权限边界。
- [Wiki 与记忆维护参考](references/wiki-maintenance.md) — `evolve kb` 常用维护、定期 Wiki Lint 巡检与多宿主历史提取命令。

## 意图路由

| 用户意图 | 路由 | 默认权限 | 主要入口 |
| --- | --- | --- | --- |
| OpenViking 存放分工、全局领域经验、原生 Memory / Resource 与项目同步 | `openviking` | 先识别作用域与现有能力 | 加载 `ew-openviking`（`evolve skill workflows show ew-openviking`）；本 Skill 保留 Wiki 治理职责 |
| 查询约束、参考、记忆 | `read` | 只读 | `evolve search "<query>" --mode context` / `evolve kb get <id>` |
| 记录、更新或同步工作记忆 | `memory` | 证据绑定的工作记忆写入 | `evolve kb add --kind episodic` / `kb update` / `kb sync` |
| Session 完成后的状态检查 | `closeout` | 只读 | `evolve kb closeouts <session-id>` → 已完成则 `kb candidates show` |
| 收尾失败恢复 / 独立任务最终收尾 | `recover` | 按已完成任务及授权范围提取候选 | 见收尾协议；指定 Session 优先，`kb harvest pending` 仅用于项目批量维护 |
| 候选审查与归因 | `review` | 只读 / 暂存 | `evolve kb candidates show` / `kb candidates route` / `kb propose` → `kb changes` |
| 正式 Wiki 发布 | `publish` | 固定 ID、目标与动作的人工授权 | `evolve kb apply <change-id>` / 适用时 `kb candidates publish` |
| 个人跨项目经验 | `experience` | 查询和候选审阅只读，发布需固定人类授权 | `evolve experience status\|pending\|review\|search\|get\|publish` |
| 记录知识实际使用效果 | `feedback` | 当前 Run 封存前写入既有回执 | `evolve kb feedback`（见 session-run） |
| 健康诊断、历史迁移 | `doctor` / `migrate` | 先只读，变更须授权 | `evolve kb doctor` / `kb storage` / `kb import` |
| 已归因的 Skill 缺陷或新流程 | `evolve` / `skill-design` | 候选交接；修改需授权 | `kb candidates route` → `workflow-evolve` / `ew-skill-design` |
## 核心不变式

1. **只读优先**：先识别操作与目标；检索结果、Closeout 候选、审计与回溯信号均只是证据，除收尾对内容完全相同的唯一活跃 project `reference` 经 `wiki_changes` 自动追加 `sources` 外，新建与变更均仅在明确授权发布时写入 `wiki/`。
2. **禁止绕过治理写入**：正式 Wiki 知识仅通过受治理 CLI 写入（`evolve kb propose` → `evolve kb apply`、`evolve kb candidates publish`、`evolve kb harvest` 精确重复 `sources` 自动追加、`evolve kb import --apply`、`evolve kb ingest`），严禁直接创建或修改 `wiki/entries/`、`.workflow/specs/` 或 `.workflow/knowhow/`；未批准的 Closeout 候选与 `kb propose` 预览仅暂存（staging）在 `<root>/.evolve/closeout.db`，工作记忆保存在 `<root>/.evolve/memory.db`。
3. **`-y` 不是发布授权**：自动模式、高重要性标记、机器推荐或一般性同意审查均不授权 Wiki 发布；发布必须确认固定候选 ID、关系/目标与动作，否则保留待处理清单并延期（defer）。
4. **收尾与发布解耦**：零候选、拒绝或延期均可正常完成任务与 Session；禁止为凑条目把缺参考或一次性过程写成规则。
5. **不越权开启新链**：执行知识操作时不擅自新建 `Session` / `Run`，也不把未授权工作区自由文本混入收尾证据。
