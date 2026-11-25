---
name: ew-roadmap
description: Decompose multi-release or broad requirements into a valid Session DAG with scope, success criteria, and dependency edges
argument-hint: '<requirement> [-y] [-c] [-m progressive|direct|auto] [--from <alias>] [--revise [instructions]] [--review]'
wiki_categories: [arch]
search_profile:
  wiki: [constraints, references]
  memory: [decision, correction]
session-mode: run
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
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
  - kind: brainstorm
    alias: current-brainstorm
    required: false
    schema: brainstorm/1.0
    role: primary
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
  - path: outputs/roadmap.json
    kind: roadmap
    alias: current-roadmap
    role: primary
    required: true
    schema: roadmap/1.0
  - path: outputs/roadmap.md
    kind: roadmap-doc
    role: attachment
    required: false
    schema: roadmap-doc/1.0
  gates:
    exit:
    - dag-valid
    - sessions-registered
  contract_version: 2.1
---

# Roadmap

Read [preparation](references/prepare.md), then follow the [roadmap workflow](references/workflow.md).

- Decomposes multi-session or cross-release requirements into an acyclic Session DAG (`outputs/roadmap.json`, `outputs/roadmap.md`) inside `{run_dir}/outputs/`.
- Keeps intra-session task breakdown in `ew-plan` and formal specs in `ew-blueprint`.
