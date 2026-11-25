# Host Runtime Adapters for ew-swarm

Shared `ew-swarm` protocol documents and role guides describe capabilities neutrally. Use this adapter reference to map those capabilities to the active host runtime and Evolve CLI primitives (for full `evolve agent` CLI options, prompt template, and degradation rules, inspect `evolve skill workflows show agent-usage`).

## Capability Mapping

| Capability | Antigravity | Codex | Claude Code |
|------------|-------------|-------|-------------|
| Shell command execution (`python <controller.script>`, `evolve ...`) | `run_command` | Shell execution | `Bash` |
| File read / write / glob / search | `read_file`, `write_file`, `run_command` | Workspace file tools | `Read`, `Write`, `Glob`, `Grep` |
| Parallel worker dispatch & mandatory join/await | `invoke_subagent` or `swarm` (join/stop all spawned workers before concluding) | `spawn_agent` (`agent_type: "team_worker"`, fresh context — disable parent fork) + `wait_agent` | `Agent` (`subagent_type: "team-worker"`, parallel calls + join) |
| Structured user prompt | `ask_question` | `request_user_input` | `AskUserQuestion` |
| Deep read-only exploration (external) | `evolve agent exec "<prompt>" --to codex --mode analysis` | `evolve agent exec "<prompt>" --to codex --mode analysis` | `evolve agent exec "<prompt>" --to claude --mode analysis` |
| Independent scoring pass (optional external model) | Host subagent or `evolve agent exec ... --model <model>` | Host subagent or `evolve agent exec ... --model <model>` | Host subagent or `evolve agent exec ... --model <model>` |

## Worker Context & `/swarm` Board Hygiene

- **Fresh context window by default**: Start every `ant`, `scorer`, and `analyst` worker with a clean context window so parent coordinator reasoning or prior tasks cannot leak in (critical for blind `scorer` evaluation and independent `ant` paths). Reserve `fork` strictly for explicit warm deep-dive branches that require primary-source reasoning from the parent thread.
- **Sparse pointer briefs**: Pass locators and file/CLI pointers (`session_id`, `run_id`, `run_dir`, `team_dir`, `role_spec`, `--reads` receipt path, assignment JSON) instead of inlining large files or conversation history.
- **Antigravity `/swarm` workspace & board discipline**:
  - Keep `workspace: "inherit"` (default) for read-only explorers, `scorer`, `analyst`, and `ant` workers writing only JSON artifacts under `{run_dir}/outputs/`.
  - Use `workspace: "share"` (or `"branch"`) when parallel workers edit or build repository code concurrently; land/merge their work before `swarm{action:"kill"}` (which permanently removes `share`/`branch` workspaces).
  - On the shared board, `@mention` only the peer(s) acting next (doorbell, not CC), cite prior posts by `#seq` and artifact paths, prefix final deliverables with `[done]` (e.g., `@coordinator [done] ...`), idle with `swarm_wait` rather than polling, and `kill` finished helpers before concluding.

## Phase 0 Lifecycle Reconciliation Mapping

Phase 0 recovery reconciles abstract lifecycle concepts against concrete Evolve CLI commands and host inspection capabilities:

| Lifecycle Concept | Concrete Evolve / Host Primitive |
|-------------------|----------------------------------|
| Runtime lifecycle inspection | `evolve session get --session <session_id>`, `evolve run check <run_id> --session <session_id>`, `evolve run get <run_id> --session <session_id>`, and `evolve run task list --session <session_id> --run <run_id>` |
| Live worker handles (active subagents) | Inspect currently live host workers via the host's subagent/swarm status check (e.g., `swarm{action:"status"}` / background task list in Antigravity, or live agent handles in Codex). On a fresh turn after restart with no live subagents, treat live handles as empty. |
| Non-terminal team tasks | Entries returned by `evolve run task list --session <session_id> --run <run_id>` that have neither `result` nor `cancellation`. |
| Ordered activity clock | `team-session.json.updated_at`, receipt `recorded_at` timestamps in `evolve session get`, and timestamps of `work/team/pheromone/updates/*.json` and `outputs/ant-*.json`. |
| Audited transition to `abandoned` | Record evidence and set `team-session.json` `status = "abandoned"`, then execute `evolve run cancel <run_id> --session <session_id> --actor <actor> --expected-orchestration-revision <rev> --expected-run-revision <run_rev> --reason "<abandonment evidence>"` (or `evolve run transition ... failed` if re-attempting the Run). Never delete Run metadata or `outputs/`. |
