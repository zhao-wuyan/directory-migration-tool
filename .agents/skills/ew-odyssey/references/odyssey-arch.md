# Workflow: Odyssey Arch

Error-driven codebase architecture improvement: survey error/memory/churn hotspots → audit shallow modules & seams → explore orthogonal interfaces (`Design-It-Twice`) → deepen at the seam → replace shallow tests + prove boundary guardrails bite → generalize & persist. Follow [shared phases](odyssey-base.md) and [runtime adapters](runtime-adapters.md).

---

## State Chain

```
S_INTAKE → S_SURVEY → S_AUDIT → S_DESIGN → S_DEEPEN → S_VERIFY → [back-half]
```

Back-half: `S_GENERALIZE → S_DISCOVER → S_RECORD → END` (see [shared phases](odyssey-base.md) §Shared Back-Half).

---

## Boundary

**In scope:** Error- and churn-driven structural deepening — turning shallow module clusters and missing seams into deep modules behind clean interfaces with interface-level tests and deterministic boundary guardrails.
**Out of scope:** Single bug fix with an existing seam → `--mode debug` | Multi-dimension quality fix preserving APIs → `--mode improve` | Feature delivery → `--mode planex` | Formal diff review → `--mode review` | UI optimization → `--mode ui`.

---

## Vocabulary & Core Principles

Use these exact terms across audits, designs, and `understanding.md`:
- **Module**: scale-agnostic unit with one **interface** and an **implementation**.
- **Interface**: full caller contract (types, invariants, ordering, error modes, configuration, performance).
- **Depth**: **leverage** per unit of interface callers/tests must learn; creates **locality** where changes and bugs concentrate in one place.
- **Seam**: location where an **adapter** satisfies an interface without editing callers (`1` adapter = hypothetical seam; `>=2` adapters = real seam).
- **Deletion Test**: deleting a suspected shallow module concentrates complexity (pass-through signal) vs. scattering complexity across `N` callers (deep module earning its keep).

---

## Context

### Target Resolution

Target resolution and entry modes (`path`, `symptom/error`, `HEAD/staged`, `--all`, `-c`) are defined in [the selected mode preparation](prepare/odyssey-arch.md) §Input Interpretation.

### Session Fields

```json
{ "target": "", "baseline_metrics": {}, "hotspots": [],
  "candidates": [], "design_proposals": [], "selected_design": null,
  "guardrails": { "tool": "", "proven_bite": false },
  "confirmation": null, "patterns": [], "generalization_stats": null }
```

### evidence.ndjson Phases

`survey|audit|design|deepen|guardrail|discovery|decision|issue|self-iteration`

- `survey`: `category` (`error_memory|git_churn|test_friction|domain_context`), `detail`
- `audit`: `candidate_id`, `files[]`, `deletion_test`, `dep_category` (`in-process|local-substitutable|remote-owned|true-external`), `strength` (`strong|worth_exploring|speculative`)
- `design`: `candidate_id`, `proposals[]`, `selected`, `breaking_api` (`boolean`)
- `deepen`: `candidate_id`, `files[]`, `tests_replaced`, `commit`
- `guardrail`: `rule_source`, `pass_clean`, `fail_on_violation`, `pass_after_revert`

### explore.json

```json
{"hotspots": [], "churn_clusters": [], "error_patterns": [], "glossary_terms": [], "adr_refs": [], "cli_tool": "", "timestamp": ""}
```

### phase_goals[]

| ID | Goal | Phase | skip_when |
|----|------|-------|-----------|
| G1 | Error, memory, and churn hotspots surveyed | S_SURVEY | — |
| G2 | Shallow modules and seams audited (Deletion Test + 4 dependency categories) | S_AUDIT | — |
| G3 | Orthogonal interfaces explored (`Design-It-Twice`) and target design decided | S_DESIGN | — |
| G4 | Seam deepened, shallow tests replaced, boundary guardrails proven to bite, and verified | S_VERIFY | skip_fix |
| G5 | Architectural anti-patterns generalized | S_GENERALIZE | skip_generalize |
| G6 | Discoveries triaged | S_DISCOVER | skip_generalize |
| G7 | Learnings persisted (Wiki / episodic / Glossary / ADR) | S_RECORD | — |

### understanding.md — 9 Sections

1. Target & Baseline
2. Hotspot Survey (errors, episodic memories, git churn)
3. Shallow Module & Seam Audit
4. Design-It-Twice Exploration & Decision
5. Deepening & Boundary Guardrails
6. Verification & Before/After Metrics
7. Generalization
8. Discoveries
9. Learnings

