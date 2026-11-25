# Workflow: ew-review

Two-axis code and artifact review inside an Evolve Run. Consumes `current-execution`, optional `current-plan`, and `session-priors`; isolates **Spec** and **Standards** audits in parallel fresh-context reviewers; runs a zero-residual check; and writes `outputs/review.json` (`review-findings/1.0`) and `report.md` without merging or cross-axis reranking.

## Pipeline

1. **Pin Target, Spec & Standards Sources**
2. **Parallel Two-Axis Audit (`Spec` + `Standards`)**
3. **Zero-Residual & Evidence Verification**
4. **Write `outputs/review.json` and `report.md` (Non-Merging)**
5. **Seal Run & Route Knowledge**

---

## Step 1: Pin Target, Spec & Standards Sources

1. Read bound inputs via `evolve run read artifact <run> <alias> --session <session>`:
   - `current-execution`: extract changed files, base commit, task results, and self-check evidence.
   - `current-plan` (optional): extract task requirements, acceptance criteria, and intended file scope.
   - `session-priors`: reuse applicable `review` and `coding` Wiki rules for the matching scope.
2. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) when the target adds paths or categories outside `session-priors`.
3. Pin the diff / target once and verify it resolves and is non-empty before spawning reviewers:
   - Bound `current-execution` -> `change-manifest` / changed files and diff against `base_commit`.
   - Fixed point (`<ref>`) -> `git rev-parse <ref>`, `git diff <ref>...HEAD`, and `git log <ref>..HEAD --oneline`.
   - `HEAD` / `staged` / explicit path -> `git diff HEAD`, `git diff --staged`, or explicit files/dirs.
4. Identify the **Spec source** (in priority order):
   - Bound `current-plan` and `current-execution` task specifications.
   - Issue/PR references in commit messages or an explicit spec path under `docs/` or `specs/`.
   - If no spec exists -> record `"no spec available"` and skip the Spec reviewer in Step 2.
5. Identify the **Standards sources**:
   - Search the repo for documented coding standards (`AGENTS.md`, `CLAUDE.md`, `CODING_STANDARDS.md`, `CONTRIBUTING.md`, etc.) plus resolved Wiki `review`/`coding` constraints.
   - Pair those sources with the **12 Fowler Code Smells baseline** in Step 2.

---

## Step 2: Parallel Two-Axis Audit

Run the **Spec** and **Standards** reviewers in parallel (`workflow-reviewer` / `workflow_reviewer` via the host parallel subagent capability in `evolve skill workflows show session-run`, or inline role isolation when subagents are absent; OMP hosts use native `task`/eval `agent()` with `reviewer`, batching independent slices through `context` + `tasks[]`, and join/cancel every spawned subagent before Step 3, never poll). Start **every** reviewer with a **fresh context window** and **sparse context pointers** so the two axes cannot pollute each other. Native agent duties do not enforce OS read-only permissions; strict OMP analysis uses the one-shot CLI worker below.

