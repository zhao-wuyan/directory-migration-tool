# Workflow: Odyssey Defensive

Read-only defensive-programming risk audit — business-anchor driven + local slicing + bidirectional propagation. Identify critical business sinks → backward-slice to defensive nodes → scan 8 defensive patterns → identify failure→value transformations → forward-propagate to sinks → classify exception semantics → score `R = S × P × B × H` → severity matrix report with full propagation chains → generalize defensive patterns. Follow [shared phases](odyssey-base.md) and [runtime adapters](runtime-adapters.md).

Core judgment:

> Defensive Code + Failure Suppression + Semantic Transformation + Critical Propagation = Defensive Programming Risk

---

## State Chain

```
S_INTAKE → S_ANCHOR → S_SLICE → S_SCAN → S_PROPAGATE → S_REPORT → [back-half]
```

Back-half: `S_GENERALIZE → S_DISCOVER → S_RECORD → END` (see [shared phases](odyssey-base.md) §Shared Back-Half).

**No S_FIX / S_CONFIRM** — this mode is read-only. Findings route to `--mode improve` (fix defensive defect) or `--mode debug` (root-cause).

---

## Boundary

**In scope:** Business-anchor identification → local business-flow recovery → backward slicing → 8-pattern defensive-node scan → failure→value transformation identification → forward propagation to business sinks → constraint/default consistency check → exception semantic classification → risk scoring → severity matrix with full propagation chains → generalize defensive patterns.
**Out of scope:** Fixing defensive defects → `--mode improve` | Root-cause debug → `--mode debug` | Multi-dimensional general review → `--mode review` | Security vulnerability audit → `--mode security` | UI visual optimization → `--mode ui`. Read-only invariant applies: NEVER modify source code.

---

## Target Resolution

Target resolution (input → audit scope) and tier/`--sink-depth` coverage are defined in [the selected mode preparation](prepare/odyssey-defensive.md) §Input Interpretation — that is the canonical wording.

---

## Session Fields

```json
{ "target": "", "tier": "standard", "sink_depth": "all",
  "intake_hashes": {},
  "anchors": {"critical_vars": [], "sink_layers": {"level_1": [], "level_2": [], "level_3": []}},
  "scan_result": {"by_pattern": {}, "candidates": []},
  "propagation": {"confirmed_chains": [], "risky_sinks": [], "q4_hidden": 0},
  "audit_result": {"findings": [], "risk_matrix": {}},
  "findings_count": {},
  "generalization_stats": null }
```

---

## evidence.ndjson Phases

`anchor|slice|scan|propagate|report|discovery|decision|issue|self-iteration`

Phase-specific extension fields:

- `anchor`: `category` (critical_var|business_sink|data_flow), `var_name`, `importance` (high|medium|low)
- `slice`: `anchor_var`, `source_chain`, `passes_default` (bool), `passes_except` (bool), `auto_corrected` (bool), `multiple_sources` (bool)
- `scan`: `pattern` (broad_catch|exception_swallow|default|null_fallback|auto_correct|silent_convert|dup_constraint|dup_default), `file`, `line`, `original_failure_type`, `snippet`
- `propagate`: `finding_ref`, `state_transform` (Exception→None|Exception→Empty|Exception→Numeric|Missing→Default|Invalid→Corrected|MultiErr→SingleState), `sink_layer` (1|2|3), `sink_name`, `reaches_critical_sink` (bool), `q4_downstream_aware` (bool), `scores` {S, P, B, H}, `risk_level` (Critical|High|Medium|Low), `exception_class` (A|B|C|D; optional in `quick` tier)
- `report`: `finding_ref`, `risk_level`, `remediation`

---

## phase_goals[]

