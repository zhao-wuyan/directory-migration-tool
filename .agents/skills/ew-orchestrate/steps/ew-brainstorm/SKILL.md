---
name: ew-brainstorm
description: Explore a problem space and produce bounded options for the Evolve chain
argument-hint: '<intent> [--from <artifact-alias>] [-y]'
wiki_categories: [arch, learning]
search_profile:
  wiki: references
  memory: optional
session-mode: run
contract:
  consumes:
  - kind: context-package
    alias: upstream-context
    required: false
    schema: context-package/1.0
    role: attachment
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/brainstorm.json
    kind: brainstorm
    alias: current-brainstorm
    role: primary
    required: true
    schema: brainstorm/1.0
  - path: outputs/context-package.json
    kind: context-package
    alias: upstream-context
    role: attachment
    required: false
    schema: context-package/1.0
  contract_version: 2.1
---

# Brainstorm

Brainstorm is a planning step inside the Evolve Skill. Explore the problem space, record assumptions and alternatives, and leave a bounded option set for the next chain step.

1. Read the current Session objective, `boundary_contract`, optional `upstream-context`, and available Wiki evidence. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`, reusing `session-priors` or searching `arch` scope with `--category arch` when using CLI `--reads`).
2. Separate observed facts, user constraints, open questions, and proposed options (`-y` skips interactive prompts and resolves defaults from codebase/Wiki evidence). When deeper codebase grounding or parallel option evaluation is needed, dispatch `cli-explore-agent` / `workflow-analyzer` (`cli_explore_agent` / `workflow_analyzer` in Codex, or inline role instructions when absent) with a **fresh context window** and **sparse context pointers** (`session_id`, `run_dir`, artifact/Wiki pointers, target paths; reserve `fork` only for warm deep-dive branches needing primary-source thread context; always join/cancel before Step 4). When a state-model, logic, or UI-layout question requires a runnable throwaway prototype rather than paper debate, record the open question and recommend invoking `ew-prototype` before locking an approach.
3. **Optional `DESIGN-IT-TWICE` parallel design exploration**: when evaluating competing module interfaces or architectural seams, frame the problem space first (constraints, dependencies, and a brief grounding sketch), then spawn 3+ parallel **fresh-context** subagents with radically different design constraints:
   - Variant A: minimize interface (1–3 entry points, max leverage per entry point).
   - Variant B: maximize flexibility and extension across use cases.
   - Variant C: optimize ergonomics for the most common caller / default case.
   - Variant D (optional): ports & adapters across cross-seam dependencies.
   Compare candidate outputs on **depth** (leverage at the interface), **locality** (where change concentrates), and **seam placement**, plus scope, risk, reversibility, and verification cost, and state an opinionated recommendation (or hybrid).
4. Write `{run_dir}/outputs/brainstorm.json` (with `_meta: { kind: "brainstorm", schema: "brainstorm/1.0", role: "primary", alias: "current-brainstorm" }`) and optional `outputs/context-package.json` (`_meta` matching `context-package/1.0`), citing source evidence for every material conclusion.
5. Write `{run_dir}/report.md` (`verdict: ready|ready_with_concerns|blocked|failed`; `next` proposing `ew-prototype`, `ew-blueprint`, `ew-roadmap`, `ew-analyze`, or `ew-plan` only when durable follow-up artifacts or runnable prototype checks are needed), then seal or transition the Run per `evolve skill workflows show session-run`.

This step is read-only toward repository code and preserves the Session chain without substituting an untracked recommendation.
