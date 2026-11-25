---
name: ew-auto-test
description: Layered automated test generation and execution pipeline (L0-L3) with RED-GREEN integrity and bounded convergence
argument-hint: '[scope] [-y] [-c] [--max-iter N] [--layer L0|L1|L2|L3] [--dry-run] [--re-run]'
wiki_categories: [test, coding]
search_profile:
  wiki: [constraints, references]
  memory: [error, verified_fix]
session-mode: run
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
  - kind: blueprint
    alias: current-blueprint
    required: false
    schema: blueprint/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: plan
    alias: current-plan
    required: false
    schema: plan/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: verification
    alias: latest-verification
    required: false
    schema: verification/1.0
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
  - path: outputs/auto-test-report.json
    kind: auto-test-report
    alias: latest-auto-test
    role: primary
    required: true
    schema: auto-test-report/1.0
  - path: outputs/traceability.md
    kind: traceability
    role: evidence
    required: false
    schema: traceability/1.0
  gates:
    exit:
    - tests-generated
    - execution-converged
  contract_version: 2.1
---

# Auto-Test

Read [preparation](references/prepare.md), then follow the [auto-test workflow](references/workflow.md).

- Layered automated test generation & execution pipeline (`L0 -> L1 -> L2 -> L3`, fail-fast) with RED-first verification, parallel scenario authoring/diagnosis, and bounded `--max-iter` convergence.
- Keeps existing test files untouched and never modifies production code to force green runs.
