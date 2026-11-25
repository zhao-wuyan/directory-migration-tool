# Workflow: Execute

Wave-based parallel execution with checkpoint resume, optional atomic commits (`--auto-commit`), and a scoped smoke self-check handing off to `ew-review`.

## Iron Law

Never mark a task completed without running its convergence criteria, verifying outputs match the task definition, and recording reproducible evidence.

---

## Step 0: Plan Availability & Degradation Routing

Resolve `current-plan`, `latest-review`, and `latest-debug` via the Run packet's `upstream_aliases`:
- **Plan present with incomplete tasks (or explicit `--task <TASK-ID>`)**: proceed to Step 1.
- **Plan absent, OR `current-plan` has zero incomplete tasks while unaddressed `latest-review` / `latest-debug` findings exist (and no `--task` override)**: follow the Degradation Routing table in [preparation](prepare.md). Exit via `evolve run cancel` or `evolve run transition <run_id> blocked` per `evolve skill workflows show session-run` (do NOT call `run complete` as a no-op), documenting reason, available upstream, and target command in `report.md` `next`/`concerns`. **Never proceed to Step 1+ without active tasks from a plan.**

## Step 1: Parse Execution Options

Apply explicit CLI flags first: `--task <TASK-ID>` (restrict execution to the specified task ID or comma-separated task IDs plus prerequisite checks), `--executor <tool>` (set `defaultExecutor` to `agent|codex|agy|claude|omp` without prompting), `--method <agent|cli|auto>`, `--auto-commit`, and `-y`.
If `-y` is set (skipping all interactive prompts) or `plan` already specifies remaining execution settings, use defaults. Otherwise resolve via structured prompt (`ask_question` or host equivalent; OMP `ask`) across enabled tools (`agent` / `codex` / `agy` / `claude` / `omp`):
- `executionMethod` (`auto` | `agent` | `cli` | tool) and `domainRouting` (`{frontend, backend, default: "agent"}`)
- `codeReviewTool` (optional post-execution diff review tool or `Skip`)
- `verificationTool` (smoke self-check tool or `Skip`)

## Step 2: Load & Normalize Plan

Read `current-plan`:
- If `plan.source_format == "pi-markdown"`, derive tasks, explicit/sequential dependencies, waves, and acceptance checks in memory from `plan.markdown` without overwriting `current-plan` or requiring `task_ids`/`wave_ids`.
- Otherwise load structured waves/tasks directly.

Resolve `executionMethod`, `defaultExecutor` (honoring `--executor` when provided), and `domainRouting`. **Task filter & checkpoint resume**: if `--task <TASK-ID>` is set, validate that the task exists (or `W001`) and execute only the targeted task(s) once dependencies are met; otherwise scan persisted task statuses (or `current-execution`/checkpoint records for `pi-markdown`) and start from the first wave containing incomplete tasks. If zero incomplete tasks exist and `latest-review`/`latest-debug` is bound, return to Step 0 Degradation Routing rather than completing as a no-op.

## Step 3: Load Project Rules & Optional Shared Exploration Notes

Complete the Run Knowledge Gate (`evolve skill workflows show session-run`): reuse bound `session-priors` for covered `coding` Wiki scope, and query `evolve search "<query>" --mode context` + `evolve kb get <id>` (with CLI `--reads`) for additional paths/categories, passing resolved constraints into each Step 4 task prompt.

When a concrete problem warrants cross-project research, optionally run `evolve experience status`, then search personal experience with a domain and versioned stack verified from the current project (for example `evolve experience search python python@3.12 "<problem>"`). Use non-sensitive query terms. Check applicability and conflicts before using a result. These references never override project rules or enter the required Knowledge Gate.

- **Optional exploration pre-seeding**: for multi-module plans or tasks needing cross-file/API discovery, optionally dispatch one fresh-context read-only exploration subagent (`cli-explore-agent` / `workflow-codebase-mapper` or host equivalent) before task fan-out to save shared markdown notes at `{run_dir}/work/exploration-notes.md` (run-local, outside repo source edits). Pass that path as a **context pointer** to Step 4 workers so implementers focus on implementation rather than repeating discovery.