---

## State Machine

### Transitions

```
S_SURVEY  → S_AUDIT       : hotspots identified
S_AUDIT   → S_DESIGN      : actionable deepening candidate(s) exist
S_AUDIT   → S_GENERALIZE  : no actionable deepening candidates, !skip_generalize
S_AUDIT   → S_RECORD      : no actionable deepening candidates, skip_generalize
S_DESIGN  → S_DEEPEN      : design selected & decision gate cleared, !skip_fix
S_DESIGN  → S_GENERALIZE  : skip_fix OR decision deferred, !skip_generalize
S_DESIGN  → S_RECORD      : skip_fix OR decision deferred, skip_generalize
S_DEEPEN  → S_VERIFY      : deepening + tests + guardrails implemented
S_VERIFY  → S_GENERALIZE  : verified, !skip_generalize
S_VERIFY  → S_RECORD      : verified, skip_generalize
S_VERIFY  → S_DEEPEN      : needs_rework, fix_rounds < max_fix_rounds (`fix_rounds++`)
S_VERIFY  → S_RECORD      : needs_rework, fix_rounds >= max_fix_rounds (escalate per [shared base](odyssey-base.md) `<fix_loop_cap>`)
```

### Actions

**A_INTAKE extra** — Satisfy Knowledge Gate (`arch`, `debug`, `coding` scope + episodic `error`/`correction` memories). Read `GLOSSARY.md` and `docs/adr/` if present. Capture `session.json.baseline_metrics` (`test_status`, `module_count`, `cycle_count`, `hotspot_files`, `boundary_check`). Write `understanding.md §1`.

**A_SURVEY (Error / Memory / Churn Hotspot Survey)** — Focus strictly on active pain (YAGNI):
1. Query episodic `error`/`correction` memories and prior `debug` findings where fixes felt fragile or lacked a test seam.
2. Run git churn analysis (`git log --oneline -50 -- {target}` and co-change frequency) to locate files modified repeatedly together.
3. Inspect existing test coverage, deep-import coupling, and any existing boundary checks (`dependency-cruiser`, ESLint boundaries, or repo test constraints).
4. Optional CLI-assisted pass: `evolve agent exec "Survey error hotspots, git churn clusters, and seam coupling for {target}" --to codex --mode analysis`.
5. Write `outputs/explore.json`, log `evidence phase=survey`, update `understanding.md §2`, and mark `G1`.

**A_AUDIT (Shallow Module & Seam Audit)** — Spawn fresh-context subagent(s) to examine top hotspot clusters organically:
1. Identify friction: concepts scattered across small modules, shallow pass-through wrappers, pure helpers extracted solely for unit testing while real bugs hide in callers (poor **locality**), leaky seams, and hard-to-test paths.
2. Apply the **Deletion Test** to each suspected shallow module (`concentrates complexity` vs. `scatters complexity`).
3. Classify each candidate's dependencies into the **4 Dependency Categories**:
   - **1. In-process** (pure/in-memory, no I/O) — merge directly and test through the new interface; no external adapter needed.
   - **2. Local-substitutable** (local test stand-ins such as in-memory FS or embedded DB) — keep the seam internal; test through the deepened interface using the stand-in.
   - **3. Remote but owned (Ports & Adapters)** (owned network/IPC services) — inject a port at the seam with production transport adapter and in-memory test adapter.
   - **4. True external (Mock)** (third-party services) — inject an external port with a mock adapter in tests.
4. Surface ADR conflicts only when real architectural friction warrants reopening.
5. Score candidates (`strong` | `worth_exploring` | `speculative`), log `evidence phase=audit`, update `session.json.candidates[]` and `understanding.md §3` (with Mermaid Before/After seam sketches for top candidates), mark `G2`. When presenting candidates visually (default on multi-module candidate selection or when `--visual` is set), render a self-contained HTML architecture report per `evolve skill workflows show visual-report` (`arch` view: header legend, Candidate Cards with Before/After Mermaid/Mass/Cross-section/Call-graph-collapse diagrams, red leakage arrows, thick deep-module boxes, 1-sentence Problem/Solution, `<=6`-word Wins, ADR conflict callouts, and Top Recommendation card; written to `{run_dir}/outputs/visual-report.html` or OS temp `$TMPDIR`/`%TEMP%` without touching repo files).

**GATE: seam-audit-complete** — `explore.json` populated, hotspot candidates evaluated with Deletion Test and 4 dependency categories, `understanding.md §2–§3` updated, `G1`/`G2` marked. Zero hotspots surveyed is BLOCKED (unless target has no files, `E002`).

