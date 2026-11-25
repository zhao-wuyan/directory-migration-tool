# Command: iterate

## Two Phases per Iteration

| Phase | Trigger | Action |
|-------|---------|--------|
| **Iteration start** | Phase 3 invoked OR previous iteration `converged == false` | Run "Iteration Start" workflow (select -> assign -> dispatch -> join) |
| **Iteration end** | All `N` dispatched ant workers have joined / finished | Run "Iteration End" workflow (verify roster -> optional score -> update -> converged?) |

## Iteration Start Workflow

### Step 1: Determine iteration number

Compute `k` from the controller's committed iteration in `{run_dir}/work/team/pheromone/current.json` (NOT from an in-flight `active_iteration`):

```text
committed_iteration = pheromone/current.json.iteration   # 0 right after init
k = committed_iteration + 1
```

If `k > session.max_iterations`: force converge to Phase 4 (`converge.md`) (safety net). If `assignments/<k>.json` already exists on resume, `select --iter <k>` will deterministically replay that batch.

### Step 2: Call aco.py select

Use `<controller_script>` returned by `evolve run task controller --session <canonical-session-id> --run <run-id>` (`{run_dir}/work/controller/aco.py`):

```text
python <controller_script> --session {run_dir}/work/team --run-dir <run_dir> select --iter <k>
```

Parse stdout JSON. Expected:
```json
{
  "status": "ok",
  "iteration": <k>,
  "n_assignments": <N>,
  "assignments": [
    {
      "ant_id": "ANT-<k>-<i>",
      "start_node": "<node>",
      "edge_preferences": {"a::b": 0.4, ...},
      "max_path_length": <int>,
      "iteration": <k>
    }, ...
  ]
}
```

On error -> log to issues.md and ask user via host structured user-prompt capability (see [host-adapters.md](../../../references/host-adapters.md)) to retry or abort.

For a Run, `select --iter <k>` persists `assignments/<k>.json` before dispatch. Re-run the same command after an interruption to recover the original IDs and start nodes. Use the roster and task mapping to reuse live jobs and completed artifacts. Create a worker only for an assignment that still needs one.

### Step 3: Record ant assignments in Evolve roster

Register each `ANT-<k>-<i>` assignment sequentially via:
```bash
evolve run task assign --session <canonical-session-id> --run <run-id> --id ANT-<k>-<i> --role <worker-id> --path outputs/ant-<k>-<i>.json --actor <coordinator> --expected-orchestration-revision <revision>
```
(Update the host progress plan if available.) Assignment IDs `ANT-<k>-<i>` are persisted in `assignments/<k>.json` and tracked by the Evolve team roster (`evolve run task assign` / `evolve run task list`). After registering all `N` assignments, capture the latest `orchestration_revision` returned by the final `assign` receipt (or `evolve session get --session <canonical-session-id>`) as `<current_orchestration_revision>`.

### Step 4: Persist active batch & dispatch N ant workers in parallel

