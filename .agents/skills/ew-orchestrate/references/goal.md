# Evolve Goal Amendment Flow

Read the [Run loop](orchestrator-run-loop.md) when resuming the Session after the amendment.

Under `evolve-session/1.0`, `--amend` updates an open Session through the fenced `evolve session update` + `evolve session step add|update|delete` commands rather than creating a separate Execution type or editing protocol JSON directly. `evolve session update` is the ONLY authorized command for amending `Session.objective` and `--definition-of-done` (Runtime automatically appends `CHG-NNN` to `Session.amendments[]`); it does NOT mutate persisted `Session.goals[]` or `Session.boundary_contract`. Runs advance through fenced `evolve run create` and `evolve run complete`.

## 1. Capability and Snapshot

1. Read the Evolve capability receipt (`evolve capabilities`) and retain the Session ID and current revision.
2. List open Evolve Sessions via `evolve session list --status open [--query <text>]` (never select a Session via historical memory similarity) and resolve exactly one compatible Session:
   - If `0` compatible open Sessions match: stop and report that there is no open Session to amend (offer to open a new Session instead).
   - If `>1` compatible open Sessions match: ask the user to select the target `session_id`.
   - Then inspect `evolve session get --session {session_id}`.
3. Retain `session_id` (and track `orchestration_revision` / `run_revision` plus `actor_id` when optimistic concurrency guards are needed; `--actor`, `--expected-orchestration-revision`, and `--expected-run-revision` are optional in single-coordinator sequential mode, and recommended in multi-worker / retry / parallel races — if `--participant` is supplied, it must match `--actor`). Never infer current authority from Session status.
4. Snapshot current Session `objective`, `definition_of_done`, `boundary_contract`, `goals[]`, `amendments[]`, sealed Run handoffs, pending chain, decisions, and active Runs via `evolve session get` / `evolve run get`. **Active-Run precondition**: Runtime rejects `evolve session update` if any Run in the Session has status `running` or `blocked` (`Resolve the active Run before amending the Session`), so seal (`run complete`), transition (`run transition`), or cancel (`run cancel`) active/blocked Runs first.

## 2. Parse and Assess

Use remaining `--amend` text as `change_request`; ask when empty. Classify `modify|add|remove|boundary` and derive affected goals, invalidated steps, new gaps, boundary additions, risk, reason, and evidence.

Assessment is read-only. Completed Run evidence and completed goals remain immutable. High risk always requires explicit confirmation; `-y` cannot bypass it.

## 3. Assess Goals/Boundary Scope & Build Pending-Tail Chain Change

Check whether `change_request` fits within `evolve session update` + pending-tail chain edits:
- **`evolve session update` scope**: updates `Session.objective`, optional `--definition-of-done`, and `--evidence` (Runtime generates and appends `CHG-NNN` automatically — **do NOT manually invent or double-write `CHG-NNN` entries**).
- **Immutability / CLI boundary for `goals[]` and `boundary_contract`**: the current Runtime provides no fenced CLI flag to overwrite persisted `Session.goals[]` or `Session.boundary_contract` on an already-open Session. If `change_request` requires superseding/re-versioning `goals[]` IDs or adding new `boundary_contract` entries that cannot be adequately captured via updated `objective` + `--definition-of-done` + pending-tail `chain` steps, **stop with a capability blocker / escalate to the user** (recommend either closing the current Session and opening a new follow-up Session with the updated `--goals` / `--boundary-contract` JSON, or explicitly confirming that amending `objective` / `--definition-of-done` and pending steps suffices).

When the change can proceed on the open Session, construct the pending-tail chain change in memory preserving the step shape (`command/args?/stage?/goal_ref?/decision_ref?/retry_max?`):

- Do not edit protocol JSON or issue direct chain mutations from a Skill. The orchestrator applies the pending-tail change through the fenced chain surface after Skill assessment:

`evolve session step update --session {session_id} --step-id <id> --command <name>`

or `evolve session step add --session {session_id} --step-id <id> --command <name> [--arg <value> ...] [--after-step <id>] [--goal-ref <id>] [--stage <name>] [--decision-ref <id>] [--retry-max <n>] [--decision-only]` for added steps. Skipping a non-running step uses `evolve session step delete --session {session_id} --step-id <id> --evidence "<reason>"` (add `--actor` and `--expected-orchestration-revision` when guarding concurrent/retry races).

## 4. Commit the Amendment

1. Ensure no `running` or `blocked` Run remains in the Session (`evolve session get`).
2. After confirmation, record the new objective and completion criteria with `evolve session update "<objective>" [--definition-of-done "<criteria>"] --session <session> --evidence <confirmation> [--reason "<why>"]` (Runtime appends `CHG-NNN` automatically to `Session.amendments[]`).
3. Apply approved pending-tail chain changes (`session step update|add|delete`), then dispatch with:

`evolve run create --session {session_id}`

and complete each Run with:

`evolve run complete {run_id} --session {session_id} --summary "<summary>" --verdict {done|done_with_concerns} [--next]`

(add `--actor {actor_id}`, `--expected-orchestration-revision {orchestration_revision}`, and `--expected-run-revision {run_revision}` when guarding multi-worker, retry, or parallel races; pass `--next` to seal the current Run and immediately allocate the next pending Run in one CLI call). Runtime atomically validates and seals the Run plus advances the chain step and returns `Evolve mutation receipt`. Consume its fresh `orchestration_revision` whenever concurrency guards are active. Reject by recording a blocking concern; revise by re-reading `evolve session get` and applying another fenced chain mutation.

If the installed Runtime cannot express the complete goal/boundary replacement as an `objective`/`--definition-of-done` amendment plus Session-chain pending-tail change, stop with a capability blocker and escalate to the user rather than silently mutating state files or calling unsupported commands.

## 5. Continue

Display Runtime-assigned `amendment_id` (`CHG-NNN`), risk, affected pending steps, and chain disposition. Re-read exact `evolve session get`, then continue through fenced `evolve run create`. Recovery reads `evolve run check {run_id} --session {session_id}` and transitions/cancels the stuck Run; final completion remains `evolve session complete`.
