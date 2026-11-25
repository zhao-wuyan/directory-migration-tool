# Evolve Agent Usage & Delegation Specification

Canonical specification for dispatching host-native subagents and external CLI agents (`evolve agent`). Keep top-level instructions slim and load this reference on demand via `evolve skill workflows show agent-usage` when delegating work, spawning subagents, or resolving execution failures.

---

## 1. Channel Selection

Choose the lightest channel that preserves context isolation and verification rigor:

| Scenario | Recommended Channel | Notes |
| --- | --- | --- |
| Single-concern edit or check with clear local scope (`ew-companion`, simple lookup) | **Direct execution** (no subagent) | Execute in the main context without subagent fan-out |
| Parallel read-only exploration, multi-angle review, verification, or wave task execution inside the active host | **Host-native subagent / swarm** | Always start workers fresh and join/cancel all handles before advancing |
| Cross-model second opinion, isolated CLI execution, persistent external CLI thread, or headless `--worktree` task | **`evolve agent` CLI** | Defaults to `--mode analysis`; explicit `--mode write` required to edit files |

---

## 2. Host-Native Subagent & Swarm Rules

1. **Host primitive mapping**: Resolve neutral subagent calls to the active host (`invoke_subagent` / `swarm` / `run_workflow` in Antigravity, `spawn_agent` / `spawn_agents_on_csv` + `wait_agent` in Codex, `Agent` in Claude Code) and installed definitions (`agents/*.md`, `.codex/agents/*.toml` in `snake_case`) per `evolve skill workflows show session-run`. Fall back to inline role constraints when a named definition is absent.
2. **Fresh context by default**: Start every exploration, execution, verification, and review worker with a clean context window so prior reasoning and cross-axis bias cannot leak in. Reserve `fork` strictly for warm deep-dive branches that require primary-source reasoning.
3. **Sparse context pointers**: Pass locators (`session_id`, `run_id`, `run_dir`, `--reads` receipt path, `current-plan` task ID, `file:line`, resolved Wiki constraint summaries/IDs) instead of pasting full files or conversation logs.
4. **Join-or-Cancel invariant**: Every spawned subagent or swarm worker MUST be awaited/joined, completed, or cancelled/killed before advancing to the next phase or ending scope. Never fire-and-forget.
5. **Concurrent write isolation**:
   - **Disjoint direct**: Parallelize directly in the working tree only when `files[]` do not collide and builds do not contend.
   - **Isolated workspace + dedicated merger**: When parallel writers risk collisions or shared build locks, isolate via Git `--worktree` or Antigravity `/swarm` `workspace: "share" | "branch"`. Land/merge each verified branch into the integration tip before cleanup or `swarm{action:"kill"}` (which permanently deletes `share`/`branch` workspaces).

---

## 3. `evolve agent` CLI Reference

### Command Forms

```bash
# One-shot delegation (or stateful when --agent-session <id> is supplied)
evolve agent exec "<PROMPT>" [options]

# Persistent session & worktree lifecycle
evolve agent providers
evolve agent list [--to <provider>] [--status idle|running|completed|interrupted|failed|closed]
evolve agent create "<PROMPT>" [--id <agent-session>] [options]   # alias: start
evolve agent get <agent-session>                                  # alias: status
evolve agent send <agent-session> "<PROMPT>" [--mode ...] [--model ...] [--timeout <ms>] [--stream]
evolve agent delete <agent-session> [--remove-worktree] [--force] [--delete-branch]   # alias: close
evolve agent worktree list | remove [<path>] [--force] [--delete-branch]
```

### Options

| Option | Description | Default |
| --- | --- | --- |
| `--to <provider>` / `--tool <provider>` | Target executor: `codex`, `agy`, `claude` (or registered alias) | `codex` |
| `--mode <mode>` | `analysis` (read-only / plan) or `write` (workspace-write / accept edits) | `analysis` |
| `--model <model>` | Model override passed to the provider CLI | Provider default |
| `--cd <dir>` | Working directory resolved against `--root` | `--root` (`.`) |
| `--prompt-file <path>` | Read prompt text from file (resolved against `--cd`) instead of positional args | — |
| `--session <id> --run <id>` | Bind Evolve Session & Run context into the initial prompt (`exec` / `create` only; both flags required together) | — |
| `--agent-session <id>` / `--id <id>` | Persistent agent session identifier stored in `.evolve/orchestrator.db` | Ephemeral (`exec`) / `agent-<uuid8>` (`create`) |
| `--worktree <name>` / `--isolate` | Run in an isolated Git worktree (`--isolate` auto-names `<agent-session>`) | Shared `--cd` |
| `--branch <name>` | Git branch name for the created worktree | `evolve-agent/<id>` |
| `--base-ref <ref>` | Base commit/ref for worktree creation | `HEAD` |
| `--keep-worktree` | Keep auto-created worktree after ephemeral `exec` finishes | `false` (auto-removed) |
| `--remove-worktree` | Remove created worktree on `agent delete` / `close` | `false` |
| `--force` / `--delete-branch` | Force worktree removal / delete worktree branch on close (`delete-branch` defaults `true` on `agent delete`) | — |
| `--stream` | Emit JSONL `AgentStreamEvent` records to `stdout` instead of final summary JSON | `false` |
| `--timeout <ms>` | Turn execution timeout in milliseconds (integer) | None |
| `--status <status>` | Filter `agent list`: `idle`, `running`, `completed`, `interrupted`, `failed`, `closed` | All |

