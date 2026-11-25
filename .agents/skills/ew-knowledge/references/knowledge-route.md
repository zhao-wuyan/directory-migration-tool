# Evolve 知识路由与规则

本文件定义 Evolve 统一知识模型、ID 边界、搜索回执、L3 初始化、旧知识迁移与自进化分流规则。Wiki、Session/Skill 收尾沉淀、候选审查和发布共用统一 Wiki 与 `evolve` CLI（源码仓库内使用 `node dist/workflow-cli.js`）。

## 统一 Wiki 条目

正式知识只进入 Wiki 条目，原 Spec 和 Knowhow 通过属性表达差异：

- `mode: constraint` — 执行约束、质量门禁和适用规则。
- `mode: reference` — 经验、方法、模板、术语和可选建议。
- `status: active|needs_review|deprecated` — 生命周期状态。
- Wiki 元数据 `scope` — 默认 `project`，也可用 `task:<kind>` 或 `path:<glob>` 限定适用范围；CLI 写入时通过 `--entry-scope` 设置。CLI 的 `--scope project|global|local` 只控制存储查询范围，默认 `local` 合并项目与用户全局。
- `categories`、`tags`、`keywords` — 用于查询和路由，不能替代 `mode` 与 `scope`。
- `sources` — 必须可回查到完成回执、消息锚点或迁移来源。

正式知识统一通过受治理写入命令进入 `wiki/`：`evolve kb propose` → `evolve kb apply`、`evolve kb candidates publish`（仅限无关联条目的新 `mode: reference`）、`evolve kb harvest session|conversation|skill|receipt|scan`（仅当内容与唯一活跃未锁定 project `reference` 完全一致时自动追加 `sources`）、`evolve kb import --apply` 与 `evolve kb ingest`。禁止直接创建或修改历史知识目录或 `wiki/entries/`。可用 `evolve kb init` 安全初始化 `wiki/purpose.md`、`wiki/schema.md` 与空的 `wiki/entries/`；`kb ingest` 生成的普通图谱页会自动纳入 `evolve search --mode context` / `kb search --kind reference` 召回范围（排序低于正式 `entries/`），追加 `--propose` 会在 `closeout.db` 暂存对应的 `entries/` 提案等待审查。其中 **暂存（staging）** 专指写入 `<root>/.evolve/closeout.db` 的未发布 Closeout 候选与 `kb propose` 待审变更预览（不改动 `wiki/entries/`）；工作记忆则通过 `evolve kb add --kind episodic` / `evolve kb extract|sync` 写入 `<root>/.evolve/memory.db`（`unverified_episodic`），与正式 Wiki 隔离。`.evolve/index.db` 会在检索与 `context` 读取时检测 `wiki/` 文件变更并自动刷新。

## 核心规则

