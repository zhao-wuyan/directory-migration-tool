# Workflow: ew-grill

Adversarial Socratic pressure testing: `Parse & Route -> Discovery & Terminology -> Branch Walking -> Synthesis -> Context Package -> Wrap-up`.

## Step 1: Discovery & Codebase Grounding

1. Load optional upstream artifact via `evolve run read artifact <run> <alias> --session <session>` (`upstream-context`, `current-blueprint`, `current-grill`, or read `--from @file`).
2. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) to surface architectural constraints and domain decisions.
3. Launch a read-only exploration subagent (`cli-explore-agent` / `cli_explore_agent`, via `invoke_subagent` or host equivalent, joined before Step 2; fall back to inline exploration instructions when absent) + `evolve code search` to map overlapping modules, naming conventions, error-handling patterns, and integration boundaries -> save summary to `{run_dir}/evidence/codebase-context.json`. If grounding fails, mark findings `[LOW CONFIDENCE]`.

## Step 2: Terminology Alignment (`terminology-aligned` gate)

Compare proposal nouns/verbs/states against existing codebase identifiers, Wiki concepts, and `GLOSSARY.md` (if present):
- Flag every naming collision, overloaded term, or vague abstraction, proposing precise canonical terms.
- Cross-reference domain claims against actual `file:line` code behavior and surface mismatches immediately.
- Arbitrate canonical names with the user (or codebase convention under `-y`).
- Write `{run_dir}/outputs/terminology.md` before walking deep branches.

## Step 3: Socratic Branch Walking (`branches-walked` gate)

Model decisions as a **design tree** and walk `3` (`shallow`), `5` (`standard`), or `8` (`deep`) risk branches from **basic -> specific -> adversarial** in dependency-frontier rounds until the frontier is empty:
- **Compute the settled frontier**: gather all branch questions whose upstream prerequisites are settled (defer questions that depend on still-open answers to the next round).
- **Non-blocking background fact-finding**: when a branch needs a factual lookup from the codebase or environment, dispatch a fresh read-only subagent (`cli-explore-agent` / `invoke_subagent` or host equivalent) instead of asking the user; hold only questions downstream of that lookup while asking the rest of the settled frontier immediately. Join all spawned subagents before advancing to Step 4.
- **Batch the frontier in one round**: in interactive mode, ask all settled frontier questions together via `ask_question` (or host equivalent), numbered (`Q1`, `Q2`, ...) with concrete `file:line` context and a recommended answer (`-> <recommendation>`), worded so `"yes"` accepts the recommendation.
- Under `-y`, resolve each frontier branch directly from codebase evidence, recording `source: code-grounded` and explicit assumptions.
- Challenge any contradiction with prior answers or `file:line` reality on the spot, then recompute the frontier for the next round.
- **Throwaway prototype escalation**: when a state-model, logic, or UI-layout branch cannot be settled on paper, recommend invoking `ew-prototype` to validate a runnable throwaway prototype before locking the design.

## Step 4: Synthesis, Context Package & Seal

1. Apply the **3-condition ADR gate** to settled decisions: recommend an ADR only when **all three** hold — (1) **hard to reverse**, (2) **surprising without context**, and (3) **result of a real trade-off** (skip trivial, obvious, or reversible choices). Summarize qualifying ADR candidates concisely (1–3 sentences of context, decision, and rationale, plus optional Status/Considered Options/Consequences only when high value) inside `outputs/grill-report.md` and `outputs/context-package.json` `references[]`.
2. Write `{run_dir}/outputs/grill-report.md` (frontmatter `kind: grill-report`, `schema: grill-report/1.0`, `role: primary`, `alias: current-grill`; decisions, rejected assumptions, `file:line` evidence, ADR candidates, open risks, residual severity).
3. Write `{run_dir}/outputs/context-package.json` (`_meta: { kind: "context-package", schema: "context-package/1.0", role: "attachment", alias: "upstream-context" }`, `domain`, `requirements[]`, `constraints[]`, `non_goals[]`, `insights[]`, `references[]`) for downstream `ew-brainstorm` / `ew-plan` / `ew-blueprint`.
4. Write `{run_dir}/report.md` with frontmatter (`verdict: ready|ready_with_concerns|blocked|failed`, `summary`, `constraints`, `decisions`, `concerns`, `next` proposing `ew-prototype`, `ew-brainstorm`, or `ew-plan` only when justified).
5. Seal or transition the Run per `evolve skill workflows show session-run` (`--gate terminology-aligned --gate branches-walked`). Stage reusable architectural constraints in `report.md` frontmatter or `evolve kb add --kind episodic`; defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete`.
