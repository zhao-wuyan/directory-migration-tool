---
name: ew-test
description: Run conversational UAT, smoke checks, coverage verification, and optional browser acceptance on verified deliverables
argument-hint: '[scope] [-y] [--smoke] [--auto-fix] [--frontend-verify]'
wiki_categories: [test, review]
search_profile:
  wiki: [constraints, references]
  memory: [error, verified_fix]
session-mode: run
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
  - kind: verification
    alias: latest-verification
    required: false
    schema: verification/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: plan
    alias: current-plan
    required: false
    schema: plan/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: artifact
    alias: current-execution
    required: false
    schema: artifacts/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: review-findings
    alias: latest-review
    required: false
    schema: review-findings/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: diagnosis
    alias: latest-debug
    required: false
    schema: diagnosis/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/test-results.json
    kind: test-results
    alias: latest-test
    role: primary
    required: true
    schema: test-results/1.0
  - path: outputs/test-plan.json
    kind: test-plan
    role: attachment
    required: false
    schema: test-plan/1.0
  - path: outputs/acceptance.json
    kind: acceptance
    role: evidence
    required: false
    schema: acceptance/1.0
  - path: outputs/coverage.json
    kind: coverage
    role: evidence
    required: false
    schema: coverage/1.0
  - path: outputs/uat.md
    kind: uat-log
    alias: uat-log
    role: attachment
    required: false
    schema: uat-log/1.0
  - path: outputs/issue-candidates.json
    kind: issue-candidates
    role: attachment
    required: false
    schema: issue-candidates/1.0
  - path: outputs/e2e-results.json
    kind: e2e-results
    role: evidence
    required: false
    schema: e2e-results/1.0
  gates:
    exit:
    - coverage-met
    - pass-rate-met
  contract_version: 2.1
---

# Test

Read [preparation](references/prepare.md), then follow the [test workflow](references/workflow.md).

- Behavioral UAT, `--smoke` cold-start validation, and `--frontend-verify` live browser acceptance producing `outputs/test-results.json` (`latest-test`) and `outputs/uat.md`.
- Infers gap severity automatically from observed failures without asking the user to classify severity.
