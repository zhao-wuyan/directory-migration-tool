# Workflow: Odyssey Improve

6-dimension runtime quality improvement — performance/security/architecture/reliability/observability/maintainability audit → diagnose → fix → generalize. Follow [shared phases](odyssey-base.md) and [runtime adapters](runtime-adapters.md).

---

## State Chain

```
S_INTAKE → S_SURVEY → S_AUDIT → S_DIAGNOSE → S_FIX → S_VERIFY → [back-half]
```

Back-half: `S_GENERALIZE → S_DISCOVER → S_RECORD → END` (see [shared phases](odyssey-base.md) §Shared Back-Half).

---

## Boundary

**In scope:** Runtime quality improvement — performance/security/architecture/reliability/observability/maintainability audit → diagnose → fix → generalize. **Out of scope:** UI visual → `--mode ui` | New features → `--mode planex` | Single bug → `--mode debug` | Style review → `--mode review`. Zero-residual applies.

---

## Context

### Target Resolution

Target resolution, dimension defaults, and `--fix-threshold` cutoff are defined in [the selected mode preparation](prepare/odyssey-improve.md) §Input Interpretation — that is the canonical wording.

### Dimensions (6)

1. **performance** — hot paths, N+1, memory allocation, cache efficiency, bundle size, lazy loading
2. **security** — OWASP Top 10, injection, auth bypass, data exposure, dependency vulns, secrets
3. **architecture** — layer violations, circular deps, coupling, interface contracts, SRP
4. **reliability** — error handling gaps, retry, timeout, graceful degradation, resource cleanup
5. **observability** — logging coverage, metric gaps, trace propagation, error reporting, health checks
6. **maintainability** — cyclomatic complexity, dead code, test coverage gaps, doc debt

### Session Fields

```json
{ "target": "", "dimensions": [], "diagnosis_retries": 0, "baseline_metrics": {},
  "audit_result": {}, "diagnoses": [], "confirmation": null,
  "generalization_stats": null }
```

### evidence.ndjson Phases

`survey|audit|diagnosis|fix|discovery|decision|issue|self-iteration`

- `survey`: `category` (dependency|complexity|coverage|error_pattern), `detail`
- `audit`: `dimension`, `severity`, `measurement`
- `diagnosis`: `finding_ref`, `hypothesis`, `result`, `root_cause`
- `fix`: `finding_ref`, `change_summary`, `risk`

### phase_goals[]

| ID | Goal | Phase | skip_when |
|----|------|-------|-----------|
| G1 | Survey completed | S_SURVEY | — |
| G2 | Audit completed | S_AUDIT | — |
| G3 | Diagnosis completed | S_DIAGNOSE | — |
| G4 | Zero remaining: all actionable findings (`>= fix_threshold`) fixed and verified | S_VERIFY | skip_fix |
| G5 | Pattern generalized | S_GENERALIZE | skip_generalize |
| G6 | Discoveries triaged | S_DISCOVER | skip_generalize |
| G7 | Learnings persisted | S_RECORD | — |

### understanding.md — 9 Sections

1. Target & Baseline
2. Current State Survey
3. Audit Findings
4. Root Cause Diagnosis
5. Fix & Verification
6. Generalization
7. Discoveries
8. Improvement Metrics (before/after)
9. Engineering Learnings

---

## State Machine

### Transitions

```
S_SURVEY   → S_AUDIT       : complete
S_AUDIT    → S_DIAGNOSE    : actionable findings (>= fix_threshold) exist
S_AUDIT    → S_GENERALIZE  : no actionable findings (>= fix_threshold), !skip_generalize
S_AUDIT    → S_RECORD      : no actionable findings (>= fix_threshold), skip_generalize
S_DIAGNOSE → S_FIX         : root causes identified, !skip_fix
S_DIAGNOSE → S_GENERALIZE  : root causes identified, skip_fix, !skip_generalize
S_DIAGNOSE → S_RECORD      : root causes identified, skip_fix, skip_generalize
S_DIAGNOSE → S_DIAGNOSE    : all round hypotheses failed, diagnosis_retries < 3 → A_ESCALATE_DIAGNOSIS
S_DIAGNOSE → S_RECORD      : diagnosis_retries >= 3 (Normal: user accepts escalation / INCONCLUSIVE; -y: INCONCLUSIVE)
S_FIX      → S_VERIFY      : fix implemented
S_VERIFY   → S_GENERALIZE  : verified, !skip_generalize
S_VERIFY   → S_RECORD      : verified, skip_generalize
S_VERIFY   → S_FIX         : needs_rework, fix_rounds < max_fix_rounds (`fix_rounds++`)
S_VERIFY   → S_RECORD      : needs_rework, fix_rounds >= max_fix_rounds (escalate per [shared base](odyssey-base.md) `<fix_loop_cap>`)
```

