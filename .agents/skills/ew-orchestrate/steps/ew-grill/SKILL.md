---
name: ew-grill
description: Socratic codebase-grounded pressure test of a plan, idea, or requirement to surface contradictions and terminology collisions
argument-hint: '<topic|plan> [-y] [-c] [--from <alias>] [--depth shallow|standard|deep]'
wiki_categories: [arch, review]
search_profile:
  wiki: [constraints, references]
  memory: [correction, error]
session-mode: run
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
  - kind: grill-report
    alias: current-grill
    required: false
    schema: grill-report/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: context-package
    alias: upstream-context
    required: false
    schema: context-package/1.0
    role: attachment
    accepts_negative_evidence: true
  - kind: blueprint
    alias: current-blueprint
    required: false
    schema: blueprint/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/grill-report.md
    kind: grill-report
    alias: current-grill
    role: primary
    required: true
    schema: grill-report/1.0
  - path: outputs/terminology.md
    kind: terminology
    role: attachment
    required: false
    schema: terminology/1.0
  - path: outputs/context-package.json
    kind: context-package
    alias: upstream-context
    role: attachment
    required: false
    schema: context-package/1.0
  gates:
    exit:
    - terminology-aligned
    - branches-walked
  contract_version: 2.1
---

# Grill

Read [preparation](references/prepare.md), then follow the [grill workflow](references/workflow.md).

- Adversarial Socratic pressure test grounded in concrete `file:line` codebase evidence and project Wiki constraints (`session-mode: run`).
- Challenge terminology collisions and contradictions on the spot before synthesis into `outputs/grill-report.md`, `outputs/terminology.md`, and `outputs/context-package.json`.
