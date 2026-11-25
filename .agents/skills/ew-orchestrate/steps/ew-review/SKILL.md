---
name: ew-review
description: Review a change or artifact inside the Evolve chain and produce typed findings
argument-hint: '<target> [--tier quick] [--visual] [-y]'
wiki_categories: [review, coding]
search_profile:
  wiki: [constraints, references]
  memory: [error, correction, verified_fix]
session-mode: run
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
  - kind: artifact
    alias: current-execution
    required: false
    schema: artifacts/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: plan
    alias: current-plan
    required: false
    schema: plan/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/review.json
    kind: review-findings
    alias: latest-review
    role: primary
    required: true
    schema: review-findings/1.0
  - path: outputs/visual-report.html
    kind: visual-report
    role: attachment
    required: false
    schema: visual-report/1.0
  contract_version: 2.1
---

# Review

Review is a two-axis quality step inside the Evolve Skill. Run **Spec** (requirements fidelity & scope creep against `current-plan` / `current-execution`) and **Standards** (repo standards + Wiki constraints + 12 Fowler Code Smells baseline) as isolated parallel fresh-context checks, then report both axes without merging or cross-axis reranking.

- Read [review preparation](references/prepare.md) before defining the audit scope.
- Read [review workflow](references/workflow.md) for the two-axis protocol, Fowler smell baseline, four quality dimensions, evidence format, and zero-residual gate.
- Reuse the bound `session-priors` for the same Wiki scope. Search for additional evidence when the scope changes.
- Write `outputs/review.json` (`review-findings/1.0`) with file and line evidence, axis/classification tags, severity, remediation, and verification status (plus rendered UI visual check status when UI surfaces change, and optional `outputs/visual-report.html` under `--visual`).

The review step reports findings and follows the confirmed Session chain without creating a separate orchestration entry.
