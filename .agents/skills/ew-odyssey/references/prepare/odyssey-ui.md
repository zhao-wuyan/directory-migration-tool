---
name: ew-odyssey-ui
description: "6-dimension visual experience audit with divergent exploration — survey design context, audit visual/interaction/a11y/responsive/motion/edge dimensions, diverge for polish+delight, fix, verify,"
argument-hint: "<target> [--dimensions <list>] [--fix-threshold <severity>] [--visual] [--skip-fix] [--skip-generalize] [-y] [-c]"
contract:
  contract_version: 2.1
  produces:
    - path: outputs/session.json
      kind: session
      schema: session/1.0
      role: primary
      alias: ui-session
      required: true
    - path: outputs/evidence.ndjson
      kind: evidence
      schema: evidence/1.0
      role: evidence
      alias: ui-evidence
      required: false
    - path: outputs/understanding.md
      kind: understanding
      schema: understanding/1.0
      role: primary
      alias: ui-understanding
      required: false
goal: true
---

# Pre-task Thinking: odyssey-ui

## Purpose

Odyssey UI performs a **multi-round iterative** visual experience optimization campaign: survey design context (`ew-impeccable` context + `PRODUCT.md`/`DESIGN.md`) → 6-dimension audit (visual hierarchy, interaction states, accessibility, responsiveness, micro-interactions, edge cases) → divergent exploration for polish and delight ideas → exhaustive fix → verify → generalize to sibling components → persist learnings. For **single-pass** UI design, redesign, critique, audit, or polish (or greenfield UI creation), route to `ew-impeccable` (`evolve skill workflows show ew-impeccable`) instead.

## Input Interpretation

Target resolution determines what gets audited:

| Input | Resolution |
|-------|-----------|
| Component path | Audit component |
| Page/route | Audit page |
| `staged` / `HEAD` | Diff UI changes |
| Feature area | Resolve to components/pages |

The six dimensions (default all, overridable via `--dimensions`): visual_hierarchy, interaction_states, accessibility, responsiveness, micro_interactions, edge_cases. Each dimension has a distinct focus area (see Dimensions table in workflow file).

## Required Context

Satisfy the Knowledge Gate per [shared base](../odyssey-base.md) §Phase Initialization & Pre-load (`evolve skill workflows show session-run` — `session-priors` when scope matches → `evolve search "<query>" --mode context` / `evolve kb get <id>` with CLI `--reads`) for `ui` and `coding` scope before searching code (`evolve code search "<target keywords>"` → component hierarchy, design system structure).

- **Impeccable design context (mandatory before UI survey/edits)**: load `evolve skill workflows show ew-impeccable`, run `impeccable context` once (`node <impeccable-base>/scripts/impeccable.mjs context --target <target>` if available; on launcher failure, read existing `PRODUCT.md` and `DESIGN.md` directly without inventing missing context), and read `skills/ew-impeccable/reference/craft-floor.md` immediately before any UI edit in `S_FIX`.
- Design system scan: scan for design tokens, CSS variables, theme imports at survey phase — these inform the audit baseline.

When prior UI sessions of the same target exist, check their audit results and diverge ideas first to avoid re-auditing already-addressed dimensions.

## Run Artifact Metadata

Follow [runtime adapters](../runtime-adapters.md) §4 for `_meta` attachment (`outputs/session.json`, first record of `outputs/evidence.ndjson`, YAML frontmatter of `outputs/understanding.md`), `current_state: "END"`, and `evolve run complete` sealing.

## Boundaries and Invariants

