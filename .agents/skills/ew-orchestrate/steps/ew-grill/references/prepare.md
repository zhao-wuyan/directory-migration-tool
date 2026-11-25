# Pre-flight: ew-grill

Confirm mode, depth, upstream seed, and read-only invariants before executing [workflow](workflow.md).

## Input & Mode

| Mode | Trigger | Behavior |
| --- | --- | --- |
| Resume | `-c` / `--continue` | Resume from prior Run's bound `grill-report.md` and unresolved dependency-frontier branches |
| Auto | `-y` | Codebase evidence answers Socratic branches automatically (still walks all frontier branches — never skips grilling) |
| Interactive | `<topic>` or `--from <alias>` | Full adversarial Socratic Q&A in dependency-frontier rounds with `yes`-acceptable recommendations (`ask_question` or host equivalent) |

- `--depth shallow|standard|deep`: sets branch count to `3`, `5` (default), or `8`.
- Missing topic and no `--from` / `-c` -> fail cleanly (`E001`).

## Knowledge & Context Pre-load

1. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`, CLI `--task grill` when using `--reads`) for `arch`/`review` scope.
2. Run `evolve code search "<topic>"` only after the Knowledge Gate when mapping overlapping symbols.

## Boundaries & Invariants

- **Read-only on repository source**: write artifacts only under `{run_dir}/outputs/` and `{run_dir}/evidence/`.
- **Evidence-anchored challenges**: every questioned assumption must cite concrete `file:line` anchors or observed command output, never abstract opinion.
- **Dependency-frontier batching & `yes`-acceptable framing**: map decisions as a design tree; batch the entire settled **frontier** (questions whose prerequisites are already decided) into one round, numbered (`Q1`, `Q2`, ...) with a grounded recommended answer (`-> <recommendation>`), phrased so `"yes"` accepts the recommendation. Defer downstream dependent questions to subsequent rounds.
- **Non-blocking background fact-finding subagents**: finding codebase/environment *facts* is the agent's job, never the user's. When a branch needs an unknown fact, dispatch a fresh-context read-only subagent without blocking independent frontier questions (only questions downstream of the lookup wait for it; always join/cancel all subagents before Step 4).
- **Immediate contradiction checks**: whenever an answer conflicts with codebase evidence or an earlier answer, challenge it on the spot before moving to the next branch.
- **3-condition ADR gate**: recommend creating an ADR (e.g., `docs/adr/NNNN-<slug>.md` or governed Wiki entry) only when **all three** hold: (1) **hard to reverse**, (2) **surprising without context**, and (3) **result of a real trade-off**. Skip ADR proposals when any condition is missing.
- **Exit gate intent**: `terminology-aligned` (all candidate terms collision-checked in `outputs/terminology.md`) and `branches-walked` (full depth `3/5/8` walked from basic -> specific -> adversarial until the dependency frontier is empty, producing `outputs/grill-report.md` and `outputs/context-package.json`).
