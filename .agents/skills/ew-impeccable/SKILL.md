---
name: ew-impeccable
description: "Use when the user wants single-pass frontend design, redesign, shape, critique, audit, polish, simplify, harden, animate, or otherwise improve a frontend interface: websites, landing pages, dashboards, product UI, components, forms, settings, onboarding, empty states, and native iOS or Android screens. Handles UX review, hierarchy, layout, typography, color, motion, UX copy, accessibility, responsive behavior, edge cases, and capturing or documenting design systems (DESIGN.md, tokens). Also for making bland UI bolder, loud UI quieter, live browser iteration on UI elements, or ambitious visual effects. Not for backend-only, non-UI tasks, or multi-round iterative UI audit/fix campaigns (route those to ew-odyssey / ew-odyssey-ui)."
version: 4.9.2
user-invocable: true
session-mode: run
contract:
  contract_version: 2.1
  consumes: []
  produces: []
argument-hint: "[shape · audit|critique · animate|bolder|colorize|delight|layout|overdrive|quieter|typeset · adapt|clarify|distill · harden|onboard|optimize|polish · init|document|extract|live|generate · hooks|doctor|foundations|pin|teach] [target]"
license: Apache-2.0
---

<!-- Modified for Self-Evolving Workflow. See UPSTREAM.md. -->

Impeccable 4.9.2 for Self-Evolving Workflow. Its design playbooks, launcher scripts (`scripts/impeccable.mjs`, `scripts/impeccable`, `scripts/impeccable.cmd`), and SHA-256 manifest (`scripts/engine-checksums.txt`) are bundled with this Skill; the pinned Rust engine binary itself downloads over the network on first run and verifies against `scripts/engine-checksums.txt` unless a pre-cached or `IMPECCABLE_BIN` binary is already present. See [provenance](UPSTREAM.md).

Invoke via your host's Skill or slash command (`audit <target>` / `shape <feature>`), or ask the host to read `skills/ew-impeccable/SKILL.md` with your target.

For a bound Evolve Run, use the command and target in its packet, read this entry and the selected playbook with `evolve run read skill`, resolve `<impeccable-base>` and verify script parity against `<bound-commit>` (see **Self-Evolving Workflow Adapter** below), and record verification in the Run `report.md` (`evolve skill workflows show session-run`).

- **Knowledge Gate first (`evolve search --mode context`, distinct from `impeccable context`)**: In an Evolve-managed project, satisfy the Evolve Knowledge Gate **before** opening project files or running `impeccable context`. Inside a bound Evolve Run, follow `evolve skill workflows show session-run` (read `session-priors` via `evolve run read artifact` only when `lookup_scopes` matches; otherwise use CLI `evolve search "<query>" --mode context --session <session> --reads "<run_dir>/knowledge-reads.json" [--task <task>] [--path <path>]`). In standalone invocations, run `evolve search "<1-3 keywords>" --mode context` (see `evolve skill workflows show ew-knowledge`). `PRODUCT.md`, `DESIGN.md`, and `.impeccable/*` are command-authorized workspace outputs.

Core principles:
- Finish the job. No hedging, no shortcuts: the deliverable is complete, except assets the user must provide.
- Scale ambition to the surface's mode (see Modes). Persuade and Experience get full commitment: bold, distinct work with a point of view. On Operate and Read, ambition shows as precision and restraint, and patterns the visitor already knows earn their place.
- Verify by looking at the rendered result at the sizes it ships at, and batch the checks: fix what one look shows before looking again, rather than screenshotting after every tweak.

## Setup

