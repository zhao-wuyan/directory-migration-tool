# 收尾与候选晋升协议

本文件定义任务 / Session 结束或独立知识收尾时，完成责任人（Completion Owner）遵循的规范流程：`Read → Refresh → Present → Ask → Execute → Verify`。

## 所有权与时机

- Run 完成阶段仅允许沉淀候选；已采纳决策或锁定约束不等同于 Wiki 发布授权。
- 长链中间 Run 继续推进既有链，不在中途插入发布审批；整体终端收尾或显式知识管理请求时执行本协议。
- `ew-knowledge` 自身与 `workflow-evolve` 的执行记录不进入自动沉淀，防止递归。

## 执行步骤

### 1. Read internally（只读收集与查重）

Session 完成时 Runtime 原子冻结证据并自动排队。只读查询当前 Session，按返回的真实状态选择唯一动作，不重复发起抽取：

```text
evolve kb closeouts <session-id> --root <project>
```

| 状态 | 下一步 |
| --- | --- |
| `pending` / `running` | 已入队；正常等待，不并发重跑。若长时间不推进，先 `evolve kb doctor`；确认没有正常工作的 Worker 后再恢复指定 Session。 |
| `failed` | 查明失败原因，修复后使用冻结回执定向重试：`evolve kb harvest session <session-id> --project <source-project-id> --receipt <receipt-id>`。 |
| `done` | 使用 job 的 `receipt_id` 与 `project`：`evolve kb candidates show <receipt-id> --project <source-project-id>`；零候选正常，不重跑。 |
| 无 job，但在 `unharvested` 中 | 历史已完成 Session；按实际 Session / project 使用 `kb harvest session`，不要为活跃 Session 构造回执。 |

`evolve kb harvest pending` 是项目范围的批量恢复，仅用于明确授权的知识维护；普通 Session 收尾不调用它。独立任务的 Stop 事件不等于最终收尾。只有显式收尾或用户确认最终完成后，独立对话执行 `evolve kb harvest conversation <conversation-id> --host <host> --project <source-project-id>`；独立 Skill 使用 `evolve kb harvest skill <execution-id> --skill <skill-id> --skill-version <version> --status <status> --transcript <messages.json> --project <source-project-id> [--knowledge-receipt <file>]`。独立 Skill 回执失败或仅登记时，使用 `kb harvest receipt <receipt-id> --project <source-project-id>`。历史批量扫描 `kb harvest scan` 仅用于指定维护范围。

重复投递按回执身份幂等处理，不重构或扩大证据范围。若候选与唯一活跃、未锁定的 project `reference` 正文完全一致，收尾会经治理自动合并 `sources` 并留存变更记录，其余候选保留待审。- 先用 `evolve kb search` / `evolve kb get <id|q-N:idx>...`（支持短前缀与批量读取）核对每条候选与现有活跃 Wiki 的重复、重叠或冲突，保留双方证据；`propose` / `apply` 必须使用 `kb get` 返回的规范完整条目 ID。可在授权前用 `evolve kb propose` 生成待审变更预览（`kb propose` 仅写入 `<root>/.evolve/closeout.db` 暂存区，不修改 `wiki/entries/`；真正修改 `wiki/` 需在授权后执行 `kb apply` 或 `kb candidates publish`）：

```text
evolve kb --root <project> propose <change.json> --closeout <closeout-id> --candidate <candidate-id>
evolve kb --root <project> propose <change.json> --evidence <receipt-id>
evolve kb --root <project> changes [<change-id>]
```

- 没有合格候选时直接记录零候选回执并正常结束。
- **日期 / commit SHA 锚点与被拒绝方案检查**：涉及代码实现或架构决策的候选必须附带回执/Git `HEAD` 日期与 `<repo>@<sha>` 锚点；若候选属于被拒绝的功能或设计方案（`.out-of-scope/` 概念级记录），先按概念相似度检索已有 Wiki（并将仓库现存 `.out-of-scope/*.md` 仅作只读参考），确认为持久性架构/范围拒绝而非“已实现功能”或临时延期后，再通过 `kb propose` → `kb apply`（或首次全新 `reference` 授权发布）纳入受治理 Wiki，禁止绕过 `evolve kb` 直写文件。

### 2. Present（结构化呈现）

