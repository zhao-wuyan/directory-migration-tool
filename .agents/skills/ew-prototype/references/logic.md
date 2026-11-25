# Logic Prototype Guide

Build a single, self-contained HTML **throwaway** demo so anyone—developer, designer, PM, or domain expert—can drive a state model by clicking buttons and judge whether the model feels right.

For UI layout or visual hierarchy questions, switch to the [UI prototype guide](ui.md).

## When to Use

- Testing whether a state machine handles tricky event sequences or illegal transitions.
- Checking whether a data shape represents real edge cases cleanly.
- Exploring an API or reducer surface before writing production modules or tests.

## Workflow

### 1. State the Question

Write a visible one-paragraph introduction at the top of the HTML page naming the state model and the exact design question being tested. Keep scope locked to that single question.

*Done when*: The visible header states the model under test and the question it answers.

### 2. Isolate a Portable Pure Module

Place the core domain logic inside a dedicated `<script>` section as a **portable**, **isolated** module with zero DOM coupling (`document`, event listeners, or element references stay outside):

- **Pure reducer**: `(state, action) => state` for discrete events over a single state value.
- **State machine**: Explicit states, legal transitions, and guard checks when action legality is part of the question.
- **Pure functions**: Stateless transformations over plain data types.
- **Stateful class/module**: Narrow method surface when ongoing internal state is intrinsic to the API question.

Keep data flow strictly one-way: the page calls the pure module, renders the returned state, and lifts into production intact once validated.

*Done when*: The core logic module executes identically in Node or browser with zero DOM references.

### 3. Build the Shareable HTML File

Author a single `.html` file with inline `<style>` and `<script>`—zero frameworks, zero bundlers, zero servers—so opening it by double-click or sharing it as an attachment works immediately.

Label every button, field, and explanation in **domain language** rather than internal code identifiers. Structure the page top to bottom:

1. **Title & Question Intro**: One-line summary and the question from Step 1.
2. **Current State Panel**: Readable, labeled fields showing the full relevant state (avoid raw JSON dumps), re-rendered after every action with recent changes highlighted.
3. **Free-Play Controls**: One always-available button per domain action so reviewers can test arbitrary sequences.
4. **Tabbed Guided Walkthroughs**: Scenario tabs covering the happy path, tricky edge cases, and attempted illegal transitions. Each tab displays a short plain-language setup/watch-for note and ordered step buttons that reset to a deterministic initial state and execute each action in sequence.

Use restrained styling: legible typography, generous spacing, one accent color, and zero competing animations.

*Done when*: Opening the HTML file directly renders all four sections, free-play buttons update the state panel, and every walkthrough tab resets cleanly and steps through its scenario.

### 4. Hand Over & Capture

Deliver the single HTML file for hands-on clicking; treat reactions like *"that transition should be blocked"* or *"I expected X to update"* as discoveries in the model and iterate quickly.

Once the question is settled:

- Lift the **portable** pure logic module into production code with full tests and error handling.
- Commit the complete HTML prototype as **primary source** onto an isolated `prototype/<name>` branch, keeping throwaway HTML shells out of `main`.

*Done when*: The validated pure module is integrated into production code, the verdict is recorded, and the HTML prototype is preserved on `prototype/<name>`.
