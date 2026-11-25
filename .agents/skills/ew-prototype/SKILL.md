---
name: ew-prototype
description: "Build a throwaway prototype to settle an open design question before writing production code. Use when sanity-checking whether a logic/state model feels right, or exploring structurally different UI variants."
session-mode: none
version: 0.1.0
---

# Throwaway Prototype

A prototype is **throwaway** code built to answer one design question fast. Pick the branch that matches the question, keep it isolated and observable, then fold the settled decision into production code while preserving the prototype as a **primary source**.

## 1. Pick a Branch

Identify the design question from the user prompt or surrounding code (ask the user if reachable and unclear):

- **"Does this logic/state model feel right?"** -> Follow the [logic prototype guide](references/logic.md) to build one self-contained HTML demo with domain-language controls, full state output, free-play actions, and tabbed guided walkthroughs.
- **"What should this UI look like?"** -> Follow the [UI prototype guide](references/ui.md) to render 3–5 structurally distinct variants behind a `?variant=` search param and a floating bottom switcher.

When the question is ambiguous and the user is unavailable, match the surrounding code (domain/backend module -> logic; page/component -> UI) and state that assumption visibly at the top of the prototype.

*Done when*: The single design question, chosen branch, and target module or route are stated explicitly before writing prototype code.

## 2. Build by Shared Rules

Apply these **throwaway** rules across both branches:

1. **Co-located and clearly marked**: Place prototype files near the target module or page using existing project routing and layout conventions, with `prototype` in the filename or route name so readers recognize it immediately.
2. **One-command run**: Make the prototype start effortlessly—double-click a single HTML file for logic, or run one existing task-runner command (`npm run dev`, `pnpm dev`, etc.) for UI.
3. **In-memory state by default**: Hold state in memory so behavior can be tested without infrastructure coupling. When persistence itself is the question under test, target a clearly marked scratch DB or local file (`prototype` in the name).
4. **Skip polish**: Omit unit tests, speculative abstractions, and non-essential error handling so exploration stays fast and disposable.
5. **Surface full state**: Render the full relevant state after every action (logic) or variant switch (UI) so differences are immediately visible.

*Done when*: The prototype runs with one command or file open, visibly states the question it tests, and surfaces full state on every interaction.

## 3. Capture & Fold

Once the user evaluates the prototype and settles the question:

1. **Record the verdict**: Capture the settled question, chosen direction, and rationale in the task commit, issue, or Evolve episodic memory (`evolve kb add --kind episodic` when durable context matters).
2. **Fold the decision**: Rewrite the validated UI design cleanly to production standards—or lift the pure logic module directly—into the working branch without leaving throwaway switchers or demo shells behind.
3. **Preserve primary source**: Commit all prototype variants and throwaway shells onto an isolated `prototype/<name>` branch (kept separate from `main`) and reference that branch from the production commit or issue.

*Done when*: The validated decision lives cleanly in production code, all prototype variants are committed on `prototype/<name>`, and no throwaway wrapper or switcher remains in production paths.
