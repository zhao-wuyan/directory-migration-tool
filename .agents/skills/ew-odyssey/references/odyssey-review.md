# Workflow: Odyssey Review

Follow [shared phases](odyssey-base.md) and [runtime adapters](runtime-adapters.md) for parallel agents, structured prompts, and metadata.

## State Chain

```
S_INTAKE → S_ARCHAEOLOGY → S_EXPLORE → S_REVIEW → S_FIX → S_CONFIRM → [back-half]
```

Back-half: `S_GENERALIZE → S_DISCOVER → S_RECORD → END` (see [shared phases](odyssey-base.md)).

---

## Boundary

**In scope:** Multi-iteration multi-dimensional deep review of target code → exhaustive fix ALL findings within `fix_threshold` (default `all`) by severity → generalize patterns project-wide.
**Out of scope:** Single-pass 4-dimension review (findings-only or inside an Evolve Session chain after `ew-execute`) → `ew-orchestrate` (`ew-review`) | Root cause debug → `--mode debug` | Feature implementation → `--mode planex` | UI visual optimization → `--mode ui`. Zero-residual applies (fix ALL findings within `fix_threshold`, default `all`).

---

## Target Resolution

Target resolution (`file|dir|HEAD|staged|session|run|phase#|PR#`) and `--dimensions`/`--fix-threshold` options are defined in [the selected mode preparation](prepare/odyssey-review.md) §Input Interpretation — that is the canonical wording.

---

## Session Fields

```json
{ "target": "", "dimensions": [], "fix_rounds": 0, "max_fix_rounds": 5,
  "review_result": {"remaining_actionable": 0},
  "patterns": [], "confirmation": null, "generalization_stats": null }
```

---

## evidence.ndjson Phases

`archaeology|explore|review|fix|discovery|decision|issue|self-iteration`

---

## phase_goals[]

| ID | Goal | done_when | phase | skip_when |
|----|------|-----------|-------|-----------|
| G1 | Review completed | all selected dimensions reviewed | S_REVIEW | — |
| G2 | Explore context | explore.json populated (or W006 gap logged) | S_EXPLORE | — |
| G3 | Zero remaining | `remaining_actionable == 0` | S_CONFIRM | skip_fix |
| G4 | Pattern generalized | patterns[] ≥1 | S_GENERALIZE | skip_generalize |
| G5 | Discoveries triaged | all hits classified | S_DISCOVER | skip_generalize |
| G6 | Learnings persisted | reusable Wiki candidates staged/proposed (or none actionable) | S_RECORD | — |

---

## understanding.md — 8 Sections

1. Target & Scope
2. Archaeology
3. Exploration
4. Review Results
5. Fix & Confirmation
6. Generalization
7. Discoveries
8. Learnings

Wiki constraints: `evolve kb list --kind constraint --category review`.

---

## State Machine — Transitions

```
S_ARCHAEOLOGY → S_EXPLORE  : complete
S_EXPLORE     → S_REVIEW   : complete
S_REVIEW  → S_FIX          : !skip_fix AND findings
S_REVIEW  → S_GENERALIZE   : skip_fix OR no findings, !skip_generalize
S_REVIEW  → S_RECORD       : both skip
S_FIX     → S_CONFIRM      : tier complete
S_CONFIRM → S_GENERALIZE   : confirmed, !skip_generalize
S_CONFIRM → S_RECORD       : confirmed, skip_generalize
S_CONFIRM → S_FIX          : needs_rework, fix_rounds < max_fix_rounds (`fix_rounds++`)
S_CONFIRM → S_RECORD       : needs_rework, fix_rounds >= max_fix_rounds (escalate per [shared base](odyssey-base.md) `<fix_loop_cap>`)
```

Discover routes: fixable sibling → S_FIX; new target needing review → S_REVIEW (`cross_phase_loops < max_loops`; after S_CONFIRM, re-run S_GENERALIZE only on newly added fixes per [shared base](odyssey-base.md) `A_DISCOVER`).

---

## Actions

