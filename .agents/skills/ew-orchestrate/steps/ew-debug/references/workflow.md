# Workflow: ew-debug

Scientific root-cause pipeline: `Symptom Baseline -> Cluster -> Isolated Hypothesis Investigation -> Confidence & Pressure Pass -> Synthesis & Seal`.

## Step 1: Collect Symptoms & Tight-Red-Loop Baseline

1. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) and check bound inputs (`latest-test`, `latest-auto-test`, `latest-review`, `current-execution`).
2. **Tight-Red-Loop Gate (before code theorizing)**: construct and run one fast (seconds), deterministic, agent-runnable command (failing test, CLI fixture diff, HTTP/curl script, headless browser, replayed trace, throwaway harness, property/fuzz loop, or bisection) that exercises the bug path and goes red on the user's exact symptom. For flaky bugs, apply **100x flake amplification** (100x trigger loop, concurrency stress, narrowed timing windows, pinned RNG/time) until reproduction rate is high enough to debug. Redact secrets as `<REDACTED>`. Do NOT advance to code theorizing until this red-capable command exists (if impossible locally, block and ask the user for env access, redacted logs/HAR, or temporary instrumentation).
3. **Minimal Load-Bearing Repro**: shrink the failing repro one element at a time (inputs, callers, config, steps), re-running after each cut until every remaining element is load-bearing (removing any element turns the loop green). Record expected vs actual behavior, `git log --oneline -10`, exact command, exit code, and redacted symptom into `outputs/reproduction.json`.
4. Under `--from-test`, group failures (from `latest-test` `gaps[]`, `latest-auto-test` `app_defects[]`, or `latest-review` `findings[]`) into independent component/flow clusters.

## Step 2: Isolated Hypothesis Investigation (`hypothesis-tested` gate)

Dispatch an isolated read-only investigation subagent (`workflow-debugger` / `workflow_debugger` via `invoke_subagent` or host equivalent; fall back to inline role instructions when absent; in `--parallel` mode, one subagent per non-overlapping cluster; **join or cancel all spawned subagents cleanly before Step 3**).

Each investigator follows strict discipline:
1. Form **3–5 ranked falsifiable hypotheses** (`"If <X> is the cause, then <changing Y> makes the bug disappear / <changing Z> makes it worse"`) before testing any single one, then test ONE variable at a time via backward tracing (`evolve code trace` + reproduction logs). (For performance regressions, capture a timing/profiler/query-plan baseline and bisect rather than logging.)
2. Prefer debugger/REPL inspection over logs; when targeted boundary logs are needed, tag every diagnostic log with a unique `[DEBUG-xxxx]` prefix (e.g., `[DEBUG-a4f2]`) for single-`grep` cleanup.
3. Append every test step, observed result, and `file:line` anchor to `{run_dir}/evidence/evidence[-<cluster>].ndjson` and summarize active state in `{run_dir}/evidence/understanding[-<cluster>].md`.
4. Enforce **3-strike stop**: after 3 falsified hypotheses without confirmation, stop guessing, record `INVESTIGATION INCONCLUSIVE` with the architectural contradiction anchors, and advance to the `inconclusive` exit path in Step 3/4 (do not fabricate a root cause).

## Step 3: Confidence Scoring, Seam & Mutation Check (`evidence-grounded` gate)

Compute multi-factor confidence (`evidence_depth * 0.30 + evidence_strength * 0.25 + coverage_breadth * 0.20 + reproduction * 0.15 + consistency * 0.10`; `< 40%` low, `40–70%` medium, `> 70%` high) and run mandatory checks before writing outputs:
- **Pressure Pass**: cross-check confirmed causes (or 3-strike refutations) against every refuted hypothesis and contradicting observation.
- **Devil's Advocate** (when isolation `> 0.7`): verify whether the apparent cause hides a deeper upstream invariant violation.
- **Seam, Forced-Mutation & Cleanup Check**:
  - Verify whether a **correct seam** exists for `ew-plan --gaps` / `ew-execute` to lock down a regression test covering the real caller/integration pattern; if no valid seam exists, flag `missing_seam` explicitly rather than recommending a shallow test.
  - If any red signal was forced by temporarily mutating code or a fixture, run `git diff` against a pristine copy to prove the mutation landed before trusting it, then revert.
  - Run a `grep` check confirming zero remaining `[DEBUG-xxxx]` tags and revert/delete any throwaway workspace probes.
- **Readiness check**:
  - **`confirmed` / `partial`**: confirm evidence completeness `>= 40%`, pressure pass complete, zero unresolved contradicting evidence, and concrete `affected_files[]` + `fix_direction` (plus `missing_seam` handoff note when applicable).
  - **`inconclusive` (3-strike stop)**: confirm `>= 3` falsified hypotheses logged with `file:line` refutation anchors and architectural contradiction summary; do NOT fabricate `affected_files[]` or `fix_direction`.

## Step 4: Write Outputs & Seal

1. Write `{run_dir}/outputs/diagnosis.json` (`_meta: { kind: "diagnosis", schema: "diagnosis/1.0", role: "primary", alias: "latest-debug" }`, `status: confirmed|partial|inconclusive`, `clusters[]`, `confidence`, `pressure_pass`, `missing_seam?`, `escalation_target?`), `{run_dir}/outputs/hypotheses.json`, and `{run_dir}/outputs/reproduction.json`. Write `{run_dir}/outputs/fix-directions.json` (`_meta: { kind: "fix-directions", schema: "fix-directions/1.0", role: "attachment", alias: "latest-fix-directions" }`, actionable root cause + target `file:line` + verification criteria + seam/handoff notes) only for `confirmed`/`partial` diagnoses.
2. Write `{run_dir}/report.md` and seal/transition per `evolve skill workflows show session-run` (`--gate hypothesis-tested --gate evidence-grounded`):
   - `confirmed`: `verdict: ready`, `next: [{ command: ew-plan, args: ["--gaps"], reason: "...", needs: [latest-debug] }]` (if `missing_seam` is flagged, include architectural handoff recommendation to `ew-analyze` or standalone `ew-odyssey --mode arch`) -> `evolve run complete ... --verdict done`.
   - `partial`: `verdict: ready_with_concerns`, `next` -> `ew-plan --gaps` (or `ew-analyze` / `ew-odyssey --mode arch` when `missing_seam` blocks regression locking) -> `evolve run complete ... --verdict done_with_concerns`.
   - `inconclusive` (complete 3-strike refutation dossier ready for architectural re-analysis): `verdict: ready_with_concerns`, `next: [{ command: ew-analyze, reason: "3-strike inconclusive diagnosis or missing seam; architectural analysis required", needs: [latest-debug] }]` (note standalone `ew-odyssey --mode arch` for seam deepening or `ew-odyssey --mode debug` for multi-iteration repair campaigns) -> `evolve run complete ... --verdict done_with_concerns`.
   - Hard blocker (insufficient reproduction/evidence or unable to safely continue without human intervention): `verdict: blocked`, `next` -> `ew-analyze` / human intervention -> do **not** call `run complete`; transition via `evolve run transition <run_id> blocked ... --reason "INVESTIGATION INCONCLUSIVE: ..."` (see `evolve skill workflows show session-run`).
3. When `diagnosis.status` is `confirmed` or `partial`, record verified non-obvious root causes and workarounds in working memory via `evolve kb add "<root cause and workaround>" --kind episodic --type <verified_fix|error> --scene debug --session <session_id> --evidence <source-ref>` (use verified_fix only for a verified repair; label partial or unresolved results) and stage reusable patterns in `report.md` frontmatter; defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete` (never run `kb harvest` mid-chain).
