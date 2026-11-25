# Workflow: Odyssey Planex

Single requirement delivery loop — requirement parsing → acceptance criteria → plan → execute → verify → generalize. Follow [shared phases](odyssey-base.md) and [runtime adapters](runtime-adapters.md).

---

## State Chain

```
S_INTAKE → S_PLAN → S_EXECUTE → S_VERIFY → S_FIX → [back-half]
```

FIX↔VERIFY loop until all criteria pass. Back-half: `S_GENERALIZE → S_DISCOVER → S_RECORD → END` (see [shared phases](odyssey-base.md) §Shared Back-Half).

---

## Boundary

**In scope:** Single requirement delivery loop — requirement parsing → all acceptance criteria passing + generalization.
**Out of scope:** Multi-requirement orchestration → `/ew-orchestrate "<multi-requirement intent>"` | Deep debugging → `--mode debug` | Code review → `--mode review` | UI optimization → `--mode ui`

---

## Context

### `--template <name>` and Target Resolution

Template patterns, target resolution, and `--method`/`--executor` options are defined in [the selected mode preparation](prepare/odyssey-planex.md) §Input Interpretation — that is the canonical wording.

### Session Fields

```json
{ "requirement": "",
  "acceptance_criteria": [{"id":"AC1","criterion":"","verify_method":"test|grep|cli-review|manual","status":"pending","evidence":"","passed_at":null}],
  "plan": {"tasks":[{"id":"T1","title":"","description":"","criteria_refs":["AC1"],"status":"pending","files_modified":[],"domain":"general","executor":"agent"}],"created_at":""},
  "execution_config": {"method":"auto","default_executor":"","domain_routing":{"frontend":"agent","backend":"agent","default":"agent"},"code_review_tool":"Skip","verification_tool":"Auto","confirmed":false},
  "iterations": [{"iteration":1,"started_at":"","completed_at":"","criteria_before":{"passed":0,"total":0},"criteria_after":{"passed":0,"total":0},"gaps_fixed":[],"files_modified":[]}],
  "current_iteration": 0, "max_iterations": 3, "iteration_extended": false,
  "patterns": [], "generalization_stats": null }
```

### evidence.ndjson Phases

`planning|execution|verification|fix|generalization|discovery|decision|issue|self-iteration`

### phase_goals[]

| ID | Goal | done_when | phase | skip_when |
|----|------|-----------|-------|-----------|
| G1 | Acceptance criteria defined | ≥1 criterion in acceptance_criteria[] | S_INTAKE | — |
| G2 | Plan created | session.json.plan populated | S_PLAN | — |
| G3 | Implementation complete | all plan tasks executed | S_EXECUTE | skip_fix AND skip_verify |
| G4 | All criteria pass | all acceptance_criteria[].status == passed | S_VERIFY | skip_verify |
| G5 | Pattern generalized | patterns[] ≥1 entry | S_GENERALIZE | skip_generalize |
| G6 | Discoveries triaged | all scan hits classified | S_DISCOVER | skip_generalize |
| G7 | Learnings persisted | reusable Wiki candidates staged/proposed (or none actionable) | S_RECORD | — |

### understanding.md — 8 Sections

1. Requirement & Criteria
2. Plan
3. Execution
4. Verification
5. Fix Log
6. Generalization
7. Discoveries
8. Learnings

---

## State Machine

### Transitions

```
S_PLAN    → S_EXECUTE    : !(skip_fix AND skip_verify)
S_PLAN    → S_GENERALIZE : skip_fix AND skip_verify AND !skip_generalize
S_PLAN    → S_RECORD     : skip_fix AND skip_verify AND skip_generalize
S_EXECUTE → S_VERIFY     : !skip_verify
S_EXECUTE → S_GENERALIZE : skip_verify AND !skip_generalize
S_EXECUTE → S_RECORD     : skip_verify AND skip_generalize
S_VERIFY  → S_GENERALIZE : all passed AND !skip_generalize
S_VERIFY  → S_RECORD     : all passed AND skip_generalize
S_VERIFY  → S_FIX        : some failed AND !skip_fix AND current_iteration < max_iterations
S_VERIFY  → S_PLAN       : fundamental plan flaw AND cross_phase_loops < max_loops → cross_phase_loops++ (replan, criteria preserved)
S_VERIFY  → S_RECORD     : some failed AND (skip_fix OR current_iteration >= max_iterations) (PARTIAL if skip_fix; else escalate per [shared base](odyssey-base.md) `<fix_loop_cap>`)
S_FIX     → S_VERIFY     : fix implemented (loop)
```

