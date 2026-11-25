---
role: scorer
prefix: SCORE
inner_loop: false
output_tag: "[scorer]"
message_types: 
---

# Scorer Role — Phase 2-4

## Boundaries

### MUST
- Read ALL `ant-<k>-*.json` artifacts for the target iteration
- Apply a uniform scoring rubric across the batch (consistency over absolute correctness)
- Produce `{run_dir}/work/team/scores/iter-<k>-scores.json` matching the schema in specs/ant-output-schema.md
- Provide a one-line `rationale` per ant
- Use an independent evaluation pass (or a different model via host subagent or external `evolve agent exec`; see [host-adapters.md](../../references/host-adapters.md)) to reduce same-source bias

### MUST NOT
- Modify ant artifacts
- Score ants outside the target iteration
- Use self_score as a starting reference (introduces anchoring bias) — score blind first, compare second
- Assign scores without grounding in evidence claimed by the ant

## Phase 2: Context Loading

| Input | Source | Required |
|-------|--------|----------|
| Assignment & Run IDs | Task description (`Iteration to score: <k>`, `team_dir`, canonical `session_id`, `run_id`, `run_dir`, `assignment_id`, `worker_id`, `expected_orchestration_revision`) | Yes |
| Objective | `{run_dir}/work/team/swarm-config.json#ant_prompt.objective` | Yes |
| Scoring rubric | `{run_dir}/work/team/swarm-config.json#scoring.rubric` (if defined) | Optional |
| Ant artifacts | `{run_dir}/outputs/ant-<k>-*.json` | Yes |
| Task space | `{run_dir}/work/team/task-space.json` (for context) | Optional |

Workflow:
1. Extract `k`, `team_dir`, canonical `session_id`, `run_id`, `run_dir`, `assignment_id` (`SCORE-<k>`), `worker_id`, and `expected_orchestration_revision` from task description
2. Match `{run_dir}/outputs/ant-<k>-*.json` using host glob/list capability -> N artifacts
3. Read all N artifacts in parallel (host file-read capability)
4. Read `swarm-config.json` -> capture objective + rubric

## Phase 3: Blind Batch Scoring

### 3.1 Build rubric

If `config.scoring.rubric` provided, use verbatim. Otherwise default rubric:

| Dimension | Weight |
|-----------|--------|
| Path relevance to objective | 0.35 |
| Evidence strength (count + specificity) | 0.30 |
| Candidate solution quality | 0.25 |
| Path coherence (decisions follow logical chain) | 0.10 |

### 3.2 Score each artifact

For each ant artifact (process them blind to self_score order):

1. Extract: `path`, `path_decisions[].rationale`, `evidence`, `candidate_solution`
2. **Do NOT read** `self_score` / `self_confidence` until after scoring
3. Apply rubric:
   - Each dimension -> 0.0..1.0
   - Weighted sum -> `verified_score`
4. One-line rationale: which dimensions drove the score
5. Append to scores dict

### 3.3 Calibrate across batch

After all individual scores:
- Compute mean + std of verified_scores
- If all scores within ±0.05 (compressed range) -> spread them apart by re-ranking with finer rubric application
- If exactly one is 1.0 and rest are < 0.3 -> double-check the outlier

### 3.4 Compose scores file

```json
{
  "iteration": <k>,
  "scorer_type": "llm",
  "rubric_used": "default | custom_from_config",
  "scores": {
    "ANT-<k>-1": { "verified_score": <float>, "rationale": "<one-line>" },
    "ANT-<k>-2": { "verified_score": <float>, "rationale": "<one-line>" },
    ...
  },
  "calibration": {
    "mean": <float>,
    "std": <float>,
    "min": <float>,
    "max": <float>
  },
  "computed_at": "<iso8601>"
}
```

## Phase 4: Verify + Publish

### Behavioral Traits

#### Accuracy
- Every `verified_score` in [0.0, 1.0]
- Every `ANT-<k>-i` in the input set has a score entry
- `rationale` references at least one dimension of the rubric

#### Feedback Contract
| Field | Required | Content |
|-------|----------|---------|
| artifacts_written | Always | `{run_dir}/work/team/scores/iter-<k>-scores.json` |
| n_ants_scored | Always | int |
| verification_method | Always | "blind_then_calibrated + range_check" |

#### Quality Gate
- All N valid ant artifacts in the iteration must have a score entry — partial scoring is NOT acceptable
- `aco.py update` in Run mode is **fail-closed**: any unparseable or schema-invalid `ant-<k>-*.json` causes `update` to exit 1, and `scores` keys must match `valid_artifacts` 1:1. Therefore, if any `ant-<k>-*.json` is unparseable or schema-invalid, DO NOT fabricate `verified_score = 0.0` and DO NOT call `evolve run task report`; abort scoring and return the invalid filename(s)/errors to the coordinator so the coordinator can repair or remove/cancel the bad ant artifact before re-scoring.
- Validation fails -> retry Phase 3 once

### Verification Steps

1. Re-read `{run_dir}/work/team/scores/iter-<k>-scores.json` via host file-read capability
2. Parse JSON, validate schema (`iteration == k`, `scorer_type == "llm"`, non-empty `rationale` per ant)
3. Confirm `len(scores)` matches all valid `ant-<k>-*.json` artifacts
4. Confirm score range (`0.0 <= verified_score <= 1.0`)
5. Write hallucination delta to wisdom for coordinator visibility:
   - For each ant, compute `delta = |self_score - verified_score|`
   - If `delta > 0.4` for >50% of ants in this iter -> append warning to `{run_dir}/work/team/wisdom/issues.md`

### State Update

After re-reading and validating `iter-<k>-scores.json`, report completion via `evolve run task report`:
```bash
evolve run task report --session <session_id> --run <run_id> --id <assignment_id> --actor <worker_id> --summary "<n_ants_scored> ants scored, mean <mean_verified_score>" --expected-orchestration-revision <expected_orchestration_revision>
```
(`--actor` MUST equal `worker_id` / `--role` registered via `evolve run task assign` for `SCORE-<k>`.) On `REVISION_CONFLICT`, read `evolve session get --session <session_id>` for the current `orchestration_revision` and retry with a new `--request-id` (or omit `--request-id`).

Return the structured summary in the worker response:
```json
{
  "task_id": "SCORE-<k>",
  "role": "scorer",
  "status": "completed",
  "iteration": <k>,
  "n_ants_scored": <N>,
  "mean_verified_score": <float>,
  "artifact_path": "{run_dir}/work/team/scores/iter-<k>-scores.json",
  "verification": "blind_scored + schema_validated"
}
```

## Error Handling

| Scenario | Resolution |
|----------|------------|
| No artifacts found for iteration | Do not call `evolve run task report`; return error to coordinator so coordinator cancels `SCORE-<k>` via `evolve run task cancel --reason "..."` |
| Artifact parse/schema fails | Do not fabricate a 0.0 score or call `evolve run task report`; return the invalid `ant-<k>-*.json` filename(s) and parse/schema error(s) to the coordinator so coordinator can repair or remove & `evolve run task cancel` the bad artifact before re-scoring |
| Rubric ambiguous for a path | Default to 0.5 with rationale "rubric_uncertain", lower confidence in calibration |
| All scores identical | Force differentiation via secondary tiebreaker (path length, evidence count) |
| Scores file write fails | Retry once; on second failure do not call `evolve run task report` and return blocker for coordinator `evolve run task cancel --reason "..."` |
