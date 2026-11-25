---
name: ew-plan
wiki_categories: [arch]
search_profile:
  wiki: [constraints, references]
  memory: [work_method, error, retry]
description: Decompose confirmed analysis or requirements into an executable DAG, waves, and collision-free tasks
argument-hint: '[scope] [--from <alias>] [--gaps] [--tdd] [--revise [instructions]] [--dir <plan-dir>] [--check <plan-dir>] [-y]'
contract:
  consumes:
  - kind: findings
    alias: current-analysis
    required: false
    schema: findings/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: brainstorm
    alias: current-brainstorm
    required: false
    schema: brainstorm/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: diagnosis
    alias: latest-debug
    required: false
    schema: diagnosis/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: review-findings
    alias: latest-review
    required: false
    schema: review-findings/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: verification
    alias: latest-verification
    required: false
    schema: verification/1.0
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
  - kind: uat-log
    alias: uat-log
    required: false
    schema: uat-log/1.0
    role: attachment
    accepts_negative_evidence: true
  - kind: blueprint
    alias: current-blueprint
    required: false
    schema: blueprint/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: roadmap
    alias: current-roadmap
    required: false
    schema: roadmap/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: context-package
    alias: upstream-context
    required: false
    schema: context-package/1.0
    role: attachment
    accepts_negative_evidence: true
  - kind: plan
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
  - path: outputs/plan.json
    kind: plan
    alias: current-plan
    role: primary
    required: true
    schema: plan/1.0
  - path: outputs/tasks/TASK-{NNN}.json
    kind: plan-task
    role: attachment
    required: false
    schema: plan-task/1.0
  - path: outputs/waves.json
    kind: execution-waves
    role: attachment
    required: false
    schema: execution-waves/1.0
  - path: outputs/dependency-graph.json
    kind: dependency-graph
    role: evidence
    required: false
    schema: dependency-graph/1.0
  - path: outputs/collision-report.json
    kind: collision-report
    role: evidence
    required: false
    schema: collision-report/1.0
  - path: outputs/plan-check.json
    kind: plan-check
    role: evidence
    required: false
    schema: plan-check/1.0
  gates:
    exit:
    - context-collected
    - plan-generated
    - plan-checked
    - plan-confirmed
  contract_version: 2.1
required_links:
  - references/prepare.md
  - references/workflow.md
session-mode: run
---

# Plan

Read [preparation](references/prepare.md), then follow the [workflow](references/workflow.md).

- Follow the Boundary Grill protocol (`evolve skill workflows show boundary-grill`) when task boundaries or file writes conflict (write findings into `plan.json#boundary_grill[]` + `report.md`; never append raw Markdown into `.json`).
- Read [test-first planning](references/tdd.md) when invoked with `--tdd`.
