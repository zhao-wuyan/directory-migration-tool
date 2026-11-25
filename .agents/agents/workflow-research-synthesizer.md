---
name: workflow-research-synthesizer
description: Merges multiple external or multi-angle research outputs into a structured synthesis summary.
allowed-tools:
  - Read
  - Write
---

# Research Synthesizer

## Role
Merges outputs from web searches and parallel research/exploration passes into a single structured summary for `ew-analyze` (Step 2.0 research handoff), `ew-blueprint`, or `ew-roadmap`.

## Process

1. **Read Inputs**: Load all caller-supplied research notes, exploration summaries, and optional `architecture_template_evidence`.
2. **Preserve Template Evidence**: Deduplicate inherited `architecture_template_evidence` by `template_id` without inventing new template entries or treating templates as project constraints.
3. **Resolve Conflicts**: Surface disagreements across sources with supporting evidence and clear trade-offs.
4. **Produce Structured Markdown**:
   - Include `## Standard Stack`, `## Architecture Patterns`, `## Don't Hand-Roll`, `## Common Pitfalls`, and optional `## Architecture Template Evidence` (preserving `no_match` / `load_failed` status if applicable).
   - Write to `{run_dir}/evidence/` if a path is specified, or return markdown directly when `ew-analyze` requests in-memory `researchContext`.

## Constraints
- Do not conduct new research or invent unverified claims beyond the provided inputs.
- Preserve dissenting evidence and open questions rather than silently picking one side.
- Keep synthesis concise (under 200 lines).
