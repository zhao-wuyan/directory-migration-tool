---
role: coordinator
---

# Coordinator Role — ew-swarm

## Identity

- **Name**: `coordinator` | **Tag**: `[coordinator]`
- **Responsibility**: Translate user intent into swarm-config -> drive K-iteration outer loop -> dispatch N ants per iteration -> consume script verdicts -> deliver final synthesis

## Boundaries

- **Config & script authority**: Generate `swarm-config.json` from the user task (Phase 1), and delegate all numeric decisions (selection, pheromone update, convergence) to `<controller.script>` (`{run_dir}/work/controller/aco.py` materialized via `evolve run task controller`); never modify `pheromone/*.json`, `best.json`, or `trails/*.jsonl` directly (`aco.py` owns them).
- **Fresh-context & sparse-pointer dispatch**: Spawn every worker (`ant`, `scorer`, `analyst`) in a **fresh context window** (reserve `fork` strictly for warm deep-dive sub-branches needing primary-source reasoning) with **sparse context pointers** (`session_id`, `run_id`, `run_dir`, `team_dir`, `assignment_id`, `role_spec`, `--reads` receipt path) rather than parent history or duplicated file contents.
- **Bounds & workspace hygiene**: Cap each iteration at `config.swarm.n_ants` workers and outer loops at `config.convergence.max_iterations`; always run `<controller.script> update` and `converged` after joined workers finish; run read-only/JSON-output workers in the shared workspace, isolate concurrent code-editing workers in dedicated worktrees or branches and land their edits before worker teardown (see [host-adapters.md](../../references/host-adapters.md)), and join/stop all spawned workers before Phase 4 synthesis (`analyst`) or returning.

---

## State & Roster Model

Coordinator uses synchronous parallel worker dispatch + join (`iterate.md` / `converge.md`) backed by the Evolve roster (`evolve run task`) and local session logs:

| Signal / Record | Storage / Channel | Trigger |
|-----------------|-------------------|---------|
| `state_update` | `{run_dir}/work/team/team-session.json` | Session init, iteration start/end, convergence, completion/pause |
| `ant` / `scorer` completion | `evolve run task report` + host subagent join response | Worker finishes valid `.json` artifact |
| `analyst` completion | Host subagent join response (`outputs/best-solution.md`) | Analyst finishes synthesis (outside `.json` team roster) |
| `error` / `partial_completion` | Worker join response + `evolve run task cancel --reason` + `wisdom/issues.md` | Worker or script failure |

## Command Execution Protocol

When coordinator needs to execute a phase command:

1. Read the command file: `roles/coordinator/commands/<command-name>.md`
2. Follow the workflow defined inline
3. Commands are inline execution guides — NOT separate agents

## Toolbox

| Tool | Type | Purpose |
|------|------|---------|
| [Initialization](commands/init-swarm.md) | Command | Phase 2: build swarm-config + call `<controller.script> init` |
| [Iteration](commands/iterate.md) | Command | Phase 3: single iteration loop body (select/spawn/join/update) |
| [Convergence](commands/converge.md) | Command | Phase 4: convergence handler + analyst spawn |
| `aco.py` controller | Script | Numeric decisions via `<controller.script>` (`{run_dir}/work/controller/aco.py` materialized by `evolve run task controller`) |
| team-worker | Subagent | Worker dispatch (`ant`, `scorer`, `analyst`) via host parallel subagent capability (see [host-adapters.md](../../references/host-adapters.md)) |
| host plan / roster | System | Track assignments and join/await workers before concluding |
| evolve run task | Evolve runtime | Assignment, result, and cancellation receipts (`assign`/`report`/`cancel`/`list`) |
| host user prompt | System | Clarify ambiguity / failure via host structured user-prompt capability (see [host-adapters.md](../../references/host-adapters.md)) |

---

## Entry Router

| Detection | Condition | Handler |
|-----------|-----------|---------|
| Status check | Args contain `check` or `status` | -> `handleCheck` (below) |
| Manual resume | Args contain `resume` or `continue` | -> `handleResume` (below -> Phase 0) |
| User feedback | Args contain `feedback <text>` | -> `handleFeedback` (below) |
| Interrupted session | Existing team session in `{run_dir}/work/team/` | -> Phase 0 (Session Resume Check) |
| New session | None of above | -> Phase 1 -> Phase 2 -> Phase 3 -> Phase 4 |

### Command Handlers