1. Run `node <impeccable-base>/scripts/impeccable.mjs context` (**`impeccable context`**, the design-engine context command — distinct from the Evolve Knowledge Gate `evolve search --mode context`) once per session, keeping `cwd` at the user's project root. In standalone calls, `<impeccable-base>` is the absolute directory of the loaded `SKILL.md`; in a bound Evolve Run, resolve `<impeccable-base>` per **Base Directory & Bound-Run Script Verification** below. That same base directory resolves every `node <impeccable-base>/scripts/impeccable.mjs <verb>` command in this skill and its references (`<impeccable-base>/scripts/impeccable` or `impeccable.cmd` on Windows also invoke the same engine launcher). The launcher runs the pinned Impeccable engine binary (using a local/cached binary, `IMPECCABLE_BIN`, or `PATH`, or downloading and SHA-256 verifying it against `scripts/engine-checksums.txt` on first run; offline or sandboxed environments must pre-populate the cache or set `IMPECCABLE_BIN`). Pass a named source file or route as `--target <path>`. It loads `PRODUCT.md`, `DESIGN.md`, the matching surface brief, and native-platform guidance when applicable; follow its directives and do not rerun it.
2. Load the request's playbook: its Commands-table reference for an explicit/implied sub-command, or [reference/new-work.md](reference/new-work.md) for a new surface, a replacement visual world, or a build from an approved comp. Some playbooks come in parts; read the part this request needs in the same step, not when the playbook later mentions it: [reference/new-work-world.md](reference/new-work-world.md) when a visual world is created or replaced (a redesign, a new look, or no established world), [reference/new-work-comp.md](reference/new-work-comp.md) for a build from an approved comp or on a comp-led build path, and [reference/critique-rubric.md](reference/critique-rubric.md) for a critique whose Assessment A runs in this context (a native platform, or no sub-agent tool); a delegated Assessment A gets the rubric's path in its prompt instead. Inspect target and incumbent visual truth before editing. When the app cannot run, start with committed visual-regression goldens or screenshot fixtures; verify target and freshness against current tokens, CSS, components, or assets, resolve conflicts, and compare theme/variant captures.
3. After resolving analysis and direction, read [reference/craft-floor.md](reference/craft-floor.md) immediately before any UI edit, including small refinements. It carries the quality floor, the absolute bans, and the reflexes no detector catches. Do not load it for planning-only work.

**Launcher unavailable:** On refusal or failure, send a separate message **before the next tool call**: “Context loading did not run; I’ll read the existing project context directly.” Then read existing `PRODUCT.md` and `DESIGN.md` without inventing missing context (for a bare no-argument invocation, follow the static menu fallback in [reference/routing.md](reference/routing.md)), follow applicable steps 2–3, including reading [reference/craft-floor.md](reference/craft-floor.md) immediately before any UI edit, and continue through permitted tools. This applies to planning and editing; launcher failure alone does not block either.

## How to design

- **The brief wins.** Honor pinned aesthetics, eras, materials, fonts, and palettes even when they conflict with a saturated-pattern warning. Redirecting a clear brief toward your taste is failure.
- **Refinement preserves; redesign replaces.** Refinement keeps the incumbent identity, behavior, copy, and everything outside scope. Ask before replacing factual copy or adding claims. Redesign keeps product truth, content, function, native affordances, and constraints, but treats the old look as evidence and anti-reference; choose a replacement world in new-work and replace DESIGN.md. Never split the difference into polish on the discarded look.
- **Loaded symbols stay out of the decoration.** A subject's world does not license emblems tied to militarism, supremacy, or hate movements as motifs, badges, or ornament, such as the Rising Sun flag's rays, the Confederate battle flag, or Nazi-era insignia and their stylised variants; reach for that world's neutral forms instead. Content that documents such a symbol as fact stays as it is.
- **Visual authority is evidence, not a filename.** Missing DESIGN.md alone does not make a project greenfield; new-work decides whether to preserve, expand, or replace the incumbent world.

## Modes

The mode names what the visitor's success looks like on this surface.