**A_DESIGN (Design-It-Twice Parallel Interface Exploration)** — Frame constraints, dependency category, and an illustrative sketch for the chosen candidate(s), then spawn **3+ parallel fresh-context subagents** with orthogonal constraints (join all before comparing):
- **Agent 1 (Minimal surface):** 1–3 entry points max; maximize leverage per entry point.
- **Agent 2 (Maximum flexibility):** Support diverse callers and extension without exposing internals.
- **Agent 3 (Common-caller ergonomics):** Make the default call path trivial; hide wiring and configuration.
- **Agent 4 *(when dependency category 3 or 4 applies)*:** Ports & adapters around cross-seam dependencies (`>=2` concrete adapters required for a real seam).

Each subagent returns: (1) full interface (types, methods, invariants, ordering, error modes), (2) caller usage example, (3) implementation hidden behind the seam, (4) dependency/adapter strategy, (5) leverage vs. thinness trade-offs.

Compare designs on **depth**, **locality**, and **seam placement**, and synthesize the strongest design (or hybrid).
- **Explicit Public API Break / Dependency Decision Gate:** If the selected design breaks a public interface, introduces a new dependency (e.g., `dependency-cruiser`), or alters cross-module ownership, log `evidence phase=decision`:
  - **Normal mode:** present options (direct public break vs. backward-compatible facade/deprecation path vs. skip) via structured prompt before editing code.
  - **`-y` mode:** select a backward-compatible facade/re-export when feasible and record `deferred`; if an unapproved breaking API or external dependency cannot be avoided safely, record `status: "deferred"` + `phase=issue` and advance without applying the breaking edit.
- Log `evidence phase=design`, write `session.json.selected_design`, update `understanding.md §4`, mark `G3`.

**GATE: design-selected** — `>=3` orthogonal interface proposals compared on depth/locality/seam placement, selection recorded, and any breaking API or new-dependency decision resolved before code changes.