### A_ARCHAEOLOGY & A_EXPLORE

Follow [shared base](odyssey-base.md) §Shared Discovery Phases with `target_or_issue = {target}` (archaeology subagents + `explore.json` + `understanding.md` §2–§3 + `G2` + `W003`/`W006` probe).

### A_REVIEW

Run N parallel subagents (one per selected dimension — default all 4 below, or `--dimensions` subset; join all before advancing — see [runtime adapters](runtime-adapters.md)):
- **Correctness**: logic errors, boundary conditions, null/undefined, race conditions
- **Security**: injection, XSS, CSRF, data exposure, auth bypass
- **Performance**: hot paths, N+1, memory leaks, unnecessary recomputation
- **Architecture**: layer violations, circular deps, interface contracts, SoC

Each returns `[{title, severity, file, line, description, suggestion, cwe}]`. Merge → evidence phase=review. Write `review_result` + §4 severity matrix. Mark G1.

**GATE: all-dimensions-reviewed** — all selected dimension agents (default all 4: correctness, security, performance, architecture; or `--dimensions` subset) completed, findings merged into `review_result` with severity classification, evidence phase=review logged, §4 severity matrix written, G1 marked. Zero dimensions reviewed is BLOCKED (W002 partial with ≥1 dimension allowed).

### A_FIX

Exhaustive iterative fix — descend by severity until `remaining_actionable == 0`:
```
for tier in [critical, high, medium, low].filter(>= threshold):
  for each unfixed candidate: read ±20 lines → fix → evidence phase=fix
  re-review modified area (new findings → append, continue; max 2 per tier)
  tier done → auto-commit
```
Normal: structured prompt per tier | `-y`: auto-proceed fixing all actionable findings within `fix_threshold` (record human tier-confirmation as `deferred`). Cumulative `fix_rounds` across both `A_FIX` retry rounds and `S_CONFIRM → S_FIX` (`needs_rework`) rework loops is capped at **MANDATORY hard limit: `max_fix_rounds = 5`**. Unchanged 2 rounds → classify each individually (`issue` / `decision`). After `fix_rounds >= max_fix_rounds` with `remaining_actionable > 0` → escalate per [shared base](odyssey-base.md) `<fix_loop_cap>` (Normal: structured prompt for 1 bounded `+2` extension or classify remaining as `deferred`/`issue` → `S_RECORD`; `-y`: classify remaining as `deferred`/`issue` as `ESCALATED` → `S_RECORD`). **No partial-tier advancement** — a tier is complete only when every finding is fixed or individually classified. Blanket "pre-existing" forbidden. Commit per tier: `"odyssey-review({slug}): FIX-{tier} — {N} items fixed"`.

### A_CONFIRM

Run tests (reusing still-valid passing results from `A_FIX` rounds where files did not change; rerunning invalidated targets) + `evolve agent exec "Review modified files for zero-residual defects across selected dimensions; return remaining findings and verdict" --to codex --mode analysis`. Update `confirmation` + `remaining_actionable` + §5:
- `remaining == 0 AND new == 0` → `confirmed`, mark G3
- otherwise (`needs_rework`) with `fix_rounds < max_fix_rounds` → `fix_rounds++` → `S_FIX`
- `needs_rework` with `fix_rounds >= max_fix_rounds` → escalate per [shared base](odyssey-base.md) `<fix_loop_cap>` → `S_RECORD`.

**GATE: zero-remaining** — exhaustive fix applied tier-by-tier, `remaining_actionable == 0` (or remaining classified as `deferred`/`issue` after `max_fix_rounds` escalation), tests pass, CLI re-review confirms no new findings, confirmation + §5 written, G3 marked. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to `S_RECORD`); skippable only when `skip_fix == true`.

---

## Generalize Source

Review findings with severity >= medium.

**Discover routing:** fixable sibling → S_FIX; new target → S_REVIEW (`cross_phase_loops < max_loops`, re-entry per [shared base](odyssey-base.md) `A_DISCOVER`).

---

## Knowledge Persistence (§8)

