# Workflow: ew-blueprint

6-phase formal specification chain: `P1 Discovery -> P1.5 Clarification -> P2 Product Brief -> P3 PRD -> P4 Architecture -> P5 Epics -> P6 Readiness Check`.

## Phase Execution

1. **P1 Discovery**:
   - Complete the Run Knowledge Gate (`evolve skill workflows show session-run`). Under `-c`, recover existing phase outputs from the prior Run via `evolve run get` (or bound `current-blueprint`) and resume from the first incomplete phase.
   - Confirm spec profile (`service` / `api` / `library` / `platform`) and depth (`light` / `standard` / `comprehensive`), saving `outputs/blueprint-config.json`.
   - Ground existing system context via read-only codebase exploration (`evolve code search` + optional `cli-explore-agent` / `workflow-external-researcher` / `workflow-research-synthesizer` subagent, joined before P2; fall back to inline instructions when absent) -> write `outputs/discovery-context.json`. Use `search_web` only if external API constraints are needed.
2. **P1.5 Requirement Expansion** (skip if `upstream-context` or `current-brainstorm` already supplies confirmed requirements/options):
   - Analyze completeness across functional scope, user scenarios, NFRs, integrations, data model, and failure modes.
   - Clarify critical gaps via structured prompt (`ask_question` or host equivalent, up to 5 rounds) -> write `outputs/refined-requirements.json`.
3. **P2 Product Brief**:
   - Author `outputs/product-brief.md` (frontmatter `kind: blueprint`, `schema: blueprint/1.0`, `role: primary`, `alias: current-blueprint`; vision, target users, scope, non-goals, KPIs) and `outputs/glossary.json` (`>= 5` domain terms, `required: true`).
4. **P3 Detailed PRD**:
   - Write `outputs/requirements/_index.md` (summary + MoSCoW table + traceability) plus `outputs/requirements/REQ-NNN-<slug>.md` and `NFR-<slug>.md` (matching `REQ-{slug}.md` / `NFR-{slug}.md` slots).
5. **P4 Architecture**:
   - Write `outputs/architecture/_index.md` (system boundaries, components, tech stack, data flow) plus `outputs/architecture/ADR-NNN-<slug>.md` (matching `ADR-{slug}.md`) reflecting active Wiki constraints.
6. **P5 Epics & Stories**:
   - Write `outputs/epics/_index.md` (dependency map, MVP cut) and `outputs/epics/EPIC-NNN-<slug>.md` (matching `EPIC-{slug}.md`), deriving realistic slice sizes from P4.
7. **P6 Readiness Check & Packaging**:
   - Validate requirement coverage, ADR traceability, terminology consistency, and testability (optionally dispatching `workflow-integration-checker` / `workflow_integration_checker` joined before finalizing) -> write `outputs/readiness-report.md` and `outputs/blueprint-summary.md`.
   - Threshold branches:
     - `>= 80%`: passes cleanly (`verdict: ready`).
     - `60–79%`: run at most `2` targeted auto-fix/revision passes to lift to `>= 80%`; if still `60–79%`, proceed with explicit caveats in `readiness-report.md` and `report.md` (`verdict: ready_with_concerns`).
     - `< 60%`: run at most `2` targeted revision passes; if still `< 60%`, record blockers (`verdict: blocked`) and exit via `evolve run transition <run_id> blocked` without calling `run complete`.
   - Emit `outputs/context-package.json` (`_meta: { kind: "context-package", schema: "context-package/1.0", role: "attachment", alias: "upstream-context" }`) for downstream `ew-roadmap`, `ew-analyze`, or `ew-plan`.

## Wrap-up & Seal

Write `{run_dir}/report.md` (`verdict: ready|ready_with_concerns|blocked|failed`, `next` proposing `ew-roadmap` or `ew-plan`) and seal or transition the Run per `evolve skill workflows show session-run` (`--gate phases-complete --gate readiness-passed`). Stage reusable architectural decisions in `report.md` frontmatter or `evolve kb add --kind episodic`; defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete`.
