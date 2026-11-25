# Project Skill Package Contract

## Frontmatter, Strict YAML & Invocation Parity

- **`name`**: Matches the package directory name (except `ew-odyssey-*`, where `name` is `ew-odyssey` and `src/workflow-skills.ts` binds per-mode `contract` and `required_links` from `references/prepare/odyssey-<mode>.md`).
- **Strict YAML**: Frontmatter must parse cleanly under `parseDocument(..., { strict: true, uniqueKeys: true })`. Quote any string value containing `: ` (e.g. `"Explicit /workflow-evolve only (disable-model-invocation: true)"`) or starting with a YAML indicator character, and keep all keys unique.
- **Invocation parity & `description`**:
  - **Model-invoked** (default): Omit `disable-model-invocation` (and omit `policy.allow_implicit_invocation: false` in `agents/openai.yaml` when present). Write a model-facing `description` that front-loads the leading word and lists one trigger per distinct branch.
  - **User-invoked**: Set `disable-model-invocation: true` in `SKILL.md` frontmatter and `policy.allow_implicit_invocation: false` in `agents/openai.yaml` when present (plus `interface.display_name` / `interface.short_description`). Write a one-line human-facing summary in `description` with trigger lists stripped.
  - **Operative dependencies**: Invoke other **model-invoked** Skills explicitly via the host's Skill tool (one Skill per call), avoiding cross-folder `../` links. When a precondition is **user-invoked**, instruct the user to run `/<skill>` directly.
- **`session-mode`**: `none` for standalone Skills; `run` for stateful Skills with `contract` (`contract_version: 2.1`).
- **`required_links`**:
  - Include only documents every invocation must preload; keep conditional or mode guides as standard body links outside `required_links`.
  - Every item in `required_links` MUST appear as a literal relative Markdown link inside `SKILL.md` (for `ew-odyssey-*`, inside `ew-odyssey/SKILL.md`), resolve after path normalization (`posix.join`) to a committed regular `.md` file inside the same Skill package root (package-relative only — reference other packages via `evolve skill workflows show <id>`), and any `#heading` fragment must match an actual heading.
- **Host portability**: Use capability-neutral wording in shared workflow rules instead of hardcoding host-specific tool names. Isolate host tool mappings in a dedicated adapter reference (or an explicit host adapter section in vendored playbooks).

## Authoring Discipline (`writing-for-agents`)

- **Dual Loads (`context load` vs `cognitive load`)**: Every always-loaded pointer (`AGENTS.md`, model-invoked `description`, `required_links`) spends **context load** on every turn; push branch-only material behind pointers and mark human-only Skills `disable-model-invocation: true` so they spend **cognitive load** only when needed.
- **Information Hierarchy & Co-location**: Rank content as (1) in-file steps, (2) in-file reference, (3) disclosed reference under `references/`. Inline what every branch needs; disclose what only some branches reach. Keep each concept's definition, rules, and caveats together under one heading.
- **Leading Words & Positive Framing**: Collapse repeated multi-word explanations into a single crisp **leading word** reused consistently across prompts, docs, and code. State target behaviors positively ("write one-line comments") so undesired alternatives stay out of context; pair any unavoidable hard guardrail with its positive target.
- **Checkable Completion Criteria**: End every step (and flat reference section) on an observable, exhaustive done-condition ("every modified file accounted for", "every rule verified") to block premature completion. Split a sequence across a real context boundary (hand-off or subagent dispatch) only when sharpening the bound fails to prevent rushing.
- **Pruning (Single Source, Environment, Sediment, No-ops)**: Keep each meaning in one authoritative place. Leave fast lookups (`package.json`, `--help`, directory layout) to the environment. Delete stale **sediment** and whole **no-op** sentences that merely restate model defaults or rely on weak qualifiers.

## Session/Run Contract (`contract_version: 2.1`)

```yaml
session-mode: run
required_links:
  - references/check.md
contract:
  contract_version: 2.1
  consumes:
    - kind: findings
      alias: current-analysis
      schema: findings/1.0
      role: primary
      required: true
  produces:
    - path: outputs/verification.json
      kind: verification
      schema: verification/1.0
      role: primary
      alias: latest-verification
      required: true
```

## Output & Boundary Rules

- Put all typed outputs under the Run's `outputs/` directory with matching `_meta` / frontmatter (`kind`, `schema`, `role`, optional `alias`).
- Use `report.md` for execution narrative and `evidence/` for verification traces; keep scratch files outside `outputs/`.
- Define observable pass conditions, blocked states, and next authorized actions at each phase boundary. The coordinator drives progression via Session/Run commands and receipts.