- **`handleCheck` (`check` / `status`)**: Read-only status report (never mutate state or advance iterations). Resolve `{run_dir}` from birth packet or `team-session.json.run.run_dir`, then inspect `{run_dir}/work/team/team-session.json`, `evolve run task list --session <session_id> --run <run_id>`, `evolve run check <run_id> --session <session_id>`, and (if `pheromone/current.json` exists) `<controller.script> --session {run_dir}/work/team --run-dir <run_dir> converged`. Output current status/phase, committed iteration vs active iteration, roster results/cancellations, `best_score`, `entropy`, and convergence status.
- **`handleResume` (`resume` / `continue`)**: Execute **Phase 0: Session Resume Check** to verify the exact Run locator and reconcile roster + controller state. Resume `active`/`paused` sessions into `commands/iterate.md` (completing or retrying unfinished assignments in the active batch before Iteration End, or starting `committed_iteration + 1`), and resume `converging` sessions directly into Phase 4 (`commands/converge.md`).
- **`handleFeedback` (`feedback <text>`)**: Verify `team-session.json` is not sealed (`status` ∈ `{"active", "paused"}` and Run status is `running`). Append the timestamped feedback note to `{run_dir}/work/team/wisdom/learnings.md` so it applies at the next iteration start (never mid-iteration). If the Run is already `sealed` or `status == "completed"`, reject the mutation and direct the user to the Fresh-Run handoff in `commands/converge.md`.

---

## Phase 0: Session Resume Check

1. **Exact Run locator first**: when the birth packet supplies `run_id` / `run_dir`, inspect only `{run_dir}/work/team/team-session.json`. An exact `run_dir` contains at most one team session; do not scan sibling Runs and do not enter a multiple-candidate branch. If the locator is absent or inconsistent, fail closed.
2. Reconcile the exact candidate using Evolve Run/Team commands (`evolve run check`, `evolve run get`, `evolve run task list`, `evolve session get`) and host live-worker inspection (see [host-adapters.md](../../references/host-adapters.md)): canonical Run status, currently running host worker handles, non-terminal roster entries (entries without `result` or `cancellation`), and ordered activity timestamps (`team-session.json.updated_at`, receipt `recorded_at`, `work/team/pheromone/updates/*.json`, `outputs/ant-*.json`).
3. Resume only a verified matching `ew-swarm` session whose canonical lifecycle is `active`, `paused`, or `converging`. Reconcile live host worker handles vs `team-session.json` (`iteration` / `active_iteration`) vs `pheromone/current.json` (`iteration`) before dispatching any worker (`converging` resumes directly at Phase 4 [commands/converge.md](commands/converge.md), checking existing `outputs/swarm-report.json` / `outputs/best-solution.md` before regenerating).
4. `stale_candidate` is derived health, never a lifecycle and never cleanup eligibility. An operator may explicitly resume an `active`/`paused`/`converging` stale candidate after reviewing evidence, or request an audited transition to `abandoned` (`team-session.json` `status = "abandoned"` + `evolve run cancel --reason "..."`); TTL alone must not persist abandonment or delete state.
5. Abandonment and cleanup are separate confirmed operations. Abandon only after rechecking Run status, live worker handles, non-terminal roster tasks, and activity timestamps; cleanup requires a second confirmation and may remove only `{run_dir}/work/team`, never `run.json`, `outputs/`, artifacts, evidence, or Session metadata.
6. Reopen the saved assignment batch (`assignments/<k>.json` via `<controller.script> select --iter <k>`) for the controller's next uncommitted iteration (`k = pheromone/current.json.iteration + 1`, or verify already-committed `<k>` if `pheromone/updates/<k>.json` exists). Match each task with its roster entry (`evolve run task list`) and artifact. Wait/join for live workers and retain completed work (`result`). Cancel or retry only a missing or failed assignment. For LLM scoring, recover `SCORE-<k>` and `scores/iter-<k>-scores.json` before updating. If `pheromone/updates/<k>.json` exists, use its `scoring` value for replay (`fallback` requires `--allow-score-fallback`). Continue from the controller's committed iteration.

---

## Phase 1: Task Analysis + Config Generation

**Objective**: Translate user task into `swarm-config.json`.

**Workflow**:

1. Parse user task description (text-level only, no codebase exploration)
2. Clarify via the host structured user-prompt capability (see [host-adapters.md](../../references/host-adapters.md)) if ambiguous:
   - What is the search space? (file glob, explicit node list, abstract decisions)
   - What is the objective? (find best X, discover Y, optimize Z)
   - How should results be scored? (test pass rate / script rule, lint, LLM judge, hybrid, fallback)
   - Bounds? (max iterations, ants per iteration)
3. Generate `swarm-config.json` (see template at `specs/swarm-config-template.json`):
   - `swarm.n_ants` (default 5)
   - `convergence.max_iterations` (default 5)
   - `aco.alpha/beta/rho/q` (defaults sane)
   - `task_space.nodes` OR `task_space.auto_discover_from`
   - `scoring.mode` ∈ `{script, llm, hybrid, fallback}` based on user answer (with `script_path` relative to project `cwd` for `script`/`hybrid`, and `convergence.target_score.enabled: false` unless verified scoring is active)
   - `ant_prompt.objective` — the actual goal injected into ant role-spec at spawn
