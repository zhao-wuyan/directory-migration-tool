---
role: ant
prefix: ANT
inner_loop: false
output_tag: "[ant]"
message_types: 
---

# Ant Role — Phase 2-4

## Boundaries

### MUST
- Read assignment JSON from task description (start_node, edge_preferences, max_path_length)
- Load swarm-config.json to understand objective + task semantics
- Build a path of length 1..max_path_length starting from start_node
- Bias choices using `edge_preferences` (pheromone-derived) BUT may deviate when evidence supports it
- Output strict-schema JSON to `{run_dir}/outputs/ant-<k>-<i>.json` (where `k` is iteration and `i` is the trailing index of `ant_id = "ANT-<k>-<i>"`, matching `specs/ant-output-schema.md`)
- Self-validate output before reporting (JSON parses + required fields + node validity)
- Provide ≥ 1 evidence anchor per path

### MUST NOT
- Modify pheromone state, best.json, trails/, or other ants' artifacts
- Skip path_decisions array (one entry per edge traversed)
- Report self_score > 0.9 without strong evidence (≥ 3 evidence anchors)
- Visit a node outside the task-space.json nodes list
- Loop back to a previously visited node in the same path (no cycles)

## Phase 2: Context Loading

| Input | Source | Required |
|-------|--------|----------|
| Assignment & Run IDs | Task description (`team_dir`, canonical `session_id`, `run_id`, `run_dir`, `assignment_id`, `worker_id`, `expected_orchestration_revision`, assignment JSON) | Yes |
| Objective | `{run_dir}/work/team/swarm-config.json#ant_prompt.objective` | Yes |
| Task semantics | `{run_dir}/work/team/swarm-config.json#ant_prompt` (full block) | Yes |
| Task space | `{run_dir}/work/team/task-space.json` (valid nodes list) | Yes |
| Pheromone hints | `assignment.edge_preferences` (already passed in) | Yes |
| Wisdom from prior iters | `{run_dir}/work/team/wisdom/learnings.md` (if exists) | Optional |

Workflow:
1. Extract `team_dir` (`{run_dir}/work/team`), canonical `session_id`, `run_id`, `run_dir`, `assignment_id` (`ANT-<k>-<i>`), `worker_id`, and `expected_orchestration_revision` from task description
2. Parse assignment JSON block from task description
3. Read swarm-config.json -> capture `ant_prompt.objective`, `ant_prompt.evidence_requirements`, `task_space.max_path_length`
4. Read task-space.json -> build valid_nodes set
5. If `{run_dir}/work/team/wisdom/learnings.md` exists -> read for prior-iteration insights

## Phase 3: Exploration

**Goal**: Build a path of nodes that maximizes likelihood of achieving the objective. The objective is task-defined (find buggy code, find best refactor target, etc.); ant is task-agnostic infrastructure.

Workflow:

### 3.1 Initialize path
- `path = [assignment.start_node]`
- `path_decisions = []`
- `visited = {start_node}`
- `current = start_node`

### 3.2 Per-step exploration loop (until len(path) reaches max_path_length OR ant decides to stop early)

For each step:

1. **Compute candidate neighbors**: all nodes in task_space NOT in `visited`
2. **Build choice weights**:
   - `assignment.edge_preferences` contains ONLY the top-8 outgoing hints from `assignment.start_node` (first hop).
   - At step 1 (`current == assignment.start_node`), compute `weight = edge_preferences.get(f"{current}::{c}", baseline) * heuristic(c)` with `baseline = 1.0 / max(1, len(candidates))`.
   - From step 2 onward (`current != assignment.start_node`), `edge_preferences` does not cover `current`; compute `weight = heuristic(c)` purely from evidence and task semantics.
   - `heuristic(c)` = ant's own evidence-based judgment (1.0 if no opinion).
3. **Investigate top candidates** using available tools:
   - Tool selection: use host file-read and search capabilities (or `evolve code search`) for code-based task spaces, or dispatch a read-only host subagent (or external `evolve agent exec`) for deeper inspection; see [host-adapters.md](../../references/host-adapters.md).
   - Gather evidence about each top candidate before committing
4. **Choose next node**: weighted-random OR argmax (when high confidence)
5. **Record decision**:
   ```json
   {
     "from": "<current>",
     "to": "<chosen>",
     "rationale": "<one-line>",
     "guided_by": "pheromone | heuristic | evidence",
     "pheromone_weight": <edge_preferences value at step 1, omit or 0.0 for step >= 2>,
     "deviation_from_hint": <bool — at step 1, true if chosen != argmax(edge_preferences); false for step >= 2>
   }
   ```
6. **Append to path**, update `visited`, `current = chosen`
7. **Early-stop check**: if evidence shows objective achieved OR no productive next step exists -> stop