For an independent cross-model check, explicitly select an authorized model different from the original model and optionally run:
`evolve agent exec "<review prompt with target pointers>" --to codex --mode analysis --session <session> --run <run>`
(or `--to agy --mode analysis` without `--session`/`--run` for an unbound second opinion; for OMP use `--to omp --mode analysis --model <authorized-selector> --session <session> --run <run>` with the same prompt — OMP is a one-shot restricted worker). Explicitly resolve the authorized second-opinion model on each host before claiming cross-model independence. Same-model children are another perspective, not independent-model verification; apply the [host contract](../../../SKILL.md#omp-native-delegation-and-strict-cli-analysis).

### Axis A: Spec Reviewer (Requirements Fidelity & Scope Creep)

- **Inputs (sparse pointers)**: pinned diff command / commit list / changed files + pointer or excerpt of `current-plan` / `current-execution` / originating spec. (Skipped if no spec source exists.)
- **Prompt brief**: inspect the diff against the spec and report per finding (`file:line` + quoted spec/plan line):
  1. **Missing or partial requirements**: items asked for in `current-plan` / `current-execution` / spec that are absent or incomplete.
  2. **Scope creep**: behavior or abstractions added in the diff that were not requested.
  3. **Wrong behavior**: requirements that look implemented but deviate from spec semantics or edge cases.

### Axis B: Standards Reviewer (Repo Standards + 12 Fowler Code Smells Baseline)

- **Inputs (sparse pointers)**: pinned diff command / commit list / changed files + list of repo standards files + resolved Wiki rules + the full **12 Fowler Code Smells baseline** below.
- **Dimensions covered**: `correctness`, `security`, `performance`, `architecture` (under `--tier quick`, focus on `correctness`, `security`, and hard standard breaches in changed hunks).
- **Binding rules**:
  - **Repo standards override**: documented repo standards and active Wiki constraints always win; suppress any baseline smell endorsed by repo rules.
  - **Hard violations vs judgement calls**: breaches of documented repo standards, Wiki constraints, or `correctness`/`security` invariants can be **hard violations** (`critical`/`high`/`medium`); baseline smells are always labelled **judgement calls** (e.g., `"possible Feature Envy"`).
  - **Skip tool-enforced checks**: ignore formatting, lint, or type rules already enforced by project tooling.
- **12 Fowler Code Smells Baseline** (*what it is* -> *how to fix*):
  1. **Mysterious Name**: function, variable, or type name fails to reveal intent -> rename clearly.
  2. **Duplicated Code**: same logic shape appears across multiple hunks/files -> extract shared helper.
  3. **Feature Envy**: method reaches into another object's data more than its own -> move onto the envied data.
  4. **Data Clumps**: same few fields/params repeatedly travel together -> bundle into a type.
  5. **Primitive Obsession**: primitive/string standing in for a domain concept -> introduce a small domain type.
  6. **Repeated Switches**: same `switch`/`if`-cascade on the same type recurs -> replace with polymorphism or shared map.
  7. **Shotgun Surgery**: one logical change forces scattered edits across many files -> gather into one module.
  8. **Divergent Change**: one file/module is edited for unrelated reasons -> split by reason to change.
  9. **Speculative Generality**: abstractions, params, or hooks added for unrequested future needs -> delete and inline.
  10. **Message Chains**: long `a.b().c().d()` navigation exposing internals -> hide behind one method.
  11. **Middle Man**: class/function mostly delegating onward -> remove and call real target directly.
  12. **Refused Bequest**: subclass/implementer ignoring most inherited behavior -> replace inheritance with composition.

---

## Step 3: Zero-Residual & Evidence Verification

Cross-check implementation completeness against disk state:

1. Verify every claimed file change and convergence criterion in `current-execution` matches actual repository files.
2. Check for untracked residual defects in reviewed diffs (`TODO`/`FIXME`/debug statements, dead imports, broken relative Markdown links, or skipped/weakened test assertions), building on `ew-verify`'s `antipattern-report.json` when present rather than duplicating its full scan.
3. Run the narrowest relevant verification command(s) if not already evidenced in `current-execution` and record the exact exit status and output summary. For **UI / frontend changes** (`.tsx/.jsx/.vue/.svelte/.css/.html`, UI routes, or design tokens), run a **Visual Truth Check** via host browser inspection (`evolve skill workflows show session-run` -> `browser` subagent / Chrome DevTools / Playwright) across desktop and mobile viewports covering layout hierarchy, interactive/empty/error states, and console/network cleanliness (or inspect committed visual-regression goldens and record `unverified_visual` explicitly if no browser runtime is available; recommend `ew-impeccable` `critique`/`audit` when deep UX craft remediation is needed).
4. Keep `spec` and `standards` findings strictly separated: deduplicate identical `(file, line, root_cause)` entries **only within the same axis** — never merge findings across `spec` and `standards` or rerank them into a single cross-axis winner. Classify each finding's severity (`critical`, `high`, `medium`, `low`), `axis` (`spec` | `standards`), and `classification` (`hard_violation` | `judgement_call`).

---

## Step 4: Write Outputs & `report.md`

Write `{run_dir}/outputs/review.json` matching `review-findings/1.0` (keep all core top-level keys and finding fields intact; `axes` and per-finding `axis`/`classification` are additive compatible fields):

```json
{
  "_meta": {
    "kind": "review-findings",
    "schema": "review-findings/1.0",
    "role": "primary",
    "alias": "latest-review"
  },
  "target": "<resolved target>",
  "tier": "standard",
  "dimensions_reviewed": ["correctness", "security", "performance", "architecture"],
  "axes": {
    "spec": { "status": "reviewed", "total": 0, "worst_id": null },
    "standards": { "status": "reviewed", "total": 0, "hard_violations": 0, "judgement_calls": 0, "worst_id": null }
  },
  "summary": {
    "critical": 0,
    "high": 0,
    "medium": 0,
    "low": 0,
    "total": 0,
    "remaining_actionable": 0
  },
  "zero_residual_check": {
    "status": "passed",
    "checks": []
  },
  "findings": [
    {
      "id": "REV-001",
      "axis": "standards",
      "classification": "hard_violation",
      "dimension": "correctness",
      "severity": "high",
      "file": "path/to/file.ts",
      "line": 42,
      "title": "Short title",
      "description": "What is wrong and why",
      "evidence": "Observed code/diff or command output at file:line (or quoted spec/rule)",
      "suggestion": "Concrete remediation",
      "status": "open"
    }
  ]
}
```

Write `{run_dir}/report.md` with frontmatter (`verdict`, `summary`, `constraints`, `decisions`, `concerns`, `next`) and fixed sections (`Summary`, `Conclusion/Verdict`, `Discussion/Retrospective`, `Artifacts`, `Handoff/Next`). Inside `Summary` / `Discussion/Retrospective`, present `## Standards` and `## Spec` side by side (verbatim or lightly edited from each axis reviewer, never merged or reranked across axes, noting `"no spec available"` if Spec was skipped), include a **Visual Check Status** summary when UI files changed (and a compact Mermaid dependency/impact diagram for cross-module architectural changes), and end with a one-line per-axis summary (count + worst issue *within each axis*). When `--visual` is set (or for multi-module/UI reviews needing visual side-by-side inspection), also write `{run_dir}/outputs/visual-report.html` per `evolve skill workflows show visual-report`:
- Set `verdict: ready` when `critical == 0` and `high == 0` and the zero-residual check passes -> seal with `--verdict done`.
- Set `verdict: ready_with_concerns` whenever the review itself finished (`outputs/review.json` written) yet actionable findings remain (`critical > 0`, `high > 0`, non-blocking findings, or unverified limits) so `latest-review` seals and binds for downstream remediation -> route `next` to `ew-plan` (`--gaps`, `needs: [latest-review]`) for cross-module/multi-step fixes, `ew-companion` (`<= 3` findings, single concern), or `ew-odyssey-review` (iterative standalone repair campaign) — **never** route `next` directly to `ew-execute` — and seal with `--verdict done_with_concerns`.
- Set `verdict: blocked` or `failed` ONLY when `ew-review` itself cannot perform a grounded review (missing/unreadable target or broken verification tools) -> do **not** call `run complete`; transition via `evolve run transition` per `evolve skill workflows show session-run`.

---

## Step 5: Seal Run & Knowledge Hooks

Seal or transition the Run per `evolve skill workflows show session-run` (all required reads satisfied; no extra exit gates on `ew-review`). When high-signal recurring defect patterns surface, record them via `evolve kb add "<pattern>" --kind episodic --type error --scene review --session <session_id> --evidence <source-ref>` and stage reusable constraints in `report.md` frontmatter; defer `ew-knowledge` closeout/`kb harvest` until after `evolve session complete` (never run `kb harvest` mid-chain).