### Authoritative Rules

1. **`--mode` is authoritative**: Always pass `--mode analysis` or `--mode write` explicitly on the CLI. `MODE:` inside prompt text is a semantic hint only; sandboxing is enforced by `--mode`:
   - `codex`: `analysis` -> `--sandbox read-only`; `write` -> `--sandbox workspace-write`
   - `agy`: `analysis` -> `--mode plan`; `write` -> `--mode accept-edits`
   - `claude`: `analysis` -> `--permission-mode plan`; `write` -> `--permission-mode acceptEdits`
2. **Executor Normalization Rule**: `evolve agent exec --to` accepts ONLY concrete CLI providers (`codex`, `agy`, `claude`). Probe availability first via `evolve agent providers` (`available: true`). Normalize `"Auto"` to the first available CLI in `[codex, agy, claude]`, or fall back to a joined host subagent (emitting `W006` in workflows) when none is installed. Never pass `"Auto"` or `"agent"` to `--to`.
3. **Provider Capabilities & Context Injection**:

| Provider | `supportsResume` | Prompt Transport | `--session <id> --run <run>` Behavior | Notes |
| --- | --- | --- | --- | --- |
| `codex` | `true` | `stdin` | Appends `Evolve Run context` JSON + `evolve run get/skill-read/artifact-read` + `evolve search` CLI hints (`exec` / `create`) | Ephemeral by default (`--ephemeral`); persists & resumes `thread_id` when `--agent-session` or `agent create` is used |
| `claude` | `true` | `-p <prompt>` | Same as above on `exec` / `create` | Resumes external `session_id` across `agent send` / `--agent-session` turns |
| `agy` | `false` | `--print=<prompt>` | Same as above on `exec` / `create` | Stateless per turn (no external thread resume); prefer one-shot `exec` or fresh self-contained prompts; omit `--session`/`--run` when seeking an unbound second opinion |

4. **Multi-Turn Sessions & Worktree Cleanup**:
   - `agent send` serializes turns per session ID, omits `withRunContext` re-injection (pass incremental task prompts directly), and requires `supportsResume: true` (`codex` / `claude`) for thread continuity.
   - Always land or inspect worktree commits/diffs before running `evolve agent delete <id> --remove-worktree` or `evolve agent worktree remove <path>`.

---

## 4. Standard Prompt Template

Use this compact structured template for both `evolve agent` calls and subagent briefs, keeping `CONTEXT:` as sparse pointers:

```text
PURPOSE: [goal] + [observable success criteria]
TASK: [step 1] | [step 2] | [step 3]
MODE: analysis|write
CONTEXT: [file paths / globs / file:line anchors] | Run: [session_id / run_id / run_dir] | Constraints: [Wiki constraint IDs or summaries]
EXPECTED: [JSON schema or Markdown deliverable format + exact output path if writing]
CONSTRAINTS: [scope limits, read-only guarantee or allowed write files, verification checks]
```

- For multi-line or complex prompts on Windows/Unix shells, prefer `--prompt-file <path>` (or `stdin` / heredoc) to avoid shell quoting truncation.
- Inside an active Run, pass `--session <session_id> --run <run_id>` (when bound inspection is desired) and direct workers to record knowledge reads via `--reads "<run_dir>/knowledge-reads.json"`.

---

## 5. Reliability, Retry Ladder & Auto-Invoke Triggers

### 3-Attempt Deviation Ladder

When a delegated CLI call or subagent fails, times out, or produces an unusable result, never exceed 3 attempts on the same task:
1. **Attempt 1**: Standard dispatch using the structured prompt template.
2. **Attempt 2**: Narrow scope / simplify into a self-contained prompt (switch CLI provider if the primary CLI errored or is unavailable).
3. **Attempt 3**: Fall back to a fresh host-native subagent (if CLI failed) or alternative isolated worker.
4. **All 3 fail**: Stop retrying, record checkpoint (`attempt: 3`, `last_error`, `partial_files`, `executor`), mark task `blocked`, and propagate `upstream_blocked` to dependents.

### Proactive Read-Only Triggers (`--mode analysis`)

Proactively invoke read-only cross-angle delegation (`evolve agent exec ... --mode analysis` or an isolated read-only subagent) without altering workspace files when:

| Trigger | Recommended Target / Focus |
| --- | --- |
| Self-repair fails (`>= 1` failed fix attempt) | Root-cause diagnosis with `file:line` call-chain evidence |
| Ambiguous scope or cross-module boundaries | Boundary/impact trace and task decomposition options |
| Architecture or interface seam decisions | Coupling, cycle, and trade-off review |
| Pattern uncertainty before editing | Existing codebase convention & sibling call-site scan |
| Security-sensitive or critical paths | Threat, input-validation, and invariant audit |
| Post-execution diff verification | Independent cross-model review against spec & standards |

**Completion check**: Every delegation ends with all spawned subagents joined/cancelled, all temporary worktrees cleaned up after landing changes, and output/verification evidence recorded in the active Run or response.
