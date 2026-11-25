---
name: ew-swarm
disable-model-invocation: true
description: Swarm intelligence team skill — ACO-driven multi-agent exploration with hybrid LLM coordinator and Python optimization controller. Coordinator translates the task into swarm-config.json and runs K iterations of N parallel ants guided by pheromone state. Triggers on "ew-swarm", "team swarm", "swarm intelligence", "蚁群".
session-mode: run
required_links:
  - references/team-coordination.md
contract:
  contract_version: 2.1
  consumes: []
  produces:
    - path: outputs/best-solution.md
      kind: swarm-best-solution
      schema: swarm-best-solution/1.0
      role: primary
      required: true
    - path: outputs/swarm-report.json
      kind: swarm-report
      schema: swarm-report/1.0
      role: attachment
      required: true
    - path: outputs/ant-{k}-{i}.json
      kind: swarm-ant-result
      schema: swarm-ant-result/1.0
      role: evidence
      required: false
  gates:
    entry: []
    exit: []
version: 0.6.0
---

# Evolve Swarm

Orchestrate ant-colony-style exploration over a user-defined task space (`swarm-config.json`). **Hybrid model**: LLM handles task translation, worker dispatch, and synthesis; Python (`scripts/aco.py`) owns all numeric decisions (selection, pheromone update, convergence).

## Role Router

- **No `--role` flag** -> Load [roles/coordinator/role.md](roles/coordinator/role.md) and execute Phase 0–4.
- **`--role <name>`** -> Load the assigned role only:
  - `ant` -> [roles/ant/role.md](roles/ant/role.md) (`ANT-*`)
  - `scorer` -> [roles/scorer/role.md](roles/scorer/role.md) (`SCORE-*`)
  - `analyst` -> [roles/analyst/role.md](roles/analyst/role.md) (`ANALYST-*`)

## Shared Constants & Host Mapping

- **Coordination state**: `{run_dir}/work/team/` (internal scratch; keep out of formal Run `outputs/`).
- **Formal outputs**: `{run_dir}/outputs/` (`ant-<k>-<i>.json` where `k` is iteration and `i` is the trailing index of `ant_id = "ANT-<k>-<i>"`, matching `stem.upper() == ant_id`; `swarm-report.json`; `best-solution.md`).
- **Controller CLI**: In a version-bound Run, materialize the pinned controller scripts via `evolve run task controller --session <session> --run <run>` and invoke the returned `script` path (`{run_dir}/work/controller/aco.py`, Python 3.10+) rather than `<skill_root>/scripts/aco.py`.
- **Roster**: `evolve run task list --session <session> --run <run>`. Read [Team coordination](references/team-coordination.md) before dispatching or reporting assignments.
- **Host dispatch & context hygiene**: Dispatch parallel workers via the host parallel subagent capability ([references/host-adapters.md](references/host-adapters.md)), starting every worker with a **fresh context window** (`fork` reserved strictly for explicit warm deep-dive branches) and communicating via **sparse context pointers** (`session_id`, `run_id`, `run_dir`, `team_dir`, `role_spec`, `--reads` path). Join all spawned workers before finishing.
- **Optional `DESIGN-IT-TWICE` mode**: For interface or architecture search, seed `swarm-config.json` with constraint-divergent `ant` prompts (e.g., minimal entry-point leverage, maximal flexibility, common-caller ergonomics, ports-and-adapters) and have `analyst` compare solutions by depth, locality, and seam placement.

## User Commands

| Command | Handler / Action |
|---------|------------------|
| `check` / `status` | Coordinator `handleCheck` (`roles/coordinator/role.md`): read-only inspection of `team-session.json`, `evolve run task list`, `evolve run check`, and `aco.py converged` |
| `resume` / `continue` | Coordinator `handleResume` (`roles/coordinator/role.md` Phase 0): verify exact Run locator, reconcile roster and controller state, and resume interrupted iteration or Phase 4 synthesis |
| `feedback <text>` | Coordinator `handleFeedback` (`roles/coordinator/role.md`): append to `work/team/wisdom/learnings.md` for next iteration start (rejected on sealed/completed Runs) |

Committed iterations and sealed Runs are immutable (`aco.py` rejects historical re-runs or altered replay inputs); use the Fresh-Run handoff in `roles/coordinator/commands/converge.md` to start another round.

## References

- [references/host-adapters.md](references/host-adapters.md) — Host capability-to-tool mappings and Phase 0 lifecycle reconciliation
- [specs/swarm-protocol.md](specs/swarm-protocol.md) — Master architecture, script <-> coordinator interface, lifecycle
- [specs/pheromone-schema.md](specs/pheromone-schema.md) — Pheromone JSON structure, update formula, evaporation
- [specs/ant-output-schema.md](specs/ant-output-schema.md) — Ant & scorer JSON contracts
- [specs/convergence-criteria.md](specs/convergence-criteria.md) — Stop conditions and multi-criterion logic
- `specs/swarm-config-template.json` — Config template (`scripts/aco.py`, `scripts/pheromone.py`, `scripts/scoring.py`)