**`A_DISCOVER` override (per [shared base](odyssey-base.md)):** Discover routes to `S_EXECUTE` (not `S_FIX`) for areas needing the same implementation (`cross_phase_loops++`). Both `replan` and `discover` share the bounded `cross_phase_loops < max_loops` budget (log `replan` vs `discover` in `evidence.ndjson`); after `S_EXECUTE → S_VERIFY` passes, re-run `S_GENERALIZE` on newly added implementation patterns if `!skip_generalize && cross_phase_loops < max_loops`, else proceed to `S_RECORD`.

### Actions

**A_INTAKE extra** — Define acceptance criteria: analyze requirement → derive testable criteria, each with `verify_method` (test|grep|cli-review|manual). Normal: structured prompt to confirm/edit | `-y`: auto-derive, record `{"phase":"decision","type":"criteria-confirmation","status":"deferred"}`. Mark G1.

**GATE: criteria-defined** — ≥1 criterion in `acceptance_criteria[]`, each with a testable `criterion` and assigned `verify_method` (test|grep|cli-review|manual); confirmed by user or auto-derived with deferred confirmation under `-y`; G1 marked.

**A_PLAN** — (1) Decompose requirement into ordered tasks mapped to acceptance criteria. (2) CLI-assisted planning (optional; normalize CLI target to `codex|agy|claude` (or explicit `omp`) or skip with `W006` if none available):
```bash
evolve agent exec "PURPOSE: Create implementation plan for: {requirement}
TASK: Decompose into subtasks | Map to acceptance criteria | Identify dependencies
MODE: analysis
CONTEXT: @**/* | Criteria: {criteria_summary}
EXPECTED: JSON [{task_id, title, description, criteria_refs, deps}]
" --to codex --mode analysis
```
(3) Write `session.json.plan`, append evidence (planning), update §2. Mark G2. (If `skip_fix AND skip_verify`, skip `S_EXECUTE`/`S_VERIFY`/`S_FIX` and advance to `S_GENERALIZE` / `S_RECORD`.)

**A_EXECUTE** —

- **Step 1 — Execution Options & CLI Normalization.** Probe available CLI executors (`codex`, `agy`, `claude`, `omp` accepted by `evolve agent exec --to`) on host `PATH` / config; host subagents (`runtime-adapters.md`) serve the `agent` path. OMP is an explicit one-shot restricted worker: `--timeout` is milliseconds, `analysis` is read-only, and worktrees/`--stream` are unsupported. Apply CLI flags first:
  - `--method agent|cli|auto` sets `execution_config.method` (default `auto`).
  - `--executor <codex|agy|claude|omp>` sets `execution_config.default_executor` (if `--method` omitted, implies `cli`; if `--method auto`, sets the target CLI for `domain_routing` CLI tasks).
  - `--skip-verify` overrides `verification_tool` to `"Skip"`.
  - Skip interactive confirmation if `-y` OR `--method` explicitly set OR `execution_config.confirmed == true` (resume). Otherwise prompt via structured prompt (3 questions): Executor (Auto domain routing | Agent all | specific CLI `codex|agy|claude|omp`) / Review (Skip | `codex|agy|claude|omp`) / Verify (Auto | `codex|agy|claude|omp` | Skip). Write `execution_config`, set `confirmed: true`.
  - **Executor Normalization Rule:** `evolve agent exec --to` accepts `codex | agy | claude | omp`. Normalize `"Auto"` (for `verification_tool`, `code_review_tool`, or `default_executor`) to the first available executable in `[codex, agy, claude]`, or fall back to joined host subagent / emit `W006` if no CLI executor is installed; OMP is selected only explicitly. Never pass `"Auto"` or `"agent"` to `--to`.

