# Workflow: ew-retrospective

Four-lens retrospective: `Load Evidence -> Parallel Lens Analysis -> Triage, Synthesize & Route -> Seal`.

## Step 1: Load Read-Only Evidence

1. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) and load bound artifacts (`current-execution`, `latest-verification`, `latest-review`), `evolve session get` (or `evolve run get` for `<session|run>`), and any `--compare` prior retrospective.
2. Read the repo's check/lint/test commands (`package.json` or build config, pre-commit hooks, CI workflows) and steering files (`AGENTS.md` / `CLAUDE.md`) in read-only mode so existing unwired checks and steering bloat are visible before analysis.
3. *Done when*: Knowledge Gate recorded, all bound Run artifacts and repo guardrail/steering snapshots loaded, and source artifacts remain untouched.

## Step 2: Independent Four-Lens Analysis (`lenses-complete` gate)

Run one read-only subagent per selected lens in parallel (`workflow-analyzer` / `workflow_analyzer` via `invoke_subagent` or host equivalent; fall back to inline role instructions when absent; **join or cancel all spawned subagents cleanly before synthesis**):

1. **`technical`**: Architectural trade-offs, coupling surprises, brittle APIs, **navigation** friction (hidden cross-file dependencies lacking a pointer), and **information access** gaps (missing runtime/dev logs or readonly diagnostics).
2. **`process`**: Estimation drift, blocked gates, wasted retries, **tool economy** (token-heavy tool calls or inefficient CLIs), **`AGENTS.md` / `CLAUDE.md` bloat** (rules that belong in review standards or automated checks), and **no-op** instructions that leave agent behavior unchanged.
3. **`quality`**: Missed edge cases, test gaps, late regressions, and **automated checks** gaps (unwired/broken repo checks, missing pre-commit hooks, or missing CI lint/typecheck/test jobs — an un-linted repo is a primary finding).
4. **`decision`**: Assumptions that held vs broke, reversible vs irreversible choices, scope changes, and **context-pressure placement** (rules pushed onto high-pressure implementation steps instead of low-pressure review/CI).

Each lens reports findings independently with exact evidence anchors (`run_id`, `file:line`, exit code); missing a requested lens flags `[LOW CONFIDENCE]`.

*Done when*: Every requested lens subagent is joined or cancelled and has returned anchored findings (or `[LOW CONFIDENCE]` is explicitly recorded).

## Step 3: Triage, Synthesis & Routing (`insights-routed` gate)

1. **Deduplicate & assign deterministic IDs**: Merge cross-lens duplicates and compute `INS-{8hex}` per insight (`sha256(scope + normalized_title).slice(0, 8)`) so re-runs stay idempotent.
2. **Apply Mechanical-vs-Judgement triage & Context-Pressure placement** before choosing a route:
   - **Mechanical fix first**: Fixed syntactic patterns, banned APIs, import shapes, file-location rules, and missing/unwired lint, typecheck, hook, or CI checks get a deterministic check (`issue` for repo lint/hook/CI implementation, or `skill-evolve` when a Skill validator/script owns the check) rather than a prose rule.
   - **Judgement calls -> Review/CI over Implementer**: Cross-file consistency and contextual trade-offs that no static check can verify belong in review standards (`ew-review` / `wiki-candidate` review reference) rather than `AGENTS.md` or implementer-step bloat.
   - **No-ops & `AGENTS.md` bloat**: Prune `AGENTS.md` / `CLAUDE.md` down to lean navigation pointers; classify bloated or no-op Skill instructions for `retire`/pruning under `skill-evolve`, and repo steering bloat under `issue` or `wiki-candidate`.
3. **Order by severity & classify routes** (if `--no-route` is set, assign `route: "skipped"` to every insight and set `routing_skipped: true` in `outputs/retrospective.json` without triggering downstream routing or memory staging; Runtime excludes this Run’s direct evidence from automatic candidate extraction; other ordinary Session evidence remains eligible. Otherwise classify):
   - `wiki-candidate` -> prepare candidate summary for `ew-knowledge` closeout review (published only via `ew-knowledge` governance).
   - `skill-evolve` -> attribute exact Skill/step + Run receipt for `workflow-evolve` (`evolve session review` / `evolve skill draft` / `evolve skill retire`).
   - `issue` -> surface actionable mechanical guardrail, navigation pointer, tooling, or repo steering-cleanup item for user-authorized issue tracking.
   - `none` -> project-specific observation recorded in `retrospective.md` only.
4. Unless `--no-route`, collect user confirmation via structured prompt (`ask_question` or host equivalent) before invoking optional external routes (`kb add --kind episodic` / issue tracking); keep Wiki publication explicitly under `ew-knowledge`.
5. *Done when*: Every insight has a deterministic `INS-{8hex}` ID, mechanical-vs-judgement triage applied, severity order established, and explicit route assigned (`skipped` under `--no-route`).

## Step 4: Write Outputs & Seal

1. Write `{run_dir}/outputs/retrospective.json` (`_meta: { kind: "retrospective", schema: "retrospective/1.0", role: "primary", alias: "latest-retrospective" }`, `lenses_run[]`, `routing_skipped`, `delta` if `--compare`, `insights[]` ordered by severity with `id`, `lens`, `summary`, `evidence[]`, `route`, `status`) and `{run_dir}/outputs/retrospective.md` (including a `Mechanical vs Judgement` triage distribution table, `--compare` before/after delta table when comparing prior runs, and four-lens structured summary).
2. Write `{run_dir}/report.md` (`verdict: ready|ready_with_concerns|blocked|failed`), and when `--visual` is passed render `{run_dir}/outputs/visual-report.html` per `evolve skill workflows show visual-report` (`retrospective` view), then seal or transition the Run per `evolve skill workflows show session-run` (`--gate lenses-complete --gate insights-routed`).
3. Let the Runtime queue candidate extraction only after `evolve session complete`; unless `--no-route`, inspect the Session closeout and hand candidate review to `ew-knowledge` (`evolve skill workflows show ew-knowledge`). Do not run harvest at Run seal.
4. *Done when*: Both output files and `report.md` pass schema/meta verification and the Run is sealed or transitioned with the required gates.
