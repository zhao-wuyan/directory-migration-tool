# Workflow: Odyssey Debug

Follow [shared phases](odyssey-base.md) and [runtime adapters](runtime-adapters.md) for parallel agents, structured prompts, and metadata.

## State Chain

```
S_INTAKE → S_ARCHAEOLOGY → S_EXPLORE → S_DIAGNOSE → S_FIX → S_CONFIRM → [back-half]
```

Back-half: `S_GENERALIZE → S_DISCOVER → S_RECORD → END` (see [shared phases](odyssey-base.md)).

---

## Boundary

**In scope:** Multi-iteration bug-hunt and generalization campaigns — tight red-loop repro → minimal repro → falsifiable root cause → seam regression test + fix → confirm → generalize → discover adjacent defects.
**Out of scope:** Single-concern small edit (`<=2` files, no chain) → `ew-orchestrate` (`ew-companion`) | Bounded single-pass diagnosis inside an Evolve chain (`<=2–3` modules, producing `diagnosis.json` + `fix-directions.json` for `ew-plan --gaps` → `ew-execute`) → `ew-orchestrate` (`ew-debug`) | Architecture deepening / missing seam → `--mode arch` | Features → `--mode planex` | Quality review → `--mode review` | UI → `--mode ui`

---

## `--template <name>`

Template strategies and target resolution are defined in [the selected mode preparation](prepare/odyssey-debug.md) §Input Interpretation — that is the canonical wording.

---

## Session Fields

```json
{ "issue": "", "diagnosis_retries": 0, "root_cause": null, "confirmation": null,
  "patterns": [], "generalization_stats": null }
```

---

## evidence.ndjson Phases

`archaeology|explore|diagnosis|fix|discovery|decision|issue|self-iteration`

- `archaeology`: `sha`, `author`, `date`, `message`, `relevance`
- `explore`: `category` (call_chain|recent_change|error_gap|similar_pattern), `detail`
- `diagnosis`: `hypothesis`, `result` (confirmed|disproved|inconclusive)
- `fix`: `file`, `summary`, `verification`
- `discovery`: `file`, `line`, `classification` (safe|risk|bug), `action` (fix|issue|decision|skip)
- `decision`: `question`, `options`, `context`, `status` (pending|resolved|deferred), `resolution`
- `self-iteration`: `stage`, `round`, `assessment`, `expansion`

---

## explore.json

```json
{"call_chains": [], "recent_changes": [], "error_gaps": [], "similar_patterns": [], "cli_tool": "", "timestamp": ""}
```

---

## phase_goals[]

| ID | Goal | done_when | phase | skip_when |
|----|------|-----------|-------|-----------|
| G1 | Root cause identified | phase=diagnosis result=confirmed | S_DIAGNOSE | — |
| G2 | Explore context gathered | explore.json ≥1 category (or W006 gap logged) | S_EXPLORE | — |
| G3 | Fix applied and confirmed | confirmation.overall == confirmed | S_CONFIRM | skip_fix |
| G4 | Pattern generalized | patterns[] ≥1 entry | S_GENERALIZE | skip_generalize |
| G5 | Discoveries triaged | all scan hits classified | S_DISCOVER | skip_generalize |
| G6 | Learnings persisted | reusable Wiki candidates staged/proposed (or none actionable) | S_RECORD | — |

---

## understanding.md — 9 Sections

1. Issue & Scope
2. Archaeology
3. Exploration
4. Hypotheses
5. Root Cause
6. Fix & Confirmation
7. Generalization
8. Discoveries
9. Learnings

---

## State Machine — Transitions

