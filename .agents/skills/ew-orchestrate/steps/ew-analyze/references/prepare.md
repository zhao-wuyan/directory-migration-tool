# Pre-flight: ew-analyze

Confirm goal clarity and branch routing before entering [workflow](workflow.md):

- **Mode check**: `[topic]` matching a `current-roadmap` `sessions[].id` (or `--from current-roadmap`) -> Session-scoped impact deep-dive; free text -> macro topic analysis; `--gaps [ISS-ID]` -> [issue gap analysis](issue-gaps-analyze.md); `-q` -> decision extraction only (must not be omitted); `-c` -> resume/reuse prior bound analysis state via `run get` without re-scoping.
- **Upstream `--from` alias**: read only via injected paths (`upstream-context`, `current-brainstorm`, `current-roadmap`, `current-blueprint`, `latest-debug`); keep already-locked decisions invariant and focus open decisions/questions. If `latest-debug` is present, treat the diagnosis (or `inconclusive` 3-strike refutation dossier) as confirmed symptom context and analyze architectural impact/fix direction rather than repeating ad-hoc hypothesis testing.
- **Knowledge & scope invariants**: complete the Run Knowledge Gate (`evolve skill workflows show session-run`) first; record new domain terms in `report.md`; never modify source files during analysis.
- **Evidence & convergence**: exploration subagents (`invoke_subagent` or host subagent, always joined before advancing) + independent `evolve agent exec` analysis provide required evidence — manual reads alone do not satisfy scoring gates. Ground `scope_verdict` (`small|medium|large|unknown`) accurately to guide downstream `ew-plan` decomposition (never guess `small|medium|large` under `-y` when evidence is insufficient — emit `unknown`).
