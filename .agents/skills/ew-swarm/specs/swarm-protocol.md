# Swarm Protocol

Master protocol document for ew-swarm: defines how the LLM coordinator and Python ACO controller interface, how ants explore the task space, and how exploration results flow back to update pheromone state.

## Design Philosophy

| Principle | Rationale |
|-----------|-----------|
| **Outer loop = script, inner loop = LLM** | Optimization math is cheap and deterministic; LLM evaluation is expensive and noisy. Separate them. |
| **Coordinator is a hybrid** | LLM coordinator translates user intent + dispatches workers; Python script makes all numeric decisions (selection, update, convergence). |
| **Fresh context & sparse pointers** | Start every worker (`ant`, `scorer`, `analyst`) in a fresh context window (reserving `fork` strictly for warm deep-dive branches) and pass pointer locators rather than parent history or large file dumps. |
| **Workspace isolation** | Run read-only / JSON-output workers in the shared workspace and isolate concurrent code-editing workers in dedicated worktrees or branches, landing changes before workspace cleanup (see [host-adapters.md](../references/host-adapters.md)). |
| **Schema-locked ant output** | LLM output → algorithm input bridge demands strict JSON contract. Free-text outputs cannot feed pheromone updates. |
| **Two-layer scoring** | `verified_score` (`llm`, `script`, or `hybrid`) is authoritative whenever active; when falling back (`mode: "fallback"`, missing/failed script, or `--allow-score-fallback`), `FallbackScorer` derives `effective_score` from `self_score * self_confidence * self_score_discount`. |
| **Universal task space** | Coordinator stays domain-agnostic: `swarm-config.json` defines task space + scoring rule (`script_path` resolved relative to project `cwd`). Optionally use `DESIGN-IT-TWICE` constraint-divergent `ant_prompt` slices for architecture/interface search. |

## Three-Component Architecture

```
+-----------------------------------------------------+
|  LLM Coordinator (roles/coordinator/role.md)       |
|  - Parses user task → emits swarm-config.json       |
|  - Phase 3 main loop: calls script, dispatches       |
|  - Translates worker results back into script ops  |
+--------+--------------------------------------------+
         | CLI subprocess (host shell execution)
         v
+-----------------------------------------------------+
|  Python ACO Controller (<controller.script>)       |
|  - Materialized via `evolve run task controller` at    |
|    {run_dir}/work/controller/aco.py                |
|  - CLI subcommands: init/select/update/converged   |
|  - Owns pheromone matrix + elite tracker            |
|  - Pure functions of session state files            |
+-----------------------------------------------------+
         ^                                  |
         | reads {run_dir}/outputs/ant-*.json | writes pheromone/*.json
         |                                  v
+-----------------------------------------------------+
|  LLM Workers (team-worker agents)                  |
|  - ant: explores assigned path, writes JSON         |
|  - scorer: assigns verified_score (llm/hybrid)      |
|  - analyst: final synthesis of elite solutions      |
+-----------------------------------------------------+
```

## Iteration Lifecycle

```
[Coordinator] init phase
  └─> evolve run task controller --session <session_id> --run <run_id>  → <controller.script>
  └─> python <controller.script> --session {run_dir}/work/team --run-dir <run_dir> init
       └─> writes pheromone/current.json + task-space.json

[Coordinator] iteration k (k = 1..K):
  ├─> python <controller.script> --session {run_dir}/work/team --run-dir <run_dir> select --iter k
  │    └─> returns N ant assignments (paths to explore)
  ├─> register N ant assignments in Evolve roster (`evolve run task assign`)
  ├─> dispatch N × ant workers in parallel and wait/join
  └─> advance when all N ants finish (`result` or `cancellation` in roster)
  
[Coordinator] all N ants resolved → Iteration End
  ├─> (when scoring.mode = llm or optional hybrid) assign `SCORE-<k>`, dispatch scorer worker → `work/team/scores/iter-<k>-scores.json` (`script` mode evaluates in memory without writing scores/)
  ├─> python <controller.script> --session {run_dir}/work/team --run-dir <run_dir> update --iter k
  │    └─> reads {run_dir}/outputs/ant-k-*.json + scores/iter-k-scores.json (if llm/hybrid)
  │    └─> updates pheromone + elite + history (fail-closed in Run mode)
  ├─> python <controller.script> --session {run_dir}/work/team --run-dir <run_dir> converged
  │    └─> {converged: true|false, reason: ...}
  └─> converged → Phase 4; else → iteration k+1

[Coordinator] Phase 4: converge
  ├─> python <controller.script> --session {run_dir}/work/team --run-dir <run_dir> report → stdout JSON saved as outputs/swarm-report.json
  ├─> dispatch analyst worker (outside team roster) → outputs/best-solution.md
  └─> completion action (Archive & Finalize / Keep Active / Export Best Solution / Run Another Round)
```