```
S_ARCHAEOLOGY → S_EXPLORE   : complete
S_EXPLORE     → S_DIAGNOSE  : complete
S_DIAGNOSE → S_FIX          : confirmed, !skip_fix
S_DIAGNOSE → S_GENERALIZE   : confirmed, skip_fix, !skip_generalize
S_DIAGNOSE → S_RECORD       : confirmed, skip_fix, skip_generalize
S_DIAGNOSE → S_DIAGNOSE     : all hypotheses in round failed, diagnosis_retries < 3 → A_ESCALATE_DIAGNOSIS
S_DIAGNOSE → S_RECORD       : diagnosis_retries >= 3 (Normal: user accepts escalation / INCONCLUSIVE; -y: INCONCLUSIVE)
S_FIX      → S_CONFIRM      : fix implemented
S_CONFIRM  → S_GENERALIZE   : confirmed, !skip_generalize
S_CONFIRM  → S_RECORD       : confirmed, skip_generalize
S_CONFIRM  → S_FIX          : needs_rework, fix_rounds < max_fix_rounds (`fix_rounds++`)
S_CONFIRM  → S_RECORD       : needs_rework, fix_rounds >= max_fix_rounds (escalate per [shared base](odyssey-base.md) `<fix_loop_cap>`)
```

---

## Actions

### A_ARCHAEOLOGY & A_EXPLORE

Follow [shared base](odyssey-base.md) §Shared Discovery Phases with `target_or_issue = {issue}` (archaeology subagents + `explore.json` + `understanding.md` §2–§3 + `G2` + `W003`/`W006` probe).

### A_DIAGNOSE

*(Redact secrets/auth headers in captured commands and logs as `<REDACTED>`.)*

1. **Tight-red-loop gate (before hypothesizing):** Build and run **one** fast, deterministic, agent-runnable command (test, curl, CLI fixture, replay trace, or minimal harness) asserting the user's exact symptom goes red.
   - **Non-deterministic / race bugs:** Amplify reproduction rate (`100x` loop, parallel stress, narrowed timing windows, injected sleeps) until reproduction is high enough to debug against.
   - If a red-capable loop genuinely cannot be built, pause before hypothesizing and ask the user for environment access, redacted trace/dump, or temporary instrumentation permission (`evidence phase=decision`).
2. **Minimize repro:** Shrink the failing scenario one element at a time (inputs, callers, config, steps) until every remaining element is load-bearing (removing any one turns the loop green). Record the exact repro command and minimal scenario in `understanding.md §4`.
3. **Falsifiable hypotheses (3–5 ranked):** Rank `[HIGH]/[MEDIUM]/[LOW]` in `understanding.md §4`, each with an explicit prediction: `"If <X> is the cause, then changing <Y> will eliminate the failure / changing <Z> will worsen it."` Share the ranked list in Normal mode when user domain context helps re-rank; proceed immediately under `-y` or when AFK.
4. **Instrument one variable at a time:** Prefer debugger/REPL inspection or targeted boundary probes (for performance regressions, capture timing/profiler baseline first and bisect). Tag every temporary log with a unique `[DEBUG-xxxx]` prefix so end-of-run cleanup can grep and remove all tags cleanly. Log each tested prediction to `evidence phase=diagnosis`.
5. **Ambiguity** → `evidence phase=decision` (Normal: structured prompt | `-y`: defer).
6. **Confirmed** → record `session.json.root_cause` + `understanding.md §5` (Root Cause). Mark `G1`.

**GATE: diagnosis-confirmed** — tight red-capable repro loop run and minimized to load-bearing elements, 3–5 falsifiable hypotheses tested one variable at a time with evidence, root cause confirmed with reproduction or code/log evidence (or `INCONCLUSIVE` after 3-strike round escalation), `understanding.md §4–§5` written. Zero hypotheses tested is BLOCKED.

### A_ESCALATE_DIAGNOSIS

`diagnosis_retries++` (tracks failed diagnosis rounds where all round hypotheses are disproved/inconclusive).

