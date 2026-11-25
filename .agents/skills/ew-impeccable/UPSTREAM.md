# Bundled Impeccable Core

This package bundles Paul Bakaus's Impeccable design core for Self-Evolving Workflow (`ew-impeccable`).

## Provenance

- Upstream: <https://github.com/pbakaus/impeccable>
- Upstream Skill version: `4.9.2`
- Upstream Engine version: `0.1.25`
- Upstream commit: `d41d9860b0094dcb68030657da63461212835aaa`
- Imported surfaces: `.claude/skills/impeccable/SKILL.md`, `reference/`, and `scripts/`
- Historical Maestro Flow initial port reference: <https://github.com/catlog22/maestro-flow/tree/1d0696c30f4800f2226583332f8ee9275c209a22/workflows/impeccable>
- License: Apache License 2.0; see `LICENSE`
- Third-party notices: see `NOTICE.md`

## Self-Evolving Workflow adaptation

- The core is installed as `skills/ew-impeccable/`. The directory containing its `SKILL.md` is the base for every playbook and script.
- The host invokes `ew-impeccable`. Evolve binds the package to a Git revision for Session and Run work.
- Upstream 4.9.2 replaced the legacy Node `.mjs` helper scripts with the Rust `impeccable` engine (`0.1.25`) launched by `scripts/impeccable` / `scripts/impeccable.cmd` (verified against `scripts/engine-checksums.txt`).
- `scripts/impeccable.mjs` wraps the engine launcher cleanly across platforms (`Windows` / `macOS` / `Linux`), pins `IMPECCABLE_SKILL_DIR`, disables upstream `UPDATE_AVAILABLE` checks (`IMPECCABLE_NO_UPDATE_CHECK=1`), and adapts `hooks`, `pin`, `context`, and `doctor` to recognize `skills/ew-impeccable/` and `.agents/hooks.json` alongside standard harness paths.
- Role guidance (`reference/degraded/*.md`) is carried alongside the Skill and can be given to a fresh subagent (joined cleanly before ending scope) or used inline when the harness lacks subagent capability.
- Updates are distributed with Self-Evolving Workflow.
