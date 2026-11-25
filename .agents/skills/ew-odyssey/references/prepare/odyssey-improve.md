---
name: ew-odyssey-improve
description: 6-dimension runtime quality audit → diagnose → fix → verify cycle with baseline metrics tracking and zero-residual enforcement
argument-hint: "<target> [--dimensions <list>] [--fix-threshold <severity>] [--skip-fix] [--skip-generalize] [-y] [-c]"
contract:
  contract_version: 2.1
  produces:
    - path: outputs/session.json
      kind: session
      schema: session/1.0
      role: primary
      alias: improve-session
      required: true
    - path: outputs/evidence.ndjson
      kind: evidence
      schema: evidence/1.0
      role: evidence
      alias: improve-evidence
      required: false
    - path: outputs/understanding.md
      kind: understanding
      schema: understanding/1.0
      role: primary
      alias: improve-understanding
      required: false
goal: true
---

# Pre-task Thinking: odyssey-improve

## Purpose

Odyssey improve performs a systematic 6-dimension quality audit of a target module or changeset, diagnoses root causes for actionable findings (`>= fix_threshold`, default `all`), applies exhaustive fixes by severity tier, and verifies improvements against a captured baseline. Before starting, establish the target scope, dimension coverage, and baseline metrics.

## Input Interpretation

Target resolution determines what gets audited:

| Input | Resolution |
|-------|-----------|
| Module/dir path | Audit that module |
| `HEAD` / `staged` | Review changes in diff |
| Feature area keyword | Resolve to related files |
| `--all` | Full project scan (use with caution) |

The six dimensions (default all, overridable via `--dimensions`): performance, security, architecture, reliability, observability, maintainability. `--fix-threshold` controls the minimum severity that triggers a fix (default: all).

## Required Context

Satisfy the Knowledge Gate per [shared base](../odyssey-base.md) §Phase Initialization & Pre-load (`evolve skill workflows show session-run` — `session-priors` when scope matches → `evolve search "<query>" --mode context` / `evolve kb get <id>` with CLI `--reads`) for `coding` and `debug` scope before searching code (`evolve code search "<target keywords>"` → module boundaries, layering rules).

- Baseline metrics: capture current test pass rate, bundle size, dependency count, complexity hotspots before any changes — these form `session.json.baseline_metrics` for before/after comparison.

When prior improve sessions of the same target exist, check their audit results and fixes first to avoid re-diagnosing already-resolved issues.

## Run Artifact Metadata

Follow [runtime adapters](../runtime-adapters.md) §4 for `_meta` attachment (`outputs/session.json`, first record of `outputs/evidence.ndjson`, YAML frontmatter of `outputs/understanding.md`), `current_state: "END"`, and `evolve run complete` sealing.

## Boundaries and Invariants

- **Zero-residual** — every finding MUST have a concrete action (fix / issue / decision). "Report and shelve" and blanket "pre-existing" skips are forbidden.
- **Dimension independence** — each of the 6 dimensions produces findings independently; one dimension's result must not suppress or override another's.
- **No partial-tier advancement** — each severity tier (critical → high → medium → low) must be fully addressed before advancing to the next; blanket "pre-existing" skip forbidden.
- **Baseline integrity** — `baseline_metrics` captured at INTAKE are immutable throughout the session; before/after comparison in §8 uses these exact values.
- **Fix scope** — fixes target diagnosed root causes, not symptoms. Symptoms may indicate a deeper architectural issue requiring `--mode debug` escalation.
- **Evidence append-only** — evidence.ndjson entries are immutable observations; modifying or deleting them is forbidden.
- **Generalize is mandatory** unless `skip_generalize == true`; prior-phase convergence is NOT a valid skip reason.
- **Exhaustive audit** — all 6 dimensions (or `--dimensions` subset) must be attempted. Zero dimensions reviewed is BLOCKED, not a warning.
- **Behavioral equivalence** — fixes MUST preserve existing behavior. All tests MUST pass after each individual change, not just at the end of a tier.
- **Scope locked after confirmation** — once the fix plan is confirmed (or auto-proceeded via `-y`), no scope expansion to additional files without re-confirmation.
- **Incremental verification** — each discrete fix step MUST be verified (tests run) before proceeding. NEVER batch multiple unrelated changes into a single verification.
- **No feature creep** — fixes MUST NOT add new functionality, change APIs, or alter public interfaces. Beneficial changes discovered → evidence phase=decision as recommendations, NOT applied.
- **Rollback safety & fix loop cap** — commit per verified fix immediately on test pass so that if any test fails after a subsequent fix step, reverting that uncommitted change (`git checkout -- {file}` / patch revert) never discards earlier verified fixes; NEVER proceed with failing tests. `S_VERIFY → S_FIX` rework loops are bounded by `fix_rounds < max_fix_rounds` (default `5`).

## Risk Checklist

- Is every finding anchored to `file:line` with severity, dimension, description, and measurement? Unanchored findings are not actionable.
- Were baseline metrics captured before any changes? Without a baseline, the before/after comparison in §8 is meaningless.
- Are all 6 dimensions (or the `--dimensions` subset) audited with independent agents? Missing dimensions mean incomplete coverage.
- Does root cause diagnosis trace to the true source, not the symptom? Fixing symptoms leaves the root cause live.
- After each severity tier fix, was re-verification scoped to the current tier's dimension only? Cross-dimension regression checks belong at S_VERIFY.
- Were 3 diagnosis retries exhausted before marking INCONCLUSIVE? Premature escalation misses solvable issues.
- Is zero-residual enforced — every finding has fix / issue / decision, with no blanket skips?
- Are all 3 generalization layers (syntax/semantic/structural) attempted? A single-layer quick grep does NOT satisfy the thoroughness floor.
- Does each fix preserve behavioral equivalence? A fix that changes observable behavior (API shape, return values, side effects) is scope creep, not a fix.
- Was each fix step individually verified before proceeding? Batched changes make regression attribution impossible.
- If a test failed after a fix, was that specific change reverted before trying alternatives? Proceeding with failing tests compounds risk.

## Gate Intent

- `all-dimensions-audited`: all 6 dimensions (or `--dimensions` subset) completed audit with structured findings (`[{title, severity, dimension, file, line, description, suggestion, measurement}]`), merged into severity matrix, and evidence phase=audit logged for each. Zero dimensions reviewed is BLOCKED (W002 partial coverage from agent failure is a warning, not a block).
- `zero-remaining-verified`: all diagnosed findings are either fixed and verified, individually classified with justification (issue created / decision recorded), or legitimately skipped via `--skip-fix` flag. Tests pass over modified areas. Metrics re-captured and compared against `baseline_metrics`. No unaddressed actionable findings remain. Before/after improvement metrics table written to understanding.md §8.
