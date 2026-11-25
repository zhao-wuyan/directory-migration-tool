---
name: ew-companion
description: Execute and verify one bounded edit or state-changing task in an Evolve Run without multi-step decomposition.
argument-hint: '<intent> [--from <alias>]'
wiki_categories: [coding]
search_profile:
  wiki: [constraints, references]
  memory: [error, correction, verified_fix]
session-mode: run
contract:
  consumes:
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
  - kind: fix-directions
    alias: latest-fix-directions
    required: false
    schema: fix-directions/1.0
    role: attachment
    accepts_negative_evidence: true
  - kind: priors
    alias: session-priors
    required: false
    schema: priors/1.0
    role: evidence
  produces:
  - path: outputs/companion-log.md
    kind: companion-log
    role: attachment
    required: false
    schema: companion-log/1.0
  contract_version: 2.1
---

# Companion

Execute one single-concern task, feature, localized fix, or verifiable check directly inside an Evolve Run using full model capability (`evolve skill workflows show ew-orchestrate` / `companion-route.md`):

1. Read the bound task (read `skill_entry_path` directly with native file tools; `evolve run read skill` is optional and needed only for pinned-commit isolation or audit tracking). For simple deliverable fixes, `knowledge-reads.json` (`--reads`) is optional (reuse bound `session-priors` when `coding` scope matches, or run a quick `evolve search "<query>" --mode context` / `evolve kb get <id>` as needed; retain the Run receipt when knowledge materially affects the task), and inspect any bound `latest-review` / `latest-debug` inputs (`evolve run read artifact`).
2. Locate targets and perform the scoped changes directly in the current Run context (file count is not a hard blocker; do NOT split into subagents or `ew-analyze`/`ew-plan` phases by default unless isolated parallelism is explicitly needed), then run the narrowest direct verification command(s).
3. For simple deliverable fixes, `{run_dir}/outputs/companion-log.md` is optional (`run_dir/outputs` is created lazily only when files are actually written or required by the step contract; when written, use YAML frontmatter `kind: companion-log`, `schema: companion-log/1.0`, `role: attachment`). Persist any non-obvious verified fix or environment quirk directly via `evolve kb add "<lesson>" --kind episodic --type verified_fix --scene coding --session <session_id> --evidence <source-ref>` with applicability, cause, method and verification. Record material knowledge feedback before sealing, then seal or transition the Run per `evolve skill workflows show session-run` (`evolve run complete <run_id> --session <session_id> --summary "..." --verdict done|done_with_concerns [--next]`; omit `--actor` and `--expected-*-revision` in single-coordinator mode). Keep the Topic Session `open` across iterative follow-ups and close with `evolve session complete --session <session_id>` at topic wrap-up (or escalate in the same Session via `evolve run cancel` + `session step delete` + `session step add` if unexpected cross-module coupling or root-cause ambiguity requires multi-step decomposition).