向用户逐条展示每个可处理候选的：
- 精确 `candidate-id`、建议动作（新增 / 更新 / 追加来源 / 标记冲突或取代 `superseded` / 跳过）与理由；
- 真实验证证据锚点（完成回执、消息 ID、命令结果，以及代码类断言对应的日期与 commit SHA）；
- 与既有 Wiki 条目（含概念级被拒绝方案）的命中对照关系（不要将不可信原始对话摘录直接当作已验证正文）。

### 3. Ask（人类审查与固定授权）

通过宿主提供的结构化用户提问能力一次性收集**固定候选 ID 列表、关系裁决与发布/分流动作**的明确授权：
- 必须提供选择、拒绝与延期选项；群组确认仅对本次展示的固定 ID 列表生效。
- `-y`、高重要性标记或机器建议**不能**替代人类发布确认；无提问工具且无先前针对同批固定 ID 的显式授权时，直接延期保留待审查清单，不要输出裸命令敷衍了事。
- 不得为清空待办而伪造 `duplicate` 等裁决。

### 4. Execute（按固定授权逐项执行）

仅对明确批准的固定 ID 执行对应操作（禁止 `--all` 扩大选择范围）：

```text
# 路径 A：对审核通过的变更执行 Wiki 应用或直接发布
evolve kb --root <project> apply <change-id>
# kb candidates publish 仅适用于创建无 matched_entry 与 related_entries 的全新 mode: reference 条目；
# 创建 constraint、存在关联条目或执行 update/promote/retire 时，必须走 kb propose → kb apply
evolve kb candidates publish --root <project> <closeout-id> <candidate-id> "<title>" [--entry entries/<slug>]

# 路径 B：问题可归因于具体 Skill 或需要新流程包时按序分流
# 1) 先用 kb candidates route 判定路线（wiki | skill-addition | skill-design | deterministic-check；review-required 需补充归因）
evolve kb candidates route --root <project> <closeout-id> <candidate-id> [--skill <skill-id>] [--skill-version <version>]
# 2) 按需补充检索历史运行信号与会话摘要（纯本地增量，不依赖 LLM）
evolve --root <project> history collect
evolve --root <project> history search "<query>" --signals-only
evolve --root <project> session review <session-id> --no-llm
```

根据 `kb candidates route` 返回的 `route` 执行交接：
- **`deterministic-check`**：不写 Wiki 或修改 Skill；由实际工程任务落实为代码、测试、Lint 或 Hook，并保留复现和验证证据。
- 后续 Run 的知识使用反馈遵循 `evolve skill workflows show session-run`，在 Run 封存前写入既有知识回执；不将检索命中视为采纳。
- **`skill-design` / `skill-addition`**：统一交由 Agent 执行的 **`workflow-evolve`**（新建或结构重构遵循 `ew-skill-design` 封装规范），先基于候选证据、`evolve history` 与 `memory.db` 给出《自进化策略》（Skill 修改或创建），经用户确认后实施并用 `evolve skill validate` 校验；若需使用离线自动化回放状态机，可继续按 `evolve skill design task/run/review/replay/apply` 或 `skills/workflow-evolve/scripts/evolve-loop.mjs` 执行。

### 5. Verify（回读确认）

执行后回读 `evolve kb get <id>` / `evolve kb changes <change-id>` / `evolve kb candidates show <receipt-id>`，如实报告成功发布的条目 ID 与仍处于待审查/延期/阻塞状态的候选项。

## 个人跨项目经验审阅

`evolve experience pending` 读取配置允许的已完成项目，按封存证据列出本地候选；不上传内容、不另调模型。候选提炼与正式 Wiki 发布、项目完成互不阻塞。

用 `evolve experience review <候选ID>` 查看精确待发正文、目标账号、同栈可能相关项、建议关系和审批哈希。任何正文或验证摘要含有内部信息时暂缓，不应发布。相同经验仅补验证来源；发现冲突先展示双方，不覆盖旧条目。人工裁决重复或冲突时，用 `--relation duplicate|conflict --related <条目ID>` 重新预览。相似候选列表不是完整的冲突证明。

按本协议一次获取固定条目及动作的授权；拒绝或延期不执行写入。批准后由操作者在 CLI 运行 `evolve experience publish <候选ID> --relation <已确认关系> --approval <该预览的哈希>`，如预览指定过 `--related`，发布时带上相同 ID。候选、目的地或参考版本变化须重新预览和确认；`-y` 不是授权。发布回读成功后保留本地回执。服务返回失败或结果未知时，不自动重发，先重新预览并核对远端状态。
