# Workflow: Odyssey UI

6-dimension visual experience audit — survey design context → audit dimensions → divergent exploration → fix → verify → generalize. Follow [shared phases](odyssey-base.md) and [runtime adapters](runtime-adapters.md).

---

## State Chain

```
S_INTAKE → S_SURVEY → S_AUDIT → S_DIVERGE → S_FIX → S_VERIFY → [back-half]
```

Back-half: `S_GENERALIZE → S_DISCOVER → S_RECORD → END` (see [shared phases](odyssey-base.md) §Shared Back-Half).

---

## Boundary

**In scope:** Multi-round iterative UI experience campaign — survey design context (`ew-impeccable` context + `PRODUCT.md`/`DESIGN.md`) → 6-dimension audit → divergent exploration → fix → verify → generalize across sibling components/pages.
**Out of scope:** Single-pass UI design, redesign, critique, audit, or polish (or greenfield UI creation) → `ew-impeccable` (`evolve skill workflows show ew-impeccable`, interactive `/ew-impeccable <verb>`) | Backend/data/API → `--mode planex` | Deep bug investigation → `--mode debug` | Non-UI code quality review → `--mode review`.

---

## Decision Gate

**Mode override of [shared base](odyssey-base.md) §Execution Discipline #2 (`Confident edits vs decision gate`):** in UI mode, replace the generic decision-gate triggers with these UI-specific triggers — ONLY these qualify as decisions: brand/style direction requiring human creative judgment | layout restructuring that significantly changes user flow | requires new design tokens or breaking component API. ("Unsure how", "too big", "pre-existing" remain invalid triggers.)

---

## Context

### Target Resolution

Target resolution and dimension selection are defined in [the selected mode preparation](prepare/odyssey-ui.md) §Input Interpretation — that is the canonical wording.

### Dimensions (6)

| Dimension | Focus |
|-----------|-------|
| visual_hierarchy | Spacing, typography scale, color contrast, alignment, whitespace, visual weight |
| interaction_states | Hover, focus, active, disabled, loading, error, empty, selected states |
| accessibility | WCAG AA contrast, focus management, aria labels, keyboard nav, screen reader |
| responsiveness | Breakpoints, overflow, touch targets, fluid typography, container queries |
| micro_interactions | Transitions, animations, feedback indicators, loading states, progress |
| edge_cases | Long text truncation, empty data, error states, extreme values, i18n, RTL |

### Session Fields

```json
{ "target": "", "dimensions": [],
  "audit_result": { "dimensions_audited": 0, "finding_count": 0, "severity_distribution": {} },
  "diverge_result": { "improvements_proposed": 0, "creative_ideas": 0 },
  "patterns": [], "confirmation": null, "generalization_stats": null }
```

### evidence.ndjson Phases

`survey|audit|diverge|fix|discovery|decision|issue|self-iteration`

### phase_goals[]

| ID | Goal | Phase | skip_when |
|----|------|-------|-----------|
| G1 | Survey completed | S_SURVEY | — |
| G2 | Audit completed | S_AUDIT | — |
| G3 | Divergent exploration done | S_DIVERGE | — |
| G4 | Zero remaining: all actionable findings (`>= fix_threshold`) and `high-impact` ideas fixed and verified (remaining ideas triaged) | S_VERIFY | skip_fix |
| G5 | Pattern generalized | S_GENERALIZE | skip_generalize |
| G6 | Discoveries triaged | S_DISCOVER | skip_generalize |
| G7 | Learnings persisted | S_RECORD | — |

### understanding.md — 8 Sections

1. Target & Design Context
2. Survey
3. Audit
4. Diverge
5. Verify
6. Generalize
7. Discover
8. Learnings

---

## State Machine

### Transitions

```
S_SURVEY  → S_AUDIT       : complete
S_AUDIT   → S_DIVERGE     : complete
S_DIVERGE → S_FIX         : !skip_fix AND actionable findings/ideas
S_DIVERGE → S_GENERALIZE  : (skip_fix OR no actionable) AND !skip_generalize
S_DIVERGE → S_RECORD      : (skip_fix OR no actionable) AND skip_generalize
S_FIX     → S_VERIFY      : fix implemented
S_VERIFY  → S_GENERALIZE  : verified, !skip_generalize
S_VERIFY  → S_RECORD      : verified, skip_generalize
S_VERIFY  → S_FIX         : needs_rework, fix_rounds < max_fix_rounds (`fix_rounds++`)
S_VERIFY  → S_RECORD      : needs_rework, fix_rounds >= max_fix_rounds (escalate per [shared base](odyssey-base.md) `<fix_loop_cap>`)
```

Discover routes: new component to audit → S_AUDIT; fixable sibling → S_FIX (`cross_phase_loops < max_loops`; after S_VERIFY, re-run S_GENERALIZE only on new fixes per [shared base](odyssey-base.md) `A_DISCOVER`).

### Actions

