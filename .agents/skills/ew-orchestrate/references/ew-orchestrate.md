# Evolve chain catalog

Match the intent to the smallest Evolve chain that can produce and verify the requested result. Confirm the boundary, completion criteria, and required handoffs before opening the Session. The execution guide owns dispatch and recovery.

## Exact-Match Short-Circuits

- **`status` / `状态` / `dashboard`** -> **Read-only short-circuit (no chain creation, routes directly to `END`)**: run `evolve session get --session <id>` (or `evolve session list --status open`), present the result, and stop for user input without inferring or launching a chain.
- **`continue` / `next` / `go` / `继续` / `下一步`** -> Run `evolve session list --status open`:
  - **1 compatible open Session** (an open Session whose `objective`, `boundary_contract`, and pending/active chain cover the request without topic mismatch) -> resume it (`S_CONTINUE` / `nextAction`).
  - **>1 open Sessions** -> ask the user which `session_id` to continue (`S_FALLBACK`); never select by historical memory similarity.
  - **0 open Sessions (Cold-start fallback)**: check `.git` (`HEAD`), `.evolve/`, `wiki/`, and `.codegraph/`:
    - If not yet a Git repository with a committed `HEAD` (**pre-Git bootstrap**), run `git init`, `evolve skill install --scope project --yes`, and commit Skill packages to `HEAD` before opening an `ew-init` Session;
    - If Git exists but `.evolve/` / `wiki/` / `.codegraph/` are uninitialized, route to `ew-init`;
    - If a bound `current-roadmap` / `current-blueprint` exists, propose the first pending Session/spec scope;
    - Otherwise route bounded work via `ew-companion`, complex multi-module work via `ew-analyze` / `ew-plan`, or ask what to build (`S_FALLBACK`).

## Routing Preference & Scope Guard

- **Converge Session creation & reuse Topic Sessions**: do NOT open a new Session for read-only diagnostics, probe scripts, sensitivity calculations, or trivial 1-turn micro-edits that need no durable Run audit (`END` instead). When durable tracking IS warranted, check `evolve session list --status open` first and reuse a compatible open Topic Session (`session step add` + `run create`) rather than spawning a fresh Session per turn.
- **Prefer the lightest sufficient route**: default non-trivial, single-concern development, localized bug-fix, and deliverable-verification tasks to `ew-companion` rather than splitting into multi-agent sub-steps (`ew-analyze` -> `ew-plan` -> `ew-execute`).
- **File count is irrelevant**: a cohesive change across multiple files (e.g., updating callers, wiring an option, fixing a bug plus its tests) still belongs to `ew-companion` as long as no multi-phase architecture uncertainty, cross-package interface redesign, downstream typed artifact handoff, or formal decision gate (`--goal`) is needed.
- **Scope guard**: action keywords (`implement`, `add`, `fix`, `refactor`) identify semantic intent only and do NOT force heavy multi-step chains when `ew-companion` suffices. Escalate to `ew-analyze` / `ew-plan` / `ew-execute` / `ew-verify` / `ew-review` only for genuine multi-module uncertainty (`>= 3` coupled modules or cross-package interfaces), `--goal` chains, or explicit user requests for those specialist stages.

## Semantic Intent -> Route Matrix

