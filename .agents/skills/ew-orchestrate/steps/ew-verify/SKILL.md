---
name: ew-verify
description: Independently verify current-execution requirement coverage, behavioral correctness, and anti-pattern risk against current-plan
argument-hint: '[scope] [--strict] [--skip-tests] [--skip-antipattern] [--visual]'
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
  - kind: plan
    alias: current-plan
    required: true
    schema: plan/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: artifact
    alias: current-execution
    required: true
    schema: artifacts/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: review-findings
    alias: latest-review
    required: false
    schema: review-findings/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: uat-log
    alias: uat-log
    required: false
    schema: uat-log/1.0
    role: attachment
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/verification.json
    kind: verification
    alias: latest-verification
    role: primary
    required: true
    schema: verification/1.0
  - path: outputs/requirement-coverage.json
    kind: requirement-coverage
    role: evidence
    required: false
    schema: requirement-coverage/1.0
  - path: outputs/antipattern-report.json
    kind: antipattern-report
    role: evidence
    required: false
    schema: antipattern-report/1.0
  gates:
    exit:
    - goal-backward-verified
    - nyquist-covered
  contract_version: 2.1
---

# Verify

Read [preparation](references/prepare.md), then follow the [verification workflow](references/workflow.md).

- Independent iron-gate verification over bound `current-plan` and `current-execution` (plus optional `latest-review` when re-verifying after review remediation): goal-backward structural proof + live command coverage + canonical static anti-pattern scan.
- Emits `outputs/verification.json` (`latest-verification`), feeding `ew-review` (standard acceptance) or `ew-plan --gaps` on failure.
