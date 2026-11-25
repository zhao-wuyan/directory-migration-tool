---
name: ew-odyssey-review
description: "Odyssey review mode — multi-dimensional deep code review through archaeology, exploration, 4-dimension audit, exhaustive severity-tiered fix, and zero-residual confirmation, producing review"
argument-hint: "<target: file|dir|HEAD|staged|phase#|PR#> [--dimensions <list>] [--fix-threshold <severity>] [--skip-fix] [--skip-generalize] [-y] [-c]"
contract:
  contract_version: 2.1
  produces:
    - path: outputs/session.json
      kind: session
      schema: session/1.0
      role: primary
      alias: review-session
      required: true
    - path: outputs/evidence.ndjson
      kind: evidence
      schema: evidence/1.0
      role: evidence
      alias: review-evidence
      required: false
    - path: outputs/understanding.md
      kind: understanding
      schema: understanding/1.0
      role: primary
      alias: review-understanding
      required: false
    - path: outputs/explore.json
      kind: exploration
      schema: exploration/1.0
      role: evidence
      alias: review-explore
      required: false
goal: true
---

# Pre-task Thinking: odyssey-review

## Purpose

Odyssey review is a multi-iteration full-lifecycle code review campaign: target intake → archaeology → code exploration → multi-dimensional review audit → exhaustive severity-tiered fix → zero-residual confirmation → generalization → discovery → knowledge persistence. Unlike single-pass code review (`ew-orchestrate` `ew-review`, which produces findings inside an orchestration chain or standalone diff without fixing), odyssey-review carries through to fix ALL findings within `fix_threshold` (default `all`) by severity, confirm zero residual, generalize patterns project-wide, and persist learnings.

## Input Interpretation

Entry & target resolution (canonical):

| Input / Mode | Trigger | Resolution |
|--------------|---------|------------|
| `file/dir` | File or directory path in `<intent>` | Review those files directly |
| `diff` | `HEAD` or `staged` in `<intent>` | `git diff HEAD` or `git diff --staged` |
| `session/run` | Session/Run ID or commit range in `<intent>` | Evolve Run artifacts / `git diff <base>..<head>` |
| `phase#` | Legacy plan phase label in `<intent>` | Resolve files scoped to that plan phase (or `E002` if unresolvable; prefer Session/Run IDs) |
| `PR#` | PR number in `<intent>` | `gh pr diff <num>` (or resolved `<base>...HEAD` range); fall back to `git diff main...HEAD` with `W001` if unresolvable |
| `continuation` | `-c` | Resume via latest session, jump to `current_state` |

Dimensions default to all 4 (`correctness`, `security`, `performance`, `architecture`), overridable via `--dimensions <list>`. `--fix-threshold <sev>` sets the minimum severity to fix (default `all`).

## Required Context

Satisfy the Knowledge Gate per [shared base](../odyssey-base.md) §Phase Initialization & Pre-load (`evolve skill workflows show session-run` — `session-priors` when scope matches → `evolve search "<query>" --mode context` / `evolve kb get <id>` with CLI `--reads`) for `review` and `coding` scope before searching code (`evolve code search "<target keywords>"` → module boundaries, ownership).

When prior review artifacts of the same scope exist, check their findings first to avoid re-reviewing already-addressed patterns.

## Run Artifact Metadata

Follow [runtime adapters](../runtime-adapters.md) §4 for `_meta` attachment (`outputs/session.json`, `outputs/explore.json`, first record of `outputs/evidence.ndjson`, YAML frontmatter of `outputs/understanding.md`), `current_state: "END"`, and `evolve run complete` sealing.

## Boundaries and Invariants

