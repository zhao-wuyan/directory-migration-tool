# Workflow: ew-verify

Independent goal-backward verification: `Constraint Pre-check -> Must-Haves Breakdown (Truths / Artifacts / Key Links) -> Anti-Pattern & Regression Coverage -> Seal`.

## Step 1: Load Artifacts & Constraint Pre-check

1. Read bound `current-plan` and `current-execution` (`read_artifact` / `evolve run read artifact`; also read `latest-review` when re-verifying after review-triggered fixes to confirm all prior findings are resolved). Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) for applicable coding/review constraints.
2. Check changed files against any active Wiki tech-stack constraints (`allowed_libs`, `disallowed_imports`, `required_patterns`). Record violations as `CV-N` (`disallowed_import`, `unlisted_dependency`, `missing_required_pattern`).

## Step 2: Goal-Backward Structural Verification (`goal-backward-verified` gate)

When delegating verification, dispatch read-only `workflow-verifier` (`workflow_verifier` in Codex) and, for cross-module boundaries, `workflow-integration-checker` (`workflow_integration_checker`), joining all spawned subagents before sealing (fall back to inline verification when named agents are absent).

Normalize every criterion from `current-plan` (plus any UAT human gaps) into three verification layers:
1. **Truths** (observable behaviors): locate implementation + wiring, mark `VERIFIED`, `FAILED`, or `UNCERTAIN` (`--strict` rejects `UNCERTAIN`).
2. **Artifacts** (on-disk modules & deliverables): inspect across 3 levels:
   - `L1 Existence`: file exists (`MISSING` on fail)
   - `L2 Substance`: real implementation rather than stub/placeholder/`TODO: implement` (`STUB` on fail)
   - `L3 Wiring`: imported and invoked by callers via `evolve code callers` / ripgrep (`ORPHANED` on fail)
3. **Key Links**: verify live wiring for critical boundaries (`Component -> API`, `API -> DB`, `Form -> Handler`, `State -> Render`, `Event -> Handler`) with `file:line` proof (`WIRED` / `PARTIAL` / `NOT_WIRED`).

## Step 3: Anti-Pattern & Test Coverage (`nyquist-covered` gate)

1. **Canonical static anti-pattern scan** (unless `--skip-antipattern`): scan changed files for `TODO`/`FIXME`/`HACK`, stub returns, skipped/disabled tests (`skip`/`@ts-ignore`/`as any`), and dead imports -> write `outputs/antipattern-report.json` (`ew-verify` owns this formal static scan; `ew-execute` runs only smoke sanity and `ew-review` checks diff residuals against this report when present).
2. **Narrowest live verification** (unless `--skip-tests`): execute the targeted test/typecheck/build commands covering modified modules and their direct importers, recording exact commands, exit codes, and pass/fail counts -> write `outputs/requirement-coverage.json`.

## Step 4: Write `outputs/verification.json` & Seal

1. Write `{run_dir}/outputs/verification.json` (`_meta: { kind: "verification", schema: "verification/1.0", role: "primary", alias: "latest-verification" }`, `status: passed|gaps_found|blocked`, `truths[]`, `artifacts[]`, `key_links[]`, `constraint_violations[]`, `gaps[]` (`GAP-N` with `severity` and `fix_direction`), `commands_run[]`).
   - If `evolve experience status` reports `collecting: true`, the verifying AI may include `experiences[]` in `verification.json` only for an independently verified, reusable lesson. Use a passed result and a unique `truths[].id` with `status: VERIFIED`. Each draft has `domain`, a versioned `stack` (e.g. `python@3.12`), `title`, `applicability`, `lesson`, `verification` (sanitized reproduction or before/after observation), and `evidence` (that truth ID). Use single-line fields; omit project names, paths, code, raw logs, secrets, and guesses. If nothing qualifies, omit the field. Session completion stages the sealed evidence locally; it does not publish.
2. Write `{run_dir}/report.md` (`verdict: ready|ready_with_concerns|blocked|failed`; include a compact `Truths × Artifacts (L1/L2/L3) × Key Links` verification matrix plus a Mermaid `Key Links` wiring diagram marking `WIRED`/`PARTIAL`/`NOT_WIRED`, separating static code wiring from rendered/runtime verification, and optional `--visual` report per `evolve skill workflows show visual-report`; set `next` to `ew-review` when `passed` for formal acceptance, or `ew-plan --gaps` / `ew-debug` when `gaps_found` remains).
3. Seal or transition the Run per `evolve skill workflows show session-run` (`--gate goal-backward-verified --gate nyquist-covered`; map `passed`/`ready` -> `--verdict done`, `gaps_found`/`ready_with_concerns` -> `--verdict done_with_concerns` so `latest-verification` binds downstream, `blocked`/`failed` -> `evolve run transition` without `run complete`). Persist recurring verification pitfalls via `evolve kb add "<pitfall>" --kind episodic --type error --scene verify --session <session_id> --evidence <source-ref>` and stage reusable lessons in `report.md` frontmatter; defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete`.
