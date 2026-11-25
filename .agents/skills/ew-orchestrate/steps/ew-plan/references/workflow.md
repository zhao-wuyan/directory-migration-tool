# Workflow: Plan

5-step pipeline: Context Collection -> Clarification -> Planning -> Plan Checking & Collision -> Confirmation.

Produces `outputs/plan.json` + `outputs/tasks/TASK-{NNN}.json` + `outputs/waves.json`, `outputs/dependency-graph.json`, `outputs/collision-report.json` (plus `outputs/plan-check.json` when checked).

## Modes & Topology

```
Create (default): Step 1 -> Step 2 -> Step 3 -> Step 4 -> Step 4.5 -> Step 5
--tdd:            Step 1 -> Step 2 -> Step 3 (TDD chain) -> Step 4 -> Step 4.5 -> Step 5
--revise:         Load existing plan -> apply targeted diff -> Step 4 -> Step 4.5 -> Confirm
--check <dir>:    Step 4 only (read-only)
```

- **Scope selection**: `--from <alias>` targets bound upstream context (`current-analysis`, `current-brainstorm`, `current-blueprint`, or `current-roadmap` `sessions[]`); `--gaps` plans issue/review/verification/test/debug gap fixes (skipping Step 1.4 exploration); `--dir <plan-dir>` targets an explicit plan directory for `--revise` / `--check`; otherwise use latest `ew-analyze` findings or fail `E001`.
- **Agent topology**: consume `current-analysis.scope_verdict` when present (`small` / `medium` -> 1 planner subagent, `<= 8` tasks; `large` -> `2+1` split: 2 parallel planners `<= 8` tasks each + 1 synthesis subagent, `<= 16` tasks total; `unknown` -> refuse blind decomposition, fail/pause with `E001` / `clarification_needs[]` and require user confirmation or deeper `ew-analyze` first, even under `-y`). When `scope_verdict` is absent, route by module count (`<= 3` vs `> 3` modules). Always join/await all spawned subagents before advancing.

## Blocking Step Gates (Create mode)

| Gate | Requirement to advance |
|------|------------------------|
| `context-collected` (1 -> 2) | Upstream context/exploration and codebase/Wiki rules loaded (`E001` if zero context or unresolved `scope_verdict: unknown`) |
| `plan-generated` (3 -> 4) | Planner subagent produced `outputs/plan.json` + `outputs/tasks/TASK-*.json` (inline planning forbidden) |
| `plan-checked` (4 -> 5) | Plan checker passed (zero unresolved criticals), boundary grill + 5-dim confidence + >=1 pressure pass completed, UI waves cover `[UI-observable]` |
| `plan-confirmed` (5 -> wrap-up) | User confirmed (`-y` auto-confirms) before registering/reporting completion |

---

## Process

### Step 1: Context Collection

1. **Upstream decisions** (via injected aliases):
   - `current-analysis`: treat `decisions[class=locked]` as hard constraints, `[free]` as implementer discretion, `[deferred]` as excluded; map `implementation_scope` 1:1 to tasks when present and non-empty (skipping parallel exploration), otherwise derive scope from `recommendations[]`, `decisions[]`, and `scope_verdict`.
   - `current-brainstorm`: read bounded options, constraints, and `references[]` when present.
   - `latest-review`: filter actionable findings (`status == "open"`, prioritizing `severity` in `{critical, high}` plus `medium`/`low` when targeted) and normalize each via `id`, `file`, `line`, `severity`, and `suggestion`, clustering shared root causes within normal task guardrails.
   - `current-blueprint` / optional `current-roadmap`: extract seed requirements and `sessions[]` scope boundaries (replacing legacy milestone numbering).
   - `architecture_template_evidence`: inherit read-only from `current-analysis`, `current-blueprint`/`current-brainstorm` `references[]`, or upstream research findings — never re-search.
