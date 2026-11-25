---
name: ew-odyssey-defensive
description: "Odyssey defensive mode — read-only defensive-programming risk audit driven by business anchors: identify critical business sinks, backward-slice to defensive nodes, scan 8 defensive patterns (broad catch, exception swallowing, defaults, null fallback, auto-correction/clamp, silent conversion, duplicate constraints, duplicate defaults), forward-propagate to business sinks, classify exception semantics, and score risk R=S×P×B×H. Produces severity matrix report with full propagation chains. No fix loop."
argument-hint: "<target: file|dir|HEAD|staged|phase#|PR#> [--tier quick|standard|deep] [--sink-depth <list>] [--skip-generalize] [-y] [-c]"
contract:
  contract_version: 2.1
  produces:
    - path: outputs/session.json
      kind: defensive-audit-result
      schema: defensive-audit-result/1.0
      role: primary
      alias: defensive-session
      required: true
    - path: outputs/evidence.ndjson
      kind: evidence
      schema: evidence/1.0
      role: evidence
      alias: defensive-evidence
      required: true
    - path: outputs/understanding.md
      kind: defensive-report
      schema: defensive-report/1.0
      role: primary
      alias: defensive-understanding
      required: true
    - path: outputs/anchors.json
      kind: defensive-anchors
      schema: defensive-anchors/1.0
      role: evidence
      alias: defensive-anchors
      required: false
goal: true
---

# Pre-task Thinking: odyssey-defensive

## Purpose

Odyssey defensive performs a systematic read-only audit of defensive-programming risk in a target module or project. Its core question is NOT "does defensive code exist", but:

> Does defensive logic transform an exception state, illegal state, or system error into an apparently-normal business state that then influences a critical business result?

The audit follows the canonical investigation path — **business-anchor driven + local program slicing + bidirectional propagation**:

```
业务关键结果 → 业务流恢复 → 反向数据追踪 → 识别防御性代码
            → 识别状态转换 → 正向传播分析 → 业务影响判断 → 风险分级与定位
```

It covers 8 defensive patterns (broad exception catch, exception swallowing, defaults, null fallback, auto-correction/clamp, silent conversion, duplicate constraints, duplicate defaults), 6 failure→value state transformations, exception semantic classification (A/B/C/D), and risk scoring `R = S × P × B × H`. Unlike other Odyssey modes, defensive is strictly read-only: it produces a severity matrix report with full propagation chains and remediation suggestions, but NEVER modifies source code. Before starting, establish the target scope, audit tier, and business anchors (critical variables + sink layering).

## Input Interpretation

Target resolution determines what gets audited:

| Input | Resolution |
|-------|-----------|
| File/dir path | Audit those files |
| `HEAD` / `staged` | Review changes in diff for defensive patterns introduced/modified |
| Session/Run or commit range | Evolve Run artifacts / `git diff <base>..<head>` |
| `phase#` (legacy phase label) | Resolve files scoped to that plan phase (or `E002` if unresolvable; prefer Session/Run IDs) |
| `PR#` | `gh pr diff <num>` (or resolved `<base>...HEAD` range); fall back to `git diff main...HEAD` with `W001` if unresolvable |
| Project root (`.` / `--all`) | Full project scan (explicit target/scope required; omitted target emits `E001`) |
| continuation (`-c`) | Resume latest session, jump to `current_state` |

Tier selection (default: `standard`):

| Tier | ANCHOR | SLICE | SCAN | PROPAGATE | 一致性检查 | 异常分类 |
|------|--------|-------|------|-----------|-----------|---------|
| quick | ✓ (top sinks) | ✓ (shallow) | 4 类核心 (P1-P4) | ✓ (sink only) | — | — |
| standard | ✓ | ✓ | 全 8 类 (P1-P8) | ✓ | ✓ | ✓ (A/B/C/D) |
| deep | ✓ (全量) | ✓ (deep) | 全 8 类 + 历史考古 | ✓ + cross-module | ✓ + 单位校验 | ✓ 全 4 类 |

`--sink-depth <list>` explicitly constrains propagation analysis to given sink layers (1|2|3|all). Default `all` — full three-tier sink analysis.

## Required Context

Satisfy the Knowledge Gate per [shared base](../odyssey-base.md) §Phase Initialization & Pre-load (`evolve skill workflows show session-run` — `session-priors` when scope matches → `evolve search "<query>" --mode context` / `evolve kb get <id>` with CLI `--reads`) for `coding` and `review` scope before searching code (`evolve code search "<target keywords>"` → module boundaries, business-semantic layer, ownership).

When prior defensive-audit sessions of the same target exist, check their findings first to avoid re-reporting already-documented defensive patterns.

Use [read-only audit artifacts](../audit-artifacts.md) for the checkpoint (`intake_hashes`), output metadata (`_meta` per [runtime adapters](../runtime-adapters.md) §4), and sealing.

## Boundaries and Invariants

