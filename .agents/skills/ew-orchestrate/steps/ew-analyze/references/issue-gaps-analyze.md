# Issue Gaps Analysis — Root-Cause Protocol

Root-cause analysis for tracked or Run-surfaced issues, triggered by `ew-analyze --gaps [ISS-ID]`. Supports a single issue (`ISS-ID`) or batch (all `open` / `registered`) with grouping and parallel analysis, writing structured `issues[]` enrichment into `outputs/findings.json` (`mode: "gaps"`) for downstream `ew-plan --gaps`.

## Input & Issue Sources

- `ISS-ID` (optional): specific issue to analyze; if omitted, analyze all `open` / `registered` issues.
- `DEPTH`: `standard` or `deep` (default `standard`).
- **Authorized sources**:
  1. **Evolve-native Run/Session inputs (default)**: issue IDs/context passed in the Run task or bound upstream artifacts (`latest-debug`, `latest-test` `outputs/issue-candidates.json`, `latest-review`, `latest-verification`).
  2. **External issue tracker (only with explicit user authorization)**: read issues via the project's authorized tracker CLI/MCP; never mutate external tracker state unless the user explicitly authorized tracker writes (`-y` does NOT authorize external tracker mutations).

## Unified Issue Status Lifecycle

`open` / `registered` -> `analyzed` (set by `ew-analyze --gaps`, treating legacy `diagnosed` as alias for `analyzed`) -> `planned` (set by `ew-plan --gaps`) -> `resolved` (synced by `ew-execute` on convergence).

## Pipeline

```
Load Issues -> Classify & Group -> Gather Context -> Parallel Analysis -> Enrich Issue Records -> Write outputs/findings.json
```

1. **Load issues**: if `ISS-ID` is given, load that record from the Run context or authorized tracker (`E_ISSUE_NOT_FOUND` if missing) and skip grouping; otherwise load all `open`/`registered` records (`E_NO_ISSUES` if none).
2. **Classify & group (batch only)**: group by same location prefix (first 2 path segments) or shared `affected_components`; ungrouped items stay 1-issue groups `{ group_id, label, issues[], shared_context_keywords[] }`. Display breakdown unless `-y`.
3. **Gather codebase context (per group)** — complete the Run Knowledge Gate (`evolve skill workflows show session-run`) first, then merge keywords (`title`, `description`, `location`, `affected_components`):
   - **standard**: `evolve code search` per group + targeted file reads.
   - **deep**: multi-query `evolve code search` + parallel exploration subagents (`cli-explore-agent` / `cli_explore_agent`, error handling, data flow, dependencies); join all subagents before proceeding.
4. **Parallel analysis (per group)** — mandatory, not substitutable by manual reads alone:
   Dispatch per group via host subagent (`workflow-analyzer` / `workflow_analyzer`, via `invoke_subagent` or host equivalent, joining every spawned subagent before advancing; fall back to inline instructions when absent) or `evolve agent exec "<group prompt>" --to codex|agy|claude|omp --mode analysis` (pass `--session <session> --run <run>` when using `--to codex` or `--to omp` in a Run; OMP is a one-shot restricted worker, Agy stays standalone). For each issue output `{ iss_id, root_cause, impact, related_files[], confidence, suggested_approach, cross_refs[] }` backed by `file:line` evidence. Retry once with a narrowed prompt or fallback executor on failure, noting any unverified scope explicitly.
5. **Enrich issue records (`status: "analyzed"`)**: build `issue.analysis = { root_cause, affected_files, impact_scope, fix_direction, confidence, cross_refs, analyzed_at, tool, depth }` with `status: "analyzed"` inside `outputs/findings.json#issues[]` (and optional `evidence/issue-analysis.json`). Sync `analysis` back to an external tracker ONLY when the user explicitly authorized external tracker writes (re-read to confirm when synced); otherwise keep the external tracker untouched.
6. **Output `outputs/findings.json` & `report.md`**: aggregate `issues[]`, grouped root-cause findings, cross-group dependencies, and **Locked** (evidence-derived) / **Free** (planner discretion) decisions into `outputs/findings.json` (`_meta` matching `findings/1.0` + `alias: "current-analysis"`, `mode: "gaps"`), summarizing highlights in `report.md` (`next` -> `ew-plan --gaps`).
