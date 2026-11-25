---
name: ew-retrospective
description: Post-execution retrospective across four independent lenses (technical, process, quality, decision) routing distilled insights
argument-hint: '[session|run] [--lens technical|process|quality|decision] [--no-route] [--compare <run>] [--visual] [-y]'
wiki_categories: [learning, arch]
search_profile:
  wiki: [constraints, references]
  memory: [correction, decision, work_method]
session-mode: run
required_links:
  - references/prepare.md
  - references/workflow.md
contract:
  consumes:
  - kind: retrospective
    alias: latest-retrospective
    required: false
    schema: retrospective/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: artifact
    alias: current-execution
    required: false
    schema: artifacts/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: verification
    alias: latest-verification
    required: false
    schema: verification/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: review-findings
    alias: latest-review
    required: false
    schema: review-findings/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/retrospective.json
    kind: retrospective
    alias: latest-retrospective
    role: primary
    required: true
    schema: retrospective/1.0
  - path: outputs/retrospective.md
    kind: retrospective-doc
    role: attachment
    required: false
    schema: retrospective-doc/1.0
  - path: outputs/visual-report.html
    kind: visual-report
    role: attachment
    required: false
    schema: visual-report/1.0
  gates:
    exit:
    - lenses-complete
    - insights-routed
  contract_version: 2.1
---

# Retrospective

Read [preparation](references/prepare.md), then follow the [retrospective workflow](references/workflow.md).

- Runs four independent retrospective lenses (`technical`, `process`, `quality`, `decision`) over sealed execution, verification, review, and repo guardrail/steering artifacts.
- Applies **Implementation-vs-Review context-pressure asymmetry** (offload standards enforcement to review/CI rather than overloading implementers), **Mechanical-vs-Judgement triage** (build deterministic lint/hook/test/CI checks before writing prose rules), and **No-op / `AGENTS.md` bloat** auditing.
- Produces `outputs/retrospective.json` and `outputs/retrospective.md` with severity-ordered, deterministic `INS-{8hex}` insight IDs, routing project learnings to `ew-knowledge`, mechanical/backlog fixes to `issue`, and Skill defects to `workflow-evolve`.