- **Step 2 — Executor Resolution:**
  - `method == "agent"` -> `"agent"` for all tasks.
  - `method == "cli"` -> `normalize_cli(execution_config.default_executor || "Auto")` (or `"agent"` + `W006` if no CLI executable available).
  - `method == "auto"` -> domain routing: frontend (UI/component/page/style, `.tsx/.jsx/.vue/.css/.html/.svelte`) | backend (API/server/db/service, `.go/.rs/.java/.py/.sql/.proto`) | general (mixed/config/tests, `.ts/.js/other`). Resolution: `execution_config.domain_routing[domain]` (if `"cli"`/`"Auto"` or `--executor` set, normalize to `codex|agy|claude` with `omp` only when explicitly configured; otherwise default `"agent"`).

- **Step 3 — Task Execution** per plan order (independent tasks may parallelize cleanly via joined subagents — see [runtime adapters](runtime-adapters.md)). **If `resolved_executor == "agent"` (Agent path):** launch joined subagent with task definition + criteria refs + prior task summaries + `wiki_constraints` (bound Wiki priors/constraints) → implement → verify convergence → auto-fix (max 3) → return (OMP hosts use native `task`/eval `agent()`; results auto-deliver, join/cancel before advancing, never poll). **If `resolved_executor ∈ {codex, agy, claude, omp}` (CLI path):**
```bash
evolve agent exec "PURPOSE: Implement task ${task_id}: ${title}; success = criteria ${criteria_refs} satisfied
TASK: ${description} | Read existing code first | Verify convergence criteria after changes
MODE: write
CONTEXT: @${scope}/**/* | Criteria: ${criteria_summary}
EXPECTED: Working code changes, convergence evidence, summary of what was done
CONSTRAINTS: Scope limited to task files | Follow bound Wiki constraints

## Acceptance Criteria (must satisfy)
${criteria_refs.map(ref => criteria[ref].criterion).join('\n')}

## Implementation Steps
${task.description}

## Project Wiki Constraints
${wiki_constraints}

## Prior Task Summaries
${prior_summaries}
" --to ${resolved_cli} --mode write
```
**Deviation Rule (MANDATORY hard limit — max 3 retries per task):** first attempt normal dispatch → retry with simplified self-contained prompt via `evolve agent exec "<simplified prompt>" --to ${resolved_cli} --mode write` (if CLI path) → switch to joined Agent path → all 3 fail → mark task `blocked`, checkpoint, continue remaining. NEVER exceed 3 attempts on a single task.

- **Step 4 — Per-Task Evidence:** `{"phase":"execution","type":"task-completed","task_id":"T1","executor":"...","files_modified":[],"summary":"","attempt":1}`; update task status.

- **Step 5 — Post-Execution Validation.** Skip if `verification_tool == "Skip"` OR `--skip-verify` OR no completed tasks. **Check 1** Summary Consistency (task status vs git diff). **Check 2** CLI Verification Gate (normalize `verification_tool` from `"Auto"` -> `resolved_verify_cli ∈ {codex, agy, claude, omp}`; if no CLI executor available, log `W006` and verify via host checks):
```bash
evolve agent exec "PURPOSE: Verify execution output meets acceptance criteria; success = all criteria verified with file:line evidence
TASK:
1. CONVERGENCE: For each criterion, read actual code, verify behavior exists, report status with evidence
2. EXISTENCE: Verify all expected files exist on disk
3. SUBSTANCE: Verify real implementation — flag stubs, placeholders, TODO-only
4. ANTI-PATTERNS: Scan for TODO/FIXME/HACK, console.log debug, disabled tests
MODE: analysis
CONTEXT: @${modified_files}
EXPECTED: JSON { convergence: [{criterion, status, evidence}], issues: [{type, file, line, severity}], overall: passed|gaps_found }
CONSTRAINTS: Read-only | Check ALL criteria exhaustively | Evidence must be file:line

## Acceptance Criteria (verify each)
${acceptance_criteria.map(c => c.criterion).join('\n')}

## Modified Files
${modified_files.join('\n')}
" --to ${resolved_verify_cli} --mode analysis
```
`overall == "passed"` → proceed to S_VERIFY with boosted confidence; `gaps_found` → log findings, proceed. **Check 3** Code Review (if `code_review_tool != "Skip"` and normalized `resolved_review_cli ∈ {codex, agy, claude, omp}` is available): `evolve agent exec "Review git diff for correctness, style, bugs, and code quality" --to ${resolved_review_cli} --mode analysis`.

- **Step 6 — Completion:** update §3. Mark G3. Commit workspace code edits if files changed: `"odyssey-planex({slug}): EXECUTE — task implementation complete"`.

