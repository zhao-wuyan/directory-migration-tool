# Workflow: ew-init

Project onboarding: `Pre-flight & State Detection -> Greenfield Questioning or Brownfield Scan -> Knowledge & CodeGraph Setup -> Package & Seal`.

## Step 1: Detect State & Check Bootstrap (`state-detected` gate)

1. Check Git main-worktree status (`git rev-parse --git-common-dir`), `AGENTS.md` / `skills/`, `.evolve/`, `.codegraph/`, `wiki/`, source manifests (`package.json`, `tsconfig.json`, `pyproject.toml`, `go.mod`, etc.), existing **domain/design assets** (`GLOSSARY.md`, `GLOSSARY-MAP.md`, `docs/adr/`, `PRODUCT.md`, `DESIGN.md`), **automated guardrail baseline** (`build`/`test`/`lint`/`typecheck` scripts, `.husky/` / `.pre-commit-config.yaml` / `lefthook`, `.github/workflows/`, `dependency-cruiser` or ESLint boundary rules), and legacy `.workflow/`.
2. Complete the Run Knowledge Gate (`evolve skill workflows show session-run`, CLI `--task init` when using `--reads`) to load any already-recorded project rules.
3. Classify in strict priority order (`Path A (greenfield)` -> `Path D (initialized)` -> `Path C (legacy-maestro)` -> `Path B (brownfield)` per [preparation](prepare.md)). Under Path D without `--refresh`, emit idempotent status into `outputs/init-report.json` (`_meta: { kind: "init-report", schema: "init-report/1.0", role: "primary", alias: "current-init" }`) AND a non-mutating snapshot `outputs/context-package.json` (`_meta: { kind: "context-package", schema: "context-package/1.0", role: "attachment", alias: "upstream-context" }`) without modifying existing Wiki/Session state (rendering `outputs/visual-report.html` only if `--visual` is requested), then skip to Step 4. With `--refresh`, re-run CodeGraph, domain-asset, and guardrail baseline checks before Step 3 without overwriting active Wiki entries.

## Step 2: Gather Context by Path

- **Path A (Greenfield)** (skip questioning if `--from` / `upstream-context` or `-y` with `@file`):
  - Conduct focused discovery via structured prompt (`ask_question` or host equivalent) covering: vision & core value (the one thing that must work), target users, 2–5 primary goals, tech preferences, hard constraints, and non-goals.
- **Path B (Brownfield)** / **Path C (Legacy)** / **Path D (`--refresh`)**:
  1. Ensure code graph is indexed via `evolve code build` (if `.codegraph/` is absent or stale).
  2. Spawn up to 4 read-only parallel subagents (`workflow-codebase-mapper` / `workflow_codebase_mapper`, via `invoke_subagent` or host equivalent; fall back to inline role instructions when absent; **always joined or cancelled before Step 3**) mapping `stack`, `architecture`, `conventions`, and `pitfalls` -> write summaries under `{run_dir}/evidence/`.
  3. Under Path C, run `evolve kb import` (dry-run preview; pass `--knowhow-only` if requested) and summarize pending Spec/Knowhow migration candidates cleanly for `ew-knowledge`.

## Step 3: Knowledge Seeding & Artifact Packaging (`context-initialized` gate)

1. Never write `wiki/entries/` directly. Ensure `wiki/purpose.md`, `wiki/schema.md`, and `wiki/entries/` exist via `evolve kb init` when missing (`ew-init` initializes templates only, leaving `wiki/entries/` governed).
2. Under Brownfield/Legacy paths, when `README.md` or key architecture docs exist and extraction LLM settings are configured (or suggest `evolve kb config --scope user --host agy` if missing), optionally ingest core docs with `evolve kb ingest <doc> --propose` (or prepare `evolve kb propose <change.json> --evidence <receipt-or-session>` for discovered conventions) — always deferring `evolve kb apply` to explicit user approval via `ew-knowledge` (never treat `-y` as Wiki approval).
3. Write `{run_dir}/outputs/project-brief.md` (`kind: project-brief`, `schema: project-brief/1.0`; vision, core value, validated capabilities for brownfield, active goals, constraints, non-goals, key decisions, **System & Module Architecture Map** as a grounded Mermaid diagram for codebases, **Domain & Design Assets** index, and **Guardrail Baseline** matrix covering build/test/lint/typecheck/hooks/CI/import boundaries).
4. Write `{run_dir}/outputs/context-package.json` (`_meta: { kind: "context-package", schema: "context-package/1.0", role: "attachment", alias: "upstream-context" }`, `domain`, `requirements[]`, `constraints[]`, `non_goals[]`, `insights[]`, `references[]` including discovered `GLOSSARY.md` / `docs/adr/` / `DESIGN.md` pointers) and `{run_dir}/outputs/init-report.json` (`_meta: { kind: "init-report", schema: "init-report/1.0", role: "primary", alias: "current-init" }`, `path`, `codegraph_status`, `wiki_entries_count`, `domain_assets`, `guardrail_health`, `legacy_import_preview`, `suggested_next`).
5. When `--visual` is passed (or on complex multi-module brownfield onboarding), generate `{run_dir}/outputs/visual-report.html` following `evolve skill workflows show visual-report` (`init` view: interactive Mermaid module graph, domain/ADR asset inventory, and guardrail status badges — strictly read-only over observed JSON/CodeGraph evidence).

## Step 4: Seal & Route Next

Write `{run_dir}/report.md` (`verdict: ready|ready_with_concerns|blocked|failed`; include an inline **Onboarding Snapshot & Guardrail Summary** table and `next` proposing `ew-roadmap`, `ew-blueprint`, `ew-grill`, or `ew-brainstorm` based on user intent, plus missing-guardrail or legacy-migration follow-ups) and seal or transition the Run per `evolve skill workflows show session-run` (`--gate state-detected --gate context-initialized`). All JSON outputs must include matching `_meta` and Markdown outputs matching YAML frontmatter.
