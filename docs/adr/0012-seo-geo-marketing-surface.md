# 0012. Public marketing/SEO surface: server-rendered content pages, structured data, and an AI-crawler-allow robots policy

- **Status:** accepted
- **Date:** 2026-05-29
- **Deciders:** Ruben G.

## Context

Until now CompliDrop's entire public, indexable web presence was a single page (`/`). Every other route is behind auth (`/dashboard`, `/documents`, …) or a token (`/portal/{token}`). There was no `robots.txt`, no `sitemap.xml`, no structured data, and no content pages — so search engines had almost nothing to rank, and AI assistants (ChatGPT, Gemini, Perplexity, Copilot) had nothing to cite. The homepage was also a `"use client"` component, which both shipped marketing JS to every visitor and prevented per-page metadata/JSON-LD.

Three research streams (technical SEO, GEO/AEO, and the COI-tracking competitive landscape) informed this work ([#176]). The load-bearing conclusions:

- For a brand-new domain in a niche owned by funded incumbents (TrustLayer, myCOI/illumend, Jones, Certificial, SmartCompliance, bcs — all sales-led, quote-only, ~$1.5k–$10k+/yr), the durable wins are **flawless technical hygiene**, **bottom-of-funnel + ultra-niche content the incumbents ignore**, and **off-site signals** (review sites, listicles). The off-site lever is dominant for AI recommendations but is out-of-repo work.
- **GEO is ~80% good SEO + reviews + PR.** The most-hyped on-site tactic, `llms.txt`, is **not consumed by any major assistant in 2026** (corroborated by Ahrefs, Google's Mueller, and a 129k-domain study) — so we deliberately do not ship one.
- **AI crawlers must be allowed.** Blocking `OAI-SearchBot` removes us from ChatGPT Search; blocking `Google-Extended` removes us from Google AI Overviews. A new product trying to be discovered has everything to gain and no ad revenue to protect.

This ADR records the structural decisions so future content pages follow one pattern and no one accidentally regresses the crawler policy.

## Decision

1. **Public marketing pages are server components.** The homepage and all content pages (`/faq`, `/glossary`, `/glossary/[slug]`, `/coi-tracking-software-vs-spreadsheet`, `/coi-tracking-for-event-venues`) render their content server-side so it appears in the initial HTML for crawlers and AI, and ship no marketing JS. The single client island is `MarketingHeader` (auth-aware CTA via `useMe`); everything else, including CTA hover, uses CSS rather than JS handlers, composing from the design-system tokens (`primary`/`accent`/`foreground`/`muted-foreground`) wherever one exists. (The dark-surface `#082F49` and decorative-blob shades stay literal — `globals.css` has no token for them — matching the pre-change palette.)

2. **Brand facts have one source.** `lib/site.ts` owns the canonical origin and NAP facts (name, description, category, price — the price imported from `lib/plans.ts`). Metadata, `sitemap.ts`, `robots.ts`, `manifest.ts`, and the JSON-LD builders all compose from it, so a fact can't drift between surfaces. Consistent brand facts are both an SEO signal and a GEO factor.

3. **Per-page metadata goes through `pageMetadata()` (`lib/seo.ts`).** It always sets a self-referencing canonical, an Open Graph block, and a Twitter card. Canonical is set **per page, never in the root layout** — a layout-level canonical would point every route at `/`.

4. **Structured data via typed builders + an escaped `<JsonLd>`.** `lib/structured-data.ts` builds `Organization` + `WebSite` (site-wide), `SoftwareApplication` + `Offer` (home), `FAQPage`, `BreadcrumbList`, and `DefinedTerm` (glossary). `components/JsonLd.tsx` renders a native `<script type="application/ld+json">` and unconditionally escapes `<` → `<` (JSON.stringify does not sanitize XSS). **No fabricated `aggregateRating`/`review`** — synthesizing reviews is a Google manual-action risk; ratings are added only when real review data exists.

5. **`robots.ts` allows all crawlers (including AI) on everything except the app surfaces** (`/api/`, dashboard routes, `/portal/`). The AI crawlers (`OAI-SearchBot`, `ChatGPT-User`, `GPTBot`, `PerplexityBot`, `Google-Extended`, `ClaudeBot`, `Claude-Web`, `CCBot`) are listed explicitly — functionally redundant with `*`, but it documents the deliberate welcome so a later "cleanup" can't silently block us.

6. **No `llms.txt`.** It is not consumed by any major assistant in 2026; shipping one would imply a maintained contract that does nothing. Revisit only if a major assistant announces real support.

## Consequences

### Positive
- Search engines and AI assistants now have a sitemap, structured data, and several genuinely useful pages to index and cite.
- The homepage ships less JS (better INP) and carries keyword-aligned metadata + `SoftwareApplication` schema.
- One documented pattern for every future content page (glossary terms, comparison/alternative pages, per-industry/per-state expansion).
- The `/coi-tracking-for-event-venues` page doubles as a landing page for the cold-email GTM beachhead.

### Negative
- More public surface to maintain (copy can go stale; the sitemap must be kept in sync as pages are added — called out in `sitemap.ts`).
- Per-page OG images are not yet generated (all pages inherit the brand card). A future polish.

### Neutral
- The dominant GEO lever — review-site presence (G2/Capterra), "best COI software" listicles, Bing Webmaster Tools / Search Console verification, Wikidata/Crunchbase — is **off-site work owned by the founder**, not code, and is explicitly out of scope here.
- The research's Reddit/LinkedIn recommendations are **not adopted**: they conflict with the stated GTM constraints (no Reddit — prior failure; no LinkedIn — employer). Quora and a NAP-only company entity are milder substitutes left to the founder's judgment.

## Alternatives considered

### Option A — Keep the homepage a client component, add JSON-LD inside it
JSON-LD would still render in the SSR HTML, so this "works." Rejected because it keeps shipping the full marketing bundle to every visitor, blocks per-page `metadata`, and leaves the content-page pattern undefined. Converting to a server component with one small client island is cleaner and faster.

### Option B — A `(marketing)` route group with a shared layout
Cleaner in principle (header/footer in one layout). Rejected for now because it requires moving `app/page.tsx` into the group and reworking the homepage test's render model for marginal benefit; each page composing `MarketingHeader`/`MarketingFooter` directly is explicit and low-risk. Revisit if the page count grows.

### Option C — Ship `llms.txt`
Rejected: no major assistant consumes it in 2026 (see Context). It would be cargo-cult maintenance.

## References

- Tickets: [#176](https://github.com/neboxdev/complidrop/issues/176) (this work; full research synthesis in the session log)
- ADRs: [0011](0011-plan-vocab-unified-with-founding-as-authenticated-only-promo.md) (`lib/plans.ts`, the price source `lib/site.ts` reads from)
- External: Next.js Metadata / sitemap / robots / JSON-LD file-convention docs (v16.2.x, bundled in `node_modules/next/dist/docs`); Princeton GEO study (arXiv:2311.09735); SE Ranking 129k-domain ChatGPT-citation study; Ahrefs and Google (Mueller) on `llms.txt` non-adoption
