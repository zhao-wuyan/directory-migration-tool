---
name: ew-odyssey-arch
description: "Odyssey arch mode — error-driven codebase architecture deepening: hotspot survey, shallow-module & seam audit, Design-It-Twice parallel interface exploration, seam deepening, replace-don't-layer tests, and deterministic boundary guardrails"
argument-hint: "<target|symptom> [--visual] [--skip-fix] [--skip-generalize] [-y] [-c]"
contract:
  contract_version: 2.1
  produces:
    - path: outputs/session.json
      kind: session
      schema: session/1.0
      role: primary
      alias: arch-session
      required: true
    - path: outputs/evidence.ndjson
      kind: evidence
      schema: evidence/1.0
      role: evidence
      alias: arch-evidence
      required: false
    - path: outputs/understanding.md
      kind: understanding
      schema: understanding/1.0
      role: primary
      alias: arch-understanding
      required: false
    - path: outputs/explore.json
      kind: exploration
      schema: exploration/1.0
      role: evidence
      alias: arch-explore
      required: false
goal: true
---

# Pre-task Thinking: odyssey-arch

## Purpose

Odyssey arch runs an error-driven architecture deepening campaign: anchor on recurring errors, episodic friction, and git churn hotspots; audit shallow modules and missing seams with the Deletion Test and 4 dependency categories; explore radically different interfaces in parallel (`Design-It-Twice`); deepen the winning seam with an explicit decision gate for public API breaks; replace shallow tests at the deepened interface; enforce deterministic boundary guardrails ("prove the rules bite"); and generalize/persist learnings across the codebase. Unlike `--mode improve` (6-dimension health sweep preserving APIs) or `--mode debug` (targeted root-cause fix), `--mode arch` restructures seams for caller **leverage**, maintainer **locality**, and interface-level testability.

## Input Interpretation

Target resolution determines where hotspot survey begins:

| Input | Resolution |
|-------|-----------|
| Module / dir path | Survey that subsystem and its immediate callers/adapters |
| Symptom / error / debug ref | Trace failing/flaky area and episodic error memories to missing seams |
| `HEAD` / `staged` | Audit newly introduced or modified seams in diff |
| `--all` | Rank repo-wide churn and error hotspots first (YAGNI: deepen top hotspots only) |
| `-c` | Resume latest unfinished `ew-odyssey-arch` session at `current_state` |

## Required Context

Satisfy the Knowledge Gate per [shared base](../odyssey-base.md) §Phase Initialization & Pre-load (`evolve skill workflows show session-run` — `session-priors` when scope matches → `evolve search "<query>" --mode context` / `evolve kb get <id>` with CLI `--reads`) for `arch`, `debug`, and `coding` scope (including episodic `error`, `correction`, and `work_fact` memories) before searching code (`evolve code search "<target keywords>"`).

- Read `GLOSSARY.md` and relevant ADRs (`docs/adr/`) first when present in the repository so seam names follow domain language and settled decisions are respected unless friction warrants reopening.
- Capture `session.json.baseline_metrics` (`test_status`, `module_count`, `cycle_count`, `hotspot_files`, `boundary_check`) before editing source files.

## Run Artifact Metadata

Follow [runtime adapters](../runtime-adapters.md) §4 for `_meta` attachment (`outputs/session.json`, `outputs/explore.json`, first record of `outputs/evidence.ndjson`, YAML frontmatter of `outputs/understanding.md`), `current_state: "END"`, and `evolve run complete` sealing.

## Boundaries and Invariants

- **State chain:** `S_INTAKE → S_SURVEY → S_AUDIT → S_DESIGN → S_DEEPEN → S_VERIFY → [back-half]`
- **Design vocabulary:** Use **module**, **interface**, **implementation**, **depth**, **seam**, **adapter**, **leverage**, and **locality** consistently (avoid drifting to "component", "service", "API", or "boundary").
- **Deletion test & seam discipline:** Keep a seam only when deleting it would scatter complexity across callers or when at least two concrete adapters exist (e.g., production + test stand-in). Keep internal seams private behind the module's external interface.
- **Public API break gate:** Any design that breaks a public interface, introduces a new dependency, or shifts cross-module contracts requires an explicit `phase=decision` checkpoint (structured prompt in Normal mode; under `-y`, prefer a backward-compatible facade/shim or record `deferred` / `issue` rather than breaking external callers unapproved).
- **Replace, don't layer tests:** Write behavior tests against the deepened module's interface and delete obsolete shallow-module unit tests once equivalent interface-level coverage passes.
- **Prove the rules bite:** Deterministic boundary guardrails pass on clean code, fail with the expected rule when a temporary violation is injected, and pass again after reverting the test violation.
- **Evidence append-only & goal tracking:** Append `evidence.ndjson` entries without mutating prior records; update `phase_goals[]` cleanly across transitions; enforce `max_fix_rounds = 5` on `S_VERIFY → S_DEEPEN`; run 3-layer `S_GENERALIZE` unless `--skip-generalize`.
- **In scope:** Error/churn-driven deepening, seam restructuring, interface tests, deterministic boundary guardrails. **Out of scope:** Single bug fix with existing seam → `--mode debug` | Multi-dimension code quality sweep without seam redesign → `--mode improve` | New feature delivery → `--mode planex` | UI work → `--mode ui`.

## Risk Checklist

- Did the hotspot survey combine error/memory signals with recent git churn rather than refactoring stable code (YAGNI)?
- Was every deepening candidate evaluated with the Deletion Test and classified into one of the 4 dependency categories (`in-process`, `local-substitutable`, `remote-owned`, `true-external`)?
- Did `S_DESIGN` explore 3+ orthogonal constraints in fresh-context subagents before committing to an interface?
- Were public interface breaks or new dependencies gated at `phase=decision` with explicit confirmation (or backward-compatible shims under `-y`)?
- Were obsolete shallow unit tests removed after interface-level tests passed, instead of layering duplicate tests?
- Did deterministic boundary guardrails prove they bite (pass → injected violation fails → revert passes)?

## Gate Intent

- `seam-audit-complete`: error/memory/churn hotspots surveyed in `outputs/explore.json`, deepening candidates scored with Deletion Test + dependency category, ADR conflicts noted where load-bearing, and `understanding.md §2–§3` updated (`G1`, `G2` marked).
- `design-selected`: 3+ orthogonal `Design-It-Twice` interface proposals compared on depth, locality, and seam placement; winning design (or hybrid) selected and any breaking public API or dependency change resolved via `phase=decision`; `understanding.md §4` updated (`G3` marked).
- `deepening-verified`: seam deepened, replace-don't-layer tests pass, deterministic boundary guardrails proven to bite and clean, cross-angle CLI verification completed, before/after metrics written to `understanding.md §6` & `§8`, and `G4` marked (skippable via `--skip-fix`).
