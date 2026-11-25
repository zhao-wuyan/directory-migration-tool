# Command: init-swarm

## Inputs

- `swarm-config.json` from Phase 1 (in memory until the exact Run directory is resolved)
- canonical `session_id` from the birth packet or self-start receipt
- `skill_root` = directory containing the active `ew-swarm/SKILL.md` (do not assume project cwd or a particular mirror)

## Workflow

### Step 0: Resolve Run authority

Perform the coordinator role's **Run Lifecycle Integration** before creating `work/team` or writing config. Obtain the exact `session_id`, `run_id`, and `run_dir` from the birth packet or receipt; do not infer a Run path from a local sidecar.

### Step 1: Resolve paths and materialize controller

Invoke `evolve run task controller --session <session_id> --run <run_id>` to materialize the pinned controller scripts into `{run_dir}/work/controller/` and capture `controller.script` (`{run_dir}/work/controller/aco.py`) and `controller.config_template`:

```
run_dir = <exact path returned by Run birth packet or team-session.json.run.run_dir>
session_path = Path(run_dir) / "work" / "team"
controller_script = <script returned by evolve run task controller>  # {run_dir}/work/controller/aco.py
# Do not prepend pwd to run_dir: it may already be absolute.
```

### Step 2: Create session directory tree

Create `<session_path>` and subdirectories (`pheromone/history`, `trails`, `scores`, `artifacts`, `wisdom`) cross-platform (do NOT use POSIX brace expansion `{...}` on Windows PowerShell):

```python
python -c "from pathlib import Path; s = Path(r'<session_path>'); [ (s/d).mkdir(parents=True, exist_ok=True) for d in ('pheromone/history','trails','scores','artifacts','wisdom') ]"
```

### Step 3: Write swarm-config.json

Write the Phase 1-generated config to `<session_path>/swarm-config.json`.

Validate before write:
- `task_space.nodes` OR `task_space.auto_discover_from` present
- `swarm.n_ants` >= 2 (single-ant defeats swarm purpose)
- `convergence.max_iterations` >= 1
- If `scoring.mode` is `"script"` or `"hybrid"`, `scoring.script_path` MUST be non-empty and resolve to an existing `.py` file relative to project `cwd` (or an absolute path) defining `def score(ant_artifact) -> float` (otherwise `aco.py` silently degrades to fallback if omitted or exits 2 if missing)
- If `scoring.mode` is `"fallback"`, `convergence.target_score.enabled` MUST be `false`

### Step 4: Verify Evolve Run roster

```bash
evolve run task list --session <session_id> --run <run_id>
```

### Step 5: Write role-binding.json

```json
{
  "ant": "roles/ant/role.md",
  "scorer": "roles/scorer/role.md",
  "analyst": "roles/analyst/role.md"
}
```

Saved at `<session_path>/role-binding.json` — in a version-bound Run, workers resolve these package-relative paths via `evolve run read skill <run_id> <role_path> --from SKILL.md --session <session_id>` (outside Run mode, resolve under `<skill_root>/`).

### Step 6: Call aco.py init

```text
python <controller_script> --session <session_path> --run-dir <run_dir> init
```

Parse stdout JSON. On `status: "error"`:
- exit_code 2 -> config validation error -> ask user via host structured user-prompt capability (see [host-adapters.md](../../../references/host-adapters.md)) to fix
- exit_code 1 -> runtime error -> log to issues.md + retry once

On success, capture:
- `n_nodes` — search space size
- `n_edges` — initial edge count
- `pheromone_path` — confirm written

### Step 7: Initialize team-session.json

```json
{
  "run_id": "<run-id>",
  "session_id": "<canonical-session-id>",
  "task_description": "<user task>",
  "status": "active",
  "team_name": "swarm",
  "skill": "ew-swarm",
  "iteration": 0,
  "active_iteration": null,
  "max_iterations": <config.convergence.max_iterations>,
  "n_ants_per_iter": <config.swarm.n_ants>,
  "config_path": "swarm-config.json",
  "pheromone_path": "pheromone/current.json",
  "roles": ["coordinator", "ant", "scorer", "analyst"],
  "scoring_mode": "<config.scoring.mode>",
  "active_workers": [],
  "completed_iterations": [],
  "completion_action": "interactive",
  "created_at": "<iso8601>",
  "updated_at": "<iso8601>",
  "run": { "session_id": "<canonical-session-id>", "run_id": "<run-id>", "run_dir": "<run-dir>" }
}
```

### Step 8: Initialize wisdom files

Create empty wisdom files with headers:
- `wisdom/learnings.md` — cross-iteration insights
- `wisdom/decisions.md` — config refinements made mid-pipeline
- `wisdom/issues.md` — errors and hallucinations log

### Step 9: Log initialization state_update

Save the phase summary in team-session.json.

### Step 10: Proceed to Phase 3 (iterate.md)

Do NOT spawn any workers in this command. First spawn happens in iterate.md step 4.

## Success Criteria

- `{run_dir}/work/team/swarm-config.json` exists and validates
- `{run_dir}/work/team/pheromone/current.json` exists with `iteration: 0`
- `{run_dir}/work/team/task-space.json` exists with `n_nodes > 0`
- team-session.json initialized with `iteration: 0`

## Failure Recovery

| Failure | Action |
|---------|--------|
| Config invalid | Ask user via host structured user-prompt capability (see [host-adapters.md](../../../references/host-adapters.md)), regenerate, retry |
| `aco.py init` runtime error | Log to issues.md, retry once, then ask user (abort/refine) |
| Directory creation fails | Check disk space / permissions, retry |
| Roster conflict / existing session | Resolve the exact `run_id` / `run_dir`, inspect its one `work/team/team-session.json`, and offer resume only if lifecycle reconciliation verifies a matching `active`, `paused`, or `converging` `ew-swarm` session; otherwise fail closed |

### Run Conflict Recovery Contract

1. Start from the birth-packet `run_id` / `run_dir`. Do not scan sibling Runs and do not treat an arbitrary existing team name as a resumable match.
2. Inspect the exact team session and reconcile canonical Run status (`evolve run check` / `evolve run get`), live host worker handles (via host live-worker inspection in [host-adapters.md](../../../references/host-adapters.md)), non-terminal roster tasks (`evolve run task list` entries lacking `result` and `cancellation`), and ordered activity timestamps (`team-session.json.updated_at`, `evolve session get` receipt `recorded_at`, `work/team/pheromone/updates/*.json`, `outputs/ant-*.json`).
3. Offer **Resume** only when the exact candidate is a verified matching `ew-swarm` session with lifecycle `active`, `paused`, or `converging` (`converging` resumes directly at Phase 4 `converge.md`). If health is `stale_candidate`, show the evidence and require an explicit operator choice; never convert stale health into cleanup eligibility.
4. If exact locator evidence is absent, mismatched, `unknown`, or `inconsistent`, fail closed without scanning or guessing an unlocated candidate.
5. Do not offer a generic "clean or resume" action. Transitioning to `abandoned` requires an explicit audited update (`team-session.json` `status = "abandoned"` + `evolve run cancel --reason "<evidence>"`) after all liveness/activity checks, and cleanup requires a second confirmation that removes only `{run_dir}/work/team` coordination state, never Run authority or `outputs/`.