### Actions

**A_INTAKE extra** — Baseline capture: record current metrics (test pass rate, bundle size, dependency count, complexity hotspots) to `session.json.baseline_metrics`.

**A_SURVEY** — (1) Dependency audit (package.json/lock), complexity scan (size/nesting), test coverage map, error handling scan (empty catch, unhandled promise). (2) CLI-assisted (optional): `evolve agent exec "Survey dependency health, complexity hotspots, coverage gaps, and error patterns for {target}" --to codex --mode analysis`. (3) Evidence phase=survey. Update §2. Mark G1.

**A_AUDIT** — Run 6 parallel subagents (one per dimension, or `--dimensions` subset; join all before advancing — see [runtime adapters](runtime-adapters.md)). Each returns `[{title, severity, dimension, file, line, description, suggestion, measurement}]`. Merge → evidence phase=audit. Write `session.json.audit_result`. Update §3 (findings by dimension + severity matrix). Mark G2.

**GATE: all-dimensions-audited** — all 6 dimensions (or `--dimensions` subset) completed with structured findings, merged into severity matrix, evidence phase=audit logged per dimension. Zero dimensions reviewed is BLOCKED (W002 partial from agent failure is a warning).

**A_DIAGNOSE** — Root cause analysis for actionable findings (`>= fix_threshold`, prioritizing critical → high → medium → low) — don't fix symptoms. (1) Group by dimension, prioritize by severity; for each: hypothesis → trace code path + git history → evidence phase=diagnosis. (2) Ambiguity → evidence phase=decision; Normal: structured prompt | `-y`: defer. (3) CLI-assisted for complex findings via `evolve agent exec "<prompt>" --to codex --mode analysis`. (4) Write `session.json.diagnoses[]`. Update §4. Mark G3.

**A_ESCALATE_DIAGNOSIS** — `diagnosis_retries++`. `< 3`: `evolve agent exec "Analyze failed hypotheses for {finding} and propose new root-cause hypotheses with file:line evidence" --to codex --mode analysis`, new hypotheses, → S_DIAGNOSE. `>= 3`: Normal → structured prompt (1 bounded user-clue extension or `INCONCLUSIVE → S_RECORD`) | `-y` → `INCONCLUSIVE → S_RECORD`.

**A_FIX** — (1) Exhaustive fix: ALL diagnosed issues by severity tier (critical → high → medium → low within `fix_threshold`), one dimension at a time. After each tier, re-verify **current tier's dimension only** (not all dimensions); new findings at same or higher severity append to current tier. Cross-dimension regression checks run once at S_VERIFY after all tiers. **No partial-tier advancement** — each tier fully addressed (fixed or individually classified) before advancing; blanket "pre-existing" skip forbidden. (2) For each discrete fix: implement → **run tests covering the modified area immediately** → log `evidence phase=fix` → **commit per verified fix** (`"odyssey-improve({slug}): FIX — {finding_ref} ({dimension}/{tier})"`). **Incremental verification** — each discrete fix step verified before proceeding; NEVER batch unrelated changes. **Rollback safety** — if tests fail after a fix step, revert that uncommitted change (`git checkout -- {file}` / `git diff` patch revert — safe because prior verified fixes are already committed) before attempting alternatives; NEVER proceed with failing tests. **Behavioral equivalence** — fixes MUST preserve existing behavior; no API changes, no new functionality. Beneficial changes discovered during fixing → evidence phase=decision as recommendations, NOT applied. **Scope lock** — once tier plan confirmed, no scope expansion without re-confirmation. (3) Normal: structured prompt per-fix | `-y`: auto-proceed, record `deferred`.

**A_VERIFY** — (1) Run tests covering modified areas. (2) Re-capture metrics, compare with `baseline_metrics`. (3) CLI-assisted: `evolve agent exec "Review fixes for {target} against quality dimensions and regressions" --to codex --mode analysis`. (4) `verified` → mark G4; `needs_rework` with `fix_rounds < max_fix_rounds` → `fix_rounds++` → `S_FIX`; `needs_rework` with `fix_rounds >= max_fix_rounds` → escalate per [shared base](odyssey-base.md) `<fix_loop_cap>` → `S_RECORD`. (5) Write `confirmation`. Update §5 (fix & verification results) and §8 (before/after metrics table).

**GATE: zero-remaining-verified** — every diagnosed finding (`>= fix_threshold`) is fixed and verified, individually classified (`issue` / `decision`), or skipped via `--skip-fix`; tests pass over modified areas; metrics re-captured and compared against `baseline_metrics`; no unaddressed actionable findings; before/after table written to understanding.md §8.

---

## Generalize Source

