# Odyssey Shared Base

Shared by all eight Odyssey modes (`debug`, `improve`, `planex`, `review`, `security`, `defensive`, `ui`, `arch`). Mode files own their front-half state machine, pattern source, category table, completion summary, and `W001-W007` warnings. Everything below is single-source; map host tools via [runtime adapters](runtime-adapters.md).

<execution_discipline>

1. **Workspace commits (mutable modes only)** — in mutable modes ONLY when workspace source files change during `FIX` (or `DEEPEN` in `arch`, `EXECUTE` in `planex`), commit workspace source changes using `"odyssey-{mode}({slug}): {STATE} — {summary}"` at the mode's fix granularity (per verified fix in `debug`/`improve`/`ui`; per completed severity tier in `review`; per verified deepening/guardrail step in `arch`; per `EXECUTE`/`FIX` pass in `planex`). Never emit empty commits on read-only/analysis phases (`SURVEY`, `AUDIT`, `DIAGNOSE`, `DIVERGE`, `DESIGN`, `PLAN`, `VERIFY`/`CONFIRM`), and never commit `{run_dir}/outputs/*` (`session.json`, `evidence.ndjson`, `understanding.md`); register them atomically via `evolve run complete`. Read-only `security` and `defensive` modes never touch source files or commits.
2. **Confident edits vs decision gate** — implement confident fixes immediately; record ambiguous tradeoffs as `evidence.ndjson {"phase":"decision","status":"pending"}` without modifying code. Only these justify waiting on a decision (unless overridden by a mode's explicit `## Decision Gate`, e.g., `odyssey-ui`): cross-module architecture tradeoffs | business-semantics ambiguity | new dependency or breaking API. ("Unsure how", "too big", "pre-existing" are NOT valid triggers.)
3. **Subagent & CLI assist** — parallelize independent scans/dimensions cleanly via host subagents (always joining/cancelling every spawned subagent — see [runtime adapters](runtime-adapters.md)) and run `evolve agent exec "<prompt>" --to codex|agy|claude|omp --mode analysis|write` for cross-angle checks (pass `--session <id> --run <run>` only with `--to codex` or `--to omp`). OMP runs as a one-shot restricted worker: `analysis` keeps the read-only tool boundary and `--timeout` is milliseconds. For independent second opinions explicitly select an authorized model; same-model children are not independent-model verification. Keep existing Codex defaults.
4. **No context-exhaustion aborts** — complete every campaign cleanly through `S_RECORD -> END`.
5. **Zero-residual** — every finding needs a concrete disposition (`fix` / `issue` / `decision`); "report only" and blanket "pre-existing" skips are forbidden. Unfixed backlog items recorded as `issue` append a deduplicated `phase: "issue"` entry to `evidence.ndjson` (never calling external issue trackers without explicit user authorization).

</execution_discipline>

<shared_invariants>

Mode `prepare/odyssey-<mode>.md` files restate these as pre-flight guardrails; keep wording aligned:

- **Generalize is mandatory** unless `skip_generalize == true`; prior-phase convergence is NOT a valid skip reason.
- **Thoroughness floor** — all 3 generalization layers (syntax/semantic/structural) must be attempted and logged. A single-layer quick grep does NOT satisfy it.
- **Evidence append-only** — evidence.ndjson entries are immutable observations; modifying or deleting them is forbidden.
- **Per-item justification** — when `cross_phase_loops >= max_loops`, every unfixed finding needs its own reason. A blanket "pre-existing" is forbidden.

</shared_invariants>

<shared_schemas>

Artifact paths, `required` flags, `_meta` placement (`session.json`, `explore.json`/`anchors.json`, first record of `evidence.ndjson`, YAML frontmatter of `understanding.md`), and `evolve run complete` sealing follow [runtime adapters](runtime-adapters.md) §4 (single source).

- **`session.json` core**: `{ current_state: "S_INTAKE", flags: { skip_fix: false, skip_generalize: false, auto_confirm: false }, fix_rounds: 0, max_fix_rounds: 5, remaining_actionable: 0, phase_goals: [], phase_goals_all_done: false, self_iteration_log: [], cross_phase_loops: 0, max_loops: 5, created_at: "", updated_at: "" }` + mode fields.
- **`evidence.ndjson` shared phases & lines**: Every mode logs `{ ts, phase, type, source, content, note }` + phase fields; shared cross-mode phases are `discovery|decision|issue|self-iteration` (alongside mode-specific front-half phases):
  - **`issue` record**: `{ ts, phase: "issue", id: "ISS-NNN", title, severity, file, line, source_phase, reason, status: "open|deferred", external_ref? }`. Deduplicate by `(file, line, title)` before appending; `Issues: {N} created` in Completion Summary equals the count of unique `phase: "issue"` records in `evidence.ndjson`. Populate `external_ref` only if the user explicitly authorized an external tracker update.
- **`generalization_stats`**: `{ patterns_extracted, total_hits, cross_layer_confirmed, regression_risks, by_layer: { syntax, semantic, structural }, deepening_triggered }`.

</shared_schemas>

<anti_stall>

Track `progress_metrics: { phase_stats: {}, stale_count: 0, heartbeat_stalls: 0, last_productive_phase: "", convergence_trend: "unknown" }` and `directions_tried: []` (`{ phase, round, strategy_type: "scope_widen|perspective_shift|tool_switch|structural_pivot", strategy_desc, result }`):
- After each analytical phase, count deduplicated `new_findings` vs `repeated`: `new == 0` -> `stale_count++`, `stalling`; `new > 0` -> reset `stale_count`, update `last_productive_phase`; 2 declining phases -> `diminishing`.
- New self-iteration strategies must differ from same-phase `directions_tried` (`stale_count`: 1 = shift perspective, 2 = structural pivot, 3 = structured user escalation or `-y` `INCONCLUSIVE`).
- Optional `--heartbeat`: update `session.json.updated_at` each phase; >15 min stalls increment `progress_metrics.heartbeat_stalls` (separate from `stale_count`; see [runtime adapters](runtime-adapters.md)).

</anti_stall>

<self_iteration>

Evaluate every analytical stage + `S_GENERALIZE` across three checks: **Coverage** (related files covered), **Depth** (`>=80%` with `file:line`), **Actionability** (concrete next step). If any fails and `stale_count < 3`, pick an untried strategy (`scope_widen` | `perspective_shift` | `tool_switch` | `structural_pivot`) and log to `evidence.ndjson {"phase":"self-iteration"}` + `session.json`. Exit when all suffice or `stale_count >= 3`.

</self_iteration>

<fix_loop_cap>

Unified fix-loop bound across all mutable modes (`debug`, `improve`, `review`, `ui`, `arch` use `fix_rounds` / `max_fix_rounds` default `5`; `planex` uses `current_iteration` / `max_iterations` default `3`):
- Every `S_CONFIRM` / `S_VERIFY → S_FIX` (or `S_VERIFY → S_DEEPEN` in `arch`) `: needs_rework` transition (and `review` internal `A_FIX` retry rounds) increments `fix_rounds++` in `outputs/session.json`.
- Re-entry to `S_FIX` / `S_DEEPEN` is guarded by `fix_rounds < max_fix_rounds` (or `current_iteration < max_iterations` in `planex`).
- When `fix_rounds >= max_fix_rounds` (or `current_iteration >= max_iterations`) and actionable issues/failed criteria remain:
  - **Normal mode:** structured user prompt — either (a) record remaining items as `deferred`/`issue` (`ESCALATED`/`PARTIAL`) and transition to `S_RECORD`, or (b) approve **at most one** bounded extension (`max_fix_rounds += 2` or `max_iterations += 2`, max 1 extension; exceeding again forces exit to `S_RECORD`).
  - **`-y` mode:** NEVER bypass escalation or auto-accept unmet criteria — record remaining items as `deferred`/`issue` (`ESCALATED` / `PARTIAL`) with per-item reasons and transition to `S_RECORD`.

</fix_loop_cap>

## Shared Discovery Phases (`debug` / `review`)

### A_ARCHAEOLOGY

Run 2 parallel subagents (join before advancing — see [runtime adapters](runtime-adapters.md)):
- **Timeline**: `git log --oneline -20 -- {files}`
- **Blame**: top 3 files `git blame -L {region}`

Log `evidence.ndjson` (`phase=archaeology`). If a supported CLI executor (`codex|agy|claude|omp` on `PATH` / host config) is available, run:
`evolve agent exec "PURPOSE: review recent modifications related to {target_or_issue}. EXPECTED: JSON [{commit_sha, risk_level, analysis, could_cause_issue, explanation}]" --to <cli> --mode analysis`

Update `understanding.md §2` (`Archaeology`). If an archaeology subagent or delegate call fails, log `W003` and proceed with available results.

### A_EXPLORE

Check host `PATH` / config for an available CLI delegate (`codex`, `agy`, `claude`, or `omp`). If none is configured/available or `evolve agent exec` cannot launch an executable, emit `W006`, record the coverage gap in `explore.json` (`cli_tool: "none"`), and skip the delegate explore call; otherwise run:
`evolve agent exec "PURPOSE: trace call chains, recent changes, error gaps, and similar patterns for {target_or_issue}. EXPECTED: JSON {call_chains, recent_changes, error_gaps, similar_patterns}" --to <cli> --mode analysis`

Write `outputs/explore.json` + `evidence.ndjson` (`phase=explore`). Update `understanding.md §3` (`Exploration`). Mark `G2`.

**GATE: discovery-complete** — archaeology/explore evidence logged, `understanding.md` §2–§3 updated, `G2` marked (`W003` partial / `W006` CLI-unavailable skip acceptable).

## Shared Back-Half

`S_GENERALIZE -> S_DISCOVER -> S_RECORD -> END` runs cleanly at the end of every mode.

<shared_actions>

### A_GENERALIZE

1. **3-layer pattern extraction** from each mode's `## Generalize Source` into `session.json.patterns[]` (`[{ id, source, layer, signature, description, risk, fix_template, confidence }]`):
   - **Syntax**: regex/grep signature (`eval(`, missing `await`, etc.)
   - **Semantic**: anti-pattern reasoning across related modules
   - **Structural**: parallel file/import/override structures
2. **4-way parallel scan** (Syntax grep, Semantic scan, Structural match, Historical `git log -S`; join all workers before deduplicating) -> multi-layer boost / single-layer `needs_review` / prior-fix `regression_risk`; >=3 hits in one module triggers 1 targeted deep scan.
3. Log `session.json.generalization_stats` + `understanding.md` generalization section. Zero hits after attempting all 3 layers emits the mode's zero-hit warning, never blocks `S_RECORD`.

### A_DISCOVER

1. Triage every scan hit (`±10` lines context) as `bug` / `risk` / `safe`:
   - `bug` + `fix_template` -> fix & re-enter the mode's `Discover routing` target (default `S_FIX` in mutable audit modes; **modes override this target in `## Generalize Source` (`Discover routing`)**, e.g. `planex` routes to `S_EXECUTE`, `review` to `S_REVIEW`, `debug`/`improve` to `S_DIAGNOSE`, `arch` to `S_DEEPEN`, `ui` to `S_AUDIT`, read-only `security`/`defensive` to `S_SCAN` or `issue`/`decision`)
   - `bug` needing decision / read-only mode -> record `phase: "issue"` or `phase: "decision"`
   - `risk` -> add guard if safe (mutable) or record `issue`/`decision`
   - `safe` -> skip
   *(Use structured prompt in Normal mode; follow mode's `-y` table when `-y`.)*
2. Update `remaining_actionable` and increment `cross_phase_loops`:
   - **Re-entry path**: when `cross_phase_loops < max_loops` and re-entry occurs, complete the re-entered loop (`S_CONFIRM` / `S_VERIFY` / `S_REPORT`); then re-run `S_GENERALIZE` ONLY on newly added fixes/findings if `!skip_generalize && cross_phase_loops < max_loops`, otherwise advance straight to `S_RECORD`.
   - **Loop bound**: if `cross_phase_loops >= max_loops`, record per-item justification (`issue`/`decision`) for every unfixed finding and proceed to `S_RECORD`. Update `evidence.ndjson` + `understanding.md`.

### A_RECORD

1. Finalize `understanding.md` summary/learnings per the mode's `## Knowledge Persistence` table; resolve pending decisions and audit `phase_goals[]` (`pending -> done|skipped|failed`, `phase_goals_all_done = true` iff all confirmed).
2. Before Run seal, record verified non-obvious lessons with applicability, method, verification, and real evidence using the memory/feedback contract in `evolve skill workflows show session-run`. Set `current_state = "END"` in `outputs/session.json`, emit the mode’s Completion Summary, and seal with fenced `evolve run complete` and a grounded summary.
3. Let the orchestrator finish the Session, then inspect its queued closeout through `evolve skill workflows show ew-knowledge`. Do not initiate harvest at Run seal; candidates are reviewed after `evolve session complete`, and Wiki publication requires explicit authorization.

</shared_actions>

<shared_appendix>

### Phase Initialization & Pre-load

Initialize `phase_goals[]` in `session.json` after INTAKE and mirror cleanly via [runtime adapters](runtime-adapters.md). Under `-y`: pending decisions -> default + `deferred`; 3-strike diagnosis -> `INCONCLUSIVE` (`S_RECORD`); fix/iteration cap -> `ESCALATED` (`S_RECORD`, never auto-accept unmet criteria); goal audit -> mark `done` only for goals whose `done_when` is evidenced (unmet goals stay `failed` or `skipped`).

Before discovery or code search, satisfy the **Knowledge Gate** (`evolve skill workflows show session-run`):
1. **Bound `session-priors` first** (when available in Run inputs and its `lookup_scopes` matches the current scope): read via `evolve run read artifact <run> session-priors --session <session> --scope '<json>'`.
2. **Otherwise query the CLI** (one receipt source per Run): use `evolve search "<keywords>" --mode context --session <id> --reads "<run_dir>/knowledge-reads.json"` (add `--format compact` for terse text; `--mode context` uses short prefix IDs, `q-N` + `idx`, inline `<= 300`-char `content` / `truncated: true` snippets, and `20`-item/`4000`-char `constraints` cap), followed by `evolve kb get <id|q-N:idx>... --resolved --reads "<run_dir>/knowledge-reads.json"` (or `--pick <n,...>`, auto-linking `from_search_id` on `--reads`).
3. After the Knowledge Gate, inspect the codebase with `evolve code search "<keywords>"` and check `evolve session list` when `-c` or prior scope overlap may exist.

### Error Codes

`E000-E004` and `W008` are unified in [SKILL.md](../SKILL.md). `W001-W007` are owned per mode (the same warning code has mode-specific meaning; never assume cross-mode equivalence).

### Next Step Routing

| Outcome | Recommended follow-up |
|---|---|
| Reusable patterns/rules ready for review | Follow `evolve skill workflows show ew-knowledge` after Session completion |
| Independent second opinion needed | `evolve agent exec "<review prompt>" --to codex\|agy\|claude\|omp --mode analysis`; explicitly select an authorized different model for cross-model verification (OMP `--model <selector>`, one-shot restricted worker), with binding rules in [runtime adapters](runtime-adapters.md) |
| Pending `phase=decision` items remain | Filter `evidence.ndjson` (`phase == "decision" && status == "pending"`) |
| Backlog `phase=issue` items logged | Filter `evidence.ndjson` (`phase == "issue"`) |

</shared_appendix>