**A_SURVEY** — (1) **Load Impeccable design context first**: inspect `evolve skill workflows show ew-impeccable`, run `impeccable context` once (`node <impeccable-base>/scripts/impeccable.mjs context --target {target}` when available; if launcher is unavailable, read existing `PRODUCT.md` and `DESIGN.md` directly without inventing missing context) to identify the committed visual world, surface mode, and design tokens. (2) Design system inventory: scan for design tokens, CSS variables, theme imports. (3) Current state analysis + rendered check: inspect styling patterns, layout strategy, component hierarchy, and capture/view rendered output via host browser/preview if runnable (otherwise log `W001` and fall back to committed visual-regression fixtures + static layout/contrast checks). (4) CLI-assisted: `evolve agent exec "Survey design tokens, spacing, typography, hierarchy, and visual consistency for {target}" --to codex --mode analysis`. (5) Evidence phase=survey. Update §2. Mark G1.

**A_AUDIT** — (1) Capture/inspect rendered UI screenshots at desktop & mobile breakpoints (batched per look) when a browser/preview runtime is available (or log `W001` fallback to committed screenshot goldens + static DOM/CSS checks; never fabricate browser verification). (2) Run 6 parallel subagents (one per dimension, or `--dimensions` subset; join all before advancing — see [runtime adapters](runtime-adapters.md)), grounding visual/interaction findings in rendered evidence when available (plus `impeccable detect` if launcher is available). Each returns `[{title, severity, file, line, description, suggestion, dimension}]`. (3) Merge → evidence phase=audit. Write `audit_result`. Update §3 severity matrix. Mark G2.

**GATE: all-dimensions-audited** — all 6 dimensions (or `--dimensions` subset) completed with structured findings grounded in rendered browser inspection (or `W001` static/fixture fallback), merged into severity matrix, evidence phase=audit logged per dimension. Zero dimensions reviewed is BLOCKED (W002 partial from agent failure is a warning).

**A_DIVERGE** — Goes beyond defect fixing — "what would make this delightful?"

**Step 1 — 2 parallel subagents** *(joined before consolidating)*:
- **Polish Agent**: shadows, borders, transitions, hover, feedback, empty states, skeleton loading, scroll behavior.
- **Delight Agent**: motion design, progressive disclosure, smart defaults, contextual hints, celebratory feedback, personality in copy.

Each returns `[{idea, category (polish|delight), impact, effort, description, inspiration}]`.

**Step 2 — CLI-assisted:** `evolve agent exec "Explore polish opportunities, micro-interactions, visual rhythm, and delight moments for {target}" --to codex --mode analysis`.

**Step 3 — Consolidate:** merge audit findings + divergent ideas → prioritized list (severity × impact × effort). Evidence phase=diverge. Update §4. Mark G3.

**GATE: diverge-explored** — both Polish and Delight agents completed, returning distinct `[{idea, category, impact, effort, description, inspiration}]` entries; CLI-assisted analysis completed; all outputs consolidated with audit findings into a prioritized list (severity × impact × effort); evidence phase=diverge logged; understanding.md §4 written.

**A_FIX** — Skip if `--skip-fix`. (1) **Mandatory pre-edit guardrail (`IM-01`)**: immediately before ANY UI edit (including small refinements), read `skills/ew-impeccable/reference/craft-floor.md` (via `evolve skill workflows show ew-impeccable` setup/paths) and verify edits honor `PRODUCT.md`, `DESIGN.md`, and the committed visual world. (2) Exhaustive fix: ALL actionable audit findings within `fix_threshold` (default `all`: critical → high → medium → low) PLUS `high-impact` diverge ideas (`critical → high → medium → low + high-impact ideas`). Medium/low-impact diverge ideas not implemented are explicitly triaged as `issue` (`evidence.ndjson phase=issue`) or `skipped` with per-item reasons. After each priority tier, re-review — new findings append. (3) Each verified fix → evidence phase=fix, commit per verified fix/action (`"odyssey-ui({slug}): FIX — {finding_or_idea_ref}"`). (4) Normal: structured prompt per-fix | `-y`: auto-proceed, record `deferred`.

**A_VERIFY** — (1) Run tests (lint, unit, visual regression) and verify rendered UI across desktop & mobile breakpoints in browser/preview runtime (batched checks per look, capturing **Before vs After** state comparison across the `6` dimensions and `ew-impeccable` world; if browser/runtime unavailable, log `W001` and mark `unverified_visual` explicitly in `understanding.md §5` and optional `--visual` `visual-report.html` per `evolve skill workflows show visual-report` without fabricating live renders). (2) `evolve agent exec "Review UI fixes for {target} across visual correctness, interaction states, accessibility, and responsiveness" --to codex --mode analysis`. (3) Update §5, write `confirmation`: `verified` → mark G4; `needs_rework` with `fix_rounds < max_fix_rounds` → `fix_rounds++` → S_FIX; `needs_rework` with `fix_rounds >= max_fix_rounds` → escalate per [shared base](odyssey-base.md) `<fix_loop_cap>` (Normal: structured prompt for bounded +2 extension once or record remaining as `deferred`/`issue` → S_RECORD; `-y`: record remaining as `deferred`/`issue` as `ESCALATED` → S_RECORD).

