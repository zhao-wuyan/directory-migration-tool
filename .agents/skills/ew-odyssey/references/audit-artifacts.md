# Read-only Odyssey audit artifacts

Resume the exact Evolve Run, then read `outputs/session.json` for the audit phase, target, tier and findings already recorded. Store audit progress in that file and keep lifecycle state in the Evolve Run store.

Follow [runtime adapters](runtime-adapters.md) §4 for `_meta` placement (`session.json`, Defensive `anchors.json`, first record of `evidence.ndjson`, YAML frontmatter of `understanding.md`), `current_state = "END"`, and `evolve run complete` sealing.

At `S_INTAKE`, record file byte digests (`sha256` or `git hash-object`) for all resolved target files into `outputs/session.json.intake_hashes`. At report time, reconcile severity counts with evidenced findings, record unverified categories and unresolved reviews explicitly, verify audited file bytes still match `intake_hashes` (and `git diff --name-only` shows zero source edits), then seal with `evolve run complete` and follow the returned continuation.
