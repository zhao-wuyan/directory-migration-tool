# Pre-flight: ew-debug

Confirm entry mode, Knowledge Gate context, and strict read-only diagnosis invariants before running [workflow](workflow.md).

## Entry Modes

| Mode | Trigger | Symptom Source |
| --- | --- | --- |
| Standalone | `<issue>` description | Interactive collection (expected, actual, error, regression window, repro steps) + recent `git log`/`diff` |
| From test / review | `--from-test` or bound `latest-test` / `latest-auto-test` / `latest-review` | Failed scenarios, `app_defects[]`, or findings clustered by affected module or flow |
| Parallel | `--parallel` | One isolated debug subagent per non-overlapping failure cluster (all joined before synthesis) |
| Continuation | `-c` | Resume from prior failed Run's `evidence` / `understanding` via `run get` |

Missing issue and no bound failure artifact -> error `E001`.

## Knowledge & Codebase Pre-load

1. Follow the Run Knowledge Gate (`evolve skill workflows show session-run`): reuse bound `session-priors` (`debug`/`coding`); otherwise query `evolve search "<query>" --mode context` + `evolve kb get <id>` (with CLI `--reads` with `--task debug`) to check known pitfalls and prior fixes.
2. Run `evolve code search` / `evolve code callers` / `evolve code trace` after the Knowledge Gate to trace propagation paths.

## Scientific Boundaries (Iron Law)

- **Tight-red-loop gate (before code theorizing)**: establish one fast (seconds, not minutes), deterministic, agent-runnable command that exercises the exact bug path and goes red on the user's exact symptom before reading code to theorize root causes. If no loop can be built locally, stop and ask the user for environment access, a redacted artifact (HAR/logs/core dump), or temporary instrumentation permission.
- **100x flake amplification**: for non-deterministic bugs, raise the reproduction rate first (loop the trigger 100x, parallelize, add stress, narrow timing windows, pin time/RNG) until reliably debuggable (`>= 50%` rate rather than `1%`).
- **Minimal load-bearing repro**: before hypothesis testing, shrink inputs, callers, config, and steps one at a time, re-running after each cut until every remaining element is load-bearing (removing any element turns the loop green).
- **Read-only on repository source & `[DEBUG-xxxx]` tags**: never apply speculative fixes during diagnosis; temporary reproduction harnesses live in `{run_dir}/evidence/`, any temporary diagnostic log MUST carry a unique `[DEBUG-xxxx]` prefix (e.g., `[DEBUG-a4f2]`) for single-`grep` removal before exit, and every captured secret must be redacted as `<REDACTED>`. Fixing belongs to `ew-plan --gaps` -> `ew-execute`.
- **Forced-mutation `diff` proof**: whenever a red signal is forced by temporarily mutating code or a fixture, verify `git diff` against a pristine copy to prove the mutation actually landed before trusting the red verdict (and revert it cleanly before exit).
- **Evidence before confirmation**: a hypothesis stays `"suspected"` until backed by a live reproduction or concrete `file:line`/log evidence.
- **Single-variable testing & 3-strike escalation**: form 3–5 ranked falsifiable hypotheses first, test one variable at a time, and record append-only observations in `{run_dir}/evidence/evidence.ndjson`. If 3 hypotheses fail, STOP proposing ad-hoc theories (`INVESTIGATION INCONCLUSIVE`), record all falsified hypotheses + architectural contradiction anchors, and escalate to architectural analysis (`ew-analyze` inside the Session, or standalone `ew-odyssey --mode arch` / `ew-odyssey --mode debug`).
- **Missing-seam handoff**: if no valid seam exists to write a non-trivial regression test matching the real multi-caller trigger pattern, record the missing seam explicitly rather than proposing a shallow test, and recommend architectural hardening (`ew-analyze` or `ew-odyssey --mode arch`).
- **Backward tracing**: trace from symptom origin back to where valid data first turned invalid — fix root cause, never surface symptoms.
- **Exit gates**: `hypothesis-tested` (tight red-capable loop + minimized load-bearing repro + isolated hypotheses logged + zero leftover `[DEBUG-xxxx]` tags) and `evidence-grounded`:
  - For `confirmed` / `partial`: pressure pass done, multi-factor confidence recorded (`>= 40%`), zero unaddressed contradicting evidence, concrete `affected_files[]` + `fix_direction` (plus explicit `missing_seam` escalation note when no safe test seam exists).
  - For `inconclusive` (3-strike architectural contradiction): complete 3-strike refutation dossier logged across `outputs/reproduction.json` + `outputs/hypotheses.json` (`>= 3` falsified hypotheses with `file:line` refutation anchors) + `outputs/diagnosis.json` (`status: "inconclusive"`, `fix_direction: null`, explicit `escalation_target`), without fabricating `affected_files[]` or `fix_direction`.