## Step 4: Task Graph Frontier & Parallel Execution

- **Executor routing** (priority: task assignment > explicit `--executor` / `--method` > auto domain routing):
  - `frontend` (`.tsx/.jsx/.vue/.css/.html` or UI scope), `backend` (`.go/.rs/.java/.py/.sql/.proto` or API/DB scope), `general` (mixed, `.ts/.js`, config, tests). Log before dispatch: `TASK-001 [frontend] -> <executor>`.
- **Fresh-per-task & sparse pointer brief**: start **every** task worker with a **fresh context window** (never reuse a prior task's worker or fork the coordinator thread). Communicate via **sparse context pointers** rather than duplicating large files: include `PURPOSE`, `TASK`, `MODE: write`, `CONTEXT` (pointer to `current-plan` task ID/spec section, `{run_dir}/work/exploration-notes.md` if generated, prior dependency summaries/commits, and resolved Wiki constraints), `EXPECTED`, and `CONSTRAINTS` (`Files`, `Read First`, `Implementation`, `Convergence Criteria`).
- **Frontier scheduling alongside waves**: use `current-plan` waves as baseline ordering while maintaining the **task graph frontier** — any incomplete task whose dependencies (`depends_on` / prior wave prerequisites) are all `completed` (and merged, when isolated). Whenever a task finishes (and merges if isolated), recompute the frontier and dispatch newly unblocked ready tasks right away instead of waiting for unrelated slow tasks, respecting `--task` filtering and tracking wave/task completion cleanly in `waves[]`.
- **Workspace isolation & dedicated merger**:
  - **Disjoint direct wave** (default when tasks touch strictly non-colliding `files[]` without build contention): dispatch ready tasks in parallel inside the working tree and join before advancing dependent tasks.
  - **Isolated worktree + merger** (when parallel tasks share build targets or risk file collisions; resolve host parallel/isolation primitives via `evolve skill workflows show session-run`): give each task its own isolated worktree or branch based on the current integration tip. Each isolated implementer verifies its base matches the integration tip (resetting/rebasing if behind), implements and verifies convergence criteria, merges the latest integration tip back into its branch before reporting completion, and returns its branch/commit/summary pointer. A dedicated **merger** worker (or sequential coordinator merge) lands each completed branch onto the integration branch/working copy before recomputing the frontier and before deleting or tearing down isolated worker workspaces.
- **Dispatch paths**:
  - **`agent` path**: launch a fresh executor subagent (`workflow-executor` / `workflow_executor`, via the host's subagent/parallel dispatch capability mapped in `evolve skill workflows show session-run`; fall back to inline role instructions when absent; OMP hosts use native `task` or eval `agent()`, batching independent items with shared `context` + `tasks[]`, and join/cancel all children before ending the wave without polling) to implement -> verify convergence -> auto-fix (<=3 attempts) -> commit only if `--auto-commit` -> write `evidence/summaries/${task.id}.md` -> report status/checkpoint.
  - **`cli` path**: run `evolve agent exec "${prompt}" --to ${executor} --mode write` (supports `codex|agy|claude|omp`; include `--session ${session_id} --run ${run_id}` when `--to codex` or `--to omp`; `--to agy` runs standalone without `--session`/`--run`), verify convergence criteria against disk, write `evidence/summaries/${task.id}.md`, and commit only if `--auto-commit`. For OMP, `--role` selects a model only, `--model` overrides it, and `--timeout` is milliseconds; neither grants permission nor enters Plan mode.
- **Failure handling**: up to 3 focused retries (for CLI retries, narrow prompt then fall back to a fresh subagent). After 3 failures, mark `blocked` with checkpoint `{attempt: 3, last_error, partial_files, executor}` and mark unmet downstream dependents `upstream_blocked`. Prompt between waves/frontier batches only if a task blocked and `-y` is unset.

## Step 5: Post-Wave Validation

1. Check completed tasks have `evidence/summaries/${task.id}.md` (`missing_summary` warning).
2. Cross-check task statuses vs wave results (mismatch = critical).
3. Scan changed files against spec `allowed_languages`/`disallowed_imports` (hit = critical `tech_stack_violation`).
4. Optional CLI diff check via `evolve agent exec "<review changed files for cycles/dead code/breaking changes>" --to codex|agy|claude|omp --mode analysis`; bind Codex/OMP with both `--session`/`--run`. For OMP, this is the one-shot restricted worker. For an independent second opinion explicitly select an authorized different model (OMP `--model <selector>`), not a same-model child.
5. Clean up any remaining isolated task worktrees or temporary worker branches after confirming all completed work has landed.

Any critical failure marks the run `blocked` with recorded violations.

## Step 6: Optional Diff Review & Fresh Fixer

Unless `codeReviewTool == "Skip"`, run `evolve agent exec "<review git diff>" --to ${codeReviewTool} --mode analysis` (or a fresh read-only review subagent) and record summary findings. If in-scope fixes are applied before handing off to `ew-verify`/`ew-review`, dispatch a **single fresh fixer subagent** (fresh context window, never reusing an earlier task implementer) and re-run affected convergence checks.

## Step 7: Scoped Smoke Self-Check

Unless `verificationTool == "Skip"` or zero tasks completed, run one scoped smoke pass (narrowest build/test suites covering changed files + convergence evidence and quick sanity checks; formal static anti-pattern scanning belongs to `ew-verify` and untracked residual audit to `ew-review`) and write `outputs/self-check.json` (`overall: "passed"|"gaps_found"`). Note: `gaps_found` does **not** fail `self-check-passed`; record concerns for `ew-verify` and formal acceptance in `ew-review`.

## Step 8: Write Outputs & `report.md`

Write `outputs/execution.json` (`_meta: { kind: "artifact", schema: "artifacts/1.0", role: "primary", alias: "current-execution" }`, `plan_ref`, `status: completed|partial|blocked`, `waves[]`, `completed_tasks[]`, `blocked_tasks[]`), `outputs/task-results.json`, `outputs/change-manifest.json`, plus `outputs/self-check.json` (each JSON carrying matching `_meta`).

Write `report.md` with fixed sections (`Summary`, `Conclusion/Verdict`, `Discussion/Retrospective`, `Artifacts`, `Handoff/Next`), inline GateRecords for `execution-complete` and `self-check-passed`, and frontmatter:
- `verdict`: `ready` (all succeeded -> seal `--verdict done`), `ready_with_concerns` (non-critical blocked or smoke gaps -> seal `--verdict done_with_concerns`), `blocked` (critical failure -> `run transition ... blocked`, do NOT call `run complete`), `failed` (execution errored -> `run transition ... failed`, do NOT call `run complete`).
- `next`: `[{ command: ew-verify, reason: implementation complete; structural verification before formal ew-review acceptance, needs: [current-execution] }]`.

## Step 9: Knowledge Staging & Seal

Inspect `evidence/summaries/*.md`, deduplicate new patterns/pitfalls against Step 3 rules, and stage reusable findings through `report.md` frontmatter (`constraints`/`decisions`) for post-session closeout after `evolve session complete` (do **not** run `kb harvest` or invoke `ew-knowledge` closeout mid-chain). When execution resolves a non-obvious build/API/runtime pitfall, record a concise lesson via `evolve kb add "<lesson>" --kind episodic --type verified_fix --scene execute --session <session_id> --evidence <source-ref>`. Never write Wiki files directly.
Seal or transition the Run per `evolve skill workflows show session-run` with exit gates `--gate execution-complete --gate self-check-passed` (mapping `report.md` `verdict` per Step 8 and `session-run`).

---

## Error Codes

| Code | Condition | Recovery |
|------|-----------|----------|
| `E001` | No plan and no usable upstream | Run `ew-plan` first or route per Step 0 |
| `E002` | Entire wave blocked | Stop execution, mark dependents `upstream_blocked`, report checkpoint |
| `W001` | Task definition missing | Record error, skip task, continue wave |
| `W002` | Subagent dispatch failed | Retry once, mark `blocked` if still failing |
| `W003` | Delegate failed | Retry with narrowed prompt or subagent (max 3), then checkpoint `blocked` |
| `W004` | `--auto-commit` failed | Record warning; do not claim commit completion |
