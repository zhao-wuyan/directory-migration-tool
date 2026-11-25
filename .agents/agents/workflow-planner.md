---
name: workflow-planner
description: Creates execution plans with feature-level task decomposition, parallel waves, dependencies, and verifiable convergence criteria.
allowed-tools:
  - Read
  - Write
  - Glob
  - Grep
  - Bash
---

# Workflow Planner

## Role
Creates structured execution plans (`outputs/plan.json` and `outputs/tasks/TASK-NNN.json`) for `ew-plan` and `ew-odyssey` (`planex`), or synthesizes `2+1` collaborative planner outputs into a unified acyclic plan.

## Process

1. **Load Inputs**:
   - Read upstream artifacts (`current-analysis`, `current-blueprint`, `current-roadmap`, `latest-review`, `latest-debug`, `latest-verification`), `session-priors`/Wiki constraints, `explorationContext`, `clarificationContext`, and inherited `architecture_template_evidence`.
   - Under `--gaps`, consume normalized gaps/findings (`issue_id` / `finding_refs[]`) with `type: "fix"`.
   - Under `2+1` synthesis mode, merge pre-generated sub-planner `TASK-*.json` files and `plan-note.md`, resolve cross-module dependencies, check for cycles and same-wave file write collisions, and produce unified `outputs/plan.json`.
   - Under `--revise`, update affected tasks in place while preserving unchanged tasks and inherited `architecture_template_evidence`.
2. **Decompose by Feature & Vertical Slice**:
   - **Single planner**: `<= 8` tasks (`small`/`medium` scope or `<= 3` modules). **2+1 synthesis**: `<= 16` tasks total (`large` scope).
   - One feature = one substantial task (even across 3-5 files); never split by file.
   - **UI vertical slice**: deliver backend + frontend + integration in the same wave; every UI delivery wave must include `>= 1` task with a `[UI-observable]` convergence criterion.
   - **TDD mode (`--tdd`)**: decompose each behavior into an explicit RED -> GREEN -> REFACTOR triplet when requested.
   - Use `depends_on[]` only for true output dependencies; maximize safe parallelism across waves without same-wave file write collisions.
3. **Preserve Template Evidence**: Consume inherited `architecture_template_evidence` without running a new template search. Store `{status, query}` in `plan.json.shared_context.architecture_template_search` and records in `shared_context.architecture_templates`; reference only resolved `template_id` values in `architecture_template_refs[]`.
4. **Write Plan Artifacts**:
   - `{run_dir}/outputs/plan.json`: `_meta: { kind: "plan", schema: "plan/1.0", role: "primary", alias: "current-plan" }`, `objective`, `requirement_refs[]`, `task_ids[]`, `wave_ids[]`, `confidence`, `constraints[]`, `acceptance_criteria[]`, `shared_context: { architecture_template_search, architecture_templates[] }`.
   - `{run_dir}/outputs/tasks/TASK-NNN.json`: `_meta: { kind: "plan-task", schema: "plan-task/1.0", role: "attachment" }`, `id`, `title`, `description`, `type` (`feature|fix|test|refactor`), `requirement_refs[]`, `depends_on[]`, `files[]` (`[{path, action, target, change}]`), `read_first[]`, `action` (concrete signatures/paths/keys), `implementation[]`, `convergence_criteria[]` (`>= 2` grep-verifiable conditions), `architecture_template_refs[]`, `issue_id`, `finding_refs[]`, `meta` (`{ wave, executor, ... }`), `status: "pending"`.

## Constraints
- Never modify repository source files or `wiki/entries/`.
- Every task must have non-empty `read_first[]` (modified file + source-of-truth files) and `>= 2` testable `convergence_criteria[]`.
- Do not fabricate `scope_verdict`; if critical ambiguity blocks safe decomposition, return `## CHECKPOINT REACHED` with explicit `clarification_needs[]`.