First record the in-flight batch in `team-session.json` (leave `session.iteration` at the controller's committed iteration until `aco.py update` succeeds):

```text
session.active_iteration = <k>
session.active_workers = [<list of ant IDs>]
```

Then dispatch one worker per assignment in parallel using the host's parallel subagent capability (see [host-adapters.md](../../../references/host-adapters.md)). Start every `ant` worker with a **fresh context window** (reserve `fork` only for warm deep-dive branches needing primary-source context) and pass only the **sparse pointer payload** below (avoid duplicating coordinator history or large file contents), then **join/await all dispatched workers** before Iteration End (in a version-bound Run, instruct each worker to read `roles/ant/role.md` via `evolve run read skill <run-id> roles/ant/role.md --from SKILL.md --session <canonical-session-id>`):

```text
## Role Assignment
role: ant
role_spec: roles/ant/role.md
team_dir: <session_path>                     # {run_dir}/work/team
session_id: <canonical-session-id>           # Evolve canonical Session ID (for evolve run task report --session)
run_id: <run-id>                             # Evolve Run ID (for evolve run task report --run)
run_dir: <exact run_dir from the birth packet or team-session.json.run.run_dir>
assignment_id: ANT-<k>-<i>                   # matches assignment.ant_id
worker_id: <worker-id>                       # exact --role registered in evolve run task assign (e.g., ANT-<k>-<i>)
expected_orchestration_revision: <current_orchestration_revision>
requirement: <config.ant_prompt.objective>
inner_loop: false

## Assignment
<assignment JSON>
```

### Step 5: Proceed on synchronous join

Once all `N` dispatched ant workers have finished (joined), proceed directly to the **Iteration End Workflow** below.

---

## Iteration End Workflow

### Step 1: Verify completion

Check `evolve run task list --session <canonical-session-id> --run <run-id>` for `ANT-<k>-*`. Every assignment is resolved only when it carries either `result` or `cancellation` in the Evolve roster (if any worker failed or returned `partial_completion` without calling `evolve run task report`, quarantine/remove any invalid `outputs/ant-<k>-<i>.json` outside `outputs/` and run `evolve run task cancel --session <canonical-session-id> --run <run-id> --id ANT-<k>-<i> --actor <coordinator> --expected-orchestration-revision <rev> --reason "<failure/partial_completion>"`). If any `ANT-<k>-*` still has neither `result` nor `cancellation`, wait for the remaining live worker(s) before proceeding.

If all `ANT-<k>-*` assignments are resolved (`result` or `cancellation`) -> proceed (halt and ask the user if >50% of ants in the iteration failed/cancelled or if zero valid artifacts remain).

### Step 2: (Conditional) Assign & dispatch scorer

If `config.scoring.mode == "llm"` (or `"hybrid"` when LLM scoring is used for this iteration):
1. Register the scorer assignment in the Evolve roster before dispatch:
   ```bash
   evolve run task assign --session <canonical-session-id> --run <run-id> --id SCORE-<k> --role <scorer-worker-id> --path work/team/scores/iter-<k>-scores.json --actor <coordinator> --expected-orchestration-revision <revision>
   ```
   Capture the updated `orchestration_revision` from the `assign` receipt.
2. Dispatch a `scorer` worker in a **fresh context window** (never forked from coordinator or ant history, keeping scoring blind) via the host parallel subagent capability (see [host-adapters.md](../../../references/host-adapters.md)) and **wait/join** for completion before Step 3 (instructing it to read `roles/scorer/role.md` via `evolve run read skill <run-id> roles/scorer/role.md --from SKILL.md --session <canonical-session-id>`):
   - Pass sparse pointers only: `Iteration to score: <k>`, `team_dir`, canonical `session_id`, `run_id`, `run_dir`, `assignment_id: SCORE-<k>`, `worker_id` (matching `--role`), `expected_orchestration_revision`, output path `{run_dir}/work/team/scores/iter-<k>-scores.json`, and input glob `{run_dir}/outputs/ant-<k>-*.json`.
3. After the scorer joins, verify `SCORE-<k>` in `evolve run task list` carries `result` and `scores/iter-<k>-scores.json` covers every valid `ant-<k>-*.json` artifact 1:1. If the scorer failed and you accept self-score fallback for this Run iteration, cancel `SCORE-<k>` via `evolve run task cancel --reason "<reason>"` before running `update --iter <k> --allow-score-fallback`. Once the controller commits the iteration, replay must keep the same scoring decision and inputs.

If `config.scoring.mode == "script"` or `"fallback"` (or `"hybrid"` using `script_path` only) -> proceed directly to Step 3.

### Step 3: Call aco.py update

```text
python <controller_script> --session {run_dir}/work/team --run-dir <run_dir> update --iter <k>
```

Parse stdout JSON. Expected:
```json
{
  "status": "ok",
  "iteration": <k>,
  "n_ants_processed": <N>,
  "mean_score": <float>,
  "best_score": <float>,
  "delta": <float>,
  "elite_updated": <bool>,
  "hallucinations_flagged": [<ant_ids>],
  "stats": {<pheromone stats>}
}
```

### Step 4: Log iteration result

Update `session.iteration = k` (committed), clear `session.active_iteration = null` and `session.active_workers = []`, and save the iteration summary in `team-session.json`.

If `hallucinations_flagged.length > N/2`: append warning to `wisdom/issues.md` (high-noise iteration).

### Step 5: Call aco.py converged

```text
python <controller_script> --session {run_dir}/work/team --run-dir <run_dir> converged
```

Parse:
```json
{ "converged": <bool>, "triggered_by": [...], "reason": "...", "metrics": {...} }
```

### Step 6: Branch on convergence

```
if converged:
    update session: completed_iterations.push(k), status = "converging"
    -> proceed to Phase 4 (converge.md)
else:
    update session: completed_iterations.push(k), active_workers = []
    -> re-enter "Iteration Start" workflow with k+1
```

### Step 7: Output progress to user

After each iteration:
```
[coordinator] Iteration <k>/<max> complete.
[coordinator]   best=<best_score>  mean=<mean_score>  delta=<delta>
[coordinator]   entropy=<entropy>  hallucinations=<count>
[coordinator]   Status: <converged | continuing to iter k+1>
```

---

## Edge Cases

| Condition | Handling |
|-----------|----------|
| Ant task failed or `partial_completion` | Ensure no malformed/invalid `outputs/ant-<k>-<i>.json` remains (move any broken file out of `outputs/` to `work/team/artifacts/`), cancel the assignment via `evolve run task cancel --session <canonical-session-id> --run <run-id> --id ANT-<k>-<i> --actor <coordinator> --expected-orchestration-revision <rev> --reason "<reason>"`, and log to `wisdom/issues.md`. If >50% failed/cancelled in iter -> halt and ask user via host structured user-prompt capability (see [host-adapters.md](../../../references/host-adapters.md)) |
| Ant produced no artifact | `aco.py update` skips missing `outputs/ant-<k>-*.json` files, but the roster still requires resolution: cancel missing ant(s) via `evolve run task cancel --reason "no artifact produced"`; if all ants lack valid artifacts -> `update` exits 1 -> halt |
| `aco.py update` fails (fail-closed) | Read stderr/JSON error message: (1) if caused by a malformed/schema-invalid `outputs/ant-<k>-<i>.json`, either repair/re-run that ant or move the bad file out of `outputs/` to `work/team/artifacts/` and `evolve run task cancel` its assignment; (2) if caused by missing/mismatched `scores/iter-<k>-scores.json` in `llm` mode, regenerate the score batch covering the exact remaining valid `ant-<k>-*.json` set (or cancel `SCORE-<k>` and pass `--allow-score-fallback`); (3) re-run `<controller_script> update --iter <k>`. If still failing -> halt with error report |
| Scorer worker fails | Record the failure and cancel `SCORE-<k>` via `evolve run task cancel --session <canonical-session-id> --run <run-id> --id SCORE-<k> --actor <coordinator> --expected-orchestration-revision <rev> --reason "<reason>"`. Retry scorer once or, for an intentional self-score fallback in a Run, run `update --iter <k> --allow-score-fallback` and record that choice for replay |
| Iteration takes too long | After timeout (configurable), check the host job and Evolve assignment roster |
| User sends `feedback <text>` mid-iteration | Append to wisdom/learnings.md; apply at next iteration start (not mid-iter) |
