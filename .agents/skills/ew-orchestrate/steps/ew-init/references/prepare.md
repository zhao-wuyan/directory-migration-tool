# Pre-flight: ew-init

Confirm repository state, bootstrap prerequisites, and Evolve-native boundaries before running [workflow](workflow.md).

## Modes & Input

- **Interactive (default)**: auto-detects greenfield vs initialized vs legacy Maestro vs brownfield, then collects missing project context.
- **`-y`**: auto-initializes from `--from`, `@file`/prompt text, or brownfield codebase scan without interactive interviews (`-y` still NEVER authorizes unapproved Wiki publication).
- **`--refresh`**: when Path D (`initialized`) is detected, re-scans codebase / CodeGraph, domain assets, and guardrail baseline, refreshing `outputs/init-report.json`, `outputs/project-brief.md`, and `outputs/context-package.json` without mutating existing active Wiki entries or Session state.
- **`--visual`**: generate a self-contained HTML onboarding & architecture visual report (`outputs/visual-report.html` per `evolve skill workflows show visual-report`) alongside the Markdown Mermaid map in `outputs/project-brief.md`.
- **`--from <alias>`**: consume bound `upstream-context` (`context-package.json`) to populate vision, goals, locked constraints, non-goals, and terminology without repeating discovery.

## State Detection Rules (`state-detected` gate)

Inspect repository root before scanning or proposing writes and evaluate in **strict mutually exclusive priority order**:
- **Worktree guard**: refuse to initialize a fresh project baseline inside a linked `.git` temporary worktree; run initialization on the main repository checkout.
1. **Path A (`greenfield`)**: no application source files yet -> run deep questioning ("What do you want to build?" -> core value, target users, 2–5 primary goals, constraints/non-goals, tech preferences).
2. **Path D (`initialized`)**: `.evolve/` AND `wiki/` already present -> warn idempotently (`E002`), report existing status (plus optional `evolve kb import` check if leftover `.workflow/` remains), and never overwrite active Wiki or Session state silently (unless `--refresh` refreshes CodeGraph/outputs).
3. **Path C (`legacy-maestro`)**: existing codebase AND legacy `.workflow/specs/` or `.workflow/knowhow/` present (without both `.evolve/` and `wiki/` yet) -> run `evolve code build` + codebase scan AND surface `evolve kb import` migration preview (`ew-knowledge`) rather than duplicating knowledge.
4. **Path B (`brownfield`)**: existing codebase, without `.evolve/`+`wiki/` and without legacy `.workflow/specs/` or `.workflow/knowhow/` -> run `evolve code build` + multi-angle codebase onboarding scan.

## Boundaries & Invariants

- **No legacy `.workflow/` generation**: NEVER create `.workflow/project.md`, `.workflow/state.json`, `.workflow/config.json`, or `.workflow/specs/`. State lives in `.evolve/` SQLite; initial Wiki structure uses `evolve kb init` (`purpose.md` + `schema.md` + `wiki/entries/`), and durable conventions belong in `wiki/entries/` only via `evolve kb propose` + explicit user-approved `evolve kb apply` (or `evolve kb ingest <doc> --propose` for doc ingestion + staged proposals).
- **Idempotent & scope-guarded**: only establish project identity, code/knowledge baseline, and initial `context-package.json` — do NOT pre-split `ew-roadmap` Sessions or `ew-plan` tasks.
- **Bootstrap note**: if the repository has not yet installed/committed `skills/` (so no Session Run can bind yet), run pre-Session bootstrap (`evolve skill install --scope project --yes`, `git add`/`commit` Skills, `evolve code build`) before opening the onboarding Session.
- **Exit gates**: `state-detected` (priority A -> D -> C -> B resolved cleanly) and `context-initialized` (`outputs/init-report.json` + `outputs/context-package.json` ready—including an idempotent non-mutating snapshot under Path D—and CodeGraph built when codebase exists).