2. **Project knowledge & codebase**: complete the Run Knowledge Gate (`evolve skill workflows show session-run`, CLI `--task plan` when using `--reads`) using bound `session-priors` or Wiki/Memory lookup, then run `evolve code search "<keywords>"` after the Knowledge Gate.
3. **Design tokens (UI)**: load `design-ref/MASTER.md` if present and include token/layout refs in UI tasks' `read_first[]`.
4. **Parallel exploration** (skip if `--gaps` or non-empty upstream `implementation_scope` exists): launch 1-4 exploration subagents (`cli-explore-agent` / `cli_explore_agent` via `invoke_subagent` or host subagent, joined before continuing; fall back to inline instructions when absent; OMP hosts use native `task`/eval `agent()` with `scout`, batching independent slices through `context` + `tasks[]`) covering architecture, implementation, integration, and risk -> write `evidence/exploration-{angle}.json`. Optionally run `evolve agent exec "<patterns/dependencies/collisions>" --to codex|omp --mode analysis --session <session> --run <run>` (or `--to agy --mode analysis` outside Run binding; OMP is a one-shot restricted worker).
5. **Gap mode (`--gaps`)**: merge all bound gap sources by priority:
   1. Analyzed/tracked issues (`current-analysis` `mode: "gaps"` `issues[]` with `status: analyzed|diagnosed|registered|open` or authorized external tracker -> mark `planned` in `plan.json` / tasks)
   2. `latest-review` (`findings[]` with `status == "open"`)
   3. `latest-verification` (`outputs/verification.json` `gaps[]` and `constraint_violations[]`)
   4. `latest-auto-test` (`outputs/auto-test-report.json` `app_defects[]`)
   5. `latest-test` (`outputs/test-results.json` `gaps[]` and `outputs/issue-candidates.json`)
   6. `uat-log` (`outputs/uat.md` failed or uncertain UAT gaps)
   7. `latest-debug` (`outputs/diagnosis.json` + attached `outputs/fix-directions.json`)
   Deduplicate by ID then `(root_cause, file)`, cluster to respect task limits, and error `E001` if empty across all sources.

### Step 2: Clarification (skip if `-y`, except unresolved `scope_verdict: unknown`)

Deduplicate blocking/important `clarification_needs[]`, clarify via structured prompt (`ask_question` or host equivalent, up to 3 rounds x 4 questions), and store `clarificationContext`.

### Step 3: Planning

Launch a planner subagent (`workflow-planner` / `workflow_planner`, or `2+1` topology using 2 parallel `workflow-collab-planner` + 1 synthesis `workflow-planner`; join all subagents before Step 4; fall back to inline role instructions when named agents are absent). **Inline planning in the main flow is forbidden.** Pass upstream context, `session-priors`/Wiki constraints, `explorationContext`, `clarificationContext`, and inherited `architecture_template_evidence`.

- **Guardrails**: `small` (`<= 4` tasks), `medium` (`<= 8` tasks), `large` (`> 3` modules / `scope_verdict: large`, `<= 16` tasks); 1 feature = 1 substantial task (15-60m, batch `< 5m` trivial edits); `depends_on[]` only for real output dependencies; vertical slices cutting end-to-end through every layer (with `>= 1` `[UI-observable]` criterion per UI delivery wave) and using project domain vocabulary / ADRs when present. Do prefactoring tasks first when they make downstream slices clean.
- **Wide Refactor exception (`Expand -> Batch Migrate -> Contract`)**: when a single mechanical change (e.g., column rename, shared symbol retype, pervasive contract change) fans across so many call sites that no vertical slice can land green alone, sequence it in 3 explicit waves instead of forcing vertical slices:
  1. **Expand**: add the new form beside the old so existing callers stay green.
  2. **Batch Migrate** (multiple non-colliding `type: refactor` tasks sized by package/directory blast radius, each `depends_on` Expand): migrate call sites in green batches (or onto a shared integration branch converging on a single integrate-and-verify task when batches cannot stay green independently).
  3. **Contract** (`depends_on` every migrate batch): remove the old form once zero callers remain.
- **Every `TASK-NNN.json` must follow the canonical `plan-task/1.0` schema** (`Output Skeleton` below), including:
  1. `read_first[]`: target modified files + source-of-truth/pattern files.
  2. `convergence_criteria[]`: concrete grep/test/CLI verifiable assertions (zero subjective words like "looks correct").
  3. `action` & `implementation`: exact signatures, keys, values, import paths (never "align X with Y").
  4. `architecture_template_refs[]`: only resolved `template_id` values present in `plan.json.shared_context.architecture_templates`.
- **`--gaps` tasks**: `type: "fix"`, retaining `issue_id` (record `status: "planned"` in `plan.json` / `TASK-NNN.json`; only sync to an external tracker with explicit user authorization) and `finding_refs[]`.

### Step 4: Plan Checking & Step 4.5: Collision Detection