- **`diagnosis_retries < 3`**: `evolve agent exec "Analyze failed hypotheses for {issue} and propose new root-cause hypotheses with file:line evidence" --to codex|agy|claude --mode analysis` (or architecture inspection if no CLI executor), generate new hypotheses → `S_DIAGNOSE`.
- **`diagnosis_retries >= 3`**:
  - **Normal**: structured prompt — either (a) user provides a new scope/clue (at most 1 user-assisted extension, reset `diagnosis_retries` once → `S_DIAGNOSE`), or (b) user stops → record unresolved hypotheses as `INCONCLUSIVE` → `S_RECORD`.
  - **`-y`**: `INCONCLUSIVE` → `S_RECORD`.

### A_FIX

1. Present root cause + proposed fix.
   - Normal: structured prompt (`evidence phase=decision`)
   - `-y`: auto proceed (`evidence phase=decision`, `status: "deferred"`)
2. **Seam check & regression test first:**
   - Verify a **correct seam** exists where a test exercises the real bug pattern across callers.
   - If a correct seam exists: turn the minimized repro into a failing test first, watch it fail (if red was forced via temporary code or fixture mutation, `diff` against a pristine copy to confirm the mutation landed), then apply the targeted fix and watch it pass.
   - **Missing-seam escalation:** If no correct seam exists to lock down the bug pattern cleanly, record the architectural finding (`evidence phase=decision` or `phase=issue`) and recommend `/ew-odyssey <module> --mode arch` in Completion Summary / Next Step Routing, then apply the targeted fix and verify against the Phase 1 repro loop.
3. Log `evidence phase=fix` (`file, summary, verification`) and commit per verified fix (`"odyssey-debug({slug}): FIX — {summary}"`, noting the confirmed hypothesis).

### A_CONFIRM

1. Run covering tests **and** re-run the original (un-minimized) Phase 1 red-loop command to confirm the symptom no longer reproduces.
2. Verify cleanup: `grep` for `[DEBUG-` returns zero remaining instrumentation tags and throwaway harnesses are removed.
3. `evolve agent exec "PURPOSE: review the fix for {issue} against covering tests and diff. EXPECTED: JSON {verdict, findings [{severity, description, suggestion}], regression_risk}" --to codex --mode analysis`
4. `session.json.confirmation`:
   ```json
   {"test_result": "", "cli_review": {}, "overall": "confirmed|needs_rework"}
   ```
5. Update §6 (Fix & Confirmation).
   - `confirmed` → mark G3
   - `needs_rework` with `fix_rounds < max_fix_rounds` → `fix_rounds++` → `S_FIX`
   - `needs_rework` with `fix_rounds >= max_fix_rounds` → escalate per [shared base](odyssey-base.md) `<fix_loop_cap>` → `S_RECORD`

**GATE: fix-confirmed** — regression test passes at the correct seam (or missing seam logged for `--mode arch`), original Phase 1 repro loop passes, zero `[DEBUG-xxxx]` tags remain, CLI review done, `confirmation.overall` set, understanding.md §6 written, G3 marked. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (else escalates to `S_RECORD`); skippable only when `skip_fix == true`.

---

## Generalize Source

Confirmed root cause + applied fix.

**Thoroughness floor:** ALL 3 layers (syntax/semantic/structural) must be attempted and logged.

**Discover routing:** `bug` → back to S_FIX; new bug → S_DIAGNOSE (`cross_phase_loops < max_loops`, re-entry per [shared base](odyssey-base.md) `A_DISCOVER`).

---

## Knowledge Persistence (§9)

Include verified reusable findings in the Run summary and evidence; follow the shared A_RECORD and post-Session candidate governance in `odyssey-base.md`. Do not initiate harvest at Run seal.

| Category | Content | Follow-up |
|----------|---------|-----------|
| Recurring root cause pattern | Type + triggers + fix + detection | Post-Session candidate review (`category: debug`) |
| Non-obvious workaround | Problem + steps + why obvious fix fails | Post-Session candidate review (`category: debug`) |
| Architecture boundary violation | Violation + correct boundary + verification | Post-Session candidate review (`category: arch`) |
| Reusable generalization pattern | Signature + risk + fix template + scope | Post-Session candidate review (`category: coding`) |

---