- **Persuade:** the visitor decides and acts; design is the product. Landing pages, marketing, campaigns, pricing. Earn attention and action. Ship real imagery when the brief needs it; follow the committed world, not category habit.
- **Operate:** the visitor completes a task. App UI, dashboards, editors, admin, settings, tools. Scanability, consistency, native expectations, and the real usage scene outrank expression. Brand lives in precise details.
- **Read:** the visitor understands something. Docs, articles, guides, help, changelogs. Structure for comprehension, then make the reading experience worth staying in.
- **Experience:** the visitor is inside the work itself. Portfolios, galleries, showcases. Let the artifact lead from the first viewport; the interface recedes.

Choose the mode from the requested surface, not the product, and persist it only in that surface brief. A tool's landing page is still Persuade; a fashion house's documentation is still Read; a docs index is Read, not Persuade. See [new-work.md](reference/new-work.md) for new surfaces and [operate.md](reference/operate.md) for deeper Operate/Read guidance.

## Commands

| Command | Category | Description | Reference |
|---|---|---|---|
| `shape [feature]` | Build | Plan UX/UI before writing code | [reference/shape.md](reference/shape.md) |
| `init` | Build | Capture durable product context in PRODUCT.md | [reference/init.md](reference/init.md) |
| `document` | Build | Generate DESIGN.md from existing project code | [reference/document.md](reference/document.md) |
| `extract [target]` | Build | Pull reusable tokens and components into design system | [reference/extract.md](reference/extract.md) |
| `critique [target]` | Evaluate | UX design review with heuristic scoring | [reference/critique.md](reference/critique.md) |
| `audit [target]` | Evaluate | Technical quality checks (a11y, perf, responsive) | [reference/audit.md](reference/audit.md) · native: [reference/audit.native.md](reference/audit.native.md) |
| `polish [target]` | Refine | Final quality pass (alignment, spacing, micro-details) before shipping | [reference/polish.md](reference/polish.md) |
| `bolder [target]` | Refine | Amplify safe or bland designs | [reference/bolder.md](reference/bolder.md) |
| `quieter [target]` | Refine | Tone down visual intensity and noise while preserving existing elements | [reference/quieter.md](reference/quieter.md) |
| `distill [target]` | Refine | Strip redundant elements and structural complexity to essence | [reference/distill.md](reference/distill.md) |
| `harden [target]` | Refine | Production-ready: errors, i18n, edge cases | [reference/harden.md](reference/harden.md) |
| `onboard [target]` | Refine | Design first-run flows, empty states, activation | [reference/onboard.md](reference/onboard.md) |
| `animate [target]` | Enhance | Add purposeful functional animations and motion | [reference/animate.md](reference/animate.md) |
| `colorize [target]` | Enhance | Add strategic color to monochromatic UIs | [reference/colorize.md](reference/colorize.md) |
| `typeset [target]` | Enhance | Improve typography hierarchy and fonts | [reference/typeset.md](reference/typeset.md) |
| `layout [target]` | Enhance | Fix spacing, rhythm, and visual hierarchy | [reference/layout.md](reference/layout.md) |
| `delight [target]` | Enhance | Add brand personality and memorable touches at earned moments | [reference/delight.md](reference/delight.md) |
| `overdrive [target]` | Enhance | Push past conventional limits | [reference/overdrive.md](reference/overdrive.md) |
| `clarify [target]` | Fix | Improve UX copy, labels, and error messages | [reference/clarify.md](reference/clarify.md) |
| `adapt [target]` | Fix | Adapt for different devices and screen sizes | [reference/adapt.md](reference/adapt.md) · native: [reference/adapt.native.md](reference/adapt.native.md) |
| `optimize [target]` | Fix | Diagnose and fix UI performance | [reference/optimize.md](reference/optimize.md) |
| `live` | Iterate | Visual variant mode: pick elements in the browser, iterate on alternatives | [reference/live.md](reference/live.md) |
| `generate [n] [action] [element]` | Iterate | Variants, versions, or alternatives of a named element to choose from in the live browser; no manual picking | [reference/generate.md](reference/generate.md) |
| `hooks [action]` | Utility | Manage design detector hooks (`status`, `on`, `off`, `reset`, `ignore-*`) | [reference/hooks.md](reference/hooks.md) |
| `doctor [target]` | Utility | Diagnose and repair Impeccable artifact and config drift | [reference/doctor.md](reference/doctor.md) |
| `foundations [id]` | Utility | Check and fix shipped product-foundation essentials | [reference/foundations.md](reference/foundations.md) |
| `pin <pin\|unpin> <command>` | Utility | Create or remove explicit `/<command>` shortcut skills | `node <impeccable-base>/scripts/impeccable.mjs pin` |
| `teach` | Utility | Alias for `init` (capture durable product context in PRODUCT.md) | [reference/init.md](reference/init.md) |