Diagnosed root causes + applied fixes across all dimensions. **Discover routing:** `bug` → S_FIX; new critical → S_DIAGNOSE (`cross_phase_loops < max_loops`, re-entry per [shared base](odyssey-base.md) `A_DISCOVER`).

---

## Knowledge Persistence (§9)

Include verified reusable findings in the Run summary and evidence; follow the shared A_RECORD and post-Session candidate governance in `odyssey-base.md`. Do not initiate harvest at Run seal.

A_RECORD extra: §8 improvement metrics — re-capture and build before/after comparison table from `baseline_metrics` vs current.

| Category | Content | Follow-up |
|----------|---------|-----------|
| Performance pattern | Bottleneck type + fix approach + measurement | Post-Session candidate review (`category: coding`) |
| Security rule | Vulnerability class + fix + prevention | Post-Session candidate review (`category: debug`) |
| Architecture constraint | Violation + correct boundary + check | Post-Session candidate review (`category: arch`) |
| Reliability pattern | Failure mode + handling strategy + verification | Post-Session candidate review (`category: coding`) |

---

## Completion Summary

```
--- IMPROVE ODYSSEY COMPLETE ---
Target:      {target}
Dimensions:  {dimensions}
Findings:    {critical}C / {high}H / {medium}M / {low}L
Diagnosed:   {count}
Fixed:       {count} ({verified} verified)
Metrics:     {improved} improved / {regressed} regressed
Patterns:    {count} ({by_layer})
Scan hits:   {total} ({cross_layer_confirmed} confirmed)
Issues:      {N} created
Decisions:   {N} resolved, {M} pending, {K} deferred
Learnings:   {N} persisted
Self-iter:   {N} rounds across {M} stages
Cross-loops: {N}
Goals:       {done}/{total} ({skipped} skipped)
---
```

---

## Mode `-y` Points

| Decision Point | Normal | `-y` |
|---------------|--------|------|
| A_FIX confirmation | structured prompt | auto-proceed `deferred` |
| A_DIAGNOSE ambiguity | structured prompt | auto-proceed with default, record `deferred` |
| A_ESCALATE 3-strike | structured prompt | auto INCONCLUSIVE |
| A_DISCOVER routing | structured prompt | auto create issue |
| Ambiguous items | structured prompt | all `deferred` |

---

## Phase Gates

- **Discovery gate** (SURVEY): evidence for the phase logged, understanding.md updated. Survey requires all scan types attempted.
- **Audit gate** (DIAGNOSE): all dimension agents completed, findings merged with severity classification. Zero dimensions reviewed is BLOCKED (W002 partial allowed).
- **FIX gate:** current severity tier fully addressed. Per-fix evidence phase=fix logged. **Per-change test verification** — each fix step runs tests before proceeding. **Rollback on failure** — failed uncommitted fix reverted immediately. **Behavioral equivalence** — no API/behavior changes; beneficial changes logged as recommendations only. Auto-commit per verified fix. No partial-tier advancement.
- **VERIFY gate:** tests pass; metrics re-captured; confirmation written, understanding.md updated (§5 + §8), verify goal marked. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to `S_RECORD`).

---

## Error Codes

Shared `E000–E004` and `W008` errors are defined in [SKILL.md](../SKILL.md).

| Code | Severity | Condition | Recovery |
|------|----------|-----------|----------|
| W001 | warning | No dependency manifest / no test coverage tool | Proceed with defaults |
| W002 | warning | Some dimension agents failed | Partial coverage accepted |
| W004 | warning | Generalization 0 hits after full 3-layer scan | Advance to S_RECORD |
| W005 | warning | Pending decisions | Filter evidence phase=decision |

---

## Success Criteria

- [ ] Target resolved; session + mode-contracted output files (`prepare/odyssey-improve.md`) created; baseline metrics captured
- [ ] Survey completed with all scan types attempted (dependency, complexity, coverage, error handling)
- [ ] 6-dimension audit completed with structured findings + severity matrix
- [ ] understanding.md sections written progressively (§1–§9)
- [ ] Fix + verify (unless --skip-fix) bounded by `max_fix_rounds`; zero-residual — every finding has concrete action
- [ ] Multi-layer generalization + discovery triage (unless --skip-generalize)
- [ ] phase_goals derived, tracked, and audited in `outputs/session.json` (honor session `policy.goal_mode` when opened with `--goal-mode`)
- [ ] §8 improvement metrics: before/after comparison table from baseline_metrics
- [ ] Session resumable via -c; completion summary emitted

---

See [shared base](odyssey-base.md) for generic follow-up routing (`Next Step Routing`). Related transitions: `/ew-odyssey <finding> --mode debug` (root cause), `/ew-odyssey <changed-files> --mode review` (formal review), `/ew-odyssey <component> --mode ui` (UI fixes).