## Completion Summary

```
--- DEBUG ODYSSEY COMPLETE ---
Issue:      {issue}
Root cause: {root_cause.hypothesis}
Fix:        {applied|skipped|inconclusive}
Patterns:   {patterns_extracted} ({by_layer})
Scan hits:  {total_hits} ({cross_layer_confirmed} confirmed)
Issues:     {N} created
Decisions:  {N} resolved, {M} pending, {K} deferred
Learnings:  {N} persisted
Self-iter:  {N} rounds across {M} stages
Goals:      {done}/{total} ({skipped} skipped)
---
```

---

## `-y` Decision Points

| Decision Point | Normal | `-y` |
|---------------|--------|------|
| A_DIAGNOSE ambiguity | structured prompt | deferred |
| A_ESCALATE 3-strike | structured prompt | INCONCLUSIVE |
| A_FIX direction | structured prompt | auto proceed |

---

## Mode-Specific Phase Gates

- **Discovery gate** (ARCHAEOLOGY/EXPLORE): evidence for the phase logged, understanding.md updated, discovery goal marked. Archaeology partial via W003 acceptable; explore W006 skip acceptable.
- **Audit gate** (DIAGNOSE): tight red-capable repro loop run and minimized to load-bearing elements (`100x` amplification for flaky/race bugs), 3–5 falsifiable hypotheses ranked and tested one variable at a time with evidence, root cause confirmed or escalated (`diagnosis-confirmed`). Zero hypotheses tested is BLOCKED.
- **FIX gate:** regression test at correct seam written first (with forced-mutation `diff` check if mutated) or missing seam logged for `--mode arch`; fix targets confirmed root cause (not symptom), with `evidence phase=fix` logged; rework loops bounded by `fix_rounds < max_fix_rounds` (escalate to `S_RECORD` per [shared base](odyssey-base.md) `<fix_loop_cap>`; unresolved diagnosis rounds escalate via `A_ESCALATE_DIAGNOSIS`).
- **CONFIRM gate:** tests and original un-minimized Phase 1 repro loop pass; zero `[DEBUG-` tags remain; CLI review done; `confirmation.overall == "confirmed"`, understanding.md §6 written, verify goal marked. `needs_rework` routes back to FIX while `fix_rounds < max_fix_rounds` (`fix-confirmed`).

---

## Error Codes (debug-specific)

Shared `E000–E004` and `W008` errors are defined in [SKILL.md](../SKILL.md) (including `E001` missing issue/session).

| Code | Severity | Condition | Recovery |
|------|----------|-----------|----------|
| W003 | warning | Archaeology agent or delegate failure | Proceed with available results |
| W006 | warning | No CLI delegate executable (`codex\|agy\|claude`) available | Skip delegate explore, log gap |

---

## Success Criteria (debug-specific)

- [ ] Session + mode-contracted output files (`prepare/odyssey-debug.md`) + prior knowledge searched
- [ ] Archaeology + CLI review → evidence
- [ ] CLI exploration → explore.json
- [ ] Tight red-capable repro loop built (with `100x` amplification if flaky) and minimized to load-bearing elements
- [ ] 3–5 falsifiable hypotheses tested one variable at a time, root cause with evidence refs
- [ ] understanding.md 9 sections progressive
- [ ] Seam regression test + fix + original repro loop confirmed, zero `[DEBUG-xxxx]` tags remaining (unless --skip-fix)
- [ ] Missing seam logged with `--mode arch` escalation when no clean regression seam exists
- [ ] Generalization + scan (unless --skip-generalize)
- [ ] Discoveries classified; unfixed findings individually justified
- [ ] phase_goals + goal audit + resumable via -c
- [ ] Completion summary

---

See [shared base](odyssey-base.md) for generic follow-up routing (`Next Step Routing`). Formal post-fix review: `/ew-odyssey <changed-files> --mode review`. Missing seam or structural coupling preventing regression testing: `/ew-odyssey <module> --mode arch`.