**A_DEEPEN (Deepening Refactor, Test Replacement, & Boundary Guardrails)** — Apply changes incrementally with per-step verification:
1. **Deepen at the seam:** Consolidate scattered shallow logic behind the selected interface; keep internal seams private in internal folders/modules and expose only clean entry points.
2. **Replace, don't layer tests:** Write behavior tests exercising observable outcomes across the deepened module's interface (using category-appropriate stand-ins/adapters). Once interface tests pass, delete obsolete shallow-module unit tests so test maintenance drops alongside interface size.
3. **Deterministic boundary guardrails ("Prove the rules bite"):**
   - Configure or extend deterministic seam checks (e.g., `dependency-cruiser` entry-point/no-cycle rules when present or approved, ESLint boundary rules, or a lightweight deterministic import-boundary test/script in `package.json` `check` / test suite).
   - **Prove the rules bite** before concluding:
     1. Run the boundary check on the clean repo → observe **PASS**.
     2. Temporarily inject a concrete boundary violation (e.g., an external/test deep import bypassing the module's entry point) and re-run → observe **FAIL** citing the expected rule.
     3. Revert the temporary violation and re-run → observe **PASS**.
   - Record `{ rule_source, pass_clean, fail_on_violation, pass_after_revert }` in `evidence phase=guardrail` and `session.json.guardrails`.
4. Commit each verified deepening or guardrail step (`"odyssey-arch({slug}): DEEPEN — {summary}"`); revert uncommitted failing experiments cleanly before trying alternatives. Update `understanding.md §5`.

**A_VERIFY** —
1. Run covering tests, build/typecheck, and deterministic boundary guardrails.
2. Re-capture metrics vs. `baseline_metrics` (public surface size, dependency cycles, boundary violations, test pass rate) and render Before/After deepening metrics cleanly in `understanding.md §6/§8` (plus update `visual-report.html` when `--visual` is active).
3. Run cross-angle CLI verification: `evolve agent exec "Review deepened module seams, interface depth, replaced tests, and boundary guardrails for {target}" --to codex --mode analysis`.
4. Set `confirmation` (`verified` | `needs_rework`):
   - `verified` → mark `G4`, update `understanding.md §6` & `§8` (before/after architecture metrics).
   - `needs_rework` with `fix_rounds < max_fix_rounds` → `fix_rounds++` → `S_DEEPEN`.
   - `needs_rework` with `fix_rounds >= max_fix_rounds` → escalate per [shared base](odyssey-base.md) `<fix_loop_cap>` → `S_RECORD`.

**GATE: deepening-verified** — tests and boundary checks pass cleanly after revert, `guardrails.proven_bite == true` (when guardrails added/updated), CLI review completed, before/after metrics in `understanding.md §8`, `G4` marked (skippable via `--skip-fix`).

---

## Generalize Source

Shallow pass-through patterns, leaked internal seams, deep-import patterns bypassing entry points, and shallow unit tests that outlived their seams.
**Discover routing:** fixable sibling shallow cluster or deep-import violation → `S_DEEPEN` (or `S_AUDIT` for new hotspot cluster) while `cross_phase_loops < max_loops` per [shared base](odyssey-base.md) `A_DISCOVER`.

---

## Knowledge Persistence (§9)

Include verified reusable findings in the Run summary and evidence; follow the shared A_RECORD and post-Session candidate governance in `odyssey-base.md`. Do not initiate harvest at Run seal.

| Category | Content | Follow-up |
|----------|---------|-----------|
| Architecture seam constraint | Shallow hotspot + deepened interface contract + guardrail rule | Post-Session candidate review (`category: arch`) |
| Deepening / adapter pattern | Dependency category + port/adapter shape + test surface | Post-Session candidate review (`category: coding`) |
| Boundary test / guardrail rule | Deterministic check + "prove the rules bite" verification | Post-Session candidate review (`category: test`) |

---

## Completion Summary

```
--- ARCH ODYSSEY COMPLETE ---
Target:      {target}
Hotspots:    {count} surveyed ({errors} error/mem, {churn} churn)
Candidates:  {audited} audited ({strong} strong, {deepened} deepened)
Design-2x:   {proposals} proposals -> {selected_design} (breaking_api={yes|no})
Guardrails:  {tool|none} (proven_bite={yes|no|na})
Tests:       {added} interface tests, {removed} shallow tests replaced
Patterns:    {count} ({by_layer})
Scan hits:   {total} ({cross_layer_confirmed} confirmed)
Issues:      {N} created
Decisions:   {N} resolved, {M} pending, {K} deferred
Learnings:   {N} persisted
Goals:       {done}/{total} ({skipped} skipped)
---
```

---

## `-y` Decision Points

| Decision Point | Normal | `-y` |
|---------------|--------|------|
| Candidate selection | structured prompt | top `Strong` hotspot candidate |
| Public API break / new dependency | structured prompt | choose non-breaking facade if viable (`deferred`), else skip breaking change (`issue`) |
| Boundary guardrail tool install | structured prompt | use repo-native deterministic check (`W001` if new package declined) |
| A_DISCOVER routing | structured prompt | fixable sibling (`S_DEEPEN`), else record `issue` |

---

## Error Codes (arch-specific)

Shared `E000–E004` and `W008` errors are defined in [SKILL.md](../SKILL.md).

| Code | Severity | Condition | Recovery |
|------|----------|-----------|----------|
| W001 | warning | No boundary-lint tool / no `GLOSSARY.md` or ADRs | Use repo-native deterministic import check / proceed without glossary |
| W002 | warning | Fewer than 3 `Design-It-Twice` subagents returned | Proceed if `>=2` distinct designs completed; log coverage gap |
| W004 | warning | Generalization 0 hits after full 3-layer scan | Advance to `S_RECORD` |
| W005 | warning | Pending decisions remain (`breaking_api` / dependency) | Filter `evidence phase=decision` |
| W006 | warning | No CLI delegate executable (`codex\|agy\|claude`) available | Skip CLI review, rely on host verification |

---

## Success Criteria

- [ ] Target resolved; Knowledge Gate satisfied (`arch`, `debug`, `coding` + episodic errors); baseline metrics captured
- [ ] Error/memory/churn hotspots surveyed in `outputs/explore.json`
- [ ] Shallow candidates audited with Deletion Test and 4 dependency categories
- [ ] `Design-It-Twice` parallel exploration (`>=3` orthogonal constraints) compared on depth, locality, and seam placement
- [ ] Public API breaks / new dependencies gated explicitly via `phase=decision`
- [ ] Deepening refactor + replace-don't-layer tests + deterministic boundary guardrails proven to bite (unless `--skip-fix`)
- [ ] `understanding.md` (§1–§9), `phase_goals[]`, `current_state: "END"`, and Completion Summary completed

---

See [shared base](odyssey-base.md) for generic follow-up routing (`Next Step Routing`). Related transitions: `/ew-odyssey <issue> --mode debug` (bug diagnosis), `/ew-odyssey <target> --mode improve` (6-dimension quality sweep), `/ew-odyssey <files> --mode review` (diff review).
