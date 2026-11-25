---
name: ew-init
description: Initialize and onboard a greenfield, brownfield, or legacy Maestro project into Evolve (CodeGraph, Wiki, and context packaging)
argument-hint: '[-y] [--refresh] [--visual] [--from <alias>] [<idea|@file>]'
wiki_categories: [arch, coding]
search_profile:
  wiki: [constraints, references]
  memory: [decision, work_method]
session-mode: run
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
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/init-report.json
    kind: init-report
    alias: current-init
    role: primary
    required: true
    schema: init-report/1.0
  - path: outputs/project-brief.md
    kind: project-brief
    role: attachment
    required: false
    schema: project-brief/1.0
  - path: outputs/context-package.json
    kind: context-package
    alias: upstream-context
    role: attachment
    required: false
    schema: context-package/1.0
  - path: outputs/visual-report.html
    kind: visual-report
    role: attachment
    required: false
    schema: visual-report/1.0
  gates:
    exit:
    - state-detected
    - context-initialized
  contract_version: 2.1
---

# Init

Read [preparation](references/prepare.md), then follow the [init workflow](references/workflow.md).

- Detects project state (`greenfield`, `brownfield`, `legacy-maestro`, or `initialized`) and onboards it into Evolve's native SQLite/Git/Wiki/CodeGraph stack without creating legacy `.workflow/state.json` or writing `wiki/entries/` directly.
- Outputs `outputs/init-report.json`, `outputs/project-brief.md` (with system/module Mermaid map and guardrail baseline), `outputs/context-package.json`, and optional `outputs/visual-report.html` (`--visual`) for downstream `ew-blueprint`, `ew-roadmap`, or `ew-brainstorm`.