| ID | Goal | done_when | phase | skip_when |
|----|------|-----------|-------|-----------|
| G1 | Anchors identified | critical_vars ≥1 AND ≥1 sink in selected `--sink-depth` (`level_1 ≥1` when `1` or `all` selected) | S_ANCHOR | — |
| G2 | Slice completed | every critical_var has a source_chain | S_SLICE | — |
| G3 | Scan completed (all tier patterns) | all tier-required patterns scanned | S_SCAN | — |
| G4 | Propagation verified | every in-scope candidate has reaches_critical_sink + scores + risk_level (`+ exception_class` in `standard`/`deep` tiers) | S_PROPAGATE | — |
| G5 | Report produced | risk_matrix with full propagation chains + scores | S_REPORT | — |
| G6 | Pattern generalized | patterns[] ≥1 | S_GENERALIZE | skip_generalize |
| G7 | Discoveries triaged | all hits classified | S_DISCOVER | skip_generalize |
| G8 | Learnings persisted | reusable Wiki candidates staged/proposed (or none actionable) | S_RECORD | — |

---

## understanding.md — 9 Sections

1. Target & Scope & Tier
2. Business Anchors (critical-variable table + sink layering)
3. Local Business Flow (three-layer map: business-semantic ↔ software-component ↔ code-implementation)
4. Defensive Nodes (8-pattern scan results, bucketed by pattern)
5. Failure→Value Transformations (six transformation types)
6. Propagation Chains (forward chains + sink-hit verdicts + Q4 signals)
7. Constraint & Default Consistency (cross-module aggregation)
8. Risk Matrix & Exception Classification (R=S×P×B×H scores + A/B/C/D for standard+ + standard DEF-NNN report entries)
9. Generalization & Discoveries & Learnings

Wiki constraints: `evolve kb list --kind constraint --category coding` (defensive-programming patterns).

---

## State Machine — Transitions

```
S_ANCHOR     → S_SLICE       : complete (anchors identified)
S_SLICE      → S_SCAN        : complete (source chains traced)
S_SCAN       → S_PROPAGATE   : all tier patterns scanned
S_PROPAGATE  → S_REPORT      : all in-scope candidates scored + sink verdict
S_REPORT     → S_GENERALIZE  : report produced, !skip_generalize
S_REPORT     → S_RECORD      : report produced, skip_generalize
```

Discover routes: sibling module with same defensive anti-pattern → S_SCAN (re-scan expanded scope, `cross_phase_loops < max_loops`; after S_REPORT, re-run S_GENERALIZE only on new findings per [shared base](odyssey-base.md) `A_DISCOVER`); newly discovered critical propagation chain → evidence phase=decision, recommend `--mode improve`.

---

## Actions

### A_INTAKE extra

Tier resolution: parse `--tier` (default: standard) and `--sink-depth` (default: all, subset of `1,2,3`). Record tier + applicable scan patterns + sink-layer focus + `intake_hashes` (byte digests of resolved target files per [read-only audit artifacts](audit-artifacts.md)) to `outputs/session.json`.

### A_ANCHOR

Determine business-critical anchors:

1. Identify business-critical outputs (objective / constraint / decision / state / response / DB write / control quantity / model prediction / optimization result / ...).
2. Build the **critical-variable table**: `| variable | business meaning | source | usage location | importance |`.
3. **Sink layering** (three-tier sinks):
   - **Level 1 (high risk):** business decision / control command / objective function / constraint check / model training / optimization algorithm / core DB state / financial amount / security permission.
   - **Level 2 (medium risk):** computation result / report output / intermediate model / API response / cache.
   - **Level 3 (low risk):** log / UI display / temp file / debug info.
4. Write `anchors.json` (critical_vars + sink_layers) + evidence phase=anchor. Update §2. Mark G1.

**GATE: anchor-identified** — critical_vars ≥1 AND ≥1 sink in selected `--sink-depth` layers (`sink_layers.level_1 ≥1` when `1` or `all` is in `--sink-depth`). Evidence phase=anchor logged. §2 updated. BLOCKED if no critical vars / no matching sink (E002).

### A_SLICE

Recover local business flow + backward slice from critical results:

