# Boundary Grill — Embedded Mini-Review Protocol

## Conflict Types

| ID | Signal | Example |
|----|--------|---------|
| `RSC` | Decision outside command's scope guard | `ew-analyze` outputs task-level details (`ew-plan` scope) |
| `MOD` | Changes cross 2+ module boundaries | Task touches `commands/` and `hooks/` without declaring cross-module |
| `DEC` | Upstream locked decision contradicts code | Locked "use EventEmitter" but code uses `Subject` at `file:line` |

## Trigger

Run boundary grill when ANY of:
- **RSC**: produced decision matches another command's scope guard keywords
- **MOD**: module boundary Wiki entries or code boundaries exist AND changes touch 2+ top-level modules
- **DEC**: upstream **Locked decisions** (decisions marked `Locked` in upstream `current-analysis` `findings.json#decisions[]`, `current-blueprint` constraints/ADRs, `Session.boundary_contract`, `Session.goals[]`, or active Wiki constraints) AND code search show contradicting patterns

Priority when >3 conflicts: `DEC > RSC > MOD`.

## Protocol

Per conflict (max 3 conflicts × 3 questions = 9 total):
1. State the conflict with `file:line` evidence.
2. Ask 2-3 adversarial questions via the host's structured user prompt capability (see Host Adapter Mapping in `evolve skill workflows show session-run`) — challenge with code reality.
3. Resolve: `RESOLVED` (evidence-based with user confirmation) / `DEFERRED` (needs escalation or user confirmation) / `ACCEPTED_RISK`.

**Auto mode (`-y`)**: skip interactive questioning for low-risk classification, but NEVER silently override upstream Locked decisions:
- `RSC` → defer to target scope (`DEFERRED` or warning with target step note)
- `MOD` → follow existing cross-module pattern (or record risk in `ACCEPTED_RISK` / `DEFERRED`)
- `DEC` → **NEVER silently apply "code wins" to amend Locked decisions**; record as `DEFERRED` (`status: "deferred"`, `requires_confirmation: true`) with `file:line` code evidence and surface/escalate for explicit user confirmation.

## Output

Record results as structured fields without corrupting JSON outputs:
- Add `boundary_grill: [{ id, type, conflict, resolution, status, evidence, requires_confirmation? }]` inside `{run_dir}/outputs/findings.json` (`ew-analyze`) or `{run_dir}/outputs/plan.json` (`ew-plan`), where `resolution` is `"RESOLVED" | "DEFERRED" | "ACCEPTED_RISK"` and `status` is `"resolved" | "deferred" | "accepted_risk"`. **Never append raw Markdown text into `.json` files, and never use a boolean `boundary_grill: true` value.**
- Also include the summary table inside `{run_dir}/report.md`:

```markdown
## Boundary Grill Results

| # | Type | Conflict | Resolution | Evidence |
|---|------|----------|------------|----------|
```

Only user-confirmed `RESOLVED` `DEC` entries (never `-y` auto-mode alone) may amend Locked decisions in downstream artifacts or via `goal.md`; unresolved items MUST remain `status: "deferred"` inside the `boundary_grill[]` array.

## Constraints

- Non-blocking — warnings + resolutions, never hard stops
- Evidence MUST include `file:line` — generic assertions invalid
- Results visible in command output — no silent swallowing
