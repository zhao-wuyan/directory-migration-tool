# TDD Task-Chain Generation

Protocol for `plan --tdd`: decompose each behavior into a test-first task triplet so the executor is forced through Red-Green-Refactor. This governs how the plan is *shaped*; the executor enforces the cycle at run time.

## Iron Law

**NO PRODUCTION-CODE TASK WITHOUT A FAILING-TEST TASK BEFORE IT.**

Every behavior's GREEN task must `depends_on` its RED task. A plan that ships an implementation task with no preceding test task violates TDD and must be re-generated.

## Pre-Agreed Seams & Test Quality

- **Test only at pre-agreed seams**: a **seam** is the public boundary where observable behavior is verified without reaching into internals. Before emitting RED tasks, define the seams under test (each with a one-line note on what it catches and what it misses) and confirm them in `clarificationContext` / `plan.json` constraints (under `-y`, anchor seams on existing public APIs). Mock only at system boundaries (external APIs, time/randomness, DB/FS boundary via DI or SDK-style interfaces), never internal collaborators.
- **Avoid anti-patterns in RED/GREEN design**:
  - **Tautological assertions**: never recompute the expected value the same way production code does (e.g., `items.reduce(...)` vs `calculateTotal(items)`). Expected values must come from an independent source of truth (known-good literal, worked example, or spec).
  - **Implementation-coupled tests**: never assert internal call counts/order, test private methods, or bypass the public interface via side-channel DB checks.
  - **Horizontal bulk slicing**: never batch all RED tasks ahead of all GREEN tasks; keep vertical one-seam, one-test, one-minimal-implementation tracer bullets.

## Red-Green-Refactor Triplet

For each behavior `B` (derived from requirements or convergence criteria), emit up to three tasks:

```
TASK-{N}a  RED       Write a failing test for B
TASK-{N}b  GREEN     Write minimal code to make B pass   (depends_on {N}a)
TASK-{N}c  REFACTOR  Clean up B's implementation         (depends_on {N}b, optional)
```

REFACTOR is omitted when there is nothing to clean. RED and GREEN are never merged or skipped. **Keep refactoring strictly separate from the Red -> Green cycle**: `GREEN` writes only minimal code to pass `RED`, while structural cleanup belongs exclusively to the separate `TASK-{N}c` (`type: refactor`) task or post-implementation `ew-review` stage.

## Per-task requirements

Per [workflow](workflow.md), all TDD tasks use the canonical `plan-task/1.0` schema (`depends_on[]`, `convergence_criteria[]`, `type`, `action`, `implementation`, `meta`, etc.).

### RED — `type: test`

- `action`: write a test describing the expected behavior; it MUST fail before the implementation exists.
- `convergence_criteria` (grep/run-verifiable):
  - test file exists at `{test_path}`
  - test run exits non-zero (**fails**, not errors)
  - failure message matches the expected behavior gap
- `meta`: `{ tdd_phase: "red", tdd_group: "{N}" }`
- Use real code, not mocks (unless the dependency is external). One behavior per test.

### GREEN — `type: feature`, `depends_on: [TASK-{N}a]`

- `action`: write the simplest code that makes the failing test pass — nothing beyond what the test requires.
- `convergence_criteria`: the RED test now passes AND all pre-existing tests still pass.
- `meta`: `{ tdd_phase: "green", tdd_group: "{N}" }`.

### REFACTOR — `type: refactor`, `depends_on: [TASK-{N}b]`, optional

- `action`: improve structure/naming/duplication without changing behavior.
- `convergence_criteria`: all tests still pass; no new behavior introduced.
- `meta`: `{ tdd_phase: "refactor", tdd_group: "{N}" }`.

## Convergence quality

Criteria must be grep/run/test-verifiable with the exact command or string — never subjective ("looks correct", "properly tested"). Prefer concrete forms like `test exits non-zero` (RED) / `test exits 0` (GREEN) / `<test-command> passes`.

## Interaction with plan gates

The generated triplets flow through the standard plan checker: dependency correctness must show every GREEN depending on its RED, and the Readiness Gate treats a GREEN task with no preceding RED as a blocking violation.
