# Pre-flight: ew-review

Confirm audit target, bound context, and read-only invariants before running [workflow](workflow.md).

## Input & Mode

- **Default (`current-execution` + optional `current-plan`)**: read bound `current-execution` (`read_artifact` / `evolve run read artifact`) and `current-plan` (when present) to pin the changed files, git diff against the base commit, and originating plan/task requirements.
- **Explicit `<target>`**: file/directory path, `HEAD` / `staged` (`git diff`), or commit/PR/fixed-point range (`git diff <fixed-point>...HEAD`). Validate the ref resolves and the diff/target is non-empty before spawning reviewers.
- **`--tier quick`**: focused pass on changed hunks (`Spec` fidelity/scope check + `Standards` hard violations on `correctness` and `security` + regressions).
- **`--visual`**: generate a self-contained side-by-side HTML visual review report (`outputs/visual-report.html` per `evolve skill workflows show visual-report`) keeping **Standards** and **Spec** panels strictly separated.
- **`-y`**: skip optional confirmation prompts; never suppress findings, missing evidence, or gate checks.

## Boundaries & Invariants

- **Read-only audit**: never edit repository source files; write Run outputs only under `{run_dir}/outputs/` and `{run_dir}/report.md`.
- **Two-axis isolation & fresh context**: separate the **Spec** axis (requirements fidelity, partial/missing items, wrong behavior, scope creep against `current-plan` / `current-execution` or standalone spec) from the **Standards** axis (repo documented standards + Wiki rules + 12 Fowler Code Smells baseline across `correctness`, `security`, `performance`, `architecture`). Spawn parallel reviewers with **fresh context windows** and **sparse context pointers** (`fork` reserved strictly for explicit warm deep-dive branches, never for independent audit axes).
- **Standards precedence & smell discipline**: documented repo standards and active Wiki constraints override the Fowler baseline; baseline smells are always labelled **judgement calls** (never hard violations), and anything automated tooling/linters already enforce is skipped.
- **Non-merging & non-reranking**: report **Spec** and **Standards** findings side by side without merging across axes or picking a single cross-axis winner, preserving `review-findings/1.0` compatibility in `outputs/review.json`.
- **Knowledge reuse**: follow the Run Knowledge Gate (`evolve skill workflows show session-run`): consume `session-priors` for covered `review`/`coding` scope; query `evolve search "<query>" --mode context` + `evolve kb get <id>` (with CLI `--reads` / `--from-search`) whenever the target introduces new paths or categories.
- **Evidence-first & visual truth**: every finding requires verified `file:line`, observed evidence (plus exact spec/plan quote on `Spec` or rule/smell citation on `Standards`), accurate severity (`critical|high|medium|low`), and concrete remediation. When diffs modify UI routes, styles, tokens, or rendered components, verify rendered UI behavior across viewports via host browser tools (never treat static code inspection as rendered proof; record `unverified_visual` explicitly if runtime/browser is unavailable).
- **Zero-residual check & routing**: verify claimed fixes and test results against disk state without collapsing distinct root causes. Route actionable fixes via `outputs/review.json` + `next` to `ew-plan --gaps` (cross-module/multi-step), `ew-companion` (`<= 3` findings, single concern), or `ew-odyssey-review` (iterative standalone repair campaign) — **never** route `ew-review` `next` directly to `ew-execute`.
