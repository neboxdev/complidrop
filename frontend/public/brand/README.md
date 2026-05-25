# CompliDrop brand assets — static SVG copies

These six SVG files are **static copies** of the canonical brand artwork
maintained in `docs/brand/logo-refresh-2026/svg/`. They are *not* consumed by
the live Next.js app — the live UI inlines the SVG paths directly via
`frontend/src/components/Logo.tsx` and the `next/og` icon/OG generators
(see `app/icon.svg`, `app/apple-icon.tsx`, `app/opengraph-image.tsx`).

## Why these copies exist

Browser-loadable static SVGs are still useful for non-React consumers that
will exist over the life of the product:

- **Email templates** — once HTML email wrappers are introduced (deferred
  to a future ticket), they need a hosted, cacheable logo URL.
- **External embeds** — third-party docs, press kits, partner sites, status
  pages, etc.
- **Marketing pages outside Next.js** — landing experiments, ad creative.
- **Manual debugging** — opening the SVG directly in a browser to confirm
  the artwork renders.

## Source of truth & sync

The shared TypeScript constants for **paths and colors** live at
[`frontend/src/lib/brand.ts`](../../src/lib/brand.ts). When the brand artwork
changes, update both:

1. `docs/brand/logo-refresh-2026/svg/*.svg` (canonical source)
2. `frontend/src/lib/brand.ts` (in-app rendering — `DROPLET_PATH`, `CHECK_PATH`, `BRAND_COLORS`)

Then re-copy the SVGs into this folder so external consumers see the new
artwork:

```bash
cp docs/brand/logo-refresh-2026/svg/*.svg frontend/public/brand/
```

(If the brand ever ships multiple iterations, archive each generation under
`docs/brand/<iteration>/` rather than overwriting in place.)

## Files

| File | Use |
|---|---|
| `complidrop-logo-horizontal.svg` | Primary horizontal lockup — sky droplet + navy wordmark on white/light |
| `complidrop-logo-horizontal-twotone.svg` | Marketing two-tone — "Compli" navy + "Drop" sky |
| `complidrop-logo-horizontal-reverse.svg` | White wordmark for navy / dark backgrounds |
| `complidrop-mark.svg` | Icon only — droplet + check |
| `complidrop-mark-reverse.svg` | Identical artwork to `complidrop-mark.svg` (kept for naming parity) |
| `complidrop-favicon.svg` | Sky tile + inset white droplet — favicon / app-icon source |

## Brand rules — verbatim from `CLAUDE.md` and the design handoff README

- Sky `#0EA5E9`, navy `#0C4A6E`, white `#FFFFFF`. **Never orange (`#F97316`)** —
  it's reserved for UI accents (CTAs, "Most Popular" pill, expiring badges).
- Wordmark: Plus Jakarta Sans 700, letter-spacing `-0.02em`.
- Aspect ratio of the horizontal lockup is locked — no squashing, stretching,
  or recoloring.
