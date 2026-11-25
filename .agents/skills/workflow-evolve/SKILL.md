---
name: workflow-evolve
disable-model-invocation: true
description: "Agent-driven Skill evolution: collect incremental signals from host run histories, memory, and wiki; propose a Skill modify/create strategy; apply and validate."
argument-hint: "[--session <id>] [--skill <id>] [--host codex|claude|agy|all] [--since <7d>] [--query <text>]"
session-mode: none
version: 0.6.0
---

# Workflow Evolution

Run this Skill on explicit user `/workflow-evolve` invocation. The **Agent directly executes** the evolution loop using local CLI evidence tools rather than wrapping execution in external runner loops.

## Workflow

### 1. Collect & Search Historical Signals

Start with existing candidate and receipt evidence; do not repeat model extraction by default.

```text
evolve kb closeouts [<session-id>]
evolve kb candidates show <receipt-id> --project <project-id>
evolve kb reminders
```

Use the exact job's receipt/project and existing classification. Reuse routed evidence where adequate. Only for unresolved attribution, gather missing deterministic evidence:

```text
evolve history collect [--host codex|claude|agy|all] [--session <id>]
evolve history search "<query>" [--skill <id>] [--signals-only] [--format compact]
evolve history show <session-id> --anchor <anchor-id> --include-tools
evolve kb search "<topic>" --kind episodic [--session <id>]
evolve session review <session-id> --no-llm
```

Opt into `history collect --llm` or model session review only when deterministic/candidate evidence cannot resolve the question. Inspect source anchors, not unverified summaries. Do not harvest running Sessions or perform batch recovery in this Skill. *Done when*: the responsible Skill/version or deterministic issue is grounded in concrete evidence.
### 2. Formulate Evolution Strategy & Obtain Confirmation

Before editing any file, run **Four-Way Triage** on every collected signal:

| Attribution | Route | Action |
| --- | --- | --- |
| Fixed syntactic pattern, banned API, schema check, or missing lint/test/hook | **Deterministic check** | Fix in repo code/lint/test/CI — do not add Skill prose |
| Project-wide invariant, domain fact, or stale/conflicting Wiki knowledge | **Wiki update (`ew-knowledge`)** | Stage via `evolve kb propose` → human-approved `evolve kb apply` |
| Defect, redundancy, or bloat in an existing Skill (`skills/<id>/` or `skills/ew-orchestrate/steps/<id>/`) | **Skill modification** | `patch` (targeted edit to `SKILL.md` / `references/*.md`), `consolidate` (merge bloated rules/learnings), or `retire` (prune stale/no-op rules) |
| Reusable new workflow (`>= 3` stable steps, distinct trigger, or new contract/outputs) | **Skill creation** | Scaffold a new package under `skills/<new-id>/` following `evolve skill workflows show ew-skill-design` |

Present a concise **Self-Evolution Strategy** to the user and obtain explicit approval before editing:
- **Evidence & attribution**: Exact `session_id` / `anchor` / `memory_id` / `run_id` citations.
- **Target & operation**: Target Skill ID/path and operation (`patch` | `consolidate` | `retire` | `create`), plus any deferred Wiki or code-check items.
- **File-level plan & verification**: Exact files to modify/create and the validation/regression checks to run.
- *Done when*: The user explicitly approves the proposed strategy and target Skill scope.

### 3. Execute Skill Modification or Creation

Apply the approved strategy directly in the workspace while preserving package craft (`evolve skill workflows show ew-skill-design`):

- **Targeted edits over blind appending**: Edit the relevant section in `SKILL.md` or branch-specific `references/*.md` (for example `ew-orchestrate/steps/<id>/references/workflow.md` or `ew-odyssey/references/odyssey-<mode>.md`) instead of stacking comment blocks at the top/bottom of `SKILL.md`.
- **Skill creation (`skills/<new-id>/`)**:
  - Choose `disable-model-invocation: true` (user-only `/skill`, zero context load) vs model-invoked (`description` with crisp branch triggers).
  - Choose `session-mode: none` (standalone) vs `session-mode: run` (with `contract_version: 2.1`, `consumes`, `produces`, `gates`).
  - Keep `SKILL.md` focused on frontmatter, routing table, core invariants, and ordered steps with checkable `Done when` criteria; move branch details into `references/`.
- **Positive framing & pruning**: State target actions positively, pair unavoidable hard guardrails with their positive target, delete no-ops/sediment, and bump frontmatter `version` (`patch` for fixes, `minor` for consolidations/new packages).
- *Done when*: Target Skill files are edited or scaffolded cleanly with valid relative links and updated `version`.

### 4. Validate, Verify & Close the Loop

1. **Static package validation** (after committing or inside a commit-bound check):
   - Run `evolve skill validate <id>` (or `evolve skill validate --all`) to verify strict YAML, `name` parity, `required_links` & `#heading` fragments, `session-mode` vs `contract`, `disable-model-invocation` vs `agents/openai.yaml` parity, and balanced learning markers.
2. **Behavioral verification**:
   - Verify the fix/new workflow with targeted regression checks or an isolated verification subagent against concrete failing & passing scenarios.
3. **Close the loop**:
   - Record the verified evolution via `evolve kb add "..." --kind episodic --type verified_fix --evidence <anchor>`.
   - Acknowledge handled reminders (`evolve kb reminders ack <id>` (only the exact handled reminder; never acknowledge all by default)) and sync evolved or new project Skills to host directories (`evolve skill sync [--scope project|user] [--host codex|claude|agy|all]`).
- *Done when*: Static validation and verification checks pass, and verification state + any remaining follow-ups are recorded.

## Optional Offline Replay Harness

When automated multi-round Git-worktree replay is explicitly requested, the legacy runner pipeline remains available in [auto-loop.md](references/auto-loop.md), [history-review.md](references/history-review.md), and [agy-gate.md](references/agy-gate.md).