1. For each critical_var, recover its **local business flow** — three-layer map: business-semantic layer ↔ software-component layer ↔ code-implementation layer (e.g. `DesignManager → SimulationAdapter → ResultParser → Evaluator → Optimizer` ↔ `objective() → evaluate() → run_simulation() → solver.run()`).
2. **Backward slice** from each critical_var (local to the variable's flow, not a whole-repo propagation graph), answering the 7 questions: (1) where does it originate? (2) does it pass a default value? (3) any type conversion? (4) any exception handling? (5) any auto-correction? (6) multiple possible sources? (7) intermediate-state substitution?
3. Write evidence phase=slice (one source_chain per anchor) + §3 three-layer map. Mark G2.

**GATE: slice-complete** — every critical_var has a source_chain; §3 three-layer map written. Slice tool unavailability is logged as W001, and SCAN results back-fill the chain.

### A_SCAN

Scan defensive-code nodes. Parallel agents cover P1–P4 in `quick` tier, or all 8 patterns (P1–P8) in `standard` / `deep` tiers:

| # | Pattern | Scan signals (AST/Grep) |
|---|---------|-------------------------|
| P1 | broad_catch | `except:`, `except Exception:` |
| P2 | exception_swallow | `except ...: pass`, `except ...: return None`, `return {}`, `return 0` |
| P3 | default | `.get(k, v)`, `getattr(o, x, d)`, `v or default` |
| P4 | null_fallback | `if x is None: return None` / `return {}` |
| P5 | auto_correct | `max(x, lo)`, `min(x, hi)`, `min(max(x, lo), hi)` (clamp) |
| P6 | silent_convert | `try: x = float(x) except: x = 0` |
| P7 | dup_constraint | cross-module same variable, divergent constraints (`< 10`, `<= 12`, `>= 15`) |
| P8 | dup_default | cross-module same config, divergent defaults (`temperature = 25 / 20 / 293.15`) |

Run N parallel subagents (one per active pattern; join all before advancing — see [runtime adapters](runtime-adapters.md)). Each scans the target files using AST analysis when available or Grep pattern scanning with the signals above (log W001 if AST is unavailable). Each returns `[{pattern, file, line, original_failure_type, snippet, surrounding_context}]`.

- **P7/P8 (`standard` + `deep`)**: perform **cross-module aggregation** — full-repo grep for the same variable name → compare constraints/defaults across modules → flag unit / business-meaning / duplication / conflict inconsistencies.
- **`deep` tier extra**: run **historical archaeology** on defensive nodes (`git log -S` / `git log -p -n 20 -- {target}` searching for past `except` / fallback / clamp additions hiding prior failures) and verify cross-module physical/business unit consistency for P7/P8; log findings with `phase=scan`.

Merge all findings → evidence phase=scan. Write `scan_result.by_pattern` + `scan_result.candidates`. Update §4 (per-pattern buckets) + §7 (consistency). Mark G3.

**GATE: scan-complete** — all tier-required scan patterns (plus historical archaeology in `deep` tier) completed with structured findings, merged per-pattern. Evidence phase=scan logged per pattern. Tier-required pattern not attempted is BLOCKED (W001 AST unavailable / W002 cross-module aggregation failure are warnings, not blocks).

### A_PROPAGATE

Identify failure→value transformations → forward propagation along sliced chains filtered by `--sink-depth` → sink verdict → consistency (`standard+`) → exception classification (`standard+`) → risk scoring:

1. For each scan candidate, identify the **failure→value state transformation** (six types):

   | Transformation | Signature |
   |---------------|-----------|
   | Exception → None | exception swallowed → `None` propagates |
   | Exception → Empty | exception → `{}` / `[]` |
   | Exception → Numeric | exception → `0` (HIGH risk) |
   | Missing → Default | missing key → `mass = 0` |
   | Invalid → Corrected | `temperature = -100` → clamp → `0` |
   | MultiErr → SingleState | ConvergenceError + FileNotFoundError + LicenseError + TypeError → `None` (异常语义坍缩) |

2. **Forward propagation** (filtered by `--sink-depth`): Defensive Node → Forward Propagation → which sink layer (`1|2|3`) does it reach, and does it reach a Critical Business Sink (`level_1`)? Candidates reaching only unselected sink layers are marked `skipped_by_sink_depth` (not blocked). For `deep` tier, expand cross-module callers beyond the local slice.
3. **Sink verdict** (three-tier) + **Q4 signal**: "can the downstream still know the upstream error occurred?" — `不能` (cannot) = high-risk signal, MUST be flagged.
4. **Constraint/default consistency check (`standard` + `deep`)**: cross-module same variable — are units / business meaning / duplication / conflict inconsistent? Flag `Constraint Inconsistency` / `Default Inconsistency` (`deep` also verifies physical/business unit conversions).
5. **Exception semantic classification (`standard` + `deep`; optional in `quick`)**: classify each swallowed exception as:

   | Class | Examples | Recommended handling |
   |------|----------|---------------------|
   | A — business illegal | design var out-of-bounds, illegal input, geometric infeasible, rule conflict | reject / penalty / retry |
   | B — numerical/algorithm | SolverConvergenceError, NumericalOverflow, SingularMatrix | retry / penalty / fallback solver |
   | C — system environment | LicenseError, FileNotFoundError, PermissionError, OutOfMemory | NEVER silent degrade |
   | D — program defect | TypeError, AttributeError, KeyError, IndexError | expose error, do NOT convert to normal result |

6. **Risk scoring** `R = S × P × B × H` (each dimension scored `1–3`, yielding `R ∈ [1, 81]`):
   - **S (defensive severity)**: `1` = narrow fallback/default; `2` = broad catch with partial handling or auto-clamp; `3` = silent swallow (`except: pass` / `return None|0|{}`) or multi-exception semantic collapse.
   - **P (propagation range)**: `1` = local/logging only; `2` = intra-module or API response; `3` = cross-module or feeds core computation/optimizer/decision.
   - **B (business criticality)**: `1` = Level-3 sink; `2` = Level-2 sink; `3` = Level-1 critical business sink (`reaches_critical_sink == true`).
   - **H (error-hiding degree)**: `1` = error flagged/logged and downstream notified; `2` = ambiguous sentinel; `3` = Q4 `cannot` (downstream unaware upstream error occurred).

   Map to final risk level (hard invariant: **only `reaches_critical_sink == true` (`B == 3`) may be `High+`**; Level-3-only findings (`B == 1`) default to `Low`):
   - **Critical (`R >= 54` AND `reaches_critical_sink == true`)** — hidden failure directly alters a critical business decision (e.g., `Simulation Error → mass=0 → Optimization Objective`).
   - **High (`27 <= R < 54` AND `reaches_critical_sink == true`)** — error hidden and enters core Level-1 computation without directly selecting the final decision.
   - **Medium (`9 <= R < 27`, or `R >= 27` when `reaches_critical_sink == false` at Level-2 sink)** — unreasonable fallback / default / duplicate constraint with limited or Level-2 propagation.
   - **Low (`R < 9`, or Level-3 sink `B == 1`)** — local defensive code with near-zero impact on critical business results.

7. Write `propagation.confirmed_chains` + `propagation.risky_sinks` + `propagation.q4_hidden` count + evidence phase=propagate. Update §5 / §6 / §7 / §8. Mark G4.

**GATE: propagation-verified** — every in-scope scan candidate (matching `--sink-depth`) has `reaches_critical_sink` verdict + failure→value transformation type + Q4 signal + four-dimensional score (`S/P/B/H` on `1–3` scale) + `risk_level`, plus `exception_class` (`A/B/C/D`) when `tier` is `standard` or `deep`. No-score / no-sink-verdict candidate is BLOCKED (W003 per-item `needs_review` acceptable only when truly undecidable, with a per-item reason).

### A_REPORT

Formalize risk scoring + standard `DEF-NNN` report entries:

1. Produce a finding per in-scope candidate using the **standard `DEF-NNN` report format**:

   ```
   DEF-NNN
   Risk level:        Critical | High | Medium | Low
   Defensive location: file:line
   Trigger pattern:   broad_catch | exception_swallow | default | ...
   Original failure:  SimulationSystemError
   State transform:  Exception → {} → mass = 0
   Full propagation:  solver.run() → except Exception → return {} → result.get("mass", 0) → objective() → optimizer
   Business impact:   simulation error converted to mass=0; optimizer may pick the failed design as optimal.
   Remediation:       distinguish DesignInfeasibleError (penalty) vs SimulationSystemError (raise).
   ```

2. Build the **severity matrix** (Critical / High / Medium / Low) with file:line references, full propagation chains, Q4 signals, and remediation for every finding.
3. Compute summary statistics: counts per level, per pattern, per exception class (`standard+`), Q4-hidden total.
4. Write §8 (risk matrix + exception classification + standard `DEF-NNN` report entries) + `session.json.audit_result` + `session.json.findings_count`. Evidence phase=report. Mark G5.

**GATE: report-produced** — severity matrix with standard `DEF-NNN` report entries for every finding; summary statistics computed; §8 written; `audit_result` populated; read-only invariant verified (audited files match `intake_hashes`, zero source modifications in session).

---

## Generalize Source

Critical/High findings that propagate to a level-1 sink + cross-module duplicate defensive patterns (dup_constraint / dup_default) + recurring failure→value transformation signatures.

**Discover routing:** sibling module with the same defensive anti-pattern → S_SCAN (re-scan expanded scope, `cross_phase_loops < max_loops`; after S_REPORT, re-run S_GENERALIZE only on new findings per [shared base](odyssey-base.md) `A_DISCOVER`); newly discovered critical propagation chain → evidence phase=decision, recommend `--mode improve`; needs root-cause → `--mode debug`.

---

## Knowledge Persistence (§9)

Include verified reusable findings in the Run summary and evidence; follow the shared A_RECORD and post-Session candidate governance in `odyssey-base.md`. Do not initiate harvest at Run seal.

| Category | Content | Follow-up |
|----------|---------|-----------|
| Failure→Value transformation pattern | transformation signature + trigger + detection pattern + fix template | Post-Session candidate review (`category: coding`) |
| 异常语义坍缩 (exception semantic collapse) | multi-exception → single-state signature + business consequence + detection | Post-Session candidate review (`category: coding`) |
| Constraint/default inconsistency | variable + per-location values + unit/meaning conflict + resolution | Post-Session candidate review (`category: arch`) |
| Critical propagation chain | sink + chain + hiding mechanism + remediation strategy | Post-Session candidate review (`category: debug`) |

---

## Completion Summary

```
--- DEFENSIVE ODYSSEY COMPLETE ---
Target:      {target}
Tier:        {tier}
Anchors:     {N} critical vars, {M} level-1 sinks
Patterns:    P1:{n1} P2:{n2} P3:{n3} P4:{n4} P5:{n5} P6:{n6} P7:{n7} P8:{n8}
Candidates:  {N} defensive nodes
Chains:      {N} propagated, {M} reach critical sink
Risk:        {C}C / {H}H / {M}M / {L}L
Q4-hidden:   {N} findings where downstream cannot detect upstream error
Exception:   A:{a} B:{b} C:{c} D:{d}
Patterns:    {count} ({by_layer})
Scan hits:   {total} ({cross_layer_confirmed} confirmed)
Issues:      {N} created
Decisions:   {N} resolved, {M} pending, {K} deferred
Learnings:   {N} persisted
Self-iter:   {N} rounds across {M} stages
Cross-loops: {N}
Goals:       {done}/{total} ({skipped} skipped)
---
```

---

## `-y` Decision Points

| Decision Point | Normal | `-y` |
|---------------|--------|------|
| A_ANCHOR critical-variable importance | structured prompt | auto-classify by semantic layer |
| A_SCAN false-positive dismissal | structured prompt | Dismiss only with recorded counter-evidence. Keep unresolved candidates as deferred. |
| A_PROPAGATE sink-hit ambiguity | structured prompt | mark `W003` (`needs_review`), record `deferred` with per-item reason (never auto-mark `High` without `reaches_critical_sink == true`) |
| A_REPORT risk-level downgrade | structured prompt | Downgrade only with documented counter-evidence. Keep the current severity or risk level when the decision is deferred. |
| A_DISCOVER routing | structured prompt | Record a deferred discovery with evidence. Route issue creation and improve work through an explicit follow-up. |
| Ambiguous items | structured prompt | all `deferred` |

---

## Mode-Specific Phase Gates

- **Anchor gate** (S_ANCHOR): critical_vars ≥1 AND ≥1 sink in selected `--sink-depth` (`level_1 ≥1` when `1` or `all` selected). Evidence phase=anchor logged. §2 updated. BLOCKED if no critical vars / no matching sink (E002).
- **Scan gate** (S_SCAN): all tier-required patterns (plus `deep` historical archaeology) completed. Per-pattern evidence logged. Findings merged per-pattern. Tier-required pattern missing is BLOCKED (W001/W002 partial allowed).
- **Propagation gate** (S_PROPAGATE): every in-scope candidate (matching `--sink-depth`) has reaches_critical_sink + 1–3 score (`S/P/B/H`) + state transformation + Q4 signal (`+ exception_class` in `standard`/`deep` tiers). No-score candidate = BLOCKED or per-item W003.
- **Report gate** (S_REPORT): severity matrix with standard `DEF-NNN` report entries + full propagation chains + remediation. §8 written. Read-only invariant verified (audited files match `intake_hashes`; zero source modifications).

---

## Error Codes (defensive-specific)

Shared `E000–E004` and `W008` errors are defined in [SKILL.md](../SKILL.md) (including `E001` missing target/scope and `E002` no critical vars or selected sinks found).

| Code | Severity | Condition | Recovery |
|------|----------|-----------|----------|
| W001 | warning | AST tool unavailable | Scan via Grep, note coverage gap |
| W002 | warning | Cross-module aggregation failed (no symbol table) | Local-only constraint/default check, note limitation |
| W003 | warning | Candidate undecidable for sink/score | Per-item `needs_review`, do not BLOCK |
| W004 | warning | Generalization 0 hits after full 3-layer scan | Advance to S_RECORD |
| W005 | warning | Pending decisions | Filter evidence phase=decision |

---

## Success Criteria (defensive-specific)

- [ ] Target resolved; tier + sink-depth determined; session + mode-contracted output files (`prepare/odyssey-defensive.md`) created; `intake_hashes` recorded
- [ ] S_ANCHOR: critical-variable table + sink layering (§2) with ≥1 sink in selected `--sink-depth`
- [ ] S_SLICE: per-critical-var source_chain + three-layer business-flow map (§3)
- [ ] S_SCAN: all tier-required patterns (plus `deep` historical archaeology) scanned, bucketed per-pattern (§4/§7)
- [ ] S_PROPAGATE: every in-scope candidate has state transformation + sink verdict + Q4 signal + 1–3 `S/P/B/H` score (`+ exception_class` in `standard+`) (§5/§6/§8)
- [ ] S_REPORT: severity matrix + full propagation chains + remediation, in standard `DEF-NNN` report format (§8)
- [ ] understanding.md 9 sections written progressively (§1–§9)
- [ ] Read-only invariant maintained — audited files match `intake_hashes`, zero source code modifications
- [ ] Multi-layer generalization + discovery triage (unless --skip-generalize)
- [ ] phase_goals derived, tracked, and audited in `outputs/session.json` (honor session `policy.goal_mode` when opened with `--goal-mode`)
- [ ] Session resumable via -c; completion summary emitted

---

See [shared base](odyssey-base.md) for generic follow-up routing (`Next Step Routing`). Related transitions: `/ew-odyssey <finding> --mode improve` (fix defensive defects), `/ew-odyssey <finding> --mode debug` (root cause), `/ew-odyssey <target> --mode defensive --tier deep` (deep tier).
