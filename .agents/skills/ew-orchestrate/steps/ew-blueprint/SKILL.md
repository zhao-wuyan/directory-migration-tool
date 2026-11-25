---
name: ew-blueprint
description: Generate a formal specification package (Product Brief, PRD, Architecture, Epics) through a 6-phase document chain
argument-hint: '<idea|@file> [-y] [-c] [--from <alias>]'
wiki_categories: [arch, review]
search_profile:
  wiki: [constraints, references]
  memory: [decision, correction]
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
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/product-brief.md
    kind: blueprint
    alias: current-blueprint
    role: primary
    required: true
    schema: blueprint/1.0
  - path: outputs/blueprint-config.json
    kind: blueprint-config
    role: evidence
    required: false
    schema: blueprint-config/1.0
  - path: outputs/discovery-context.json
    kind: discovery-context
    role: evidence
    required: false
    schema: discovery-context/1.0
  - path: outputs/refined-requirements.json
    kind: refined-requirements
    role: evidence
    required: false
    schema: refined-requirements/1.0
  - path: outputs/glossary.json
    kind: glossary
    role: attachment
    required: true
    schema: glossary/1.0
  - path: outputs/requirements/_index.md
    kind: requirements-spec
    role: attachment
    required: false
    schema: requirements-spec/1.0
  - path: outputs/requirements/REQ-{slug}.md
    kind: requirements-spec
    role: attachment
    required: false
    schema: requirements-spec/1.0
  - path: outputs/requirements/NFR-{slug}.md
    kind: requirements-spec
    role: attachment
    required: false
    schema: requirements-spec/1.0
  - path: outputs/architecture/_index.md
    kind: architecture-spec
    role: attachment
    required: false
    schema: architecture-spec/1.0
  - path: outputs/architecture/ADR-{slug}.md
    kind: architecture-spec
    role: attachment
    required: false
    schema: architecture-spec/1.0
  - path: outputs/epics/_index.md
    kind: epics-spec
    role: attachment
    required: false
    schema: epics-spec/1.0
  - path: outputs/epics/EPIC-{slug}.md
    kind: epics-spec
    role: attachment
    required: false
    schema: epics-spec/1.0
  - path: outputs/readiness-report.md
    kind: readiness-report
    role: evidence
    required: false
    schema: readiness-report/1.0
  - path: outputs/blueprint-summary.md
    kind: blueprint-summary
    role: attachment
    required: false
    schema: blueprint-summary/1.0
  - path: outputs/context-package.json
    kind: context-package
    alias: upstream-context
    role: attachment
    required: false
    schema: context-package/1.0
  gates:
    exit:
    - phases-complete
    - readiness-passed
  contract_version: 2.1
---

# Blueprint

Read [preparation](references/prepare.md), then follow the [blueprint workflow](references/workflow.md).

- Produces pure specification artifacts (`Product Brief → PRD → Architecture → Epics → Readiness`) inside `{run_dir}/outputs/` without writing application code.
- Passes `current-blueprint` and `outputs/context-package.json` downstream to `ew-roadmap`, `ew-analyze`, or `ew-plan`.
