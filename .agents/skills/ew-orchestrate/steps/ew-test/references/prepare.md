# Pre-flight: ew-test

Confirm evidence sources, test mode, and UAT/browser acceptance rules before running [workflow](workflow.md).

## Modes & Flags

- **Default (Conversational UAT)**: walk deliverable scenarios one by one (`yes`/`y`/`pass` -> pass; `skip`/`n/a` -> skipped; problem description -> failed gap with severity inferred directly from failure impact — never ask "how severe is this?").
- **`-y`**: non-interactive mode — run executable smoke/coverage/browser assertions and record manual-only UI scenarios as `UNCERTAIN` / `skipped` honestly without prompting.
- **`--smoke`**: execute cold-start smoke checks (build, startup, dependencies, core health routes) before behavioral testing.
- **`--frontend-verify`**: deterministic browser acceptance asserting real UI entry points, network write actions, console/network errors, and DOM state rather than conversational UAT alone (in Antigravity, delegate UI navigation/interaction to a `browser` subagent joined cleanly on completion; otherwise use Playwright / Chrome DevTools).
- **`--auto-fix`**: record gaps cleanly and emit a `chain-proposal/1.0` (`next`) routing to `ew-debug` / `ew-plan --gaps` -> `ew-execute` rather than silently mutating code mid-UAT.

## Evidence Source Priority

1. `latest-verification` (`full` confidence)
2. `current-execution` + `current-plan` (`degraded` confidence)
3. `latest-review` (`degraded - lowest`)
4. Missing all three without explicit `<scope>` target -> error `E002`.

## Knowledge & Boundaries

- Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) for `test`/`review` conventions before deriving scenarios.
- Write artifacts strictly under `{run_dir}/outputs/`. Keep `EVIDENCE_SOURCE` and any skipped/unverified scenarios explicit in `outputs/test-results.json` and `outputs/uat.md`.
- **Exit gates**: `coverage-met` (every planned scenario executed or explicitly skipped with reason) and `pass-rate-met` (observed pass rate + gap list recorded cleanly).