**GATE: plan-executed** — all plan tasks executed (`completed`) or explicitly `blocked` after 3 retries with logged evidence; per-task execution evidence recorded; post-execution validation (convergence, existence, substance, anti-patterns) completed unless `--skip-verify` (or `W006` fallback); G3 marked.

**A_VERIFY** — Iron gate (skipped only when `--skip-verify`); verify each criterion by method:

| Method | Action |
|--------|--------|
| `test` | Run relevant tests, check pass/fail |
| `grep` | Grep for expected pattern |
| `cli-review` | Normalize target CLI (`codex\|agy\|claude`; `omp` explicit); `evolve agent exec "PURPOSE: Verify criterion {id}: {criterion}\nTASK: Read implementation \| Check behavior \| Report pass/fail with file:line\nMODE: analysis\nCONTEXT: @{relevant_files}\nEXPECTED: JSON {criterion_id, status, evidence}" --to ${resolved_cli} --mode analysis` |
| `manual` | Normal: structured prompt / `-y`: record `deferred` |

Record per criterion: `{"phase":"verification","type":"criterion-check","criterion_id":"AC1","method":"","result":"passed|failed","evidence":"","iteration":N}`. Update `acceptance_criteria[].status`. Append to `iterations[]`. Update §4 pass/fail table. **Route:**
- All passed → mark G4 → `S_GENERALIZE` / `S_RECORD`.
- Some failed + `skip_fix` → mark `PARTIAL`, proceed to `S_RECORD`.
- Some failed + `current_iteration < max_iterations` → `S_FIX`.
- Some failed + `current_iteration >= max_iterations`:
  - **Normal**: structured prompt — either (a) approve **at most one** bounded extension (`max_iterations += 2`, set `iteration_extended: true` → `S_FIX`; never allow a second extension), (b) explicitly defer remaining failed criteria with user consent (`PARTIAL` → `S_RECORD`), or (c) abort as `ESCALATED` (`S_RECORD`).
  - **`-y`**: NEVER auto-accept unmet criteria — keep failed criteria `status: "failed"`, record decision `status: "deferred"`, mark G4 `failed`, set completion status `ESCALATED`, and proceed to `S_RECORD`.

**GATE: all-criteria-passed** — every `acceptance_criteria[].status == passed` with recorded evidence and `passed_at`, verified by the declared `verify_method`; iterations logged in `iterations[]`; if max iterations exceeded, criteria may be marked `deferred` ONLY with explicit human consent in Normal mode (never auto-accepted under `-y`, which exits to `S_RECORD` as `ESCALATED`); G4 marked (`done` iff all passed, else `failed`/`skipped`).

**A_FIX** — (1) Increment `current_iteration`. (2) For each failed criterion: diagnose gap → targeted code fix (not re-implementation). (3) CLI fix review (optional, normalized `--to codex|agy|claude` (or explicit `omp`)): `evolve agent exec "Review fixes in git diff for regressions and return JSON {verdict, regression_risk, concerns}" --to ${resolved_cli} --mode analysis`. (4) Append evidence (fix), update §5, commit if workspace source files changed (`"odyssey-planex({slug}): FIX — iteration {N} gap fixes"`) → `S_VERIFY`.

---

## Generalize Source

Implementation patterns from executed tasks (API contract shapes, validation shapes, error response format, config structure).

---

## Iteration Model

```
S_EXECUTE → S_VERIFY ──all pass──→ S_GENERALIZE → S_DISCOVER → S_RECORD
                │                       │
           some fail + iter < max       3-layer scan, 0 hits ─→ S_RECORD
                ▼
             S_FIX ──→ S_VERIFY (loop)
```

Max iterations (default 3; at most 1 bounded `+2` extension in Normal mode) prevents infinite loops; `--skip-fix` skips `S_FIX` (`S_VERIFY` fails exit as `PARTIAL`), and `--skip-fix --skip-verify` runs `S_PLAN` only before `[back-half]`.

---

## Knowledge Persistence (§8)

Include verified reusable findings in the Run summary and evidence; follow the shared A_RECORD and post-Session candidate governance in `odyssey-base.md`. Do not initiate harvest at Run seal.