### 3.3 Self-evaluate

After path is built:

1. **self_score** (0..1): how well does this path satisfy the objective?
   - Use `ant_prompt.evidence_requirements` as rubric
   - Be conservative — penalize for missing evidence, weak rationale
2. **self_confidence** (0..1): how sure of the self_score?
   - Low confidence if evidence is sparse or contradictory
3. **candidate_solution**: extract the actual deliverable along the path
   - `type` ∈ {string, object, file_ref}
   - `summary` — one-line
   - `content` — actual artifact OR a path to a file written by the ant

### 3.4 Compose artifact JSON

Build the full artifact matching specs/ant-output-schema.md. All required fields populated.

## Phase 4: Verify + Publish

### Behavioral Traits

#### Accuracy — outputs must be verifiable
- Every node in `path` MUST exist in task-space.json
- Every `path_decisions[i].from` MUST equal `path[i]` and `to` MUST equal `path[i+1]`
- Evidence references (e.g., `file:line`) MUST be valid (Read to confirm if file_ref)

#### Feedback Contract
| Field | Required | Content |
|-------|----------|---------|
| files_produced | If ant wrote any | `[artifact_path]` at minimum |
| artifacts_written | Always (on valid completion) | `{run_dir}/outputs/ant-<k>-<i>.json` |
| verification_method | Always | "schema_validated + node_validity_checked" |

#### Quality Gate
- Schema validation pass = REQUIRED before writing `outputs/ant-<k>-<i>.json` and calling `evolve run task report`
- Fails -> retry Phase 3 once (max 1 retry to bound cost)
- Still fails -> do NOT leave invalid JSON at `outputs/ant-<k>-<i>.json` and do NOT call `evolve run task report`; optionally save diagnostics to `{run_dir}/work/team/artifacts/ant-<k>-<i>-partial.json` and return a structured `partial_completion` payload (`{ status: "partial_completion", assignment_id, validation_errors, partial_findings }`) to the coordinator so the coordinator runs `evolve run task cancel --reason "..."`

### Verification Steps

1. **Schema validation**:
   - Parse the JSON via host file-read capability
   - Confirm all required fields from specs/ant-output-schema.md (including `_meta`, `ant_id == "ANT-<k>-<i>"`, and `>= 1` non-empty `evidence` anchor in Run mode)
   - Confirm numeric ranges (self_score, self_confidence ∈ [0,1])
   - Confirm `len(path_decisions) == len(path) - 1`
2. **Node validity**: every node in path ∈ task_space.json#nodes
3. **Evidence check**: at least 1 evidence anchor present; if `file_ref`, read the target file to confirm existence
4. **Write artifact**: write the JSON string to `{run_dir}/outputs/ant-<k>-<i>.json`
5. **Re-read to confirm write**: read it back, parse, sanity check

### State Update

After re-reading the valid artifact, report completion via:
```bash
evolve run task report --session <session_id> --run <run_id> --id <assignment_id> --actor <worker_id> --summary "<one-line result>" --expected-orchestration-revision <expected_orchestration_revision>
```
(`--actor` MUST equal `worker_id` / the `--role` registered in `evolve run task assign`.) On `REVISION_CONFLICT`, read `evolve session get --session <session_id>` for the latest `orchestration_revision` and retry with a new `--request-id` (or omit `--request-id`).

## Error Handling

| Scenario | Resolution |
|----------|------------|
| Assignment JSON or Run payload malformed | Do not call `evolve run task report`; return structured failure (`assignment_id`, error reason) to coordinator so coordinator runs `evolve run task cancel --reason "..."`, STOP |
| start_node not in task_space | Do not write output or call `evolve run task report`; return config mismatch error to coordinator for `evolve run task cancel --reason "..."`, STOP |
| No valid neighbors at step 1 | If `>= 1` valid evidence anchor exists, build single-node path (`path_decisions: []`, `self_score: 0.0`), write `ant-<k>-<i>.json`, and call `evolve run task report`; otherwise treat as `partial_completion` |
| Schema validation fails twice | Ensure no invalid JSON remains at `outputs/ant-<k>-<i>.json`, optionally save diagnostics to `work/team/artifacts/ant-<k>-<i>-partial.json`, do not call `evolve run task report`, and return structured `partial_completion` with `validation_errors` for coordinator `evolve run task cancel` |
| Evidence requirements unsatisfiable | If `>= 1` valid anchor exists, lower `self_score`, document blocker in `notes`, write valid artifact, and report; if zero valid evidence anchors exist in Run mode, return `partial_completion` for `evolve run task cancel` |
| Tool calls fail (file read/search) | Note in artifact `notes`; reduce self_confidence; proceed if valid schema & `>= 1` evidence anchor remain, else return `partial_completion` for `evolve run task cancel` |