Routing:

- **No argument:** read [routing.md](reference/routing.md) and present its context-aware menu (or its static grouped menu fallback when `impeccable context` or `impeccable signals` is unavailable); always exclude deprecated `craft` and never auto-run a command.
- **Explicit or clearly implied request to run a command:** load its reference (native variant on native platforms) and follow it. Ask once if two commands fit. Use these disambiguation boundaries:
  - `delight` vs `polish` / `animate`: `polish` fixes alignment, spacing, consistency, and pre-launch defects; `animate` adds purposeful functional motion and transitions for state or continuity; `delight` adds brand personality and memorable moments where earned.
  - `distill` vs `quieter`: `distill` removes redundant elements, options, and structural or information complexity; `quieter` keeps existing elements while toning down visual intensity (saturation, contrast, heavy ornament, visual noise).
- **Workflow or command-selection question:** read [Workflow questions](reference/routing.md#workflow-questions).
- **Otherwise:** treat the request as general design work. Missing `PRODUCT.md` routes a new surface or replacement world through `init`, then `new-work`; a narrow refinement of existing code proceeds on the incumbent implementation as `impeccable context` directs, offering `init` afterward rather than blocking on it.
- `teach` aliases [init.md](reference/init.md). [craft.md](reference/craft.md) is a deprecated compatibility alias for ordinary `new-work` and adds nothing—always omit `craft` from menus and recommendations. `shape` owns task discovery, then enters `new-work` only for visual-world and surface-concept decisions.

After `init` writes `PRODUCT.md`, resume without rerunning `impeccable context`; `init` loads the native platform reference itself when the platform it recorded is `ios`, `android`, or `adaptive`.

- **Shortcut pin, hooks, doctor & foundations**: `node <impeccable-base>/scripts/impeccable.mjs pin <pin|unpin> <command>` manages explicit `/<command>` shortcuts (relay errors verbatim; generated shortcut skills include `disable-model-invocation: true`, support `hooks`/`doctor`/`foundations`/`teach`, and reject creating new `craft` pins). Load [reference/hooks.md](reference/hooks.md) for `/ew-impeccable hooks <action>`, [reference/doctor.md](reference/doctor.md) for `/ew-impeccable doctor` (only surface `CONTEXT_STALE` without repairing unasked unless flagged `auto`), and [reference/foundations.md](reference/foundations.md) for `/ew-impeccable foundations [id]`.

## Self-Evolving Workflow Adapter

### Boundary with `ew-odyssey-ui` & Orchestration

- `ew-impeccable` owns **single-pass** frontend work: `shape` planning, read-only evaluation (`audit`, `critique`), targeted refinement/enhancement/fix passes (`polish`, `harden`, `adapt`, `extract`, etc.), `new-work`, and interactive `live` variant sessions.
- **Multi-round iterative UI campaigns** (`SURVEY -> AUDIT -> FIX -> VERIFY` loops across a surface or app) belong to `ew-odyssey` (`ew-odyssey-ui`).
- When `ew-odyssey-ui` or an orchestrator coordinates UI edits, it MUST keep Impeccable design guardrails active: run `impeccable context`, honor `PRODUCT.md` and `DESIGN.md`, and read [reference/craft-floor.md](reference/craft-floor.md) before editing UI code.

### Base Directory & Bound-Run Script Verification

- **Standalone invocation**: `<impeccable-base>` is the absolute directory containing the loaded `SKILL.md` (`<project>/skills/ew-impeccable` or the installed host skill directory such as `~/.gemini/config/skills/ew-impeccable`), with `cwd` at the project root.
- **Bound Evolve Run (`session-mode: run`)**: `evolve run read skill` reads only `.md` files from `skill_ref.commit` (`<bound-commit>`) and does not materialize executable scripts. Resolve `<impeccable-base>` to `<project>/skills/ew-impeccable` when present in the project (otherwise the installed host skill directory), keep `cwd` at the project root, and verify script consistency against `<bound-commit>` before executing `scripts/impeccable.mjs`:
  - Run `git diff --quiet <bound-commit> -- skills/ew-impeccable/scripts` (when scripts are untracked, gitignored, or resolved from an installed path, compare `<impeccable-base>/scripts/VERSION` against `git show <bound-commit>:skills/ew-impeccable/scripts/VERSION` if tracked).
  - Record the resolved `<impeccable-base>`, `<bound-commit>`, and script-consistency result in the Run's `report.md`; if tracked scripts under `skills/ew-impeccable/scripts` differ from `<bound-commit>`, record the drift in `report.md` and escalate rather than assuming revision-bound script parity.

### Run Lifecycle Mapping

- **Single-turn commands** (`audit`, `polish`, `harden`, `adapt`, `optimize`, `bolder`, `quieter`, `distill`, `clarify`, `colorize`, `typeset`, `layout`, `delight`, `animate`, `onboard`, `extract`, `doctor`, `hooks`, `foundations`, `pin`): finish execution and verification, write `report.md`, run `evolve run check`, and seal with `evolve run complete`.
- **Interactive pause commands (`shape`, `critique`, `overdrive`, and mid-flow user questions in `init`, `document`, `generate`, `new-work`)**:
  - While paused awaiting user answers (discovery questions, `critique` closing questions, or `overdrive` direction choice), **keep the Run in `running` state** across turns; do NOT call `evolve run complete` before the user replies.
  - Resume the same Run once the user replies and seal via `evolve run check` + `evolve run complete` after the terminal deliverable is finished (`shape`: confirmed brief returned with no code written; `critique`: Action Summary delivered after answers, or immediately after `Questions skipped: <reason>` when `< 3` Priority Issues or no answer channel exists; `overdrive`: after direction selection, implementation, and browser verification).
  - If unattended with no human answer channel or aborted mid-flow, document assumptions or partial state in `report.md` and either complete `shape` with `--verdict done_with_concerns` (assumptions plainly marked) or transition via `evolve run transition ... blocked` per `evolve skill workflows show session-run`.
- **`live` mode**: keep the Run `running` across `live-poll` iterations until `exit` cleanup finishes (or the user explicitly ends the session); then verify accepted edits, record changed files and cleanup status in `report.md`, and run `evolve run check` + `evolve run complete` (or `evolve run transition ... blocked|failed` if terminated before cleanup).

### Contract & Handoff Boundary (`produces: []`)

- `ew-impeccable` declares `produces: []` (no structured `outputs/*.json` artifact slot; a Run seals `report.md`, while `PRODUCT.md`, `DESIGN.md`, `.impeccable/*`, and edited source files live in the workspace).
- `shape` writes no code and emits no `current-plan` artifact; its deliverable is a user-confirmed conversational design brief (summarized in `report.md` when executed inside a Run) for downstream `ew-plan` or `ew-analyze` to absorb—never chain `shape` directly into `ew-execute` expecting a `current-plan` input.
- `audit` and `critique` are read-only evaluations whose findings feed follow-up actions (`/ew-impeccable polish`, `/ew-impeccable extract`, `ew-plan --gaps`, or `ew-odyssey-ui`).
- Code-modifying commands (`polish`, `harden`, `adapt`, `optimize`, `bolder`, `quieter`, `distill`, `clarify`, `colorize`, `typeset`, `layout`, `delight`, `animate`, `onboard`, `extract`, `overdrive`, `live`, `generate`, `new-work`) act as terminal workspace implementation/refinement steps rather than `current-plan` producers for `ew-execute`.

### Subagent Lifecycle & Critique Assessment Fallback

- Every subagent spawned by `ew-impeccable` playbooks (including `critique` Assessment A/B, `new-work` finish-reviewer/documenter/asset-producer, `animate` motion-reviewer, and `live` browser/manual-edit-applier) MUST be joined/awaited before acting on its output or ending scope, and cleanly cancelled on abort or failure (never fire-and-forget).
- For `critique` Assessment A/B: run A and B isolated and join both before synthesis. If **Assessment A** fails, times out, or returns an unusable review after join/cancel, retry a fresh isolated Assessment A once with [reference/critique-rubric.md](reference/critique-rubric.md) (without passing Assessment B output). If Assessment A still fails (or no subagent capability exists), run Assessment A inline in this context **before** reading Assessment B findings and prepend the mandatory degraded provenance banner on line 1 of the report: `⚠️ DEGRADED: single-context (Assessment A subagent failed/unavailable; inline design review)`. If Assessment B fails, fall back per `critique.md` only after joining/cancelling B and record the limitation in Run Notes.

### Host Tool Capability Mapping (Vendored Playbooks)

- Upstream vendored `reference/*.md` playbooks reference host-specific tool names (`ask_question`, `invoke_subagent`, `run_command`, `IsDaemon`, `schedule`, `browser_navigate`, `preview_start`, etc.). Interpret every such reference through the active host's equivalent capability:
  - Structured user question (`ask_question` / harness question UI) → active host's structured question tool, or concise plain-text options and wait when unavailable (`shape` marks assumptions plainly and stops if no answer channel exists).
  - Isolated subagent (`invoke_subagent` / `Agent` / `Task` / `spawn_agent`) → active host's fresh, non-forked subagent tool (`join`/cancel mandatory); fall back to `reference/degraded/*.md` inline execution with explicit disclosure/banner only when subagent capability is absent or declined.
  - Shell, background tasks, timers, and browser (`run_command`, `IsDaemon`, `schedule`, browser tools) → active host's shell runner, background process, timer, and browser tools.

### Playbook Precedence Overrides

- **`operate.md` vs committed visual world / `DESIGN.md`**: When generic defaults in [reference/operate.md](reference/operate.md) (such as `"Restrained"` gray ground with a single accent) conflict with the surface's committed visual world (for example [reference/mode-operate.md](reference/mode-operate.md), `reference/new-work-world.md`, or `reference/colorize.md`) or an established `DESIGN.md`, **the committed visual world and `DESIGN.md` win**; never regress a committed palette back to generic gray-and-accent.
- **`audit` / `critique` Recommended Actions whitelist**: When `audit` or `critique` uncovers widespread hard-coded colors, repeated patterns, or design-token drift across components, include `/ew-impeccable extract` in Recommended Actions (alongside the standard command list, before `/ew-impeccable polish`).

### Standalone Closeout

- During standalone (non-Run) `ew-impeccable` execution, persist non-obvious lessons via `evolve kb add --kind episodic` and **do not** run `kb harvest` mid-iteration or between turns. Only when the user explicitly requests closeout (`/ew-knowledge`) or confirms final standalone completion in an Evolve-managed project, seal reusable findings via `ew-knowledge` closeout (`evolve skill workflows show ew-knowledge`) using `evolve kb harvest skill <execution-id> --skill ew-impeccable --skill-version 4.9.2 --status <status> --project <source-project-id> [--transcript <messages.json>] [--knowledge-receipt <knowledge-reads.json>] [--session <session-id>] [--record-only]`. Inside an Evolve Run, only seal the Run with `evolve run complete` and defer `kb harvest` until after `evolve session complete`.
