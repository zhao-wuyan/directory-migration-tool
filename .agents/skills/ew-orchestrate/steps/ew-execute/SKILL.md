---
name: ew-execute
wiki_categories: [coding]
search_profile:
  wiki: [constraints, references]
  memory: [error, correction, verified_fix]
description: Implement code changes following the DAG and waves of current-plan, producing implementation results and a local smoke self-check
argument-hint: '[scope] [-y] [--task TASK-ID] [--method agent|cli|auto] [--executor <tool>] [--auto-commit]'
contract:
  consumes:
  - kind: plan
    alias: current-plan
    required: false
    schema: plan/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: review-findings
    alias: latest-review
    required: false
    schema: review-findings/1.0
    role: primary
    accepts_negative_evidence: true
  - kind: fix-directions
    alias: latest-fix-directions
    required: false
    schema: fix-directions/1.0
    role: attachment
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
  - path: outputs/execution.json
    kind: artifact
    alias: current-execution
    role: primary
    required: true
    schema: artifacts/1.0
  - path: outputs/task-results.json
    kind: task-results
    role: attachment
    required: false
    schema: task-results/1.0
  - path: outputs/self-check.json
    kind: self-check
    role: evidence
    required: false
    schema: self-check/1.0
  - path: outputs/change-manifest.json
    kind: change-manifest
    role: evidence
    required: false
    schema: change-manifest/1.0
  gates:
    exit:
    - execution-complete
    - self-check-passed
  contract_version: 2.1
required_links:
  - references/prepare.md
  - references/workflow.md
session-mode: run
---

# Execute

Read [preparation](references/prepare.md), then follow the [workflow](references/workflow.md).

- Dispatch each ready task from `current-plan`'s wave/DAG frontier with a **fresh context window** and **sparse context pointers** (optional shared exploration notes, plan task pointer, Wiki constraints, prior summaries/commits).
- Isolate concurrent edits in dedicated worktrees/branches (`evolve skill workflows show session-run` for host workspace-isolation mapping) with a dedicated merger step when tasks share files or build state, and use a single fresh fixer subagent for any post-execution diff review fixes.