- **Read-only** — NEVER modify source code, configuration, or dependencies during audit. Defensive audit produces reports only. All file writes target `{run_dir}/outputs/` exclusively. Fixes route to `--mode improve` or `--mode debug`.
- **Findings require a complete propagation chain** — every finding MUST reference a specific file:line, declare its trigger pattern (P1-P8), the original failure type, the failure→value state transformation, the full forward propagation chain, and the business impact. Vague or location-only findings are not actionable. Use the standard `DEF-NNN` report format in [odyssey-defensive](../odyssey-defensive.md).
- **Risk scoring is mandatory** — every in-scope finding MUST carry `1–3` scores `R = S × P × B × H` (`S` = defensive-code severity, `P` = propagation range, `B` = business criticality, `H` = error-hiding degree; `R ∈ [1, 81]`) and final level (`Critical: R >= 54 && reaches_critical_sink`, `High: 27 <= R < 54 && reaches_critical_sink`, `Medium: 9 <= R < 27 or Level-2 R >= 27`, `Low: R < 9 or Level-3 sink`; only `reaches_critical_sink == true` may be `High+`). Findings without a score are rejected.
- **8-pattern coverage is mandatory (standard+)** — all scan patterns required by the selected tier MUST complete (plus historical archaeology in `deep`). NEVER skip a tier-required pattern silently; failures are logged as W00x warnings. `quick` tier covers only the 4 core patterns (P1-P4).
- **Exception semantic classification is mandatory (standard+)** — in `standard` and `deep` tiers, every swallowed exception MUST be classified as A (business illegal) / B (numerical/algorithm) / C (system environment) / D (program defect) (`quick` tier omits `exception_class`). Classifying all exceptions as "computation failure" is forbidden (it is the exact anti-pattern — 异常语义坍缩).
- **Q4 signal is mandatory** — for every finding, the Q4 downstream-awareness question ("can the downstream still know the upstream error occurred?") MUST be answered. Q4 = "不能" (cannot) is a high-risk signal and MUST be explicitly flagged in the report and the completion summary.
- **No fix loop** — this mode has NO S_FIX or S_CONFIRM states. Findings are reported with remediation suggestions; actual fixes route to `--mode improve` (fix defensive defect) or `--mode debug` (root-cause).
- **Local slicing, not full propagation graph** — do NOT construct a complete whole-repo propagation graph. A_SLICE is local to each critical_var; A_PROPAGATE forwards only along the sliced chain (with cross-module caller expansion in `deep` tier).
- **Evidence append-only** — evidence.ndjson entries are immutable observations; modifying or deleting them is forbidden.
- **Generalize is mandatory** unless `skip_generalize == true`; prior-phase convergence is NOT a valid skip reason.

## Risk Checklist

- Is every finding anchored to `file:line` with trigger pattern, original failure type, state transformation, full propagation chain, Q4 signal, and `1–3` `R = S × P × B × H` score? Unanchored or unscored findings are not actionable.
- Were all tier-required scan patterns (P1-P4 for `quick`; P1-P8 for `standard+`; + historical archaeology for `deep`) attempted? Missing patterns mean incomplete coverage.
- For `standard+`, was exception semantic classification (A/B/C/D) applied to every swallowed exception? Treating all exceptions as one class is the anti-pattern being audited for.
- Was the Q4 signal explicitly answered for every finding reaching a sink? "Cannot detect upstream error" must be flagged.
- Did the audit remain strictly read-only? Any source modification is a violation.
- Is every discovery hit individually classified with a reason? Blanket skips are forbidden.
- Are all 3 generalization layers (syntax/semantic/structural) attempted? A single-layer quick grep does NOT satisfy the thoroughness floor.
- Did A_SLICE stay local to critical_vars instead of building a whole-repo propagation graph?
- Was the standard `DEF-NNN` report format used for every finding? Ad-hoc formats lose the propagation-chain evidence trail.
- Are duplicate constraints/defaults (P7/P8) cross-module aggregated when `standard+` tier requires it (`+ unit verification` in `deep`)?

## Gate Intent

- `anchor-identified`: at least 1 critical variable identified AND at least 1 sink present in selected `--sink-depth` (`sink_layers.level_1 >= 1` when `1` or `all` is selected). Evidence phase=anchor logged. Understanding.md §2 updated. Cannot audit without a business-critical result to protect — BLOCKED if no critical vars / no matching sink (E002).
- `scan-complete`: all tier-required scan patterns completed (P1-P4 for quick; P1-P8 for standard; + historical archaeology for deep). Evidence phase=scan logged per pattern. Findings merged into per-pattern buckets. Understanding.md §4 + §7 updated. Tier-required pattern not attempted is BLOCKED (W001/W002 partial from tool failure is a warning).
- `propagation-verified`: every in-scope scan candidate (matching `--sink-depth`) has a `reaches_critical_sink` verdict, a failure→value state transformation type, a Q4 signal, a `1–3` four-dimensional score (S/P/B/H), and a risk level — plus `exception_class` (`A/B/C/D`) in `standard` / `deep` tiers (`quick` tier omits `exception_class`). No-score / no-sink-verdict candidate is BLOCKED (W003 per-item needs_review acceptable only when truly undecidable).
- `report-produced`: severity matrix with standard `DEF-NNN` report entries (DEF-NNN / risk / location / trigger pattern / original failure type / state transformation / full propagation chain / business impact / remediation) for every finding. Summary statistics computed. Understanding.md §8 written. Session.json `audit_result` populated. Read-only invariant verified (audited files match `intake_hashes`, zero source modifications in session).