## Script ↔ Coordinator Contract

All scripts MUST:
- Read from `{run_dir}/work/team/...` (session path passed via `--session` flag)
- Write JSON to stdout for coordinator parsing (no prose)
- Use exit code 0 = success, 1 = error, 2 = config invalid
- Commit each update with its input digest before writing the derived pheromone, best result, history and trails. Replaying the same iteration with identical inputs returns the stored result. A changed replay fails. The next controller invocation restores an interrupted projection from the committed receipt.

| Subcommand | Input | Output (stdout JSON) | Side effects |
|------------|-------|---------------------|--------------|
| `init` | swarm-config.json | `{status, pheromone_path, n_nodes}` | writes pheromone/current.json, task-space.json |
| `select --iter k` | pheromone/current.json, swarm-config.json | `{iteration, assignments: [{ant_id, start_node, ...}]}` | saves `assignments/k.json` for a Run |
| `update --iter k` | {run_dir}/outputs/ant-<k>-*.json, saved assignment batch when available, `scores/iter-<k>-scores.json` in LLM mode (or optional in hybrid; `script` mode evaluates `scoring.script_path` relative to `cwd` in memory) | `{iteration, mean_score, best_score, delta, elite_updated}` | commits an iteration receipt and projects state, history, trails and best solution |
| `converged` | history/, best.json, config | `{converged: bool, reason: str, metrics: {...}}` | none |
| `report` | best.json, history/ | full JSON: `{_meta: {kind: swarm-report, schema: swarm-report/1.0, role: attachment}, best, top_k, convergence_curve, ...}` | none |

Run-scoped `select` saves `assignments/k.json` and returns the same batch on replay. An LLM score file covers the accepted artifacts for the same iteration. `update --allow-score-fallback` uses discounted self-scores for the entire LLM iteration. The fallback choice is part of the replay inputs.

## Data Flow Boundaries

| Boundary | Owner | Format |
|----------|-------|--------|
| User intent → config | LLM coordinator | swarm-config.json |
| Pheromone state | Python script | pheromone/current.json |
| Ant assignment → ant prompt | LLM coordinator (templated) | injected into role-spec at spawn |
| Ant exploration → artifact | LLM ant | {run_dir}/outputs/ant-<k>-<i>.json (`ant_id.toLowerCase()`, schema-locked) |
| Artifact → pheromone update | Python script | reads artifacts, computes delta tau |
| Elite solutions → human report | LLM analyst | {run_dir}/outputs/best-solution.md |

## Why Hybrid Coordinator (Not Pure Script)

- User input is natural language → needs LLM to map to swarm-config
- Worker spawning, roster registration, session management → all framework-bound to LLM coordinator
- Script as a sub-tool keeps the same synchronous parallel-join lifecycle
- Same pattern as `evolve agent exec` — host-shell-callable subprocess from inside an LLM role

## Universality

`ew-swarm` is task-agnostic. Specialization happens via:
1. `swarm-config.json#task_space` — defines what nodes/edges/paths mean
2. `swarm-config.json#scoring` — defines how to compute verified_score
3. `swarm-config.json#ant_prompt` — defines what ant should actually do at each node

Example domains the same skill handles:
- Code exploration (nodes = files/modules, score = suspicious code density)
- Test case generation (nodes = code paths, score = coverage delta)
- Refactor strategy search (nodes = refactor moves, score = complexity reduction)
- Hyperparameter tuning (nodes = param choices, score = metric improvement)
