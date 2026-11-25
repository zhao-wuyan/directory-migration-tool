# Agy gate

Every round has two Agy calls.

## Discussion response

The first call receives the baseline observations, history review (open signals only), the Skill learning inventory (`evolve skill learnings`), and the feedback from earlier failed attempts in the same round. It must return JSON with this shape.

```json
{
  "approved": true,
  "operation": "add|supersede|retire",
  "priority": "high|normal|low",
  "instruction": "one concise positive Skill instruction",
  "when": "optional trigger for normal or low priority guidance",
  "supersedes": ["learning ID replaced by this instruction (supersede only)"],
  "retire": ["learning ID removed without new text (retire only)"],
  "signals": ["history signal ID addressed by this change"],
  "evidence": ["receipt or verification reference"],
  "rationale": "why the change is attributable, minimal, non-mechanical, and free of bloat",
  "needs_revision": []
}
```

`approved` means the suggestion is ready to turn into a draft. Agy must set `approved` to `false` (and list concrete reasons in `needs_revision`) when any of these hold:

1. **Unattributed or oversized**: Evidence is missing, or the change is broader than the observed failure.
2. **Mechanical failure belongs in code/CI**: The defect is a fixed syntactic pattern, banned API, import shape, file-location rule, schema check, or unwired lint/test check — route it to a deterministic check (lint rule, pre-commit hook, validation script, or CI job) rather than a Skill prose rule.
3. **Sediment or no-op bloat**: The instruction restates default model behavior, uses weak qualifiers, duplicates existing Skill text/learnings, or caches fast environment lookups (`package.json`, `--help`, directory layout). Use `retire` or `supersede` to remove stale/redundant learnings instead.
4. **Structural escalation**: The candidate triggers structural criteria in `ew-knowledge` `references/knowledge-route.md` rule 8 (`evolve skill workflows show ew-knowledge`, e.g. new Skill contract, new artifacts, cross-Skill routing, >= 3 stable steps, an explicit new Skill, or `consolidate_recommended` bloat). Structural candidates route to `ew-skill-design` after the loop.

## Proposal wording bar

- **Positive framing**: State the target action directly (`"Quote YAML values containing ': '"`) rather than steering by negation; when an unavoidable hard guardrail is required, pair it with the positive target in the same sentence.
- **Leading words & brevity**: Anchor on crisp leading words shared across the Skill and codebase; keep `instruction` single-line, concrete, and verifiable.

## Operation choice

- **`add`** (default when `operation` is omitted): No existing learning covers the trigger, and the fix requires judgement beyond a deterministic check. Requires `priority` and `instruction`. Rejected when the inventory reports `consolidate_recommended`.
- **`supersede`**: An existing learning was read in the failing Run and did not prevent the failure, or needs sharper positive framing. Requires `supersedes`, `priority` and `instruction`; the old blocks, index entries and documents are removed in the same candidate.
- **`retire`**: A learning is marked `retire_candidate` / `unused`, conflicts with passing scenarios, duplicates the main body, or is a no-op/sediment rule. Requires `retire`; `rationale` becomes the retirement reason.

The loop marks `signals` (or signals whose sources appear in `evidence`) as addressed after a verified `applied` round; addressed signals do not drive later proposals.

## Priority placement and constraints

`evolve skill draft` (`src/skill-addition.ts`) maps `priority` to the exact Skill modification structure:

- **`high`**: Single-line inline instruction inserted directly after YAML frontmatter in `SKILL.md` (`<!-- evolve:learning:<id> -->`). Single line, **<= 280 characters**.
- **`normal`**: Creates `references/learnings/<id>.md` (no 280-character limit on body) and inserts a conditional Markdown link in `SKILL.md` pointing to `references/learnings/<id>.md` (`when <when>`). `when` must be single-line.
- **`low`**: Creates `references/learnings/<id>.md`, appends a bullet entry pointing to `./<id>.md` in `references/learnings/index.md`, and adds a Markdown link in `SKILL.md` (`<!-- evolve:learning:index -->`) pointing to `references/learnings/index.md`.

## Confirmation response

The second call receives the draft and the complete replay receipt. It must return `approved`, `reason` and `required_changes`. It approves only when every baseline pass remains green, every requested failure is fixed, the candidate loaded the expected Skill documents, the wording is concise/positive without no-op bloat, and the evidence is traceable to the reviewed execution.

The loop stores both responses under the round directory. `--auto` applies a receipt after this second call returns `approved: true`. Agy owns the evaluation decision for each proposed change.