Include verified reusable findings in the Run summary and evidence; follow the shared A_RECORD and post-Session candidate governance in `odyssey-base.md`. Do not initiate harvest at Run seal.

| Category | Content | Follow-up |
|----------|---------|-----------|
| Cross-dimension recurring pattern | Pattern + affected dimensions + coding standard | Post-Session candidate review (`category: review`) |
| Security finding | Vulnerability type + triggers + fix approach | Post-Session candidate review (`category: debug`) |
| Architecture violation pattern | Violation + correct boundary + verification | Post-Session candidate review (`category: arch`) |
| Reusable generalization pattern | Signature + risk + fix template + scope | Post-Session candidate review (`category: coding`) |

---

## Completion Summary

```
--- REVIEW-TEST-FIX ODYSSEY COMPLETE ---
Target:     {target}          Dimensions: {dims}
Findings:   {C}C {H}H {M}M {L}L    Fix: {fixed}, confirmed={yes|skip}
Patterns:   {N} ({by_layer})        Scan hits: {total} ({cross} cross-layer)
Issues:     {N} created
Decisions:  {N} resolved, {M} pending, {K} deferred
Learnings:  {N} persisted
Self-iter:  {N} rounds across {M} stages
Goals:      {done}/{total} ({skipped} skipped)
---
```

---

## `-y` Decision Points

| Decision Point | Normal | `-y` |
|---------------|--------|------|
| S_FIX tier candidates | structured prompt | auto-proceed fixing all actionable findings (`>= fix_threshold`), record tier confirmation `deferred` |
| S_FIX re-review new findings | structured prompt | auto-append |
| S_CONFIRM needs_rework | structured prompt | auto-route to `S_FIX` while `fix_rounds < max_fix_rounds`; if `fix_rounds >= max_fix_rounds`, classify remaining as `deferred`/`issue` and proceed to `S_RECORD` |
| A_DISCOVER routing | structured prompt | auto-fix w/ template, issue for rest |

---

## Mode-Specific Phase Gates

- **Discovery gate** (ARCHAEOLOGY/EXPLORE): evidence logged, understanding.md updated. Archaeology partial via W003 acceptable; explore W006 skip acceptable.
- **Audit gate** (REVIEW): all selected dimension agents completed, findings merged. Zero dimensions reviewed is BLOCKED (W002 partial allowed).
- **FIX gate:** current severity tier fully addressed. Per-fix evidence logged. Auto-commit per tier. No partial-tier advancement. Cumulative retries bounded by `max_fix_rounds = 5`.
- **CONFIRM gate:** tests pass; `remaining_actionable == 0` and new findings == 0; confirmation written. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to `S_RECORD`).

---

## Error Codes (review-specific)

Shared `E000–E004` and `W008` errors are defined in [SKILL.md](../SKILL.md).

| Code | Severity | Condition | Recovery |
|------|----------|-----------|----------|
| W002 | warning | Some selected dimension agents failed (≥1 dimension succeeded) | Partial coverage accepted; log uncovered dimensions |
| W003 | warning | Archaeology agent failure | Proceed with available results |
| W006 | warning | No CLI delegate executable (`codex\|agy\|claude`) available | Skip delegate explore, log gap |

---

## Success Criteria (review-specific)

- [ ] Session + mode-contracted output files (`prepare/odyssey-review.md`) + prior knowledge searched
- [ ] Archaeology + CLI review → evidence
- [ ] CLI exploration → explore.json
- [ ] All selected dimensions (default 4, or `--dimensions` subset) reviewed with findings merged
- [ ] understanding.md 8 sections progressive
- [ ] Exhaustive fix by severity tier + confirmed (unless --skip-fix)
- [ ] Generalization + scan (unless --skip-generalize)
- [ ] Discoveries classified; unfixed findings individually justified
- [ ] phase_goals + goal audit + resumable via -c
- [ ] Completion summary

---

See [shared base](odyssey-base.md) for generic follow-up routing (`Next Step Routing`). Root-cause debugging: `/ew-odyssey <issue> --mode debug`.