| Category | Content | Follow-up |
|----------|---------|-----------|
| Multi-round fix cycle pattern | Problem scenario + fix iteration + final approach | Post-Session candidate review (`category: debug`) |
| Reusable implementation pattern | Pattern + applicable scope + code template | Post-Session candidate review (`category: coding`) |
| Acceptance criteria template | Standard template + verify_method suggestion | Post-Session candidate review (`category: review`) |
| Generalization pattern | Signature + risk + fix template | Post-Session candidate review (`category: coding`) |

---

## Completion Summary

```
--- PLANEX ODYSSEY COMPLETE ---
Requirement: {requirement}
Criteria:    {passed}/{total} passed
Iterations:  {N} cycles
Patterns:    {patterns_extracted} ({by_layer} distribution)
Scan hits:   {total_hits} ({cross_layer_confirmed} cross-layer confirmed)
Issues:      {N} created | Decisions: {N} resolved, {M} pending, {K} deferred
Learnings:   {N} wiki candidates
Self-iter:   {N} rounds across {M} stages
Goals:       {done}/{total} confirmed ({skipped} skipped)
Status:      {ALL_PASSED|PARTIAL|ESCALATED}
---
```

---

## Mode `-y` Points

| Decision Point | Normal | `-y` |
|---------------|--------|------|
| S_INTAKE criteria | structured prompt | auto-derive `deferred` |
| S_EXECUTE options | structured prompt | defaults (auto/Skip/Auto normalized to `codex\|agy\|claude` or agent (`omp` explicit)) `confirmed:true` |
| S_EXECUTE task blocked | structured prompt | auto continue, log blocked |
| S_VERIFY manual criterion | structured prompt | `deferred` |
| S_VERIFY max iteration | structured prompt (1 bounded +2 ext / consent defer / ESCALATED) | terminate as `ESCALATED` (`S_RECORD`), keep failed criteria `failed` + decision `deferred` (never auto-accept) |
| A_DISCOVER routing | structured prompt | auto-route to execute, issue for rest |

---

## Phase Gates

- **Audit gate** (PLAN+EXECUTE): plan tasks mapped to criteria; all tasks executed or blocked after 3 retries (`plan-executed`).
- **FIX gate:** every failing acceptance criterion receives a targeted fix scoped to the diagnosed failure without dropping/weakening criteria (`evidence phase=fix`), bounded by `--max-iterations` (and max 1 Normal-mode `+2` extension; skipped under `--skip-fix`).
- **VERIFY gate:** every criterion verified by its declared `verify_method`; `all-criteria-passed` confirmed or max-iteration escalation handled (`PARTIAL` on explicit Normal consent / `--skip-fix`; `ESCALATED` under `-y`; `needs_rework` routes back to FIX while `current_iteration < max_iterations`).

---

## Error Codes

Shared `E000–E004` and `W008` errors are defined in [SKILL.md](../SKILL.md) (including `E001` missing requirement).

| Code | Severity | Condition | Recovery |
|------|----------|-----------|----------|
| W006 | warning | No CLI delegate executable (`codex\|agy\|claude\|omp`) available | Fall back to joined Agent path or skip optional CLI verification |
| W007 | warning | CLI review regression concern | Review before next iteration |

---

## Success Criteria

- [ ] Requirement parsed; session + mode-contracted output files (`prepare/odyssey-planex.md`) created; acceptance criteria defined
- [ ] Plan decomposed into tasks mapped to acceptance criteria
- [ ] All tasks executed (or blocked after 3 retries, unless `--skip-fix --skip-verify`); execution evidence logged
- [ ] understanding.md sections written progressively (§1–§8)
- [ ] Criteria verified by method (test/grep/cli-review/manual, unless `--skip-verify`); bounded fix loop (unless `--skip-fix`)
- [ ] Multi-layer generalization + discovery triage (unless --skip-generalize)
- [ ] phase_goals derived, tracked, and audited in `outputs/session.json` (honor session `policy.goal_mode` when opened with `--goal-mode`)
- [ ] Session resumable via -c; completion summary emitted
- [ ] Status: ALL_PASSED / PARTIAL / ESCALATED

---

See [shared base](odyssey-base.md) for generic follow-up routing (`Next Step Routing`). Related transitions: `/ew-odyssey <finding> --mode debug` (root cause), `/ew-odyssey <changed-files> --mode review` (formal review), `/ew-odyssey <component> --mode ui` (UI fixes).
