# UI Prototype Guide

Generate **3–5 structurally distinct UI variants** on a single route behind a `?variant=` URL param and a floating bottom switcher so reviewers can flip through real candidates in the browser and pick or synthesize a winner.

For business logic, state transitions, or data-shape questions, switch to the [logic prototype guide](logic.md).

## Sub-Shape Selection (Prefer Sub-Shape A)

Always judge UI variants alongside the real application shell (header, sidebar, real data, real density):

- **Sub-shape A — Existing Real Route (Preferred)**: Render variants on the target existing route via `?variant=`. Keep existing data fetching, route params, auth, and surrounding chrome intact; swap only the candidate subtree. Also use Sub-shape A when prototyping a new panel, card, or step that naturally lives inside an existing page.
- **Sub-shape B — Throwaway Route (Last Resort)**: Create `/prototype/<name>` following the project's existing routing convention only when the surface has no plausible host page (e.g., an entirely new top-level workspace). Use the exact same `?variant=` param and shared switcher.

*Done when*: Sub-shape choice is justified and documented in a one-line plan comment at the prototype entry.

## Workflow

### 1. Plan & Draft Distinct Variants

Default to **3 variants** (minimum 3, maximum 5) and write a one-line plan comment at the prototype entry (e.g., `// Three settings layouts via ?variant= on /settings`).

Build each variant (`VariantA`, `VariantB`, `VariantC`, ...) using the project's existing styling and component system while ensuring **structural** divergence:

- Vary page layout, information hierarchy, and primary interaction affordance—not just colors, spacing, or copy tweaks.
- Keep variants independent of one another (share fetched data or the outer app header, while letting each variant define its own layout).
- Use read-only data or local in-memory stubs for mutations so backend side effects stay isolated.

*Done when*: 3–5 named variant components exist with genuinely distinct layouts and information hierarchies.

### 2. Wire `?variant=` and the Floating Switcher

Read the `?variant=` search param (defaulting to `'A'`) on the route and render the matching variant alongside a shared `<PrototypeSwitcher />` component placed in the project's shared UI directory:

```tsx
const variant = searchParams.get('variant') ?? 'A';
return (
  <>
    {variant === 'A' && <VariantA {...data} />}
    {variant === 'B' && <VariantB {...data} />}
    {variant === 'C' && <VariantC {...data} />}
    {process.env.NODE_ENV !== 'production' && (
      <PrototypeSwitcher variants={['A', 'B', 'C']} current={variant} />
    )}
  </>
);
```

Build `<PrototypeSwitcher />` as a fixed bottom-center floating bar with:

- **Previous (`←`) and Next (`→`) buttons** plus keyboard `←` / `→` cycling (wrap around; skip key handling when `<input>`, `<textarea>`, or `[contenteditable]` has focus).
- **Active variant label** showing the key and descriptive name (e.g., `B (Sidebar layout)`).
- **URL sync** via the framework router's replace navigation (`router.replace`, `navigate(..., { replace: true })`, etc.) so links are reload-stable and shareable.
- **Distinct visual pill** (high-contrast floating badge with subtle shadow) clearly separate from the candidate UI.
- **Production build gate** (`process.env.NODE_ENV !== 'production'` or framework equivalent) so the switcher stays excluded from production bundles.

*Done when*: All variants cycle cleanly via both clicks and arrow keys, URL `?variant=` stays in sync across reloads, and the switcher is gated out of production builds.

### 3. Hand Over, Fold Winner & Archive

Share the live URL and `?variant=` keys. Capture synthesis feedback (e.g., *"header from B with sidebar from C"*) and record the final design verdict.

Once settled:

1. **Fold cleanly**: Rewrite the winning or synthesized layout to production standards (with proper error handling, real mutations, and tests) inside the real route—for Sub-shape B, promote it to its permanent route and remove `/prototype/<name>`.
2. **Clean production branches**: Remove losing variant files and `<PrototypeSwitcher />` from the working/main branch.
3. **Preserve primary source**: Commit all prototype variants and the switcher onto an isolated `prototype/<name>` branch so the full exploration remains inspectable without cluttering `main`.

*Done when*: The production route implements the chosen design cleanly without prototype switchers or dead variants, and all prototype variants are preserved on `prototype/<name>`.
