# Validate and Release a Project Skill

## Static, YAML & Contract Validation

- Run `evolve skill validate <id> --root <repo>` (or `node dist/workflow-cli.js skill validate <id>` in this checkout).
- Confirm strict YAML frontmatter (`parseDocument(..., { strict: true, uniqueKeys: true })`): unique keys, quoted `: ` or YAML indicator values, and non-empty `name` / `description`.
- Confirm **host-invocation parity**: user-invoked Skills set `disable-model-invocation: true`, strip trigger lists from `description`, and set `policy.allow_implicit_invocation: false` when `agents/openai.yaml` is present; model-invoked Skills omit both flags and carry branch-triggered `description` pointers.
- Remember Evolve binds the **committed Git revision (`HEAD^{commit}`)** — stage and commit edits in an isolated worktree before validating unpublished changes.
- Resolve any broken link, package-escaping `..` path, unsupported lifecycle, or output schema mismatch at the source.

## Authoring & Pruning Audit

Before finalizing a Skill change, verify every item below:

- **Dual Loads & Disclosure**: Branch-only details live under `references/` behind sharp pointers; `required_links` holds only universally preloaded documents.
- **Checkable Completion Criteria**: Every step ends on an observable, exhaustive done-condition rather than a vague readiness phrase.
- **Leading Words & Positive Framing**: Repeated multi-word rules are collapsed into sharp leading words; instructions state what to do, with any unavoidable guardrail paired with its positive target.
- **No-op & Sediment Pruning**: Delete whole no-op sentences (default model behavior, weak qualifiers), stale sediment, duplicate rules, and trivial environment caches.

## Runtime & Replay Verification

- **Stateful Skills (`session-mode: run`)**:
  - Verify a fresh Run binds the expected commit and `contract`.
  - Read `SKILL.md` and exercised links via `evolve run read skill`, confirm input aliases and output schemas, and run `evolve run check` before `evolve run complete`.
  - When migrating existing guidance, resume a Run bound to the prior commit and replay one historically successful case.
- **Standalone Skills (`session-mode: none`)**:
  - Verify the actual host entry, expected outputs, and completion receipt.
- **Design-task apply & rollback**:
  - After `review` and `replay` pass, obtain explicit confirmation before `evolve skill design apply <design-id>`; revert any failed or regressed application with `evolve skill design rollback <design-id>`.

## Routing: `ew-skill-design` vs `workflow-evolve`

- Canonical boundary: follow rule 8 of `ew-knowledge` `references/knowledge-route.md` (`evolve skill workflows show ew-knowledge`). Route to **`ew-skill-design`** when a candidate needs a new Skill contract/lifecycle gate, new artifacts/outputs, cross-Skill routing/multi-stage branching, at least 3 stable steps, `consolidate_recommended` consolidation, or an explicit new/migrated Skill.
- Route single-evidence behavioral fixes (single instruction, conditional link, `supersede`, or `retire`) to **`workflow-evolve`** (`evolve session review <session-id>` -> `draft` -> `replay` -> `apply` / `rollback`). For priority placement (`high` inline <= 280 chars vs `normal`/`low` under `references/learnings/`), see `skills/workflow-evolve/references/agy-gate.md` (`evolve skill workflows show workflow-evolve`).
