

# Ant Output Schema

**The critical contract.** Every ant MUST write a JSON file matching this schema. Pheromone updates depend on it. In Run mode (`--run-dir`), `aco.py update` is **fail-closed**: any malformed JSON, `ant_id != stem.upper()`, or schema/assignment mismatch causes `update` to exit 1 without committing the iteration. The coordinator must repair/replace or remove the bad artifact from `outputs/` and cancel its roster assignment via `evolve run task cancel --reason "..."` before re-running `update`.

## File Path

```
{run_dir}/outputs/ant-<k>-<i>.json
```

Example: `{run_dir}/outputs/ant-3-2.json` (`k=3` iteration, `i=2` trailing index from `"ant_id": "ANT-3-2"`, so `Path(f).stem.upper() == ant_id`)

## Schema

```json
{
  "_meta": {
    "kind": "swarm-ant-result",
    "schema": "swarm-ant-result/1.0",
    "role": "evidence"
  },
  "schema_version": "1.0",
  "ant_id": "ANT-3-2",
  "iteration": 3,
  "assignment": {
    "start_node": "node_a",
    "max_path_length": 5
  },
  "path": ["node_a", "node_c", "node_f"],
  "path_decisions": [
    {
      "from": "node_a",
      "to": "node_c",
      "rationale": "<one-line reason>",
      "guided_by": "pheromone | heuristic | evidence",
      "pheromone_weight": 0.30,
      "deviation_from_hint": false
    },
    {
      "from": "node_c",
      "to": "node_f",
      "rationale": "<one-line reason>",
      "guided_by": "evidence",
      "deviation_from_hint": true
    }
  ],
  "self_score": 0.78,
  "self_confidence": 0.6,
  "cost_tokens": 1200,
  "cost_seconds": 18,
  "evidence": [
    "src/foo.ts:42",
    "tests/foo.spec.ts:18"
  ],
  "candidate_solution": {
    "type": "<string|object|file_ref>",
    "summary": "<one-line>",
    "content": "<actual artifact content OR a path>"
  },
  "blockers": [],
  "notes": "<optional free text, NOT used by pheromone update>"
}
```

## Required Fields (Validation)

| Field | Type | Required | Constraint |
|-------|------|----------|------------|
| `_meta` | object | yes | complete artifact metadata; `kind` = `swarm-ant-result`, `schema` = `swarm-ant-result/1.0` |
| `schema_version` | string | yes | must be `"1.0"` |
| `ant_id` | string | yes | matches assignment |
| `iteration` | int | yes | matches assignment |
| `path` | array of string | yes | len >= 1, all nodes ∈ task_space.nodes |
| `path_decisions` | array | yes | len = len(path) - 1 |
| `self_score` | float | yes | 0.0 ≤ x ≤ 1.0 |
| `self_confidence` | float | yes | 0.0 ≤ x ≤ 1.0 |
| `cost_tokens` | int | no | recommended for budget tracking |
| `evidence` | array of string | yes | min 1 entry (forces grounding) |
| `candidate_solution` | object | yes | non-empty `summary` |

## Two-Layer Scoring

Supported `config.scoring.mode` values: `script` | `llm` | `hybrid` | `fallback` (priority in `scoring.py`: `verified_llm > verified_script > fallback_self`).

| Score | Source | Purpose |
|-------|--------|---------|
| `self_score` | Ant LLM self-report | Ignored by pheromone update when verified scoring (`llm` or `script`) succeeds, except for hallucination comparison in `verified_llm` mode; **IS** used via `FallbackScorer` when scoring falls back |
| `self_confidence` | Ant LLM self-report | Weights `self_score` when `FallbackScorer` runs |
| `verified_score` | `scoring.py` (`ScriptScorer` in memory) OR LLM `scorer` role | **Authoritative input to pheromone update.** Only the LLM `scorer` role writes `{run_dir}/work/team/scores/iter-<k>-scores.json`; `script` mode evaluates `scoring.script_path` (resolved relative to project `cwd`) in memory during `aco.py update` without writing a `scores/` file |

When no verified score applies (`mode: "fallback"`, omitted/failing `script_path`, or `aco.py update --allow-score-fallback` in `llm` Run mode), pheromone update uses `FallbackScorer`:
```
effective_score = self_score * self_confidence * config.scoring.self_score_discount  # default 0.5
```

## Scores File (`scores/iter-<k>-scores.json`)

When the LLM `scorer` role runs (`mode: "llm"` or `"hybrid"`), it writes `{run_dir}/work/team/scores/iter-<k>-scores.json` (`scorer_type` MUST be `"llm"` for Run-mode `load_verified_scores`):

```json
{
  "iteration": 3,
  "scorer_type": "llm",
  "scores": {
    "ANT-3-1": { "verified_score": 0.82, "rationale": "..." },
    "ANT-3-2": { "verified_score": 0.45, "rationale": "..." }
  },
  "computed_at": "2026-05-25T14:30:00Z"
}
```

## Hallucination Detection

In `verified_llm` scoring mode, `aco.py update` compares `self_score` vs `verified_score` per ant:
- Whenever `|self_score - verified_score| > 0.4`, `aco.py update` immediately flags `ant_id` in `hallucinations_flagged` and halves its effective score (`score *= 0.5`) for that iteration's pheromone deposit and trail entry (no cross-iteration repeat count is accumulated).

## Validation in Ant's Phase 4

Ant MUST self-validate before writing:
1. JSON parses cleanly
2. All required fields present (including `_meta` and `>= 1` non-empty `evidence` anchor in Run mode)
3. `path` nodes exist in task-space.json
4. `path_decisions` length = `len(path) - 1` and consecutive endpoints match `path`
5. Numeric ranges within bounds

Validation failure → retry once → if still failing, do NOT leave invalid JSON in `{run_dir}/outputs/ant-<k>-<i>.json` and do NOT call `evolve run task report`; optionally write diagnostics to `{run_dir}/work/team/artifacts/ant-<k>-<i>-partial.json` and return a structured `partial_completion` (`validation_errors`, partial findings) to the coordinator so the coordinator runs `evolve run task cancel --reason "partial_completion: <errors>"`.
