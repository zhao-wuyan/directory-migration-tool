# Pre-flight: ew-verify

Confirm verification sources, Knowledge Gate constraints, and the Iron Law before executing [workflow](workflow.md).

## Input & Flags

- **Bound sources (required)**: `current-plan` (criteria contract; for `pi-markdown` plans preserve every prose criterion without requiring `task_ids`) and `current-execution` (changed files & self-check claims). When re-verifying after a review-triggered fix cycle, also inspect bound `latest-review` to confirm prior findings are resolved.
- **`--strict`**: elevate bar so any `UNCERTAIN` item blocks pass and all error/boundary paths must carry explicit proof.
- **`--skip-tests`**: skip automated test coverage matrix and perform goal-backward structural + wiring verification only.
- **`--skip-antipattern`**: omit static anti-pattern scanning.

## Knowledge Gate

Complete the Run Knowledge Gate (`evolve skill workflows show session-run`): reuse bound `session-priors` (`review`/`coding`); query `evolve search "<query>" --mode context` + `evolve kb get <id>` (with CLI `--reads` + `--from-search`) when verifying files or tech-stack constraints outside `session-priors`.

## Iron Law & Boundaries

- **Read-only on repository source**: never edit production code or relax assertions to turn failures green; fixing gaps belongs to `ew-plan --gaps` -> `ew-execute`.
- **No claim without live evidence**: follow `IDENTIFY -> EVIDENCE (fresh run or still-valid uninvalidated run on the exact revision) -> READ (full output + exit code + failure count) -> VERIFY -> conclude`. Execution self-check claims alone are supporting hints, never final verification proof.
- **No silent omissions**: every requirement and criterion must resolve to `VERIFIED`, `FAILED`, or `UNCERTAIN` with explicit `file:line` or command evidence.
- **Exit gates**: `goal-backward-verified` (Truths, Artifacts L1/L2/L3, and Key Links verified) and `nyquist-covered` (automated test / check proof complete or explicitly `--skip-tests`).
