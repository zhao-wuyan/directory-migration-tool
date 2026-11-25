# Evolve Session Execution and Recovery

Use the bundled `evolve` CLI (in this source checkout, run `node dist/workflow-cli.js`). Session/Run state, `boundary_contract`, `goals[]`, `amendments[]`, requests, and sealed artifact bytes share `.evolve/workflow.db`; working outputs live under the `run_dir` returned by the controller.

## Start and Dispatch

1. **Install & validate**: Run `evolve skill install` (`--scope project --yes` for project setup), **commit Skill packages to Git `HEAD`** so Runs bind cleanly (for **pre-Git bootstrap** in a directory without `.git` or `HEAD`, run `git init`, `evolve skill install --scope project --yes`, and create the initial commit with installed Skill packages before `evolve session create`), list docs via `evolve skill workflows`, and validate contracts via `evolve skill validate <command>`.
2. **Open Session & chain**:
   - Argument-free chain: `evolve session create "<objective>" --id <session> [--definition-of-done "<text>"] [--boundary-contract '<json>'] [--goals '<json>'] [--auto] [--goal-mode] --chain "ew-analyze,ew-plan,ew-execute,ew-verify"`.
   - Step with args / retries / gates: `evolve session step add --session <session> --step-id <id> --command <command> [--arg "<val>" ...] [--retry-max <n>] [--goal-ref <id>] [--stage <name>] [--decision-ref <id>] [--after-step <id>]` (pass `-`-prefixed values as `--arg=VALUE`).
   - Standalone decision gate (`decision-only`): add `--decision-only --decision-ref <gate-id>` instead of `--command`.
3. **Dispatch Run**: `evolve run create --session <session>` — passes `session_id`, `run_id`, `run_dir`, `skill_entry_path`, `skill_ref`, `knowledge_profile` (`task`, `categories`, `wiki`, `memory_types` parsed from Skill frontmatter), bound `upstream` (primary + sibling attachments/evidence), `boundary_contract`, `goals`, and `gates` (Runtime expands `{intent}` and `{session}` in `task.args` automatically; do not pass undefined `--session` flags to step commands; `run_dir/outputs` is created lazily only when files are actually written or required by the step contract).
4. **Concurrency & idempotency (optional in single-coordinator mode)**: `--actor` (`--participant` is optional and must match `--actor` if passed), `--expected-orchestration-revision`, and `--expected-run-revision` may be omitted in standard sequential single-coordinator flows. In multi-worker, retry, or parallel races, pass them as optimistic concurrency guards advancing from each receipt (keep the same `--request-id` when retrying an uncertain call, and re-read `evolve session get` on `REVISION_CONFLICT`).

## Read and Execute

