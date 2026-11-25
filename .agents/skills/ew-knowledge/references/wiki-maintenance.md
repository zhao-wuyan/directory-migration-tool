# Wiki 与记忆维护参考

本文件汇总 `evolve kb` 的日常知识操作与跨宿主记忆提取，以及 `evolve search` 的聚合检索。`--scope project` 只查询项目，`--scope global` 只查询用户全局，默认 `--scope local` 合并两者。

## Wiki 索引、查询与摄入

| 操作 | 命令 | 说明 |
| --- | --- | --- |
| 初始化 Wiki 模板目录 | `evolve kb init [--purpose "..."] [--schema "..."] [--force]` | 创建 `wiki/purpose.md`、`wiki/schema.md` 与 `wiki/entries/`（不覆盖已有自定义模板，不直写条目） |
| 重建 / 更新 Wiki 索引 | `evolve kb build` | 扫描并索引 `wiki/` 下的 Markdown 页面、标签/关键词/分类与依赖关系（`.evolve/index.db` 在缺失或 `wiki/` 变更时由知识检索自动构建/刷新） |
| 从源文档生成/合并 Wiki 页 | `evolve kb ingest <file.md\|file.txt> [--propose]` | 需配置 OpenAI 兼容模型接口；生成页面可被 `search --mode context` 检索，加 `--propose` 同时在 `closeout.db` 暂存 `entries/` 提案 |
| 检索与图谱展开 | `evolve kb search "<query>" [--format compact\|--verbose]` | 默认返回单行精简 JSON（短前缀 ID 与内联/截断预览），`--format compact` 返回纯文本，`--verbose` 输出全量字段与分页命令模板 |
| 按范围列出约束 | `evolve kb list --kind constraint [--task <kind>] [--path <glob>] [--category <cat>]` | 解析对当前上下文生效的 `mode: constraint` 条目（按作用域特异度降序） |
| 按范围查询参考 | `evolve kb search --kind reference "<query>" [--task <kind>] [--path <glob>] [--repository <uuid>]` | 解析活跃 `mode: reference` 条目 |
| 精确读取条目、代码符号或批量序号 | `evolve kb get <id\|q-N:idx>... [--pick <n,...>] [--resolved]` | 支持短前缀、`q-N:idx`、`--pick` 批量读取及 CodeGraph 符号源码片段，返回规范完整 ID 与修订历史 |

## 定期 Wiki Lint（矛盾、孤立页与过期条目巡检）

Wiki 维护遵循三大常规动作：**Ingest（摄入）**、**Query（检索）** 与 **Lint（巡检）**。建议定期（或配合宿主 `/schedule` 定时任务）运行只读 Wiki Lint，并在人类授权后通过受治理流程处理发现的问题：

1. **矛盾检测（Contradictions）**：核对活跃 `constraint` 与 `reference`（含被拒绝方案 `out-of-scope` 条目）是否存在相互冲突的结论或重叠作用域；发现冲突时保留双方证据并创建待审查关系，不自动覆盖活跃条目。
2. **孤立页与断链检查（Orphans & Missing Links）**：检查 `wiki/entries/` 与图谱页中缺少双向关联、未被引用或 `sources` 失效的页面，必要时用 `evolve kb build` 重建索引与依赖图。
3. **过期与已被取代的断言（Superseded / Stale Claims）**：依据“写历史而非当前状态”规范核查每条代码断言的日期与 `<repo>@<sha>` 锚点；若新提交或新决策已推翻旧结论（例如曾经拒绝的 `.out-of-scope/` 概念被重新采纳，或旧代码路径已重构），新增带新日期与新 SHA 的条目并通过 `evolve kb propose` → `evolve kb apply` 将旧条目推进为 `deprecated` / `needs_review`（标记为已取代 `superseded`，绝不直接手改或删空 `wiki/entries/`）。
4. **`.out-of-scope/` 存量文件对照**：若仓库历史遗留 `.out-of-scope/*.md`，Lint 时只读比对其概念是否已通过 `evolve kb ingest <file> --propose` 纳入受治理 Wiki，防止形成双轨或绕过 `evolve kb` 写入。

## 工作记忆写入、检索与多宿主同步

本地工作记忆保存在 `<root>/.evolve/memory.db`，可通过以下 CLI 直接读写或配置自动提取：

首次确认用户纠正、持久决策、非显而易见根因或复现方法时记录。内容应含适用项目/版本/条件、事实或决策、方法、证据与验证状态；仅经验证的修复使用 `verified_fix`，未确认的结论标明待验证。先检索已有记录并优先更新；`kb update` 省略 `--evidence` 保留原来源，传入时替换完整来源列表，须重复传入所有应保留的真实引用。常规任务请求和进度不作为长期记忆。


| 操作 | 命令 | 说明 |
| --- | --- | --- |
| 手工写入记忆 | `evolve kb add "<text>" --kind episodic --type <type> --scene <scene> --session <session> --evidence <source-ref>` | 用真实 Session 或对话 ID 和证据保存高价值经验；无可关联 ID 时不虚构，先确认实际上下文 |
| 分页检索记忆 | `evolve kb search "<query>" --kind episodic [--type <t>] [--scene <s>] [--session <id>] [--page <n>] [--limit <n>] [--format compact\|--verbose]` | 默认返回短前缀 ID、`<= 300` 字符内联正文或 `snippet` + `truncated: true`（加 `--reads` 时返回 `q-N` 与 `idx`；加 `--verbose` 返回 `commands` 模板） |
| 精确读取单条或批量记忆 | `evolve kb get <id\|q-N:idx>... [--pick <n,...>]` | 支持 8+ 位短前缀或 `q-N:idx` / `--pick` 批量展开全文并自动关联 `--reads` 溯源 |
| 查看记忆旧版本 | `evolve kb history <memory-id>` | `kb update` 覆盖或 `kb delete` 删除前的内容会归档到 `memory_history`，已删除记录也可查回 |
| 追溯原始对话来源 | `evolve kb sources <memory-id>` / `evolve kb conversation <id> --host <host> [--page <n>]` | 分页查看原始对话上下文 |
| 从消息文件抽取 L1 记忆 | `evolve kb extract <messages.json> --session <id> [--mode chat\|code]` | 需配置模型接口（`AGENT_MEMORY_LLM_BASE_URL`、`AGENT_MEMORY_LLM_MODEL`、`AGENT_MEMORY_LLM_API_KEY`） |
| 配置与同步多宿主会话 | `evolve kb config --host <codex\|claude\|agy\|all\|off>`、`evolve kb sync --host <codex\|claude\|agy\|all> [--session <id>]` | 支持 Codex CLI、Claude Code 与 Antigravity 会话历史增量抽取 |