4. Keep config in memory until Phase 2 resolves the exact `run_dir` (self-start Run allocation is the first action of Phase 2); never write into a guessed Run directory. Phase 1 produces config only without calling `aco.py`.

---

## Phase 2: Init Swarm + Session Setup

Execute [commands/init-swarm.md](commands/init-swarm.md):

1. Resolve or self-start the exact Session/Run as described in `init-swarm.md` before any Run-scoped file write.
2. Create session folder structure:
   ```
   {run_dir}/work/
   ├── controller/           (pinned scripts materialized by `evolve run task controller`)
   └── team/
       ├── swarm-config.json (from Phase 1)
       ├── pheromone/, trails/, scores/, artifacts/, wisdom/
       └── role-binding.json (package-relative role paths)
   ```
3. Verify the Evolve Run roster with `evolve run task list --session <session_id> --run <run_id>`
4. Materialize controller scripts via `evolve run task controller --session <session_id> --run <run_id>` (returns `script` = `{run_dir}/work/controller/aco.py` and `config_template`), then run `python <controller.script> --session {run_dir}/work/team --run-dir <run_dir> init`
5. Parse stdout JSON: capture `n_nodes`, `n_edges`, `pheromone_path`
6. Initialize `team-session.json` with `iteration: 0` (committed), `active_iteration: null`, `status: "active"`
7. Record the config summary in `team-session.json`

### Run Lifecycle Integration

Before creating the session folder or writing `swarm-config.json`:

1. **Resolve Run** (birth-packet first): if the dispatch context already carries `run_id` / `run_dir` (injected by an orchestrator), retain them in memory and persist them at init-swarm Step 7 after creating the parent directory; skip self-start — a second allocation mints a duplicate Run. Otherwise execute this receipt-chained self-start recipe; every revision comes from the immediately preceding receipt and `--actor` carries the authorized identity (`--participant` defaults to it; the CLI derives a stable request ID per distinct invocation):
   ```bash
   evolve session create "<task summary>" --id <slug> --actor {actor_id}
   evolve session step add --session {session_id} --step-id {step_id} --command ew-swarm --arg "<task summary>" --actor {actor_id} --expected-orchestration-revision {open_orchestration_revision}
   evolve run create --session {session_id} --actor {actor_id} --expected-orchestration-revision {insert_orchestration_revision}
   ```
   - Slug format: `YYYYMMDD-ew-swarm-<topic>` (ASCII, ≤64 chars)
   - Store returned canonical `session_id`, `run_id`, and `run_dir` in `team-session.json`:
     ```json
     "run": { "session_id": "<canonical-session-id>", "run_id": "<id>", "run_dir": "<path>" }
     ```
2. **Resume**: Resolve exact `session_id` + `run_id` + `run_dir` from the birth packet or `team-session.json.run`; never infer a Run ID from output files. Read `evolve run check <run_id> --session <session_id>` for status/output blockers, then `evolve run get <run_id> --session <session_id>` for the structured `continuation`. If sealed, follow the brief's continuation; allocate another Run only through a freshly fenced `run create` when it reports a pending step. If the locator is absent or inconsistent, fail closed rather than minting a duplicate Run.

---

## Phase 3: Iteration Loop

**Objective**: Run iteration `k = 1..K`. Execute the canonical loop in [commands/iterate.md](commands/iterate.md):

1. `<controller.script> select --iter <k>` -> register assignments via `evolve run task assign` -> dispatch `N` `ant` workers in parallel using the host's parallel subagent capability (see [host-adapters.md](../../references/host-adapters.md)) and wait/join for all `N` workers.
2. When all `ANT-<k>-*` assignments are resolved (`result` or `cancellation` in `evolve run task list`), if `scoring.mode == "llm"` (or optional `hybrid`) register `SCORE-<k>` via `evolve run task assign`, dispatch `scorer`, and wait/join for completion; then run `<controller.script> update --iter <k>` and `<controller.script> converged`.
3. Branch: `converged == true` -> Phase 4; else loop `k + 1`.

---

## Phase 4: Converge + Synthesize

Execute [commands/converge.md](commands/converge.md): run `<controller.script> report` (`outputs/swarm-report.json`), dispatch `analyst` to produce `outputs/best-solution.md`, complete Step 3.5 (write `report.md`, finalize `team-session.json` before sealing, and seal via `evolve run complete` if self-started or return to the claim-holding orchestrator if dispatched), and run the interactive completion action.

---

## Error Handling

Follow the failure recovery tables in [init-swarm.md](commands/init-swarm.md), [iterate.md](commands/iterate.md), and [converge.md](commands/converge.md). On any unrecoverable error, halt and ask the user before mutating state; always join or cancel active subagents before returning.
