# Pre-flight: ew-auto-test

Confirm scenario source routing, Knowledge Gate test conventions, and RED-GREEN invariants before running [workflow](workflow.md).

## Modes & Arguments

- **Source priority**: (1) bound spec/requirement artifacts (`current-blueprint` / `current-plan`), (2) `latest-verification` / coverage gaps, else (3) code exploration (`current-execution` + `evolve code search`).
- **`--max-iter N`**: outer convergence iteration cap (`5` default; `1` = single-pass generation).
- **`--layer L0|L1|L2|L3`**: start from or restrict execution to one test layer (`L0` unit/pure -> `L1` integration -> `L2` CLI/API -> `L3` E2E).
- **`--dry-run`**: generate scenario plan and traceability matrix without writing or running test files.
- **`--re-run`** / **`-c`**: resume or re-run previously failed scenarios from `.tests/auto-test/state.json` (falling back to prior Run `run get` / sealed `latest-auto-test` if `.tests/auto-test/state.json` is absent).

## Knowledge Gate

Complete the Run Knowledge Gate (`evolve skill workflows show session-run`, using `--task auto-test` in CLI `--reads`) for `test`/`coding` conventions.

## Pipeline Invariants

- **Test isolation**: write new test files only in the project's test directory matching existing framework conventions; NEVER modify production source or weaken/overwrite existing tests to make tests pass.
- **RED-GREEN integrity**: confirm each newly written test captures real failing behavior (or exercises live assertion paths) before counting GREEN; trivial always-true assertions are forbidden.
- **Strict layer ordering**: run `L0 -> L1 -> L2 -> L3` in order, stopping early on layer blocker (`fail-fast`).
- **Dual storage**: keep resumable workspace state under `.tests/auto-test/` if resuming across Runs, AND always publish final `{run_dir}/outputs/auto-test-report.json` (plus `outputs/traceability.md` for spec routes) for Runtime registration.
- **Exit gates**: `tests-generated` (scenarios traced & generated cleanly) and `execution-converged` (layers executed fail-fast with actual pass-rate metrics within `--max-iter` and `<= 3` defect retries per scenario).