- **Skill reads (optional)**: `run complete` does not require `evolve run read skill`. Hosts may read `skill_entry_path` (and its package-relative `.md` files) directly with native file tools, or use `evolve run read skill <run> --session <session>` (and `evolve run read skill <run> <destination> --from <source> --session <session>` for package-relative `.md` links resolving inside the active Skill package root; note `steps/<name>` is an independent package root, so read cross-package docs via `evolve skill workflows show <id>`) when pinned-commit isolation or audit tracking is specifically desired.
- **Artifacts**: Read bound inputs via `evolve run read artifact <run> <alias-or-id> --session <session>` (`--scope <JSON>` for `session-priors`). JSON artifacts MUST carry `_meta: { kind, schema, role, alias? }` and Markdown artifacts matching YAML frontmatter; NEVER append raw Markdown into `.json` files.
- **Knowledge Gate inside a Run** (choose one receipt source per Run; for simple deliverable fixes in `ew-companion`, `knowledge-reads.json` is optional — see [companion-route.md](companion-route.md)):
  - **Bound priors**: Open `session-priors` via `evolve run read artifact <run> session-priors --session <session> --scope '<json>'` ONLY when its recorded `lookup_scopes` covers the current scope.
  - **CLI lookup**: `evolve search "<topic>" --mode context --session <session> --reads "<run_dir>/knowledge-reads.json"` (returns slim single-line JSON — add `--format compact` for plain text or `--verbose` for full metadata — with short unique prefix IDs, unified `search_id` `q-N` + `idx`, inline `content` when `<= 300` chars or `snippet` + `truncated: true` when longer, and a `20`-item / `4000`-char `constraints` cap emitting `constraints_notice` on overflow; `--session` prioritizes working memories matching `search_profile.memory` types and refreshes stale `.evolve/index.db`, while `--memory-session` filters historical episodic records), then inspect governing or truncated entries via `evolve kb get <id|q-N:idx>... --resolved --reads "<run_dir>/knowledge-reads.json"` (or `--pick <n,...>`; reusing `--reads` auto-links `from_search_id`, while `--from-search <search-id|q-N>` overrides explicitly; code symbol IDs `<kind>:<8hex>` return source slices). For steps declaring `outputs/priors.json`, Runtime snapshots CLI-read Wiki entries cleanly at `run complete` too.
  - **In-flight memory recording**: When a Run confirms a non-obvious root cause, workaround, or recurring pitfall, record it immediately via `evolve kb add "<lesson>" --kind episodic --type <verified_fix|error|correction|work_fact> --scene <step> --session <session> --evidence <source-ref>` alongside `report.md` frontmatter staging (`constraints`/`decisions`). Do **not** run `evolve kb harvest` or invoke `ew-knowledge` closeout during an active Run or between chain steps (`ew-knowledge` closeout runs only after `evolve session complete`).

## Verify, Continue and Recover

- **Knowledge outcome before sealing**: If a knowledge item materially affected the Run, record its actual use on the existing CLI receipt: `evolve kb feedback <full-id> --reads "<run_dir>/knowledge-reads.json" --outcome <adopted|helpful|ineffective|not_applicable|conflict> --evidence <source-ref>`. Use the exact recorded content hash (`--content-hash` when multiple versions were read); verify outcomes before labeling helpful/ineffective. A hit is not consumption. For bound priors, only use the CLI feedback command when its matching exact read is in the existing receipt; otherwise preserve actual evidence in the Run report and its prior reuse receipt. Do not create a competing receipt or invent a read. Skip feedback when no material knowledge was consumed. Before seal, record meaningful findings with evidence, not just a test count; lightweight Runs need no report unless reusable evidence would otherwise be lost.

- **Seal Run & `report.md` Verdict Mapping**: `evolve run complete <run> --session <session> --summary "<observed result>" [--gate <id>...] [--verdict done|done_with_concerns] [--next]` (omit `--actor` / `--expected-orchestration-revision` / `--expected-run-revision` in single-coordinator sequential flows; pass `--next` to seal the current Run and immediately allocate the next pending Run in one CLI call). Run `evolve run check` first to preflight gates, outputs, and `report.md` `next` commands. Map `report.md` frontmatter `verdict` to Evolve lifecycle commands as follows:
  - `verdict: ready` -> `evolve run complete ... --verdict done`
  - `verdict: ready_with_concerns` (or `verdict: blocked` on an inspection/verification step such as `ew-review` / `ew-verify` / `ew-test` whose findings artifact is sealed for downstream `--gaps` or decision routing) -> `evolve run complete ... --verdict done_with_concerns` (downstream `consumes[]` slots expecting those findings must declare `accepts_negative_evidence: true`)
  - `verdict: blocked` (when exit gates/required outputs cannot be satisfied or human intervention is needed before completion) or `verdict: failed` (execution errored / retry needed) -> do **not** call `run complete`; use `evolve run transition <run> blocked|failed ... --reason "<why>"` (or `evolve run cancel`) so recovery/retry can proceed.
