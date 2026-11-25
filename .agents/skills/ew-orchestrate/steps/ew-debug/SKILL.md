---
name: ew-debug
description: Scientific root-cause isolation inside the Evolve chain — reproduction, hypothesis testing, backward tracing, and fix directions
argument-hint: '[issue] [-c] [--from-test <scope>] [--parallel]'
wiki_categories: [debug, coding]
search_profile:
  wiki: [constraints, references]
  memory: [error, correction, verified_fix]
session-mode: run
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
  - kind: diagnosis
    alias: latest-debug
    required: false
    schema: diagnosis/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: test-results
    alias: latest-test
    required: false
    schema: test-results/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: auto-test-report
    alias: latest-auto-test
    required: false
    schema: auto-test-report/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: review-findings
    alias: latest-review
    required: false
    schema: review-findings/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: artifact
    alias: current-execution
    required: false
    schema: artifacts/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/diagnosis.json
    kind: diagnosis
    alias: latest-debug
    role: primary
    required: true
    schema: diagnosis/1.0
  - path: outputs/hypotheses.json
    kind: hypotheses
    role: evidence
    required: false
    schema: hypotheses/1.0
  - path: outputs/reproduction.json
    kind: reproduction
    role: evidence
    required: false
    schema: reproduction/1.0
  - path: outputs/fix-directions.json
    kind: fix-directions
    alias: latest-fix-directions
    role: attachment
    required: false
    schema: fix-directions/1.0
  gates:
    exit:
    - hypothesis-tested
    - evidence-grounded
  contract_version: 2.1
---

# Debug

Read [preparation](references/prepare.md), then follow the [debug workflow](references/workflow.md).

- Bounded scientific diagnosis inside the Evolve Session chain (for multi-iteration standalone repair campaigns, use `ew-odyssey` debug mode).
- Produces verified root causes and `fix_directions` (`outputs/diagnosis.json`, `outputs/fix-directions.json`) handed off to `ew-plan --gaps` → `ew-execute`.
