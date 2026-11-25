# Workflow: ew-test

Behavioral testing & UAT: `Resolve Evidence -> Build Test Plan -> Smoke / Browser / UAT Execution -> Diagnose & Package -> Seal`.

## Step 1: Build Test Plan (`outputs/test-plan.json`)

1. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) and load bound inputs (`latest-verification`, `current-plan`, `current-execution`, `latest-review`, `latest-debug`), recording `evidence_source`.
2. Derive observable test scenarios (`T-001`, `T-002`, ...) mapped to requirement/task IDs and write `{run_dir}/outputs/test-plan.json`.

## Step 2: Execute Checks by Mode

- **Cold-start smoke (`--smoke`)**: run build/startup/dependency checks first; abort or record blocker immediately on startup failure.
- **Deterministic browser acceptance (`--frontend-verify`)**: inspect live routes via `browser` subagent (joined cleanly) or headless browser runner, asserting interactive flows, API write persistence, console/network cleanliness, and responsive DOM -> write `{run_dir}/outputs/e2e-results.json`.
- **Conversational UAT & acceptance checks**:
  - Execute applicable existing verification/acceptance test commands (`coverage` / smoke suite; optionally dispatch `workflow-nyquist-auditor` / `workflow_nyquist_auditor` read-only for requirement-to-test gap checks, joined cleanly before sealing) and record exact outputs in `{run_dir}/outputs/coverage.json` (layered automated test generation & L0–L3 convergence belongs to `ew-auto-test`).
  - Present interactive scenarios one by one via structured prompt (`ask_question` or concise user check; under `-y` / automated scope, run executable assertions and mark UI-manual scenarios `UNCERTAIN` / `skipped` honestly).
  - Log every scenario outcome into `{run_dir}/outputs/uat.md` (YAML frontmatter `kind: uat-log`, `schema: uat-log/1.0`, `role: attachment`, `alias: uat-log`) and `{run_dir}/outputs/acceptance.json`, inferring severity (`critical|high|medium|low`) from user feedback/error impact.

## Step 3: Results, Gap Candidates & Seal

1. Write `{run_dir}/outputs/test-results.json` (`_meta: { kind: "test-results", schema: "test-results/1.0", role: "primary", alias: "latest-test" }`, `evidence_source`, `summary: { total, passed, failed, skipped, pass_rate }`, `gaps[]` mapping `test_id`, `truth`, `reason`, `severity`, `requirement_ref`) and optional `outputs/issue-candidates.json` (all JSON/MD carrying matching `_meta` / frontmatter).
   - If `evolve experience status` reports `collecting: true`, the testing AI may include `experiences[]` for reusable lessons with a meaningful reproduction or before/after check. Add corresponding `tests[]` rows with unique `test_id` and `status: passed`; `summary.failed` must be zero. Each draft contains `domain`, versioned `stack` (e.g. `python@3.12`), `title`, `applicability`, `lesson`, `verification` (sanitized observation), and `evidence` (the passed test ID). Use single-line fields without private names, internal paths, code, raw logs, or secrets. Omit unsupported drafts; the completed Session exposes eligible sealed entries in the local review queue, not on the server.
2. Write `{run_dir}/report.md` (`verdict: ready|ready_with_concerns|blocked|failed`; if gaps exist and results are sealable, use `ready_with_concerns` and propose `ew-debug --from-test` or `ew-plan --gaps` in `next`; if prerequisite/smoke setup blocks testing, use `blocked`/`failed`).
3. Seal or transition the Run per `evolve skill workflows show session-run` (`--gate coverage-met --gate pass-rate-met`; map `ready` -> `--verdict done`, `ready_with_concerns` -> `--verdict done_with_concerns`, `blocked`/`failed` -> `evolve run transition` without `run complete`). Stage reusable testing patterns in `report.md` frontmatter or `evolve kb add --kind episodic`; defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete`.
