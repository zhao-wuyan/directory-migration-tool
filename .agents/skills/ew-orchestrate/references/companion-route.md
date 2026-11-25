# Evolve Companion 内部路由

`ew-companion` 是 Evolve 内部的轻量单步直行通道，使用同一套 Session/Run 生命周期，省略 `ew-analyze` / `ew-plan` 多阶段拆解、默认多 Agent 拆分与复杂决策门；它是 `ew-orchestrate/steps/ew-companion/` 中的真实可绑定步骤，不创建第二套状态协议。

## 路由原则与 `END` / 主链边界

**收敛 Session 阈值与同主题复用（Converged Thresholds & Topic Reuse）**：现代强模型能够直接处理常规单关注点改动与只读排查。**不要为了纯只读排查、临时探针试算或微小单点修改而建 Session**；即使需要 Run 留档，也应优先复用同主题下仍处于 `open` 的 Session（通过 `session step add` 追加新 Run，`run-001`, `run-002`…），避免一轮对话建一个一次性 Session。**不以文件数量（如 `<= 2` 文件）作为是否走 `ew-companion` 的硬门槛**（`implement` / `fix` / `refactor` 等动作词不覆盖复杂度判断）。

| 条件 | 路由 | 说明 |
| --- | --- | --- |
| 纯问答、只读解释、状态查询、**零生产/交付物改动的只读排查/试算/探针脚本**，或**无需留档审计的微小单步修改** | `END` | 不打开 Session，直接基于只读工具与 Knowledge Gate 作答或完成微调；有价值的经验直接通过 `evolve kb add --kind episodic` 留存 |
| 涉及**实质生产源码或工程交付物改动**、需要 Run 验证留档的单一关注点任务（**不限文件数**，无需多阶段工件交接或正式门禁） | `ew-companion` | 优先复用同主题 `open` Session 追加 `ew-companion` 步骤（无匹配时才新建 Session），利用完整模型能力直接定位、改动并验证（默认不拆分多 Agent） |
| 需架构/方案权衡、跨包/多模块耦合（`>= 3` 模块）拆解、`--goal` 长链，或显式要求类型化工件与独立验收评审 | 主编排链 | `ew-analyze` / `ew-plan` / `ew-execute` / `ew-verify` / `ew-review` |

## 最小生命周期

1. 确认满足准入门槛（已排除可走 `END` 的只读排查/微小微调；确属单一关注点、无需下游类型化工件消费、无需正式决策门或跨包架构分解、可由直接检查/测试验证）。
2. **先查开放 Session 再派发**（`evolve session list --status open`）：
   - **若存在同主题/同模块范围的兼容 `open` Session**：直接通过 `evolve session step add` 追加新 `ew-companion` 步骤（`--step-id <step-id> --command ew-companion`）并派发新 Run（同一 Session 下 `run_id` 按 `run-001`、`run-002` 递增；单协调者顺序流省略 `--actor` 与 `--expected-orchestration-revision`，多 Worker/并行/重试竞态时再附带）；
   - **仅当无兼容 `open` Session 时**：打开单步 Session 并派发首个 Run：

```text
evolve session create "<intent>" --id <slug> --chain ew-companion
evolve run create --session <session-id>
```

3. **按需读取上下文（极简仪式）**：可用原生文件工具直接读取 `skill_entry_path`（`evolve run read skill` 为可选项，`run complete` 不再强制要求，仅在需要固定 commit 隔离或审计追踪时使用）；简单交付物修复可省略 `knowledge-reads.json`（仍建议在探索前用 `evolve search "<topic>" --mode context` 快速检索约束）；如有 `latest-review` / `latest-debug` 上游输入通过 `evolve run read artifact` 读取。
4. 直接在当前主上下文利用完整模型能力定位目标、完成边界内改动并执行最窄验证命令（**默认不拆分子 Agent，也不套用 `ew-analyze`/`ew-plan` 仪式**）。对于简单交付物修复，`outputs/companion-log.md` 为**可选项**（仅在需要留档或较复杂改动时写入 `{run_dir}/outputs/companion-log.md` 并带匹配 `companion-log/1.0` 的 YAML frontmatter；`run_dir/outputs` 仅在实际写文件或步骤契约要求时惰性创建）。若过程中解决了非显而易见的问题或环境坑点，仍直接通过 `evolve kb add "<lesson>" --kind episodic --type verified_fix --scene coding --session <session-id> --evidence <source-ref>` 记录适用条件、根因、方法和真实验证证据；按公共 session-run 规范先记录实际知识反馈，然后封存当前 Run（若后续已有 pending 步骤需一并派发，可加 `--next`）：

```text
evolve run complete <run_id> --session <session-id> --summary "<observed change and verification>" --verdict done|done_with_concerns [--next]
```

5. **延迟关闭 Topic Session（Deferred Closeout）**：
   - **预计同一主题仍有后续迭代/追问时**：完成 `evolve run complete` 后**保持 Session 为 `open`**（不要每轮立即关闭），等待下一轮复用该 Session 追加步骤；
   - **确认当前主题已收口或属一次性完结任务时**：再执行 `evolve session complete --session <session-id>`，自动冻结证据并进入收尾队列；先检查 `evolve kb closeouts <session-id>`，按状态转交 `ew-knowledge`（不重复抽取正在运行的任务）（`evolve skill workflows show ew-knowledge`）处理候选知识（候选为零同样正常完结）。不要在业务任务执行中自动调用 `workflow-evolve` 或 `evolve session review`；仅当 `evolve_reminder` 确实反映了 Skill/编排流程本身的缺陷时，在收尾简要告知用户可显式使用 `/workflow-evolve`。

## 退出与升级

| 发现 | 处理 |
| --- | --- |
| 目标不明确或缺少完成条件 | 若仍属单步改动，暂停询问或 `evolve run cancel` 后记录阻塞并在澄清后重派 |
| 需要深入根因调查或演化为跨模块/跨包架构改动（`>= 3` 耦合模块或接口重设计） | **同 Session 升级**：保留当前发现，先对活跃 Run 执行 `evolve run cancel`（步骤退回 `pending`），再用 `evolve session step delete --step-id ew-companion-1 --evidence "<scope expanded>"` 删除单步，并用 `evolve session step add` 追加 `ew-analyze` / `ew-debug` 等多步链（禁止遗留孤立 open Session） |
| 需要显式任务 DAG 拆解或正式多步实现/独立审查门禁 | 同上执行 `run cancel` + `session step delete --step-id ew-companion-1`，在同 Session 内 `session step add` 追加 `ew-plan` → `ew-execute` → `ew-verify` / `ew-review` |
| 需要长期多轮审计或并行探索 | 关闭/跳过当前轻量链后，明确提示用户显式调用 `/ew-odyssey` 或 `/ew-swarm` |
