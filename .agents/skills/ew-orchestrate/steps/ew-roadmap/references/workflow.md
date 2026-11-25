# Workflow: ew-roadmap

Session DAG decomposition: `Parse & Context -> Codebase Mapping -> Decompose / Revise / Review -> Validate DAG -> Wrap-up`.

## Step 1: Load Context & Upstream

1. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) and inspect existing `evolve session list` state so already-completed work is excluded. Under `-c`, inspect prior Run evidence/partial `outputs/roadmap.json` via `evolve run get` (or bound `current-roadmap`) before continuing.
2. Read bound `current-blueprint` / `current-brainstorm` / `upstream-context` when provided (`read_artifact` / `evolve run read artifact`).
3. If architectural boundaries are unclear, run a read-only exploration subagent (`cli-explore-agent` / `cli_explore_agent`, joined before Step 2) and optional `search_web` for external API constraints -> save summary to `{run_dir}/evidence/roadmap-context.json`. Dispatch `workflow-roadmapper` (`workflow_roadmapper`, or inline role instructions when absent; joined before Step 3) when delegating Session DAG decomposition/review.

## Step 2: Execute Selected Mode

- **Create mode**:
  1. Parse requirement into goals, hard constraints, and acceptance criteria.
  2. Group requirements into cohesive Sessions using the **default 1-session / 3-hard-condition split rule** (`>= 5` estimated tasks per Session). Map every active requirement to one owning Session and assign `depends_on` edges.
  3. Apply `-m` mode (`progressive`: prompt via `ask_question`/host equivalent to confirm Session splits and criteria; `direct`: finalize directly without step-by-step confirmation; `auto`: `direct` when boundaries are clear, else `progressive`; `-y` skips prompts).
  4. Re-audit splits if `> 3` Sessions exist and merge unjustified splits.
- **Revise mode (`--revise`)**:
  1. Load `current-roadmap` (`E004` if missing). Check which Sessions are already sealed/completed; refuse (`E005`) or confirm adjustment if a change alters completed scope.
  2. Update pending Sessions and edges cleanly while preserving sealed entries.
- **Review mode (`--review`)**:
  1. Inspect `current-roadmap` read-only for cycles (`E003`), orphaned requirements, undersized Sessions, and blocked dependencies; inside an active Run, copy the read-only roadmap into `{run_dir}/outputs/roadmap.json` with an appended `health_review` summary (and matching `_meta`) so the Run seals cleanly without modifying the original roadmap source.

## Step 3: Write Outputs & Seal (`dag-valid`, `sessions-registered`)

1. Write `{run_dir}/outputs/roadmap.json` (`_meta: { kind: "roadmap", schema: "roadmap/1.0", role: "primary", alias: "current-roadmap" }`, `sessions[]` with `id`, `title`, `intent`, `scope`, `requirement_refs[]`, `acceptance_criteria[]`, `depends_on[]`, `status: ready|pending|completed`) and `{run_dir}/outputs/roadmap.md` (including `## Roadmap Decisions` and dependency graph).
2. Verify acyclicity (topological sort), zero dangling `depends_on`, complete requirement mapping, and clean alignment with `evolve session list` (do NOT call `evolve session create` here; the orchestrator opens downstream execution Sessions from `sessions[]`).
3. Write `{run_dir}/report.md` (`verdict: ready|ready_with_concerns|blocked|failed`; `next` proposing the orchestrator open the first `ready` Session in `sessions[]` via `ew-analyze` or `ew-plan`) and seal or transition the Run per `evolve skill workflows show session-run` (`--gate dag-valid --gate sessions-registered`). Stage reusable architecture learnings in `report.md` frontmatter or `evolve kb add --kind episodic`; defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete`.