1. 先识别操作，再识别目标：`查、搜索、读取、引用` 进入 `read`；`收尾、沉淀、harvest` 进入 `closeout`；`审查、重复、冲突、候选` 进入 `review`；`发布、批准、应用` 进入 `publish`；`迁移、导入` 进入 `migrate`。
2. 只有明确要求发布或应用时才执行写入。检索结果、Closeout 候选、审计结果和回溯信号都只是证据。
3. Session 收尾和独立 Skill 收尾进入同一 Closeout 流程，只使用完成回执中的证据范围，不从未授权工作区文本补证。
4. Closeout 提取可复用方法后，先核对适用条件、验证证据和现有条目关系，再决定追加来源、创建候选、更新条目或标记冲突。
5. 同一内容在唯一活跃 `reference` 条目中重复验证时，可以只追加 `sources`；正文、作用域和模式变化必须走 Wiki 提案。
6. `constraint` 与 `reference` 发生冲突时保留双方证据并创建待审查关系，不自动覆盖活跃条目。
7. 归因到具体 Skill、版本、读取文件与证据的信号转交 `workflow-evolve`；项目级规则与跨任务经验留在 Wiki。
8. 候选只有在需要新契约、新产物、跨 Skill 路由、至少 3 个稳定步骤、明确要求新增 Skill，或目标 Skill learning 已膨胀（`evolve skill learnings` 报告 `consolidate_recommended`：inline > 3 或总数 > 8，`kb candidates route --skill` 自动检查）时才进入 `ew-skill-design`；单条指令、条件链接及对已有 learning 的 `supersede` / `retire` 继续由 `workflow-evolve` 处理。
9. **写历史而非“当前状态”（日期与 commit SHA 锚点）**：Wiki 是历史记录，不假设正文随代码自动保持实时同步。每条决策或事实必须标注日期（如 `2026-11-22`）；凡涉及代码实现、符号、路径或行为的断言，必须附带对应仓库与 commit SHA（如 `<repo>@<sha>`），避免无日期、无 commit 锚点地写“当前/现在/总是”。发生变更时通过受治理提案新增带新日期与新 SHA 的断言，并将旧断言/旧条目显式标记为已取代（`superseded` / `deprecated` / `needs_review`），不静默改写历史。
10. **`.out-of-scope/` 被拒绝方案治理约定（绝不绕过 `evolve kb`）**：记录明确被拒绝的功能请求或设计方案时按**概念**归并而非按单个 Issue 建档（包含项目范围/技术约束/战略权衡等持久理由及历史请求引用，避免临时性延期理由；**禁止**把已实现功能或普通 Bug 记为被拒绝范围，以免污染概念查重）。在 Evolve 项目中**禁止**直接手写或以 `.out-of-scope/*.md` 绕过 `evolve kb` 治理：评估新需求前先通过 `evolve search` / `evolve kb search` 按概念相似度查重；若仓库存在历史 `.out-of-scope/*.md`，仅作为只读迁移/对照输入（通过 `evolve kb ingest <file> --propose` 或 `kb propose` → `kb apply` 纳入受治理的 `mode: reference` / `constraint` 条目并标记 `out-of-scope` 标签与日期/SHA）；若维护者日后重新采纳该概念，同样通过 `kb propose` → `kb apply` 将旧条目状态推进为 `deprecated` / `superseded`。
11. **周期性 Wiki 维护**：定期按 [Wiki 与记忆维护参考](wiki-maintenance.md) 执行只读 `Wiki Lint`（矛盾、孤立页与断链、已被新提交取代的条目），发现需修正项时经受治理提案与人类授权更新。

## Read 与 ID 边界

按查询类型选择最窄入口；约束读取带适用上下文，参考读取保留条目 ID 和来源。

```text
evolve kb list --root <project> --kind constraint --category <category>
evolve kb search "<topic>" --root <project> --kind reference --task <kind> [--format compact]
evolve kb get <id|q-N:idx>... --root <project> --resolved --task <kind> [--pick <n,...>]
evolve search "<query>" --root <project> --mode context --task <kind> --session <session-id> [--format compact]
evolve search "<query>" --root <project> --mode context --session <current-session-id> --memory-session <historical-session-id>
evolve search "<query>" --root <project> --mode context --context-file <context.json>
evolve kb search "<query>" --root <project> --kind episodic --scene <scene> --type <type> --reads <knowledge-reads.json>
```

- 标准顺序为 `search → get`：
  - 默认输出精简单行 JSON（加 `--format compact` 输出纯文本 Agent 视图，加 `--verbose` 查看 `rawId`/`score`/`commands` 等全量字段）：正文 `<= 300` 字符直接内联 `content`，超过时仅返回 `snippet` 与 `truncated: true`。
  - 返回的 `id` 为无状态防碰撞短前缀（64 位记忆 Hex / UUID 至少 8 位；legacy Wiki 为 `entries/legacy-<8hex>`；代码符号为 `<kind>:<8hex>` 并附带 `loc: "path:start-end"`；语义化 `entries/<slug>` 原样保留）。携带 `--reads` 时顶层返回单一 `search_id`（`q-N`）与条目序号 `idx`。
  - `evolve kb get <id|q-N:idx>...` 支持短前缀、批量读取、`q-N:idx` 或 `--pick <n,...>`，并可直接读取 CodeGraph 符号源码片段，返回完整正文、作用域和规范完整 `id`。
  - `--mode context` 的 `constraints` 按作用域特异度降序排列并受默认预算上限（`20` 条 / `4000` 字符）保护，超出时输出 `constraints_truncated: true` 与 `constraints_notice`（可用 `--task`/`--path`/`--category` 收窄或用 `kb get` 读取其余条目）；`--mode merged` 通过 RRF（`K=10`）平衡代码与工作记忆/Wiki 排序。
- 写入接口（`propose`、`changes`、`apply`）必须使用 `kb get` 返回的规范化完整条目 ID、候选 ID 或变更 ID 并校验基线哈希，不接受别名或短前缀。
- 零命中时保留查询条件与空结果，禁止把“缺少参考”误写成新的 `constraint`。

## 个人跨项目经验（可选）