**GATE: zero-remaining-verified** — every actionable finding (`>= fix_threshold`) and `high-impact` diverge idea is fixed and verified, individually classified (`issue` / `decision`), or skipped via `--skip-fix`; non-implemented medium/low ideas are triaged with per-item reasons; tests pass (lint, unit, visual regression) with rendered browser verification (or `W001` fallback); confirmation written; no unaddressed actionable findings; understanding.md §5 updated. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to S_RECORD).

---

## Generalize Source

Audit findings + diverge ideas (severity >= medium OR impact = high).

**Discover routing:** new component → S_AUDIT; fixable sibling → S_FIX (`cross_phase_loops < max_loops`, re-entry per [shared base](odyssey-base.md) `A_DISCOVER`).

---

## Knowledge Persistence (§8)

Include verified reusable findings in the Run summary and evidence; follow the shared A_RECORD and post-Session candidate governance in `odyssey-base.md`. Do not initiate harvest at Run seal.

| Category | Content | Follow-up |
|----------|---------|-----------|
| Design pattern | Component pattern + applicable scenarios + token references | Post-Session candidate review (`category: ui`) |
| Interaction spec | State definitions + transition rules + feedback patterns | Post-Session candidate review (`category: ui`) |
| Accessibility rule | WCAG requirement + implementation approach | Post-Session candidate review (`category: ui`) |
| Reusable generalization pattern | Pattern signature + application scope | Post-Session candidate review (`category: coding`) |

---

## Completion Summary

```
--- UI ODYSSEY COMPLETE ---
Target:     {target} | Dimensions: {dimensions_audited}
Findings:   {C}C {H}H {M}M {L}L | Diverge: {improvements} polish + {creative} delight
Fix:        {fixed_count} applied, verified={yes|skipped}
Patterns:   {extracted} ({by_layer})
Scan hits:  {total} ({cross_layer} cross-layer)
Issues:     {N} created
Decisions:  {N} resolved, {M} pending, {K} deferred
Learnings:  {N} persisted
Self-iter:  {N} rounds
Goals:      {done}/{total} ({skipped} skipped)
---
```

---

## Mode `-y` Points

| Decision Point | Normal | `-y` |
|---------------|--------|------|
| A_FIX confirmation | structured prompt | auto-proceed `deferred` |
| A_DISCOVER routing | structured prompt | auto-fix w/ template, issue for rest |

---

## Phase Gates

- **Discovery gate** (SURVEY): `ew-impeccable` context (`impeccable context` / `PRODUCT.md` + `DESIGN.md`) loaded; evidence logged, understanding.md updated. Survey requires all scan types attempted.
- **Audit gate** (AUDIT+DIVERGE): all selected dimension agents completed with rendered browser evidence (or `W001` fallback), findings merged. Diverge agents completed, ideas consolidated. Zero dimensions reviewed is BLOCKED (W002 partial allowed).
- **FIX gate:** `skills/ew-impeccable/reference/craft-floor.md` read before UI edits; current priority tier (`>= fix_threshold` + `high-impact` ideas) fully addressed; medium/low ideas triaged. Per-fix evidence logged. Commit per verified fix/action.
- **VERIFY gate:** tests pass with rendered browser check (or `W001` fallback); confirmation written, understanding.md updated, verify goal marked. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to S_RECORD).

---

## Error Codes

Shared `E000–E004` and `W008` errors are defined in [SKILL.md](../SKILL.md).

| Code | Severity | Condition | Recovery |
|------|----------|-----------|----------|
| W001 | warning | Browser/preview runtime or `impeccable` launcher unavailable | Fall back to `PRODUCT.md`/`DESIGN.md`, committed visual-regression fixtures, and static DOM/CSS checks; record evidence limit |
| W002 | warning | Some dimension agents failed | Partial coverage accepted |
| W004 | warning | Generalization 0 hits after full 3-layer scan | Advance to S_RECORD |
| W005 | warning | Pending decisions | Filter evidence phase=decision |

---

## Success Criteria

- [ ] Target resolved; `ew-impeccable` context loaded (`impeccable context` / `PRODUCT.md` + `DESIGN.md`); session + mode-contracted output files (`prepare/odyssey-ui.md`) created
- [ ] Survey completed with design system inventory + current state analysis
- [ ] 6-dimension audit completed with rendered browser check (or `W001` fallback), structured findings + severity matrix
- [ ] Divergent exploration: polish + delight ideas consolidated with audit findings
- [ ] understanding.md 8 sections written progressively (§1–§8)
- [ ] `craft-floor.md` loaded before UI edits; fix + verify (unless --skip-fix) bounded by `max_fix_rounds`; actionable findings & high-impact ideas addressed, remaining ideas triaged
- [ ] Multi-layer generalization + discovery triage (unless --skip-generalize)
- [ ] phase_goals derived, tracked, and audited in `outputs/session.json` (honor session `policy.goal_mode` when opened with `--goal-mode`)
- [ ] Session resumable via -c; completion summary emitted

---

See [shared base](odyssey-base.md) for generic follow-up routing (`Next Step Routing`). Related transitions: `/ew-odyssey <target> --mode planex` (backend/data), `/ew-odyssey <target> --mode debug` (deep bug), `/ew-odyssey <changed-files> --mode review` (code review).
