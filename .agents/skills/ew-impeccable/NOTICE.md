# Third-Party Notices

This bundled directory includes Impeccable 4.9.2 content (engine 0.1.25) by Paul Bakaus, adapted for Self-Evolving Workflow under the Apache License 2.0. Self-Evolving Workflow changed runtime invocation paths, command names, hook registration, and update behavior so the core integrates cleanly under `skills/ew-impeccable/`; see `UPSTREAM.md`.

## Engine Binary Launchers

`scripts/impeccable`, `scripts/impeccable.cmd`, and `scripts/impeccable.mjs` locate or download the pinned upstream Impeccable engine binary (`0.1.25`), verifying SHA-256 digests against `scripts/engine-checksums.txt` before execution.

## Platform Design Skills

The `reference/ios.md` and `reference/android.md` platform reference files are distilled from ehmo's `platform-design-skills` (Apple Human Interface Guidelines and Material Design 3 rules), rewritten in Impeccable's voice.

**Original work:** https://github.com/ehmo/platform-design-skills
**Original license:** MIT
**Author:** ehmo

## Self-Evolving Workflow adaptation

Changed the Skill entry, Run binding, host routing, cross-platform Node wrapper (`scripts/impeccable.mjs`), and affected references. See `UPSTREAM.md` for the source revisions.
