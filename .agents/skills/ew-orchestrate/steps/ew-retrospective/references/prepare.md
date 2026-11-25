# Pre-flight: ew-retrospective

Confirm target Runs, active lenses, read-only source-artifact boundaries, and routing governance before executing [workflow](workflow.md).

## Modes & Flags

- **Default / `<session|run>`**: Analyze the bound Session (`evolve session get`) or target Run ID (`evolve run get`) execution/verification/review artifacts.
- **`--lens <technical|process|quality|decision>`**: Restrict to selected lens(es); default runs all four in parallel.
- **`--compare <prior-run>`**: Load a prior `retrospective.json` artifact via `evolve run read artifact` and emit a trend delta section.
- **`--no-route`**: Synthesize `outputs/retrospective.json` and `outputs/retrospective.md` without initiating downstream routing (`ew-knowledge`, `workflow-evolve`, `kb add --kind episodic`, or external issue tracking); classify every insight with `route: "skipped"` and record `routing_skipped: true` summary metadata so `insights-routed` holds honestly.
- **`-y`**: Skip optional interactive confirmation for non-Wiki steps (`kb add --kind episodic`, report formatting); reserve Wiki publication authorization (`kb propose` / `apply`) strictly for explicit human confirmation under `ew-knowledge`.

## Environment & Context-Pressure Audit Scope

- **Read repo guardrails first**: Inspect `package.json`/build scripts, pre-commit hooks, and CI workflows in read-only mode before proposing new checks so unwired or silently broken checks are surfaced directly (and an un-linted repo without hooks/CI is itself a high-priority finding).
- **Implementation-vs-Review context-pressure asymmetry**: Implementation steps carry the heaviest **context pressure** (exploration, editing, debugging); review steps carry the lightest (diff-scoped, no exploration). Route coding-standard enforcement to review (`ew-review` / review reference docs) or CI rather than expanding implementer instructions or `AGENTS.md`.
- **Steering & No-op audit**: Inspect `AGENTS.md` / `CLAUDE.md` (repo and global scope) and active Skills for oversized steering blocks, duplicated rules, trivial environment caches, and **no-op** instructions that fail to alter model behavior.

## Knowledge Gate & Boundaries

- Complete the Run Knowledge Gate (`evolve skill workflows show session-run`) to avoid re-proposing insights already documented in active Wiki entries.
- **Source artifacts are read-only**: Keep code, `verification.json`, `review.json`, `plan.json`, steering docs, and completed retrospective artifacts untouched; write only to `{run_dir}/outputs/` and `{run_dir}/report.md`.
- **Governed knowledge & evolution routes**: Route project knowledge through `ew-knowledge` (`closeout` / `kb propose`), mechanical/tooling fixes through `issue`, and Skill/workflow defects through `workflow-evolve` (`evolve session review` / `evolve skill draft`) rather than writing `wiki/entries/` or `.workflow/specs/` directly.
- **Exit gates**: `lenses-complete` (every requested lens ran independently without cross-suppression) and `insights-routed` (deterministic `INS-{8hex}` IDs + explicit route classification per insight, or explicit `route: "skipped"` + `routing_skipped: true` under `--no-route`).