1. Launch a mandatory **plan checker subagent** (`workflow-plan-checker` / `workflow_plan_checker`, joined before continuing; fall back to inline role instructions when absent) to verify requirement coverage, DAG acyclicity, grep-verifiable `convergence_criteria[]`, `read_first[]` completeness, concrete actions, same-wave write non-collision, UI coverage, and valid `architecture_template_evidence` (`kind=template`, `reference_only=true`, resolved IDs only, absent from knowledge consumption fields). Run up to 3 revision loops on criticals.
2. Score 5 dimensions (`requirements_coverage`, `task_quality`, `dependency_correctness`, `estimation_accuracy`, `collision_safety`) weighted by `completeness .30`, `specificity .25`, `structural_validity .20`, `user_validation .15`, `consistency .10`, plus >=1 mandatory **Pressure Pass** on the highest-complexity task.
3. Run the Boundary Grill protocol (`evolve skill workflows show boundary-grill`) if boundary/write conflicts arise, writing `boundary_grill[]` inside `outputs/plan.json` (never raw Markdown inside `.json`).
4. Block before Step 4.5 if coverage `< 40%`, missing `read_first`/`convergence_criteria`, no pressure pass, cycle, or invalid template ref (unless user confirms into `residual_risks`).
5. **Collision Detection (Step 4.5, skip if `standalone`)**: here `standalone` means no prior sealed `plan` exists in the current Session or sibling `current-roadmap` Sessions. Otherwise intersect new `task.files[]` (write targets only, not `read_first[]`) against sibling sealed plans -> write `outputs/collision-report.json`.

### Step 5: Confirmation

Present objective, task/wave counts, key dependencies, confidence %, weakest dimension, and pressure pass result. Prompt via structured choice (`Execute now` / `Re-verify stricter` / `View` / `Modify`; `-y` auto-confirms).

---

## Output Skeleton

- **`outputs/plan.json`**: `{ _meta: { kind: "plan", schema: "plan/1.0", role: "primary", alias: "current-plan" }, objective, requirement_refs[], task_ids[], wave_ids[], confidence, constraints[], acceptance_criteria[], shared_context: { architecture_template_search, architecture_templates[] } }` (also admits `plan/1.0` `pi-markdown` approved variant).
- **`outputs/tasks/TASK-NNN.json`** (canonical `plan-task/1.0` schema used by Step 3, `prepare.md`, and `tdd.md`): `{ _meta: { kind: "plan-task", schema: "plan-task/1.0", role: "attachment" }, id, title, description, type: "feature|fix|test|refactor", requirement_refs[], depends_on[], files[], read_first[], action, implementation, convergence_criteria[], architecture_template_refs[], issue_id, finding_refs[], meta, status: "pending" }`.
- **`outputs/waves.json`**, **`outputs/dependency-graph.json`**, **`outputs/collision-report.json`**, **`outputs/plan-check.json`**.

### Wrap-up

Write `outputs/plan.json` and `TASK-NNN.json` / `waves.json` / `dependency-graph.json` / `collision-report.json` / `plan-check.json` (all with matching `_meta`). Write `report.md` with frontmatter (`verdict: ready|ready_with_concerns|blocked|failed`, `summary`, `constraints`, `decisions`, `concerns`, `next: [{ command: ew-execute, reason: plan ready, needs: [current-plan] }]`) and sections `Summary`, `Conclusion/Verdict`, `Discussion/Retrospective`, `Artifacts`, `Handoff/Next`. Reference `current-plan` via `{{aref:...}}` rather than copying JSON. Seal or transition the Run per `evolve skill workflows show session-run` with exit gates `--gate context-collected --gate plan-generated --gate plan-checked --gate plan-confirmed`. Keep `report.md` frontmatter staging (`constraints`/`decisions`) and defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete` (never run `kb harvest` between chain steps).

---

## Revise (`--revise`) & Check (`--check`)

- **`--revise`**: locate `--dir <plan-dir>` or bound `current-plan` (`E004` if none), parse instructions (or prompt via structured choice), run planner subagent (joined) to update affected tasks in place while preserving upstream `architecture_template_evidence`, re-run Step 4 + 4.5, confirm, and write updated `outputs/plan.json` + tasks before sealing.
- **`--check <plan-dir>`**: read-only inspection (`E005` if missing) -> run checker, dependency, collision, and `current-roadmap`/Session boundary alignment checks -> print pass/warn/fail summary without editing repository or source plan files (when run inside a bound Run, emit `outputs/plan-check.json` with `mode: "check"` and an unmodified read-only snapshot copy of the inspected `outputs/plan.json` so contract schema holds before sealing).

## Error Codes

| Code | Condition | Recovery |
|------|-----------|----------|
| `E001` | No usable planning context | Supply scope, topic, or upstream artifact first |
| `E004` | No plan to revise | Specify `--dir` or create a plan first |
| `E005` | Plan directory missing (`--check`) | Verify path / `--dir` |
| `E006` | Invalid planner JSON | Retry once with schema reminder, then abort |
| `W001` | Exploration subagent failed | Retry once; mark missing angle and verify in code before confirming |
| `W002` | Plan checker exceeded 3 rounds | Surface remaining issues in `outputs/plan-check.json` for user decision |
