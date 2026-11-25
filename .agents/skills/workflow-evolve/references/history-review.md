# History Review

Use committed execution evidence whenever possible: `evolve session review` reconstructs Run transitions, retries, Skill references, loaded Skill files, and `knowledge_reads`.

- When passing an explicit `--history` file, supply normalized `transcript-evidence/1.0` records (preserve message IDs and pair every quoted fragment with a receipt or verification source; raw CLI transcripts are not directly ingested as evidence).
- The OpenViking lifecycle may pass one fixed serialized Session review with `--review <file>`; it must contain verifiable Run sources, and Agy discussion evidence must stay within those sources. A review with no signals stops the round before baseline with `round.status = "no_signals"` and zero Skill changes.
- If review returns no attributable signal (and `--allow-empty-history` is not set), `evolve-loop.mjs` stops the round before Agy discussion with `round.status = "no_attributable_signals"` and zero Skill changes (`state.status = "no_attributable_signals"`).

## Attribution Checklist

- Which Run or standalone execution used the affected Skill revision?
- What user correction, failure, retry, and verified fix link to that execution?
- Which Skill documents and memory searches were actually read?
- Is the defect in the Skill instructions, the runner, the host adapter, or a transient external dependency?

Only defects attributable to the Skill package may produce a draft; host outages and missing runtime capabilities remain in the round report for workflow repair.
