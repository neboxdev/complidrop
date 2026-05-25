# Claude Code Implementation Prompt

Paste this into Claude Code from the root of your CompliDrop project, after copying the `design_handoff_complidrop_logo/` folder into your repo.

---

You are implementing a **logo refresh for CompliDrop** (https://www.complidrop.com/).

All design context, assets, and placement rules are in `design_handoff_complidrop_logo/`. Start by reading:

1. `design_handoff_complidrop_logo/README.md` — full spec
2. `design_handoff_complidrop_logo/components/Logo.jsx` — reference React component
3. `design_handoff_complidrop_logo/svg/` — production SVG assets

## Task

Replace the existing CompliDrop logo across the codebase with the new logo system. There are **4 variants** plus a favicon — see the README for placement rules. The key facts:

- **Brand colors:** sky `#0EA5E9`, navy `#0C4A6E`, white `#FFFFFF`. Orange `#F97316` is reserved for UI accents (CTAs, alerts, "Most Popular" pill) — **do not** put orange in the logo.
- **Typography:** Plus Jakarta Sans 700, letter-spacing `-0.02em`.
- **Motif:** sky-blue droplet with white checkmark — preserved from the previous logo.

## Implementation Plan

Before making changes, **explore the codebase** to understand:

1. Where the current logo is referenced (search for `logo`, `complidrop`, `<img.*logo`, existing SVG imports, header/footer components).
2. The component framework in use (React? Next.js? Vue? plain HTML?) and adapt the reference `Logo.jsx` accordingly. Match the project's existing conventions for components, file naming, and styling.
3. How static assets are served (public folder? imports? CDN?). Place the new SVGs consistently.
4. The favicon setup (`<link rel="icon">` tags, `manifest.json`, `apple-touch-icon`, etc.) so you know what to replace.

Then, in this order:

1. **Copy SVG assets** from `design_handoff_complidrop_logo/svg/` into the project's brand-assets folder (e.g. `public/brand/` or `src/assets/brand/`).
2. **Create a `Logo` component** based on the reference at `design_handoff_complidrop_logo/components/Logo.jsx`. Adapt it to the project's component conventions — TypeScript types if applicable, named export if that's the project's pattern, CSS modules / Tailwind / styled-components instead of inline styles if that's what's in use. The component must support these variants: `primary`, `twotone`, `reverse`, `mark`.
3. **Replace logo usages** site-wide using the placement rules in the README:
   - Header → `<Logo variant="primary" height={36} />`
   - Marketing hero → `<Logo variant="twotone" height={48} />`
   - Footer → `<Logo variant="reverse" height={32} />`
   - Login / Register pages → `<Logo variant="primary" height={36} />`
   - Email templates (if any) → embed the inline SVG version of the primary lockup
4. **Update favicon and app icons:**
   - `<link rel="icon" type="image/svg+xml" href="/brand/complidrop-favicon.svg">` (modern browsers)
   - Generate a 32×32 PNG fallback from `complidrop-favicon.svg`
   - Generate a 180×180 `apple-touch-icon.png` from `complidrop-favicon.svg`
   - Update `manifest.json` icons array if PWA support exists
5. **Regenerate the OG / Twitter card image** at 1200×630. If there's a template or script for this, update it; otherwise note that this needs to be done manually in a design tool.
6. **Verify visually** by running the dev server and walking through: home, login, register, footer (any page), and viewing the favicon in the tab.
7. **Remove the old logo files** from the repo (search for any orphaned references first).

## Constraints

- **Match the project's existing patterns.** If components live in `components/`, put the Logo component there. If they use TypeScript, write `.tsx`. If they use Tailwind, use Tailwind. Don't impose new conventions.
- **Don't add orange to the logo itself.** It's reserved for UI accents.
- **Preserve accessibility:** the logo should expose `aria-label="CompliDrop"` or equivalent; the icon-only variant should be `aria-hidden` when paired with adjacent CompliDrop text.
- **Don't change site copy, layout, or other styling** — this PR is strictly the logo refresh.
- **Plus Jakarta Sans must already be loaded** on every page where the wordmark renders. Check that the font is loaded on login/register/email pages too, not just marketing.

## When you finish

Print a summary listing:
- Every file you changed (added, modified, deleted)
- Every place the logo now appears
- Anything that needs human follow-up (OG image regeneration, design-tool exports, PNG fallback generation if not scriptable, etc.)