个人经验在 OpenViking 私有 `resources/evolve/personal-experience` 下独立保存，不是原生会话采集、项目 release、Memory 或正式 Wiki 约束。默认没有配置，不改变现有项目行为。

涉及“全局”含义、原生 Memory / Experience、领域资料、个人经验与项目 release 的存放选择时，先加载 `ew-openviking`（`evolve skill workflows show ew-openviking`）。它补充 Evolve 集成规则，不替代 OpenViking 官方指导；原生 Memory 也能存经验，不能仅凭“技术经验”就搬迁或改写官方存储。

在目标 Git 仓库根目录运行 `evolve experience enable`。它用现有 OpenViking 连接确认身份，自动创建或更新用户级 `~/.evolve/experience.json`，只把当前工作树真实路径加入采集名单，不保存或复制 API key。用 `evolve experience status` 查看开关；`evolve experience disable` 在本地关闭当前仓库，不删除远端经验。隔离测试可用绝对路径的 `EVOLVE_EXPERIENCE_CONFIG` 覆盖配置位置。

未启用的仓库可查询已有参考，但不贡献候选；仓库内文件不能代替用户授权。由执行 `ew-verify` / `ew-test` 的 AI 根据实际通过的检查写入提炼字段，不另起模型；只有完成 Session 后的封存证据可被列入 `evolve experience pending`。候选审阅及发布规则见[收尾协议](closeout-protocol.md)。

项目约束优先；当语言、框架及其版本已从当前项目确认，且任务确实有参考需求时，用 `evolve experience search python python@3.12 "并发延迟"` / `evolve experience get python python@3.12 <id>`。查询词会发给 OpenViking，不包含项目机密。结果应核对适用条件、验证摘要和冲突关系，以正文哈希记录引用；不混入项目 Knowledge Gate，也不当作授权。

## 搜索回执与 `session-priors`

`evolve search "<query>" --mode context` 与 `evolve kb get <id|q-N:idx>...` 通过同一个 `--reads <run-dir>/knowledge-reads.json` 记录 `knowledge-reads/1.0`，并在封存时写入回执。分析 Run 还会把已读 Wiki 正文和约束封存为 `session-priors`；后续 Run 通过 `artifact-read` 打开该产物后，`priors_reuse` 自动记录产物 ID、哈希和原 Run ID。

- `searches[]` 记录各源底层 `search_id`、查询词、阶段过滤条件、完整规范 `returned_ids` 与命中数。
- `queries[]` 记录统一查询句柄 `q-N` 及 `idx -> { kind, id, scope, from_search_id }` 映射（供 `q-N:idx` 与 `--pick` 反解）。
- `reads[]` 记录精确读取及其关联 `from_search_id`（或 `priors` 来源）、完整规范 `id`、`content_sha256` 与 `version`。
- CLI 模式下跨调用复用同一个 `--reads <run_dir>/knowledge-reads.json` 时，`kb get` 对前缀 ID、`q-N:idx` 与 `--pick` 会自动关联对应 `from_search_id`（显式传入 `--from-search <search-id|q-N>` 仍可覆盖指定）；无搜索时不伪造记录，零命中保留 `returned_ids: []`。

## L3 初始化与无侵入回溯

- 初始化时调用 `evolve search --mode context` 加载 L3 Wiki 约束与参考（返回 `evolve-context/1.0`，分为 `constraints`、`references`、`episodic`、`code`），不读取宿主原始历史文件。
- `search --mode context --session` 只标记当前上下文；筛选历史工作记忆需显式使用 `--memory-session`。
- 回溯优先消费已提交的 Session/Run 回执；需要补充对话历史时再按显式配置的 `host_kind` 与根目录读取并转换为 `transcript-evidence/1.0`。

## 迁移旧 Spec 与 Knowhow

在旧项目根目录预览迁移条目与待审查数，核对后追加 `--apply` 写入并建索引：

```text
evolve kb import [<source-project>] [--root <target-project>] [--project <source-id>] [--knowhow-only] [--apply]
evolve kb import <legacy-root> --scope global [--project <source-id>] [--knowhow-only] [--apply]
```

- `--knowhow-only` 仅迁移 `.workflow/knowhow/`；`--scope global --apply` 导入用户全局 `~/.evolve` Wiki（用 `evolve search "<query>" --mode context --scope global` 查阅）。
- 重复导入保留既有相同条目并保留原文件出处；带仓库限制的条目可用 `--repository "<repo-uuid>"` 检索。