| Intent | Exact Route | Prerequisites & Thresholds |
| --- | --- | --- |
| Read-only Q&A, factual lookup, status check, zero-mutation diagnostics/probes/calculations, trivial 1-turn micro-edits (no durable audit needed), or standalone task in a non-Evolve project | `END` (no Session) | Zero production/deliverable mutations (or trivial 1-turn tweak needing no Run tracking), or project has not opted into Evolve Session/Run orchestration |
| Non-trivial single-concern edit, feature, localized fix, refactor, or deliverable verification needing durable Run tracking (file count irrelevant) | `ew-companion` | Default in Evolve-managed workspace (or explicit `/ew-orchestrate`) when production/deliverable changes warrant Run tracking without downstream typed artifacts, formal gates, or `>= 3`-module split; reuse matching open Topic Session first |
| Initialize or onboard project (greenfield / brownfield / legacy; optional `--visual`) | `ew-init` → `ew-roadmap` or `ew-blueprint` | Skip interactive Q&A when `--from` supplies `upstream-context`; `--visual` adds `visual-report.html` |
| Explore open alternatives or compare approaches | `ew-brainstorm` | Proposes continuation only when durable artifacts are needed |
| Pressure-test an idea/proposal against codebase reality | `ew-grill` | Grounded Socratic branches; runs before `ew-brainstorm` / `ew-plan` |
| Settle a state-model, logic, or UI-layout question with a runnable throwaway prototype | `ew-prototype` | Standalone model-invoked Skill (`session-mode: none`) when paper debate cannot settle a design; feeds validated decisions back to `ew-grill` / `ew-brainstorm` / `ew-plan` |
| Formal multi-phase spec package (Brief -> PRD -> Arch -> Epics) | `ew-blueprint` | Pure spec outputs; feeds `ew-roadmap` or `ew-plan` |
| Decompose multi-release requirements into a Session DAG | `ew-roadmap` | Only split >1 Session when hard runtime coupling holds |
| Explicitly investigate codebase or analyze existing plan (`-q`, macro, `--gaps`) | `ew-analyze` | Pass `-q` for fast decision extraction; `--gaps` for backlog gaps |
| Explicitly plan or revise an implementation (`--gaps`, `--tdd`, `--revise`) | `ew-plan` | Produces `current-plan`; use `--gaps` after failed review/verify/debug/test |
| Execute an existing confirmed `current-plan` | `ew-execute` | Requires bound `current-plan` (if missing, run `ew-plan` first or `ew-companion`) |
| Broad / multi-module implementation or architectural refactor (`>= 3` coupled modules, cross-package, or `--goal` / formal gates) | `ew-analyze -q` (if impact unclear) → `ew-plan` → `ew-execute` → `ew-verify` → `ew-review` | Omit `ew-analyze -q` when scope and patterns are already settled; ordinary single-concern multi-file edits use `ew-companion` instead |
| Independently verify requirement & wiring coverage | `ew-verify` | **Requires bound `current-plan` + `current-execution`** (`required: true`) |
| Run conversational UAT, `--smoke`, or `--frontend-verify` browser check | `ew-test` | Behavioral acceptance + UAT; consumes verification, execution, or review |
| Generate and converge layered automated tests (`L0->L3`) | `ew-auto-test` | Suites missing or low coverage; distinct from UAT/browser acceptance |
| Audit diff, file/dir, or `current-execution` across 4 quality dimensions (and UI visual truth / `--visual`) | `ew-review` | Works standalone (`HEAD`/`staged`/path) OR inside a Session after `ew-execute` |
| Diagnose broken behavior inside an orchestration chain (`<= 2` modules) | `ew-debug` → `ew-plan --gaps` → `ew-execute` → `ew-verify` | Single bounded root-cause isolation (`latest-debug` / `latest-fix-directions`); `>= 3` modules escalate via `S_DECOMPOSE` (missing-seam blockers recommend `ew-analyze` or explicit `/ew-odyssey --mode arch`) |
| Heavyweight multi-iteration architecture, audit, bug-hunt, or security campaign | `ew-odyssey --mode <arch\|debug\|improve\|planex\|review\|security\|defensive\|ui>` (`ew-odyssey-<mode>`) | **Explicit user `/ew-odyssey` invocation only** (`disable-model-invocation: true`); multi-iteration loop across subsystems/campaigns |
| Post-execution four-lens retrospective | `ew-retrospective` | Consumes sealed execution/verification/review artifacts |
| UI design, rebuild, single-pass audit, or finish polish | `ew-impeccable` | Auto-routable for UI work. Terminal polish/refinement commands finish directly; `shape` outputs a conversational design brief feeding `ew-plan` → `ew-execute` → `ew-test --frontend-verify` (multi-iteration UI campaigns require explicit `/ew-odyssey --mode ui`) |
| Explore and score competing hypotheses/paths in parallel | `ew-swarm` | **Explicit user `/ew-swarm` invocation only** (`disable-model-invocation: true`); multi-ant ACO exploration |
| Analyze & fix a tracked issue (from user's tracker ID/context) | `ew-analyze --gaps` → `ew-plan --gaps` → `ew-execute` → `ew-verify` | Keep issue reference in tasks; update tracker only with user authorization |
| Knowledge Gate lookup inside an active Run | `evolve search "<query>" --mode context` / `evolve kb get <id>` with CLI `--reads` | Keep Run evidence bound inside `{run_dir}`; do not spawn `ew-knowledge` |
| Post-seal closeout harvesting, Wiki publish/audit, memory sync, legacy `.workflow/` import | `ew-knowledge` (`evolve skill workflows show ew-knowledge`) | Run after `session complete` or on standalone knowledge requests |
| Modify Skill packages / Harness behavior (`skills/<id>/SKILL.md`) | `workflow-evolve` | **Explicit user `/workflow-evolve` invocation only** (`disable-model-invocation: true`); never auto-invoked during task execution |
| Rebuild code index / install Skills / Git worktree | `evolve code build` / `evolve skill install` / `git worktree` | Direct CLI or authorized repo operations |

## Validation & Continuation Rules

1. Follow [Run loop and recovery](orchestrator-run-loop.md) for `evolve skill validate <command>` and Git `HEAD` preflight before building chains (`ew-knowledge` runs with `session-mode: none`).
2. Pass concrete step arguments cleanly (Runtime supplies `session_id` in every Run packet and expands `{intent}` / positional `{session}` in `run.task.args`; do not invent `--session` flags on step skills); use target scope, issue, or Session IDs instead of legacy milestone/phase numbering.
3. Under `--goal <goal>`, map `<goal>` to `Session.objective` and derive `--definition-of-done`, `--boundary-contract`, and `--goals` JSON on `evolve session create` (see [planning.md](planning.md) and [auto-policy.md](auto-policy.md)), carrying `--goal-ref` and `--decision-only --decision-ref <gate>` checkpoints where coordinator verdicts are required.
4. At Run/phase boundaries (never mid-phase), apply the `Continue -> /clear -> /handoff -> Subagent -> /compact` context hygiene tree in [Run loop and recovery](orchestrator-run-loop.md) to preserve primary reasoning while keeping the coordinator window clean.
