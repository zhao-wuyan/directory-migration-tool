# Odyssey Host Runtime Adapters

Mode workflows describe host-neutral capabilities (parallel agents, structured user prompts, progress tracking, heartbeats). Map them to the active host runtime below:

## 1. Parallel Subagents & Batch Scans

| Capability | Antigravity | Codex | OMP |
|---|---|---|---|
| Parallel review / audit / archaeology / generalization scan | `invoke_subagent` (parallel), `swarm`, or `run_workflow` | `spawn_agent` (`agent_type`) / `spawn_agents_on_csv` + `wait_agent` | Native `task` (`context` + `tasks[]`) or eval `agent()`; `scout` research / `reviewer` review / `task` implementation |
| Lifecycle rule | **Always join, complete, or cancel every spawned subagent before advancing or ending scope** | Collect all batch CSV/agent outputs before merging; join/cancel children | Results auto-deliver; join at barriers or cancel before ending; never poll; use host `wait` only when fully blocked |
| Cross-angle CLI verification | `evolve agent exec "<prompt>" --to codex\|agy\|claude\|omp --mode analysis\|write` (pass `--session <id> --run <run>` only with `--to codex` or `--to omp`; for full flags, prompt template, and retry rules, read `evolve skill workflows show agent-usage`) | Same (bound Session/Run identifiers and CLI read instructions are passed to the Agent) | Bound Run supported for Codex and OMP; OMP strict analysis uses the one-shot restricted worker, not native children |

### Recommended Installed `agent_type` Mapping

When `evolve skill install` provisions `agents/` for the active host, dispatch Odyssey roles using these named definitions (falling back to inline role instructions when absent):
- **Archaeology / exploration**: `cli-explore-agent` (`cli_explore_agent` in Codex)
- **Debug hypothesis / fix**: `workflow-debugger` (`workflow_debugger`)
- **Audit / review dimensions** (`improve`, `review`, `security`, `defensive`, `ui`, `arch`): `workflow-reviewer` (`workflow_reviewer`)
- **Planex / Arch planning & interface design**: `workflow-planner` (`workflow_planner`), `workflow-plan-checker` (`workflow_plan_checker`)
- **Fix / execution / deepening**: `workflow-executor` (`workflow_executor`) or `impeccable-agent` (`impeccable_agent` for non-interactive `ew-impeccable` passes in `ui` mode)
- **Verification**: `workflow-verifier` (`workflow_verifier`), `workflow-integration-checker` (`workflow_integration_checker`)
- **OMP hosts**: map the same roles to native `scout`, `reviewer`, and `task` (or eval `agent()` with the same role instructions).

The [OMP host contract](../../ew-orchestrate/SKILL.md#omp-native-delegation-and-strict-cli-analysis) applies to every mode, including examples that retain Codex as their default. OMP CLI contract: `evolve agent exec <prompt> --to omp --mode analysis|write [--role <name>] [--model <selector>] [--timeout <milliseconds>] [--session <id> --run <id>]`. Defaults: analysis, `analysis=@plan` / `write=@task`, 600000 ms; model overrides role, roles only select models (not Plan mode or authorization). `OMP_EXECUTABLE` overrides the executable; `--trusted-extension` loads the bundled SDK bridge without an extra OMP npm dependency, community sandbox, or container. This is a one-shot restricted worker, not a managed OMP `start`/`send` session, and it does not support worktrees or `--stream`.

Analysis exposes only `read`/`grep`/`glob` plus six bound Run knowledge tools, with no `bash`/`eval`/`write`/`task` or ambient MCP. The six bound tools use shared in-process execution, not an MCP child process. Write exposes read/write tools, `bash`/`lsp`, and bound knowledge tools; never bypasses user approval or enables yolo. Agent duties and native `tools` lists are not OS permissions or proof of excluding additional MCP. Strict read-only security/defensive delegation must use `evolve agent exec --to omp --mode analysis` on OMP.

This CLI delegation is distinct from the OMP installable plugin: it does not load plugin tools and does not require the plugin to be installed. Plugin packaging and its in-process knowledge tools are a separate distribution surface owned by the OMP plugin branch.

Pass `--session` and `--run` together for Codex/OMP Run binding; Agy stays standalone. For an independent second opinion explicitly choose an authorized model (OMP `--model <selector>`), different from the original model for a cross-model claim. Same-model children are not independent-model verification. Do not create agent definitions or user configuration to obtain model access.

## 2. Structured User Prompts

- When a workflow specifies **structured prompt** at a non-`-y` decision gate: use `ask_question` in Antigravity, `request_user_input` in Codex, or `ask` in OMP.
- When `-y` (`auto_confirm: true`) is active, follow each mode's `-y` Decision Points table and record skipped human choices as `deferred`.

## 3. Progress Mirroring & Heartbeats

- **`phase_goals[]` in `outputs/session.json`** is the single authoritative campaign progress record.
- Mirror `phase_goals[]` updates via the host's native progress tracker when available (`update_plan` in Codex, `todo` in OMP; Antigravity hosts respect native `/goal` state and never re-emit `/goal` prose or recreate cleared UI state). OMP batches share background via `context` with one `tasks[]` item per independent slice; eval `agent()` handles follow the same join/cancel rule.
- **Optional `--heartbeat`**: update `session.json.updated_at` on each phase transition (if >15 min stalls occur, increment `progress_metrics.heartbeat_stalls` rather than `stale_count`; Antigravity `schedule` is used only when the user explicitly requests a recurring timer).

## 4. Run Artifact Metadata & Sealing

- Every mode stores artifacts exclusively under `{run_dir}/outputs/` (or workspace source files in mutable `FIX`/`DEEPEN`/`EXECUTE` phases). `outputs/session.json` is `required: true` in all modes; `evidence.ndjson`, `understanding.md`, `explore.json` (`debug`/`review`/`arch`), and `anchors.json` (`defensive`) follow each mode's `contract.produces` in `references/prepare/odyssey-<mode>.md` (`evidence.ndjson` and `understanding.md` are `required: true` in `debug`/`security`/`defensive`, and produced during full campaigns while `required: false` in `improve`/`planex`/`review`/`ui`/`arch`).
- Attach matching `kind`, `schema`, `role`, and `alias` from `references/prepare/odyssey-<mode>.md` `contract.produces` to:
  - Top-level `_meta` in JSON outputs (`session.json`, plus `explore.json` or `anchors.json`)
  - First record's `_meta` in `evidence.ndjson` (append-only NDJSON lines thereafter)
  - YAML frontmatter in `understanding.md`
- Set `current_state = "END"` in `outputs/session.json` before sealing atomically via `evolve run complete <run_id> --session <session_id> --actor <actor> --expected-orchestration-revision <rev> --expected-run-revision <run_rev> --summary "<summary>"`.
