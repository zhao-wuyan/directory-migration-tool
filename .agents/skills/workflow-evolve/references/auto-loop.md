# Automatic Loop

The loop orchestrates four steps per round: baseline/candidate runner execution, history review, Agy discussion/confirmation, and isolated Git worktree replay.

## Round Order

1. **Preflight**: Read `HEAD` and verify the target Skill package directory (`skills/<id>/`) has no uncommitted or untracked changes (`git status --porcelain --untracked-files=all -- <packageDir>`), without failing on unrelated untracked fixtures at the repository root. Verify `--skill <path>` points to `skills/<id>/SKILL.md` and, when `--execution <id>` is used, `--project <id>` is also supplied (omitting `--project` fails immediately).
2. **Baseline**: Run all scenarios (provided scenarios plus the `--bank` regression scenarios, which count as baseline passes) against the current Skill (must include >= 1 failing case and >= 1 passing case). A scenario with `samples: k` passes only when all `k` samples pass (pass^k). If any baseline-passing scenario fails, record `round.status = "regression_detected"` and stop immediately. If all scenarios pass, record `round.status = "converged"`, save the bank, and stop cleanly.
3. **History review**:
   - Canonical CLI commands are `evolve session review <id>` and `evolve skill review <execution-id> --project <id>`; internally, `evolve-loop.mjs` invokes the equivalent `dist/workflow-cli.js` subcommands `session review <id>` or `skill review <execution-id> --project <id>`.
   - For standalone executions without a Session, pass `--execution <id> --project <id>` or `--history <transcript-evidence/1.0 JSON>`.
   - The OpenViking lifecycle may supply one fixed `--review <serialized Session review>` with real Run sources; every cited source must be verifiable. If that review contains no signals, stop before baseline with `round.status = "no_signals"`.
   - Drop signals whose IDs are already recorded as addressed (bank or earlier rounds); record the remaining signals, Skill reference, Skill reads, and `knowledge_reads` in the round directory (`history.json`, with `addressed_signals` counting the dropped ones). `--review` is a fixed external review and is not filtered by the bank.
   - Unless `--allow-empty-history` is set, if no open signal remains, stop the round before calling Agy with `round.status = "no_attributable_signals"` and zero Skill changes.
4. **Learning inventory**: Run `evolve skill learnings <entry>` (`learnings.json`) so Agy can choose `add`, `supersede` or `retire`. When it reports `consolidate_recommended`, `add` is rejected for the round.
5. **Draft and replay via Agy (with feedback)**: Request one minimal, evidence-linked change (see `agy-gate.md`), create the draft (`evolve skill draft` / `evolve skill retire`) and replay it against the accumulated scenarios (`evolve skill replay`). All baseline passes must stay green and baseline failures must pass. When Agy declines, the bloat gate rejects `add`, or the draft/replay fails, append the concrete reason (for example `candidate failed (1/3 samples passed): <runner feedback>`) to the next discussion prompt and retry up to `--max-retries`. If Agy never approves, record `agy_rejected`; if the last approved proposal was a blocked `add`, record `consolidation_required`; otherwise record `replay_failed`. Each attempt is stored as `agy-discussion-<n>.json` / `replay-error-<n>.json`.
6. **Confirm via Agy**: Submit draft ID, candidate commit, scenario results, and loaded Skill documents. `approved: false` halts without applying (`round.status = "confirmation_rejected"`).
7. **Apply & Verify**: With `--auto`, apply the confirmed receipt (`evolve skill apply` / `dist/workflow-cli.js skill apply`) and re-run scenarios on the applied commit; on any failure, immediately `rollback` and record `round.status = "rolled_back"`. Otherwise record `round.status = "applied"`, save all scenarios to the bank as baseline passes, and mark the signals cited by Agy (`signals` or matching `evidence`) as addressed. Without `--auto`, leave the confirmed receipt pending manual `apply` (`round.status = "confirmed_pending_apply"`).
8. **Persist & Loop**: Save round state and continue until `--iterations` completes or a stop condition triggers. Pass `--run-dir <dir>` (or `RUN_DIR`) inside an Evolve Run so `outputs/evolution-report.json` is written with `_meta`.

## State and Stop Conditions

- `state.json` is the single machine state source per run (`base`, `draft`, `replay`, `history`, `allow_empty_history`, `bank`, `bank_scenarios`, Agy responses, retries and feedback, apply status, verification results). The bank file (`workflow-evolve-bank/1.0`) persists `scenarios` and `addressed_signals` across runs.
- **`round.status` values**:
  - Passing / clean round outcomes: `applied`, `converged`, `confirmed_pending_apply`.
  - Halting / non-passing round outcomes: `regression_detected`, `no_signals` (`--review` only), `no_attributable_signals`, `agy_rejected`, `replay_failed`, `consolidation_required`, `confirmation_rejected`, `rolled_back`.
- **Top-level `state.status` values**:
  - `running`: loop active.
  - `completed`: only when the final round finished with `applied` or `converged`.
  - `confirmed_pending_apply`: final round finished with `confirmed_pending_apply` (confirmed replay awaiting manual `apply` without `--auto`).
  - Non-passing terminal stop statuses (NEVER marked `completed`): `regression_detected`, `no_signals`, `no_attributable_signals`, `agy_rejected`, `replay_failed`, `consolidation_required`, `confirmation_rejected`, `rolled_back` (mirrors the stopping round's `status`), or `failed` (uncaught runtime/preflight/runner error; keeps round directory for recovery).
- Inside an Evolve Run, do not seal with `evolve run complete` as passing (`done`) unless `state.status === "completed"` (`confirmed_pending_apply` requires `done_with_concerns` or completing `apply` first; failure stop statuses must `evolve run transition ... failed|blocked`).
- History evidence is mandatory (`--allow-empty-history` is reserved for explicitly history-free test fixtures and recorded as `allow_empty_history: true|false` in `state.json`).