- **Browser is truth (with fallback)** — rendered visual output in the browser at desktop & mobile breakpoints is the authoritative source of UI quality; when no browser/preview runtime is available, log `W001` and fall back to committed visual-regression fixtures and static DOM/CSS checks without fabricating rendered evidence.
- **Impeccable guardrails before any UI edit** — honor `PRODUCT.md`, `DESIGN.md`, and the committed visual world from `impeccable context`; load `skills/ew-impeccable/reference/craft-floor.md` immediately before editing UI code in `S_FIX`.
- **Single-pass vs campaign boundary** — single-pass UI design/audit/polish belongs to `ew-impeccable` (`evolve skill workflows show ew-impeccable`); multi-iteration UI campaigns use `ew-odyssey-ui`.
- **Diverge before converge** — the DIVERGE phase explicitly generates ideas beyond the audit defects. Polish (shadows, transitions, hover, feedback, skeleton loading) and delight (motion design, progressive disclosure, smart defaults, celebratory feedback) are separate from fixing audit findings.
- **Decision gate narrow scope (overrides [shared base](../odyssey-base.md) §Execution Discipline #2)** — ONLY these qualify as decisions requiring user input: brand/style direction requiring human creative judgment | layout restructuring that significantly changes user flow | requires new design tokens or breaking component API. Everything else is actionable without user escalation.
- **All dimensions mandatory** — all 6 dimensions (or `--dimensions` subset) must be audited with independent agents. Zero dimensions reviewed is BLOCKED, not a warning.
- **Priority-ordered fixing & fix loop cap** — fix all actionable findings (`>= fix_threshold`, default `all`: critical → high → medium → low) plus `high-impact` diverge ideas (`critical → high → medium → low + high-impact ideas`); medium/low ideas not implemented are triaged as `issue` or `skipped` with per-item reasons. `S_VERIFY → S_FIX` retries are bounded by `fix_rounds < max_fix_rounds` (default `5`). No partial-tier advancement.
- **Evidence append-only** — evidence.ndjson entries are immutable observations; modifying or deleting them is forbidden.
- **Fix scope** — source code modifications during fix phase are in-scope and MUST be committed per verified fix/action. Session artifacts target `{run_dir}/outputs/` only.
- **Generalize is mandatory** unless `skip_generalize == true`; prior-phase convergence is NOT a valid skip reason.

## Risk Checklist

- Is every audit finding anchored to `file:line` with severity, dimension, description, and suggestion? Unanchored findings are not actionable.
- Were all 6 dimensions (or `--dimensions` subset) audited with independent agents? Missing dimensions mean incomplete coverage.
- Did the DIVERGE phase go beyond fixing defects — did it explore polish and delight opportunities separately? Diverge that only restates audit findings is insufficient.
- Are the 2 diverge agents (Polish + Delight) returning distinct categories? Overlapping outputs indicate the agents were not properly scoped.
- After consolidation, is the priority list sorted by severity × impact × effort? Unsorted lists lead to suboptimal fix ordering.
- Is every discovery hit individually classified with a reason? Blanket "pre-existing" skips are forbidden.
- Are all 3 generalization layers (syntax/semantic/structural) attempted? A single-layer quick grep does NOT satisfy the thoroughness floor.
- Does the decision gate only escalate brand/style/layout/token decisions? Over-escalation slows the cycle; under-escalation risks unwanted visual changes.

## Gate Intent

- `all-dimensions-audited`: all 6 dimensions (or `--dimensions` subset) completed audit with structured findings (`[{title, severity, file, line, description, suggestion, dimension}]`), merged into severity matrix, and evidence phase=audit logged for each. Zero dimensions reviewed is BLOCKED (W002 partial coverage from agent failure is a warning, not a block).
- `diverge-explored`: both Polish and Delight agents completed, returning distinct `[{idea, category, impact, effort, description, inspiration}]` entries. CLI-assisted analysis completed. All outputs consolidated with audit findings into a prioritized list (severity × impact × effort). Evidence phase=diverge logged. Understanding.md §4 written.
- `zero-remaining-verified`: all actionable audit findings (`>= fix_threshold`) and `high-impact` diverge ideas are fixed and verified, individually classified with justification (issue created / decision recorded), or legitimately skipped via `--skip-fix` flag (medium/low ideas not implemented are triaged as `issue` or `skipped` with per-item reasons). Tests pass (lint, unit, visual regression) with rendered browser verification (or `W001` fallback). Confirmation written. No unaddressed actionable findings remain. Understanding.md §5 updated. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to S_RECORD).