- **State chain:** `S_INTAKE → S_ARCHAEOLOGY → S_EXPLORE → S_REVIEW → S_FIX → S_CONFIRM → [back-half]`
- **Zero-residual applies** — fix ALL findings within `fix_threshold` (default: all severity levels). No partial-tier advancement.
- **Evidence append-only** — evidence.ndjson entries are immutable observations; modifying or deleting them is forbidden.
- **Phase goal tracking** — mark each goal done/failed before transition; no silent skips.
- **Selected dimensions mandatory** — all selected dimensions (default all 4: correctness, security, performance, architecture; or `--dimensions` subset) must be reviewed. Zero dimensions reviewed is BLOCKED.
- **Exhaustive fix by severity** — descend through [critical, high, medium, low] within `fix_threshold`, each tier fully addressed before advancing. Blanket "pre-existing" classification forbidden; each finding must be individually assessed.
- **max_fix_rounds = 5 (cumulative)** — hard limit on cumulative fix retry rounds across both `S_FIX` internal retries and `S_CONFIRM → S_FIX` (`needs_rework`) loops. After 5 rounds with remaining > 0, escalate per [shared base](../odyssey-base.md) `<fix_loop_cap>`.
- **Test evidence reuse across S_FIX/S_CONFIRM** — a per-tier commit invalidates only tests covering the committed changes; unchanged passing results carry forward, and S_CONFIRM reruns only invalidated targets plus the modified-area re-review. Never rerun the full matrix merely because a tier advanced.
- **Generalize is mandatory** unless `skip_generalize == true`; prior-phase convergence is NOT a valid skip reason.
- **Fix scope:** source code modifications during fix phase are in-scope and MUST be committed per tier. Session artifacts target `{run_dir}/outputs/` only.
- **In scope:** Multi-iteration deep review → exhaustive fix → generalize. **Out of scope:** Single-pass 4-dimension review → `ew-orchestrate` (`ew-review`) | Root cause debug → `--mode debug` | Feature implementation → `--mode planex` | UI visual optimization → `--mode ui`.

## Risk Checklist

- Are all selected review dimensions (default 4, or `--dimensions` subset) covered? Missing a selected dimension leaves an entire class of findings undetected.
- Is every finding backed by file, line, and description evidence? Vague findings without location are not actionable.
- Did you descend severity tiers in order (critical → high → medium → low)? Skipping tiers leaves high-severity issues unfixed.
- Is every finding in a tier either fixed or individually classified with a reason? Blanket "pre-existing" skips are forbidden.
- Have cumulative fix rounds (across S_FIX retries and S_CONFIRM → S_FIX) exceeded 5? Stop and escalate — do not continue fixing indefinitely.
- Are archaeology/explore results properly logged even on partial failure (W003/W006)? Missing evidence must be flagged, not silently omitted.
- Is every discovery hit individually classified with a reason? Blanket skips are forbidden.
- Are all 3 generalization layers (syntax/semantic/structural) attempted? A single-layer quick grep does NOT satisfy the thoroughness floor.
- Did the zero-residual confirmation actually re-review modified areas? A pass-through without re-review does not satisfy the CONFIRM gate.

## Gate Intent

- `discovery-complete`: archaeology and/or exploration phases have logged evidence, understanding.md §2-§3 are updated, and explore goal (G2) is marked. Log W003 on partial archaeology failure or W006 when no CLI exploration executor is available, and record what was covered.
- `all-dimensions-reviewed`: all selected dimension agents (default all 4: correctness, security, performance, architecture; or `--dimensions` subset) have completed, findings are merged into review_result with severity classification, evidence phase=review is logged, and understanding.md §4 severity matrix is written. G1 is marked. Zero dimensions reviewed is BLOCKED (W002 partial with at least 1 dimension is allowed).
- `zero-remaining`: exhaustive fix has been applied tier-by-tier, `remaining_actionable == 0` (or remaining individually classified as deferred/issue after `max_fix_rounds = 5` escalation), tests covering the modified areas pass (reusing still-valid live-run results from S_FIX rounds when nothing they cover changed since; rerunning only invalidated targets), CLI re-review confirms no new findings, confirmation is written, understanding.md §5 is updated, and G3 is marked. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to S_RECORD). Skippable only when `skip_fix == true`.
