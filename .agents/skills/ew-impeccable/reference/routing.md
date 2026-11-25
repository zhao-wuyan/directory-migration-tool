<!-- Modified for Self-Evolving Workflow. See UPSTREAM.md. -->

# Command guidance

## Workflow questions

Give advice without executing commands; the menu below is only for bare invocations. Consult relevant command references as needed for prerequisites and scope. Link to the [docs](https://impeccable.style/docs/) for the broader workflow guide. If the user also requests execution, follow that request.

Key command boundaries (see `SKILL.md` for full Self-Evolving Workflow adapter rules):

- **`ew-impeccable` vs `ew-odyssey-ui`:** `ew-impeccable` handles single-pass design, read-only evaluation (`audit`/`critique`), targeted refinement, and `live` iteration. Route multi-round iterative UI campaigns (`SURVEY -> AUDIT -> FIX -> VERIFY`) to `ew-odyssey-ui` while preserving `impeccable context`, `PRODUCT.md`, `DESIGN.md`, and `craft-floor.md`.
- **`delight` vs `polish` / `animate`:** `polish` fixes alignment, spacing, consistency, and defects; `animate` adds purposeful functional motion for state and continuity; `delight` adds brand character and memorable moments where earned.
- **`distill` vs `quieter`:** `distill` removes redundant elements, options, and structural or information complexity; `quieter` keeps existing elements while reducing visual intensity (saturation, contrast, ornament, visual noise).
- **`audit` / `critique` token drift:** When `audit` or `critique` uncovers widespread hard-coded colors or design-token drift across components, include `/ew-impeccable extract` in Recommended Actions (before `/ew-impeccable polish`).

## No-argument routing: the context-aware menu

Read this when the user invokes `/ew-impeccable` with no argument. They are asking "what should I do?" Make the menu context-aware instead of static. Always **exclude deprecated `craft`** from recommendations and menus, and include the **Utility** category (`hooks`, `doctor`, `foundations`, `pin`, `teach`) in the grouped full menu.

Setup has already satisfied the Evolve Knowledge Gate (`evolve search --mode context` / CLI `--reads`, distinct from `impeccable context`) and attempted `impeccable context`.

### Engine available (`impeccable context` succeeded)

If `impeccable context` reported `NO_PRODUCT_MD`, the project has no captured context yet: lead the menu with `/ew-impeccable init` as the top recommendation (one line on why) and still show the rest below (grouped by **Build, Evaluate, Refine, Enhance, Fix, Iterate, Utility**, excluding `craft`); don't silently jump into `init`. Otherwise run `node <impeccable-base>/scripts/impeccable.mjs signals` once and read its JSON (if `signals` fails, exits non-zero, or returns invalid JSON, drop to the **Static menu fallback** below), then lead with the **2-3 highest-value next commands**, each with a one-line reason pulled from the signals, followed by the full menu (the Commands table in `SKILL.md`, grouped by category, excluding `craft`). **Never auto-run a command; the recommendation is a suggestion the user confirms.**

Reason over the signals; there is no score to obey:

- `setup.hasDesign` false while `setup.hasCode` true → `document` (capture the visual system).
- `critique.latest` is `null` → the project has never been critiqued; for a set-up project with a real surface, offering `/ew-impeccable critique <surface>` is a strong default.
- `critique.latest` with a low `score` or non-zero `p0` / `p1` → `polish` (it reads that snapshot as its backlog and closes it when stale or cleared).
- `git.changedFiles` pointing at one surface → scope `audit` or `polish` to those files specifically, naming them.
- `devServer.running` true → `live` is available for in-browser iteration, and `generate` for one-shot variant runs on a named element; if false, don't lead with either. **`live`, `generate`, and the bundled `impeccable detect` are web-only.** If `setup.platform` is `ios`, `android`, or `adaptive`, don't lead with any of them; the browser overlay and the HTML rule engine don't apply to native app code.
- Otherwise group by intent (build new / improve what's there / iterate visually / utility maintenance), tailored to the current surface and `setup.platform`.

**If `scan.targets` is non-empty and `setup.platform` is not `ios`/`android`/`adaptive`, run `node <impeccable-base>/scripts/impeccable.mjs detect --json <scan.targets joined by spaces>` once** (the bundled detector over local files: no network, no npx; it reads HTML/CSS, so skip it for native projects). `scan.via` tells you what they are: `git-changes` (the markup/style files in your dirty tree, the most relevant set), `source-dir` (e.g. `src`, `app`), `html`, or `root`. Fold the hits into your picks: many quality / contrast hits → `audit` or `polish`; widespread hard-coded colors or repeated component drift → `extract`; a specific slop family → the matching command (gradient text or eyebrows → `quieter` / `typeset`, flat or gray palette → `colorize`, and so on). It's a real, current signal that beats guessing. If detect errors or the tree is large and slow, skip it and recommend the user run `audit` themselves; never block the suggestion on it.

### Static menu fallback (`impeccable context` or `impeccable signals` unavailable)

When `impeccable context` failed (first emit the `Launcher unavailable` message from `SKILL.md`) or `impeccable signals` exits non-zero or returns unusable output:

1. **Skip `impeccable detect`**; do not block on engine binaries.
2. **Inspect local files directly** (`PRODUCT.md`, `DESIGN.md`, and UI source directories) to choose 2-3 pointed recommendations:
   - Missing `PRODUCT.md` → lead with `/ew-impeccable init`.
   - `PRODUCT.md` present, `DESIGN.md` missing while UI code exists → suggest `/ew-impeccable document` (or `/ew-impeccable shape <feature>` for planned new work).
   - Both present or existing UI surface targeted → suggest `/ew-impeccable critique`, `/ew-impeccable audit`, or `/ew-impeccable polish` (and `/ew-impeccable doctor` or `hooks status` if configuration drift is suspected).
3. **Note in one line** that live signal and detector prioritization was skipped because engine signals were unavailable, then show the **full static grouped menu** from `SKILL.md`'s Commands table (**Build, Evaluate, Refine, Enhance, Fix, Iterate, Utility** — always **excluding deprecated `craft`**). **Never auto-run a command; wait for user confirmation.**

Keep it to 2-3 pointed picks with the exact command to type. The menu stays the fallback; the recommendation is the lede.
