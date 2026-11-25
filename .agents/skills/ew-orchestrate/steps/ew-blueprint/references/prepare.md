# Pre-flight: ew-blueprint

Confirm input source, depth, Knowledge Gate constraints, and document-only boundaries before executing [workflow](workflow.md).

## Input & Mode

- **Direct text / `@file`**: fresh specification pipeline starting from P1 Discovery & P1.5 Clarification.
- **`--from <alias>` / `upstream-context` / `current-brainstorm`**: load bound `context-package.json` (`requirements[]`, `constraints[]`, `domain.terminology[]`, `non_goals[]`, `insights[]`) or `current-brainstorm` options/constraints and skip redundant P1.5 expansion.
- **`-c` / `--continue`**: inspect prior incomplete Run's `{run_dir}/outputs/` (`blueprint-config.json`, `discovery-context.json`, `refined-requirements.json`, `product-brief.md`, `requirements/`, `architecture/`, `epics/`) and `{run_dir}/evidence/` via `evolve run get <prior-run-id> --session <session>` (or bound sealed `current-blueprint`), and resume from the first incomplete phase.
- **`-y`**: auto-select default profile (`service` / `standard` depth) and non-ambiguous suggestions; stop if broad requirements lack minimum domain truth.

## Knowledge Gate & Research

1. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`, CLI `--task blueprint` when using `--reads`) for architectural/spec constraints.
2. Exclude already-shipped features from spec scope and carry lessons learned into P1 risk assessment and P4 ADRs.

## Boundaries & Exit Gates

- **Document-only**: zero implementation code edits; write outputs exclusively under `{run_dir}/outputs/`.
- **Phase gates**: P2 requires `outputs/product-brief.md` + `>= 5` terms in `outputs/glossary.json` (`required: true`); P3 requires `outputs/requirements/_index.md` with MoSCoW prioritization & traceability.
- **Exit gates**: `phases-complete` (P2–P5 documents written and cross-linked) and `readiness-passed` (P6 readiness check `>= 80%` passes cleanly; `60–79%` runs at most `2` auto-fix passes and proceeds with explicit caveats under `done_with_concerns` if still `60–79%`; `< 60%` runs at most `2` revision passes and transitions `blocked` if still `< 60%`).
