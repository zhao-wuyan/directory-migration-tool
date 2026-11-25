# Automatic & Goal Long-Chain Policy

本文件定义 `--auto` 与 `--goal` 闭环长链的 **阶段映射（Stage Mapping）**、**建链规则（Build Rules）** 与 **决策门评估（Decision Gates）**。具体 fenced CLI 语法见 [执行循环](orchestrator-run-loop.md)，Goal 变更协议见 [goal.md](goal.md)。

## Execution Authority Invariants

1. 单协调者顺序流中，`--actor` 与 `--expected-orchestration-revision` / `--expected-run-revision` 均为**可选项**，默认可省略以精简命令（需要显式校验能力时可先查 `evolve capabilities`）。
2. 在多 Worker、重试或并行竞态场景下，建议开启乐观并发守卫：全程固定同一 `actor_id` 并传给 `--actor`（`--participant` 可选，默认等同 `--actor`，若传入必须一致），沿 `Evolve mutation receipt` 刷新 `--expected-orchestration-revision`（Run 目标操作另传 `--expected-run-revision`）；每次重试同一请求复用原 `--request-id`，遇到 `REVISION_CONFLICT` 先读 `evolve session get`。
3. 在 `session create` 时显式持久化 `--boundary-contract`、`--goals` 与 `--auto` / `--goal-mode` 策略（`Session.policy.auto_mode` 控制 `continuation.authority = "automatic"`）；未设置 `--auto` 时需按 `S_CONFIRM` 与每步人工节点推进。
4. 禁止绕过 `evolve session` / `evolve run` 命令行直接修改状态；Run 封存后不可变（连续步进可使用 `evolve run complete ... --next` 在单次调用中封存当前 Run 并立即派发下一待执行 Run），失败重试须通过 `evolve run transition ... failed`（或 `cancel` / `decide fix` 重开）后再经 `evolve run create` 派发。Runtime 计算 `--retry-max` 时统计同一 `step_id` 下全部非 `running` 的历史 Run（含 `failed`、`cancelled` 以及被 `run decide --verdict fix` 重开的 `sealed` Run）。

## Stage Mapping

| Stage | Skill / 节点配置 | Decision after | 适用条件 |
| --- | --- | --- | --- |
| init | `ew-init "{intent}"` | — | 项目未初始化或需存量接入引导时 |
| grill | `ew-grill "{intent}"` | — | 需要压测假设或对齐术语时 |
| brainstorm | `ew-brainstorm "{intent}"` | — | 开放探索阶段 |
| blueprint | `ew-blueprint "{intent}"` | — | 需要正式规格文档包时 |
| roadmap | `ew-roadmap "{intent}"` | — | 多 Session 拆解证据 |
| analyze-macro | `ew-analyze "{intent}"` | `post-analyze-scope` | 宏分析路由门 |
| analyze | `ew-analyze "{intent}"` | — | 分析阶段 |
| plan | `ew-plan` | — | 规划阶段（可按需加 `--gaps` / `--tdd`） |
| execute | `ew-execute` | `post-execute` | 执行阶段 |
| verify | `ew-verify` | — | 需要结构与约束覆盖验证时（前置 `plan` + `execute`） |
| test | `ew-test` | — | 需要 UAT / `--frontend-verify` 验收时 |
| auto-test | `ew-auto-test` | — | 需要分层自动化测试生成与收敛时 |
| review | `ew-review current-execution [--tier quick]` | `post-review` | 审查阶段（quick 模式加 `--tier quick`，也可传目标路径） |
| debug | `ew-debug "{intent}"` | — | 链内出现失败需要根因诊断时（`<= 2` 模块） |
| retrospective | `ew-retrospective {session}` | — | 阶段收尾多视角复盘时 |
| goal-audit | `--decision-only` | `post-goal-audit` | 存在 `goals` 分解时 |
| session-complete | `--decision-only` | `post-execution` | 长链终端核验节点 |
| debug-escalate | `--decision-only` | `post-debug-escalate` | 调试升级阻断节点 |

> **说明**：Runtime 在 `evolve run create` 派发 Run packet 时已自动注入 `session_id`（禁止向步骤传入未定义的 `--session` 参数，否则会错误占用 `ew-review <target>` 等位置参数）；`run.task.args` 中的 `{intent}` 及 `ew-retrospective` 位置参数 `{session}` 由 Runtime 在 `run create` 时自动展开；`--decision-only` 节点仅需 `--step-id` 与 `--decision-ref`，不绑定 Skill 包。

## Build Rules（按顺序应用）

1. **Wiki 约束预检**: 建链前执行 `evolve search --mode context` 读取项目约束（旧知识目录通过 `evolve kb import` 预览并导入，禁止直接读写）。
2. **起点与过滤**: 从当前最新状态起步，跳过已有 sealed artifact 的阶段，并确认 `ew-verify` 等硬契约上游已满足。
3. **决策门、重试上限与审计插入**:
   - `--auto` / `--goal` 链默认重试上限为 **`retry_max = 3`**。注意：`evolve session create --chain` 快捷语法创建的步骤不带 `retry_max`，因此需要有界重试的步骤必须通过 `evolve session step add ... --retry-max 3`（或 `session step update`）写入上限，并按需绑定 `--decision-ref "<gate>"`；独立核验节点使用 `--decision-only --decision-ref "<gate>" --step-id <id>`。
   - 存在 `goals` 分解时：在末个产出阶段后插入 `--decision-only --decision-ref post-goal-audit --step-id goal-audit`；每连续配置 **3 个可执行步骤**（从链首或上一个 `post-reground-<n-1>` 之后起算，只要后续仍有可执行步骤且段内已累积 3 个可执行步骤）插入一次 `--decision-only --decision-ref post-reground-<n> --step-id reground-<n>`（递增编号 `post-reground-1`, `post-reground-2`, ...，确保 Session 内 `decision_ref` 唯一）。
   - 链尾以 `--decision-only --decision-ref post-execution --step-id session-complete`（需 `run decide proceed`）收尾，再调用 `evolve session complete`。
