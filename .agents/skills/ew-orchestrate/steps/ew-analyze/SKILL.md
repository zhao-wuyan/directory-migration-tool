---
name: ew-analyze
wiki_categories: [arch]
search_profile:
  wiki: [constraints, references]
  memory: [correction, error, verified_fix]
description: Perform multi-dimensional analysis of a topic or existing implementation, producing findings and a risk-matrix consumable by plan
argument-hint: '[topic] [-y] [-q] [-c] [--from <artifact-alias>] [--gaps [ISS-ID]]'
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
  - kind: context-package
    alias: upstream-context
    required: false
    schema: context-package/1.0
    role: attachment
    accepts_negative_evidence: true
  - kind: brainstorm
    alias: current-brainstorm
    required: false
    schema: brainstorm/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: roadmap
    alias: current-roadmap
    required: false
    schema: roadmap/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: blueprint
    alias: current-blueprint
    required: false
    schema: blueprint/1.0
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
  - path: outputs/findings.json
    kind: findings
    alias: current-analysis
    role: primary
    required: true
    schema: findings/1.0
  - path: outputs/risk-matrix.json
    kind: risk-matrix
    role: evidence
    required: false
    schema: risk-matrix/1.0
  - path: outputs/priors.json
    kind: priors
    alias: session-priors
    role: evidence
    required: false
    schema: priors/1.0
  gates:
    exit:
    - exploration-done
    - discussion-round
    - scoring-complete
    - intent-covered
  contract_version: 2.1
session-mode: run
---

# Analyze

Read [preparation](references/prepare.md), then follow the [analysis workflow](references/workflow.md).

- Follow the Boundary Grill protocol (`evolve skill workflows show boundary-grill`) when a responsibility conflict is found (write findings into `findings.json#boundary_grill[]` + `report.md`; never append raw Markdown into `.json`).
- Read [issue gap analysis](references/issue-gaps-analyze.md) for the --gaps branch.
