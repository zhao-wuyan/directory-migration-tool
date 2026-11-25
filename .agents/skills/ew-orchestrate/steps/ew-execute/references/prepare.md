# Pre-flight: ew-execute

Verify plan availability, upstream compatibility, and execution invariants before running [workflow](workflow.md).

## Degradation Routing (when `current-plan` is absent or already completed with unaddressed findings)

Never proceed to Step 1+ without active incomplete tasks, and never fabricate a plan artifact or silently skip bound `latest-review`/`latest-debug` findings when all tasks in `current-plan` are already completed (unless re-running a specific `--task`). Inspect injected upstreams and route:

| Condition & available upstream | Scope | Action |
|---|---|---|
| `current-plan` absent + `latest-review` | `<= 3` findings, single concern (no multi-module DAG needed) | Cancel/transition Run -> Evolve internal `ew-companion` route with `latest-review` |
| `current-plan` absent + `latest-review` | `> 3` findings or cross-module architecture | Cancel/transition Run -> `ew-plan --gaps` (or `ew-odyssey-planex`) |
| `current-plan` 100% completed + unaddressed `latest-review` (no `--task`) | Any actionable finding | Do NOT complete as a no-op -> Cancel/transition Run -> `ew-companion` (`<= 3` findings, single concern) or `ew-plan --gaps` |
| `current-plan` absent (or 100% completed) + `latest-debug` (`latest-fix-directions`) | Single-concern clear fix vs cross-module / multi-task DAG | `ew-companion` for single-concern clear fix; `ew-plan --gaps` (or `ew-odyssey-planex`) otherwise |
| `current-plan` absent + no usable upstream | — | Report `E001`, transition Run `blocked` / cancel, require `ew-plan` first |

Never call `run complete` without real executed work and `outputs/execution.json` — exit degradation via `evolve run cancel` (or `evolve run transition ... blocked`) per `evolve skill workflows show session-run` and record the re-route reason in `report.md` (`concerns`/`next`).

## Artifact Compatibility Recovery

For an incompatible sealed input, inspect it via `evolve run read artifact`, cancel the attempt with current revision fences, insert a producer step ahead of the pending consumer to republish a derived compatibility artifact + receipt, verify publication, and dispatch via `evolve run create`. Never bypass with chain skip, Run rebind, registry mutation, or mutating the source artifact.

## Execution Invariants

- **Plan interpretation**: handle `source_format: pi-markdown` by deriving ordered tasks, dependencies, and acceptance checks from `markdown` without writing back over `current-plan`.
- **Fresh-per-task & sparse pointers**: start every task implementer with a **fresh context window** (one fresh worker per task; reserve `fork` strictly for warm deep-dive branches, never for routine task execution) and communicate via **sparse context pointers** (task ID/spec section, optional shared exploration notes path, resolved Wiki constraints, and prior summary/commit pointers).
- **Task Graph Frontier & workspace isolation**: treat `current-plan` waves as baseline ordering while unlocking downstream tasks dynamically on the **DAG frontier** as soon as their dependencies complete (and merge, if isolated). Parallelize non-colliding `files[]` directly, or isolate concurrent/build-contending tasks in per-task worktrees/branches (mapped via `evolve skill workflows show session-run`) and merge them sequentially via a dedicated merger before worker/worktree teardown. Atomic git commits run only when `--auto-commit` is enabled.
- **Fresh review-fixer isolation**: when optional post-execution diff review surfaces in-scope fixes inside `ew-execute`, resolve them in a single **fresh fixer subagent** rather than reusing an earlier task implementer.
- **Evidence & smoke scope**: record real per-task commands, files, and convergence checks (<=3 fix attempts before `blocked` checkpoint + `upstream_blocked` propagation). `self-check` is only a scoped build/test smoke before `ew-verify` (structural/coverage + canonical static anti-pattern scan) and `ew-review` (formal multi-dimensional acceptance) — `gaps_found` does not block completion.
- **Incremental learnings & issues**: track `issue_id` resolution (`open`/`registered` -> `analyzed` -> `planned` -> `resolved`) in `outputs/execution.json`/`task-results.json`, and update an external issue tracker ONLY with explicit user authorization (confirm unless previously authorized; `-y` alone does not grant external write authorization); stage candidate findings in `report.md` frontmatter (`constraints`/`decisions`) or `evolve kb add --kind episodic`, deferring `ew-knowledge` closeout/`kb harvest` until after `evolve session complete` (never write Wiki files directly).