- **Follow `continuation.action` & `authority`**:
  - When `continuation.authority === "automatic"` (`--auto`), keep executing satisfiable actions (`next` / `decide` / `complete`) in the same turn rather than stopping between steps (unless hit by a user gate, drift/confidence halt, retry limit, or unrecoverable failure).
  - `execute` -> inspect/resume active Run via `evolve run get` + `evolve run check`.
  - `recover` -> handle `blocked` Run via `evolve run transition` (or `cancel`) before retrying.
  - `decide` -> `evolve run decide <decision-id> --session <session> --verdict proceed|fix|escalate [--confidence <0-100>] [--target-step <step-id>] [--evidence <ref>] --summary "<evidence>"` (pass `--actor` / `--expected-orchestration-revision` in concurrent races; `--confidence` is required in `--auto`/`--goal` coordinator decisions; `--target-step` reopens the target step through the gate when `verdict=fix`; after `verdict=escalate`, the decision stays active until user clarification/confirmation, then unblock via optional `session step add` + re-issuing `evolve run decide <decision-id> --verdict proceed|fix ...`).
  - `next` -> allocate next pending step via `evolve run create --session <session>` (or combine with the preceding step via `evolve run complete ... --next`; if `Retry limit exceeded`, stop retrying, escalate to the user, or `session step delete` + insert a new remediation step).
  - `complete` -> `evolve session complete --session <session>`, which atomically queues a frozen evidence receipt for candidate extraction. Inspect `evolve kb closeouts <session-id>` and follow `evolve skill workflows show ew-knowledge`: pending/running needs no duplicate harvest, failed needs targeted recovery, done needs `kb candidates show <receipt-id> --project <project-id>` with the job’s actual values. Review `evolve_reminder` / `evolve_signals` separately. Never auto-invoke `workflow-evolve` during task execution; only mention `/workflow-evolve` once at session closeout if `evolve_reminder` points to an actual Skill/harness defect rather than ordinary coding friction.
- **Crash / Stuck-Run Recovery**: Read `evolve session list --status open [--query <text>]`, `evolve session get --session <session>`, `evolve run get <run> --session <session>`, and `evolve run check <run> --session <session>`. Use `evolve run transition <run> running|blocked|failed --session <session> --reason "<why>"` (or `evolve run cancel ...`; pass `--actor`, `--expected-orchestration-revision`, and `--expected-run-revision` as concurrency guards in multi-worker/retry races) before allocating a retry via `evolve run create`.

### Phase-Boundary Context Hygiene

Decide context transitions **only at Run/phase boundaries** (never mid-phase, where compaction loses the thread). Because Session/Run state and sealed artifacts persist in `.evolve/workflow.db`, evaluate top-to-bottom — **first yes wins**:

1. **Continue**: stay in the current session when the next step needs prior reasoning verbatim as a primary source (e.g., `ew-grill` -> `ew-plan` / `ew-execute`) and smart-zone capacity (~150k tokens) remains.
2. **`/clear`**: discard disposable exploration or dead ends when prior context is irrelevant to the next phase (cheapest reset; prior session stays resumable).
3. **`/handoff`**: narrow portability case only — switching harnesses (e.g., Claude -> Codex), changing directory/repo, handing to a colleague, or forking a side task mid-phase. Write the handoff doc to OS temp (`$TMPDIR` / `%TEMP%`, never the workspace), include suggested skills, point to artifacts by path/ID instead of copying them, and redact secrets.
4. **Subagent**: delegate tightly scoped AFK work (exploration, review, verification) to an isolated subagent context, keeping coordinator context clean.
5. **`/compact`**: fallback when context stays relevant in the same harness/directory and you must remain in the loop; supply a focus instruction (e.g., `/compact focus on <next step>`) to retain key decisions. (For heavyweight multi-iteration standalone campaigns outside Session orchestration, route explicitly via `/ew-odyssey --mode <arch|debug|improve|planex|review|security|defensive|ui>`.)

## Host Adapter Mapping

Shared workflow documents describe capabilities neutrally; resolve them to the active host runtime as follows. For external `evolve agent` CLI flags, prompt template (`PURPOSE/TASK/MODE/CONTEXT/EXPECTED/CONSTRAINTS`), worktree isolation, and retry rules, read [Agent usage](agent-usage.md) (`evolve skill workflows show agent-usage`):