4. **参数与契约校验**: 建链前按 [执行循环](orchestrator-run-loop.md) 的 Start and Dispatch 规范校验步骤契约并确保 Skill 已提交至 Git `HEAD`。
5. **`scope_verdict` 门控（宏分析起点）**: `ew-analyze` 在 `outputs/findings.json` 产出 `scope_verdict: small | medium | large | unknown`：
   - `small` / `medium`: 直接按计划下游进入 `ew-plan`（`small` 为单/少文件计划，`medium` 为 1–2 个可并行子系统）；
   - `large`: 保留多子系统分解进入 `ew-plan`（若涉及多 release 强耦合则先插 `ew-roadmap` 拆多 Session）；
   - `unknown`: 当探索证据不足或无法界定模块边界时产出 `unknown`，由 `post-analyze-scope` 决策门触发询问用户或以 `fix` 重开范围更窄的 `ew-analyze`（`-y` 不得猜测）。

## Decision Gate 评估与回退规范

| 类型 | `decision_ref` | 评估动作 | 检查输入 |
| --- | --- | --- | --- |
| quality-gate | `post-execute` | 评估执行与自检 | `outputs/execution.json`, `outputs/self-check.json` |
| quality-gate | `post-review` | 评估审查结论 | `outputs/review.json` |
| goal-gate | `post-goal-audit` | 对照 `goals[].done_when` 核验未完成目标 | `Session.goals` + Run 证据 |
| scope-gate | `post-analyze-scope` | 判定范围路由（`small\|medium\|large\|unknown`） | `outputs/findings.json` (`scope_verdict`) |
| reground-gate | `post-reground-<n>` | 核验累积产出是否偏离 `intent` / `boundary_contract` | `Session.boundary_contract` + handoffs |
| structural | `post-execution` | 终态核验（全部 Run sealed + chain 终结） | Session 全量状态 |
| structural | `post-debug-escalate` | 始终 `escalate` 阻断推进，等待人工解除 | — |

### Evaluator Verdict、置信度处置矩阵与恢复约定

- **决策门命令**：`--auto` / `--goal` 下调用 `evolve run decide` **必须显式传入 `--confidence <0-100>`**（防止省略参数绕过置信度门卫），语法：
  `evolve run decide <gate> --session <session> --verdict proceed|fix|escalate --confidence <score> [--target-step <step-id>] [--evidence <ref>] --summary "<evidence>"`（多 Worker/并行竞态时补充 `--actor <actor> --expected-orchestration-revision <rev>`）

| `CONFIDENCE_SCORE` | 漂移 / 门禁状态 | 协调器强制处置 |
| --- | --- | --- |
| `>= 60` | 无严重漂移且门禁满足 | `proceed`（或发现可定位修复项时 `fix --target-step <step-id>`） |
| `>= 60` | 严重漂移（`REGROUND_HALT`）或 `post-debug-escalate` | **强制 `escalate`**（`-y` 不可跳过），暂停并上报用户 |
| `< 60` | 任意（无漂移或有漂移） | **禁止自动 `proceed`**（Runtime 在无 `--evidence` 时亦拒绝）；必须 `escalate` 并向用户澄清/补证（或当有明确前置缺陷可修时 `fix --target-step <step-id>`） |

- **`fix` 重开与插链**：判定 `fix` 需要重跑上游执行/规划步骤时，**必须显式传入 `--target-step <step-id>`**（从 `--decision-only` 节点返回 `fix` 时为必填），Runtime 会把目标步骤至决策门间的步骤重开为 `pending`；若需新增修复步骤（如 `ew-debug` / `ew-plan --gaps`），先通过 `evolve session step add` 在 pending tail 插链再判定 `proceed` 或重开步骤。
- **`escalate` 解除与恢复流程**：提交 `--verdict escalate` 后，Runtime 将该步骤保留为 `completed` 且 `decision = "escalate"`，因此 `continuation.action` 仍停留在 `"decide"`（阻塞后续派发）。在用户提供澄清、确认风险或同意补救方案后，按以下顺序解除：
  1. 如需补救步骤，先通过 `evolve session step add` 在 pending tail 插入补救步骤（例如 `ew-debug` / `ew-plan --gaps`）；
  2. 重新执行 `evolve run decide <decision-id> --session <session> --verdict proceed|fix --confidence <score> [--target-step <step-id>] [--evidence <user-confirmation-or-ref>] --summary "<resolution>"`（并发竞态场景补充最新 `--actor` 与 `--expected-orchestration-revision`）；
  3. 只有 `proceed` 或带 `--target-step` 的 `fix` 会清除 `escalate` 阻塞并推进 `continuation.action`。
- **重试耗尽处理（`Retry limit exceeded`）**：当 `evolve run create` 抛出 `Retry limit exceeded for step: <step-id>` 时，严禁继续循环重试；立即停止自动重派，向用户报告失败历史并请求决策，或在用户授权/明确降级路径下通过 `evolve session step delete --step-id <step-id> --evidence "<retry limit exceeded>"` 跳过原步骤并 `chain insert` 新的诊断/补救步骤（如 `ew-debug` 或新 `step-id`）。
- **`post-execution`**：核验全部 Run 已 sealed 且无悬空门禁后，提交 `--verdict proceed --confidence <score>`，再调用 `evolve session complete --session <session>`（并发竞态场景附带最新 `--expected-orchestration-revision`）。
