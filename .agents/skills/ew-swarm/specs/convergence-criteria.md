# Convergence Criteria

When does the swarm stop iterating? Defines stop conditions computed by `aco.py converged`.

## Stop Conditions (any-of)

The swarm stops when **any** of the enabled criteria triggers:

| Criterion | Default | Description |
|-----------|---------|-------------|
| `max_iterations` | `5` | Hard cap on `state.iteration` (`iteration >= max_iterations`) |
| `stagnation` | `enabled: true`, `patience: 2`, `min_delta: 0.01` | Per-iteration max `verified_score` from `trails/*.jsonl` changes by `< min_delta` across the last `patience` consecutive transitions |
| `entropy_floor` | `enabled: true`, `threshold: 0.5` | Shannon entropy across all edges in `pheromone/current.json` drops below `threshold` (`entropy < threshold`) |
| `target_score` | template `enabled: false`, `value: 0.95` (note: `aco.py` defaults omitted `enabled` to `true`) | `best.json` `score >= value` — enable ONLY with verified scoring (`llm`, `script`, or `hybrid`) |

> Note: `cost_tokens` in ant artifacts is informational only; `aco.py converged` does not track or enforce token budgets.

## Configuration

```json
{
  "convergence": {
    "max_iterations": 5,
    "stagnation": { "enabled": true, "patience": 2, "min_delta": 0.01 },
    "entropy_floor": { "enabled": true, "threshold": 0.5 },
    "target_score": { "enabled": false, "value": 0.95 }
  }
}
```

## Output Schema (`aco.py converged`)

```json
{
  "status": "ok",
  "command": "converged",
  "converged": true,
  "iteration": 4,
  "triggered_by": ["stagnation"],
  "reason": "stagnation",
  "metrics": {
    "iteration": 4,
    "entropy": 1.85,
    "best_score": 0.78,
    "mean_score": 0.0,
    "iterations_since_best_change": 2
  },
  "recommendation": "ready for report"
}
```

- `reason`: `triggered_by[0]` if converged, otherwise `"in_progress"`.
- `recommendation`: `"ready for report"` if converged, otherwise `"continue to iteration <iteration + 1>"`.
- `metrics.mean_score`: placeholder `0.0` in `aco.py converged` (per-iteration `mean_score` is emitted by `aco.py update`; total `iterations_completed` is emitted by `aco.py report`).

## Selection Logic

```python
def check_convergence(state, best_score, trail_files, config):
    triggered = []
    iteration = state.iteration

    if iteration >= config.get("max_iterations", 5):
        triggered.append("max_iterations")

    ef = config.get("entropy_floor", {})
    if ef.get("enabled", True) and entropy < ef.get("threshold", 0.5):
        triggered.append("entropy_floor")

    ts = config.get("target_score", {})
    if ts.get("enabled", True) and best_score >= ts.get("value", 0.95):
        triggered.append("target_score")

    st = config.get("stagnation", {})
    if st.get("enabled", True):
        patience = st.get("patience", 2)
        min_delta = st.get("min_delta", 0.01)
        # per_iter_best = max(verified_score) in each sorted trails/*.jsonl file
        if len(per_iter_best) > patience:
            recent = per_iter_best[-patience - 1:]
            deltas = [abs(recent[i] - recent[i - 1]) for i in range(1, len(recent))]
            iterations_since_best_change = sum(1 for d in deltas if d < min_delta)
            if all(d < min_delta for d in deltas):
                triggered.append("stagnation")

    return {"converged": len(triggered) > 0, "triggered_by": triggered}
```

## Entropy Calculation

Shannon entropy of the normalized pheromone distribution over **all edges** in `state.tau`:

```
p_i = tau_i / sum(tau)  for every edge in tau
H = -sum(p_i * log2(p_i))  for p_i > 0
```

- High `H` → diverse exploration (early stage)
- Low `H` → concentrated on few paths (converging)
- `H < threshold` + per-iteration best plateau → safe to stop

## Why Multi-Criterion

Single criterion is fragile:
- `max_iterations` alone wastes budget if converged early
- `stagnation` alone may stop too early on noisy scoring
- `entropy_floor` alone may trigger before useful solutions emerge

Combination = early termination when safe, but always bounded by `max_iterations`.

## Anti-Patterns

- DO NOT use `stagnation` with `patience < 2` — noise will trigger false stops
- DO NOT disable `max_iterations` — runaway risk
- DO NOT leave `target_score.enabled` as `true` (or omit `target_score.enabled`, which `aco.py` defaults to `true`) without verified scoring (`llm`, `script`, or `hybrid`) — in `fallback` mode `self_score * self_confidence * 0.5` is capped at `0.5`, so `0.95` can never trigger