| Capability | Antigravity | Codex | Claude | OMP |
| --- | --- | --- | --- | --- |
| Parallel / subagent execution (always join or cancel before advancing) | `invoke_subagent` / `swarm` / `run_workflow` | `spawn_agent` (`agent_type`) / `wait_agent` | Host subagent / `Agent` (`subagent_type`) | Native `task` (`context` + `tasks[]`) or eval `agent()`; results auto-deliver, join/cancel at barriers, never poll |
| Structured user prompt & confirmation | `ask_question` | `request_user_input` | Host interactive prompt | `ask` |
| Web search / external docs | `search_web` | Host web search tool | Host web search tool | Host web search tool |
| Browser acceptance / rendering check | `browser` subagent / Chrome DevTools / Playwright | Playwright / headless browser CLI | Playwright / browser MCP | Host browser tools / Playwright |

### Installed Step Agent Roster & Degradation

`evolve skill install` provisions host-native agent definitions under `.claude/agents/<name>.md`, `.codex/agents/<name>.toml` (`name` uses underscores, e.g., `workflow_planner`), and `.agents/agents/<name>.md` (or `agents/<name>.md`). When the active host supports named subagent types, select from the mapping below; if an agent definition is unavailable on disk, supply the same role constraints inline in the subagent prompt:

| Step | Recommended `agent_type` (`codex` snake_case) | Primary Role |
| --- | --- | --- |
| `ew-init` | `workflow-codebase-mapper` (`workflow_codebase_mapper`) | Parallel brownfield mapping (`stack`, `architecture`, `conventions`, `pitfalls`) |
| `ew-brainstorm` | `workflow-analyzer` (`workflow_analyzer`) / `cli-explore-agent` | Optional codebase grounding & bounded option trade-off check |
| `ew-grill` | `cli-explore-agent` (`cli_explore_agent`) | Read-only codebase grounding & terminology discovery |
| `ew-blueprint` | `cli-explore-agent`, `workflow-external-researcher`, `workflow-research-synthesizer`, `workflow-integration-checker` | Discovery grounding, external research synthesis, and P6 interface readiness |
| `ew-roadmap` | `cli-explore-agent`, `workflow-roadmapper` | Boundary exploration & Session DAG decomposition/review |
| `ew-analyze` | `cli-explore-agent`, `workflow-external-researcher`, `workflow-research-synthesizer`, `workflow-analyzer` | L1–L3 code anchors, research handoff, and multi-perspective scoring |
| `ew-plan` | `cli-explore-agent`, `workflow-planner`, `workflow-collab-planner`, `workflow-plan-checker` | Exploration, single/`2+1` task decomposition, and 3-round plan validation |
| `ew-execute` | `workflow-executor` (`workflow_executor`) | Wave-parallel atomic task implementation & convergence check |
| `ew-verify` | `workflow-verifier`, `workflow-integration-checker` | Goal-backward 3-layer verification & cross-module integration audit |
| `ew-test` | `workflow-nyquist-auditor` (`workflow_nyquist_auditor`) | Coverage & test-plan gap audit (plus `browser` subagent for `--frontend-verify`) |
| `ew-auto-test` | `workflow-nyquist-auditor`, `workflow-debugger` | Layered L0–L3 test authoring & read-only failure diagnosis |
| `ew-review` | `workflow-reviewer` (`workflow_reviewer`) | Single-dimension read-only audit (`correctness`/`security`/`performance`/`architecture`) |
| `ew-debug` | `workflow-debugger` (`workflow_debugger`) | Isolated hypothesis investigation & 3-strike root-cause diagnosis |
| `ew-retrospective` | `workflow-analyzer` (`workflow_analyzer`) | Parallel 4-lens read-only retrospective (`technical`/`process`/`quality`/`decision`) |
| `ew-companion` | Direct main-agent execution (`workflow-executor` optional only when isolation is needed) | Single-Run direct execution for single-concern tasks without subagent fan-out |
