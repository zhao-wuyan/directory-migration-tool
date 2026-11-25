# Planning & Chain Creation

本文件定义意图分类（`S_CLASSIFY`）、建链（`S_CREATE`）与分解（`S_DECOMPOSE`）协议。完整步骤路由与前置阈值见 [意图分类目录](ew-orchestrate.md)，长链与决策门策略见 [自动与 Goal 策略](auto-policy.md)，分派/恢复命令见 [执行循环](orchestrator-run-loop.md)。

## Classification Protocol

1. **Exact match**（详见 [意图分类目录](ew-orchestrate.md)）：
   - `status` / `状态` / `dashboard` → 只读短路径，路由至 `END`（呈现状态后即止，不推断开链）。
   - `continue` / `next` / `go` / `继续` / `下一步` → `S_CONTINUE`：通过 `evolve session list --status open` 校验活跃 Session（`1` 个继续执行，`>1` 个询问选择，`0` 个走冷启动回退规则）。
2. **Selection priorities**（`S_CLASSIFY` 严格按此顺序求值；遵循“收敛 Session 阈值 + 复用同主题开放 Session + 最轻足够通道优先”与 Scope Guard，不以文件数 `<= 2` 作为门槛，也不因普通 `implement`/`fix`/`refactor` 动词强制进入重链）：
   - 显式 `/ew-swarm`、显式 `/ew-odyssey` 或显式 `/workflow-evolve`（三者均为 `disable-model-invocation: true`，仅用户显式调用时命中，且 `/workflow-evolve` 仅用于修改 Skill/Harness）
   - `>` Exact match 短路径（`status` → `END`，`continue` → `S_CONTINUE`）
   - `>` **免开 Session 直行（`END`）**：纯问答、只读状态/代码查询、**不改生产源码与项目交付物的只读诊断/计算/探针脚本**，以及**无需留档审计的微小单步修改**（如 1–2 行配置/文档/测试微调）；非显式 `/ew-orchestrate` 强契约要求时直接执行，有非显而易见经验直接 `evolve kb add --kind episodic` 留存
   - `>` **已有 compatible open Session（进入 `S_RUN_LOOP`）**：通过 `evolve session list --status open` 命中同主题或同模块范围的开放 Session（即便先前的 Step 已 `completed`），优先用 `evolve session step add`（必要时配合 `session amend`）追加步骤复用该 Session，不重复新建 Session
   - `>` `--goal` / `--auto` 长链或重型多模块/跨包架构工程链（`>= 3` 耦合模块、跨包接口变更、`>= 2` 未定开放问题，或显式要求 `analyze`/`plan`/`execute`/`verify`/`review` 多阶段流水线）
   - `>` tracked issue（带显式 Issue ID 且需要 `--gaps` 拆解的多步链；单关注点小修复仍可走 `ew-companion` 或 `END`）
   - `>` 前端 UI/设计（`ew-impeccable`，可自动路由）或一次性设计原型验证（`ew-prototype`，`session-mode: none` 独立调用，用于用可运行原型收敛状态模型/UI 结构疑问）
   - `>` 显式单步强契约任务（用户明确点名 `ew-init` / `ew-analyze` / `ew-plan` / `ew-review` / `ew-verify` / `ew-test` / `ew-auto-test` / `ew-brainstorm` / `ew-grill` / `ew-blueprint` / `ew-roadmap` / `ew-retrospective`，或已有 `current-plan` 走 `ew-execute`）
   - `>` `ew-companion`（**轻量可追溯通道**：已有 Evolve 管理的工程中或显式 `/ew-orchestrate` 下，涉及**实质生产源码或项目交付物改动**、需要可追溯 Run 证据的单关注点开发/修复/重构任务，**不限文件数**，无需下游类型化工件或正式决策门时直接在主上下文单 Run 完成）
   - `>` `END`（未启用 Evolve 编排的项目中的直接独立执行）。
3. **契约前置校验**：
   - `ew-verify` 在契约层面强依赖 `current-plan` + `current-execution`；缺任一上游时禁止单排 `ew-verify`（改走 `ew-review`，或先补 `ew-plan` → `ew-execute`）。
   - `ew-execute` 缺 `current-plan` 时前置 `ew-plan`（或降级为 `ew-companion`）。
   - `ew-test` 无 `latest-verification` 时至少需有 `current-execution` + `current-plan`、`latest-review` 或显式 `<scope>`。
4. **留痕要求**：必须记录匹配证据、排除备选方案与置信度；无记录不可进入 `S_CREATE`。
5. **权威路由表**：各类语义意图到初始链的完整映射与阈值以 [意图分类目录](ew-orchestrate.md) 的 `Semantic Intent -> Route Matrix` 为唯一权威来源。

## Decomposition Protocol (`S_DECOMPOSE`)

1. narrow / 单步链跳过分解，直接构造最小 `boundary_contract`（`in_scope: [intent]`、`definition_of_done: "step completed with passing gates"`）。
2. broad/medium 意图（影响 `>= 3` 个模块、需跨包接口变更、或范围/约束/完成标准中 `>= 2` 项不明确）最多询问 3 轮；`-y` 不跳过广泛歧义。
3. `--goal <goal>` 派生映射：将 `<goal>` 文本直接写入 `Session.objective`，并据此构造 `--definition-of-done`、`--boundary-contract` JSON 与面向结果的 `--goals` JSON 数组（每项含 `id`、`goal`、`boundary`、`done_when`、`evidence`、`lifecycle`、`status: "pending"`），连同 `--goal-mode` 持久化至 `evolve session create`。
4. 有分解时在 `session complete` 前插入 `--decision-only --decision-ref post-goal-audit --step-id goal-audit` 决策门；多里程碑拆分仅在多 release 证据时先走 `ew-roadmap`。

## Chain Creation (`S_CREATE`)

1. **Knowledge Gate**: 建链前通过 `evolve search "<query>" --mode context` 检索适用 Wiki 约束并记录读取。
2. **Skill 契约校验与 Git 绑定**: 按 [执行循环](orchestrator-run-loop.md) 的 Start and Dispatch 规范校验步骤契约并确保 Skill 已提交至 Git `HEAD`。
3. **持久化 Session 与 Chain**:
   - 通过 `evolve session create "<intent>" --id ew-<slug> [--definition-of-done "<text>"] [--boundary-contract '<json>'] [--goals '<json>'] [--auto] [--goal-mode] [--chain <commands...>] [--reason "<reason>"]` 持久化目标、边界与策略（不再停留在对话内存；单协调者顺序流省略 `--actor` 与 revision 参数，多 Worker/并行/重试竞态时再附带 `--actor`、`--request-id` 与 `--expected-orchestration-revision`）。
   - 带领域参数、`--retry-max`、`--goal-ref`、`--stage`、`--decision-ref` 或 `--decision-only` 的步骤，通过 `evolve session step add` 逐条写入（启用并发守卫时传入 `create` 返回的 `--expected-orchestration-revision`）。
   - 分解完成后可在回复中简要列出 `G1..Gn` 子目标及其 `done_when` 供用户确认（不要输出 `/goal` 斜杠命令）。

## Target Scope Resolution 优先级

1. 明确传入的目标路径、Session ID、Issue ID 或 `--from <alias>` 产物。
2. 当前项目活跃 Session 的 `goals[]` / 未完成步骤。
3. 多步链默认以 `{run_dir}` 隔离交付物；仍无法确定范围时使用宿主结构化提问能力向用户确认（见 [执行循环](orchestrator-run-loop.md#host-adapter-mapping)）。
