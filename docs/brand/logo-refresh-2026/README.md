# Handoff: CompliDrop Logo Refresh

## Overview

A new logo system for **CompliDrop** (https://www.complidrop.com/) — an AI compliance-document tracker for COIs, licenses, and permits. The previous logo used a teal palette that didn't match the actual brand colors of the site (sky-blue / navy / orange). This handoff replaces it with a coordinated logo system that:

- Uses the existing brand palette correctly (sky `#0EA5E9`, navy `#0C4A6E`)
- Uses the brand typeface (Plus Jakarta Sans 700)
- Keeps the recognizable **droplet + checkmark** motif from the old logo
- Adds variants for marketing copy that emphasizes the verb **"Drop"** (key to your brand voice — "Drop It", "Start Dropping Docs")
- Provides reverse-on-dark and small-size (favicon) variants

## About the Design Files

The files in this bundle — the `.html` preview and `Logo.jsx` component — are **design references**. The SVGs in `svg/` are production-ready assets you can ship directly. The `Logo.jsx` component is a reference React implementation you can adapt to your codebase's existing patterns (component library, styling system, TypeScript, etc.). Don't copy-paste blindly if your codebase has conventions that differ — match those.

## Fidelity

**High-fidelity.** Exact hex values, typography, spacing, and aspect ratios are specified below. The provided SVGs are the canonical artwork — match them pixel-for-pixel.

---

## Logo Variants

There are **4 variants** of the logo, plus a favicon. Use the right one for the context — see "Where to Use Each Variant" below for placement rules.

### 1. Primary horizontal lockup — `complidrop-logo-horizontal.svg`
- Sky-blue droplet (`#0EA5E9`) with white check
- Navy wordmark (`#0C4A6E`)
- **Use on:** white or `#F0F9FF` backgrounds. This is your default everywhere unless context calls for another variant.

### 2. Marketing two-tone — `complidrop-logo-horizontal-twotone.svg`
- Sky-blue droplet with white check
- Wordmark: `Compli` in navy + `Drop` in sky-blue
- **Use on:** the homepage hero, "How It Works" section header, social cards, anywhere "Drop" as a verb is being reinforced. Splitting the wordmark visually amplifies the brand's central action.

### 3. Reverse — `complidrop-logo-horizontal-reverse.svg`
- Sky-blue droplet with white check
- White wordmark (`#FFFFFF`)
- **Use on:** navy backgrounds, the footer, dark-mode contexts, OG images, email headers with dark backgrounds.

### 4. Icon only — `complidrop-mark.svg`
- Just the droplet + check
- **Use on:** mobile nav (collapsed), upload zones (consider a subtle bounce animation on drag-enter), loading states, the "Drop It" step illustration in the marketing copy.

### 5. App icon / favicon — `complidrop-favicon.svg`
- Sky tile with rounded corners + inset white droplet
- **Use as:** favicon (32×32, 16×16), iOS home-screen icon, Android adaptive icon foreground, PWA icon.

---

## Where to Use Each Variant — Placement Rules for complidrop.com

| Location | Variant | Notes |
|---|---|---|
| Site header (top nav, left) | `complidrop-logo-horizontal.svg` | Height: 32–36px. Replace current header logo. |
| Marketing hero ("Stop Chasing Paper") | `complidrop-logo-horizontal-twotone.svg` | Height: 40–48px above headline. Reinforces "Drop" verb. |
| Footer | `complidrop-logo-horizontal-reverse.svg` | Already has navy background; use white-text variant. |
| Login / Register pages | `complidrop-logo-horizontal.svg` | Height: 36px, centered above form. |
| Email transactional headers | `complidrop-logo-horizontal.svg` | Embed inline. Plus Jakarta Sans should fall back gracefully — see "Font fallback" below. |
| Favicon | `complidrop-favicon.svg` | Plus PNG fallback at 32×32 for older browsers. |
| Apple touch icon | `complidrop-favicon.svg` | Export to 180×180 PNG. |
| OG / Twitter card | Use `complidrop-logo-horizontal-twotone.svg` on white, or `-reverse.svg` on navy. | Compose into 1200×630 canvas. |
| App upload zone | `complidrop-mark.svg` | Use as a visual cue. Consider subtle bounce on `dragenter`. |

---

## Design Tokens

```css
/* Brand colors */
--color-sky:    #0EA5E9;  /* Primary — droplet, links, primary buttons */
--color-navy:   #0C4A6E;  /* Wordmark, dark text */
--color-orange: #F97316;  /* Accent — "Most Popular" pill, CTAs, alerts. NOT used in logo. */
--color-tint:   #F0F9FF;  /* Light section backgrounds */
--color-white:  #FFFFFF;

/* Typography */
--font-brand: 'Plus Jakarta Sans', system-ui, sans-serif;
--font-weight-wordmark: 700;
--letter-spacing-wordmark: -0.02em;
```

### Important — keep orange OUT of the logo
The orange (`#F97316`) is doing critical UI work on the site as an attention/urgency color: "Most Popular" pill, CTAs, expiration warnings. Diluting it inside the logo weakens it in the UI. **Reserve orange for:**
- "Most Popular" pricing pill
- "Expiring soon" badges
- Primary CTAs on navy backgrounds
- Alert / warning notifications

---

## Spacing & Sizing Rules

- **Minimum clear space** around the logo: 0.5× the icon height on all sides.
- **Minimum size:** horizontal lockup — 100px wide. Below that, switch to the icon-only mark.
- **Aspect ratio of horizontal lockup:** lock it. Don't squash, stretch, or recolor.
- **Icon-to-wordmark gap:** 0.35× the icon height (the React component handles this).
- **Wordmark font-size:** 1.3× the icon height.

---

## Font fallback

Plus Jakarta Sans is the brand font and is already loaded on complidrop.com via Google Fonts. The SVG lockups use `<text>` elements that render with this font when present. **For contexts where the font may not be available** (email clients, third-party embeds, social previews), do one of:

1. Export the SVG with text outlined to paths (e.g. via Figma "Outline stroke" → "Flatten").
2. Rasterize to PNG at 2× resolution for that use case.

---

## Files in this Bundle

```
design_handoff_complidrop_logo/
├── README.md                                  ← you are here
├── PROMPT.md                                  ← paste this into Claude Code to implement
├── preview.html                               ← open in browser to see all variants
├── svg/
│   ├── complidrop-logo-horizontal.svg         (primary)
│   ├── complidrop-logo-horizontal-twotone.svg (marketing hero)
│   ├── complidrop-logo-horizontal-reverse.svg (for navy backgrounds)
│   ├── complidrop-mark.svg                    (icon only)
│   ├── complidrop-mark-reverse.svg            (icon only — same artwork)
│   └── complidrop-favicon.svg                 (square app icon / favicon)
└── components/
    └── Logo.jsx                               (reference React component)
```

---

## Implementation Checklist

- [ ] Drop SVGs into `public/brand/` (or wherever the project keeps static brand assets).
- [ ] Replace the current header logo with `complidrop-logo-horizontal.svg`.
- [ ] Update the marketing hero to use `complidrop-logo-horizontal-twotone.svg`.
- [ ] Update the footer logo to `complidrop-logo-horizontal-reverse.svg`.
- [ ] Replace `favicon.ico` and `apple-touch-icon.png`:
  - `favicon.svg` → use the SVG directly via `<link rel="icon" type="image/svg+xml" href="/complidrop-favicon.svg">`
  - `favicon.png` → export at 32×32 as fallback
  - `apple-touch-icon.png` → export at 180×180
- [ ] Optionally adopt `components/Logo.jsx` as a single reusable component (recommended if you have 4+ logo placements).
- [ ] Regenerate OG image (`og-image.png`) at 1200×630 with the two-tone or reverse lockup.
- [ ] Update any places that still embed the old teal logo.

---

## Optional polish (not strictly required)

- On the marketing site's upload demo (the "Drop It" step), add a subtle bounce animation to the icon on `dragenter`. Brand reinforcement of the verb.
- On `Most Popular` pricing card, the orange pill can use the icon as a tiny inline accent (sky on white) at 14px to reinforce the brand without competing with the orange.
