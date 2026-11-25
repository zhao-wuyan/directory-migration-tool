# Pre-flight: ew-roadmap

Confirm mode, Knowledge Gate inputs, and DAG altitude boundaries before running [workflow](workflow.md).

## Input & Mode

| Mode | Trigger | Behavior |
| --- | --- | --- |
| Create (default) | requirement / `--from` / `current-blueprint` / `current-brainstorm` | Build acyclic Session DAG from requirements and upstream context |
| Revise | `--revise [instructions]` | Update bound `current-roadmap` while preserving all sealed sessions (`E005` if modification collides) |
| Review | `--review` | Read-only health assessment of DAG acyclicity, coverage, and sizing (`E004` if missing) |
| Resume | `-c` / `--continue` | Inspect prior Run's `{run_dir}/evidence/roadmap-context.json` and partial `outputs/roadmap.json` via `evolve run get` (or bound sealed `current-roadmap`) and resume from the last recorded checkpoint |

- **`-m progressive|direct|auto`**: `progressive` confirms Session split boundaries and acceptance criteria interactively; `direct` generates the Session DAG directly from requirements/`--from` context without step-by-step prompts; `auto` selects `direct` when boundaries are clear and falls back to `progressive` on split ambiguity (`-y` defaults to `direct` unless hard ambiguity remains).
- Missing requirement without `--revise`/`--review` -> error `E001`.

## Knowledge & Context Pre-load

1. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`, CLI `--task roadmap` when using `--reads`) for `arch` constraints.
2. Read active Session registry (`evolve session list`) and codebase boundaries (`evolve code search`) after the Knowledge Gate.

## Decomposition & Output Boundaries

- **Run artifacts only**: write `outputs/roadmap.json` and `outputs/roadmap.md` under `{run_dir}/outputs/`; never write legacy `.workflow/roadmap.md` or deprecated `milestones[]` / `current_milestone` / `accumulated_context` fields.
- **Default to 1 Session**: split into multiple Sessions only when ALL THREE hard-dependency conditions hold: (1) Session B runtime code consumes Session A real output without mocking, (2) interface contracts cannot unblock concurrent work, and (3) every Session A task must complete before Session B starts. Otherwise keep 1 Session and let `ew-plan` model execution waves.
- **Sizing guard**: aim for `>= 5` substantial tasks per Session; if `> 3` Sessions result, re-audit every split against the 3 hard conditions and merge weak splits.
- **Altitude guard**: define Session boundaries, success criteria, and `depends_on` edges only — leave task splitting to `ew-plan`.
- **Exit gates**: `dag-valid` (acyclic, full requirement coverage, valid dependencies) and `sessions-registered` (every planned Session node in `sessions[]` is recorded cleanly in sealed `outputs/roadmap.json` against `evolve session list` without mutating completed Sessions; `ew-roadmap` does not itself call `evolve session create`—the orchestrator opens downstream execution Sessions starting from the first `ready` Session).
