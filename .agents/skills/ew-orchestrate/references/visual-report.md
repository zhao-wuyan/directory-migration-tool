# Visual Inspection & Enhanced Report Standard

Shared reporting and visual inspection specification for `ew-init`, `ew-review`, `ew-verify`, `ew-retrospective`, `ew-odyssey` (`arch` / `ui`), and `ew-test`. Visual artifacts amplify human comprehension and evidence inspection without replacing primary JSON contracts or weakening verification invariants.

## 1. Two-Layer Report Model (Non-Destructive & Evidence-Bound)

1. **Layer 1 — Enhanced Markdown (`report.md`, `outputs/project-brief.md`, `outputs/retrospective.md`, `understanding.md`)**:
   - Preserve existing YAML frontmatter and required sections verbatim for backward compatibility.
   - Embed concise, evidence-grounded **Mermaid** diagrams where structure or flow carries more signal than prose (architecture module boundaries, `Key Links` wiring, before/after call collapse, or diff impact paths).
   - Pair prose conclusions with compact decision/metric tables (`Guardrail Baseline`, `Standards vs Spec` side-by-side summary, `Before vs After` delta, `Mechanical vs Judgement` triage).
2. **Layer 2 — Self-Contained Visual HTML Report (`outputs/visual-report.html` or OS Temp)**:
   - Generate when `--visual` is passed, or when visual side-by-side structural / UI comparison accelerates human decisions (`ew-odyssey-arch` candidate review, multi-module `ew-init`, or UI-touching `ew-review`).
   - **Placement rules (never pollute repo source)**:
     - Inside an active Evolve Run: write `{run_dir}/outputs/visual-report.html` (declared as `required: false` optional visual attachment; never replaces `.json` primary artifacts).
     - Outside a Run (or when keeping workspace/Run clean): write `<tmpdir>/evolve-<mode>-<timestamp>.html` (`$TMPDIR` / `/tmp` / `%TEMP%`) and report the absolute path to the user.
   - Use **Tailwind CSS CDN** + **Mermaid ESM CDN** (`theme: "neutral"`) + **hand-built HTML/SVG** only — zero build step, zero app JS framework, static and offline-inspectable in any browser or built-in viewer.

## 2. Visual Truth & Anti-Drift Rules

- **Rendered visual check when UI changes**: When a diff touches UI surfaces (`.tsx/.jsx/.vue/.svelte/.css/.html`, routes, or design tokens), inspect rendered output across desktop and mobile viewports via the host browser capability (`evolve skill workflows show session-run` -> `browser` subagent / Chrome DevTools / Playwright): verify visual hierarchy, overflow/clipping, interactive/empty/error states, and zero console/network errors.
- **Explicit fallback instead of silent pass**: If no live server/browser runtime exists, inspect committed screenshots or visual-regression goldens and tag the report explicitly as `unverified_visual` / `UNCERTAIN` with the exact limit — **never** claim rendered visual verification without looking at rendered output.
- **Data fidelity**: Every diagram node, edge, file path, metric, and finding in Markdown/HTML must trace back to observed JSON/CLI/code outputs (`file:line` or command receipt). Never invent metrics or merge cross-axis findings (`Standards` vs `Spec` must stay in separate panels/sections).
- **Vocabulary lock**: In architecture views (`ew-init`, `ew-odyssey-arch`), stick consistently to **module**, **interface**, **implementation**, **depth**, **seam**, **adapter**, **leverage**, **locality** — never drift into generic synonyms that blur seam boundaries.

## 3. Diagram Pattern Palette (Mix to Fit Signal)

Pick the clearest pattern per section instead of forcing every view into one template:

- **Mermaid `flowchart` / `sequenceDiagram`**: Best for module dependencies, caller-to-callee leaks, multi-hop call chains, and `Key Links` (`Component -> API -> Store -> UI`). Style boundary violations in red (`stroke:#dc2626`) and deepened modules with thick dark borders.
- **Hand-Built Boxes & Inline SVG**: Use when Mermaid auto-layout fights hierarchical containment (e.g., showing one thick-bordered deep module enclosing greyed-out internal helpers, or dashed seam borders).
- **Mass Diagram** *(interface vs implementation height)*: Two adjacent rectangles per module — short interface + tall implementation signals a deep module; near-equal heights expose a shallow pass-through.
- **Cross-Section Stack**: Horizontal layered bands (`border-l-4`) comparing `N` thin pass-through layers (Before) collapsed into one cohesive band (After).
- **Call-Graph Collapse**: Nested caller tree (Before) collapsed into one entry point enclosing faded internal steps (After).

## 4. Mode View Templates (What Each Report Shows)

| Workflow / View | Core Visual & Report Sections | Required Safeguard |
|---|---|---|
| `ew-init` (`Onboarding`) | System & module boundary Mermaid graph, `GLOSSARY.md` / `docs/adr/` / `PRODUCT.md` / `DESIGN.md` asset index, **Guardrail Baseline** matrix (`build`, `test`, `lint`, `typecheck`, pre-commit hooks, CI, import boundaries), hotspot summary | Read-only snapshot; missing guardrails are surfaced as recommendations, never fabricate Wiki entries |
| `ew-review` (`Two-Axis + Visual`) | Strict side-by-side **Standards** panel (`hard_violation` vs `judgement_call`) + **Spec** panel (`missing`, `scope_creep`, `wrong_behavior`), diff/dependency diagram for architectural changes, **UI Visual Check** status card (viewports, states, console/network, or `unverified_visual`), `file:line` anchors | **Never** merge or cross-rank `Standards` and `Spec` findings across axes |
| `ew-verify` (`Verification`) | `Truths × Artifacts (L1/L2/L3) × Key Links` traceability matrix, Mermaid `Key Links` wiring graph (`WIRED` / `PARTIAL` / `NOT_WIRED`), command receipts, antipattern summary | Distinguish static wiring checks from live runtime/browser checks |
| `ew-odyssey-arch` (`Architecture Deepening`) | Compact header legend (`solid = module`, `dashed = seam`, `red = leak`, `thick dark = deep module`), Candidate Cards (`Strong` / `Worth exploring` / `Speculative` badge + dependency category + Before/After side-by-side diagrams + 1-sentence Problem/Solution + `<=6`-word Wins + ADR conflict callout), Top Recommendation card | Wait for user candidate choice before proposing interface changes unless `-y` |
| `ew-retrospective` (`Retro`) | Four-lens findings panel (`technical` / `process` / `quality` / `decision`), `--compare` before/after delta table, `Mechanical vs Judgement` triage breakdown, deduplicated `INS-{8hex}` routes | Source artifacts remain untouched; external issue/Wiki routes require approval |

## 5. Minimal Self-Contained HTML Scaffold

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>{{Report Title}} — {{Target}}</title>
    <script src="https://cdn.tailwindcss.com"></script>
    <script type="module">
      import mermaid from "https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs";
      mermaid.initialize({ startOnLoad: true, theme: "neutral", securityLevel: "loose" });
    </script>
    <style>
      .seam { stroke-dasharray: 4 4; }
      .leak { stroke: #dc2626; }
      .deep-module { border: 2px solid #0f172a; background: #f8fafc; }
    </style>
  </head>
  <body class="bg-stone-50 text-slate-900 font-sans antialiased">
    <main class="max-w-5xl mx-auto px-6 py-10 space-y-8">
      <!-- Header + Legend, Evidence Cards / Split Panels, Summary / Next Steps -->
    </main>
  </body>
</html>
```
