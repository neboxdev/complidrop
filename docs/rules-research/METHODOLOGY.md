# Compliance rule research methodology

This document governs every entry in `docs/rules-research/`. It is the contract
between researchers (human or model) and the rule engine: **an obligation that
does not follow this methodology does not get encoded.**

## Scope (locked 2026-07-07, Phase 0 interview)

- **Market: United States only.** Federal rules + the State of Texas. No other
  state, no other country. Spanish/EU regulation is explicitly out of scope and
  must never appear here, in fixtures, or in examples.
- **Rule subjects (entity types):**
  - Vendor types tracked by event venues: `caterer`, `event-rental`,
    `security-service`, `transportation`, `photographer-videographer`
  - The venue organization itself: `venue-org`
- **Obligation categories:** business/activity licenses & permits; per-worker
  certifications/credentials; insurance **where mandated by law** (explicitly
  distinguished from contractual COI requirements, which the existing checklist
  feature already covers). W-9/tax forms deferred.
- **Jurisdiction levels:** `federal` and `state-tx`. Municipal/county
  obligations (city health permits, fire marshal inspections, certificates of
  occupancy) are OUT of encoding scope for v1 but must be **noted** in the
  dossier under "Local-level obligations (noted, not encoded)" wherever they
  exist, so the product can say "check your city/county" instead of implying
  completeness.

## Non-negotiables

1. **No rule without a primary source.** The citation of record must be an
   official US primary source:
   - Federal: eCFR (`ecfr.gov`), U.S. Code (`uscode.house.gov`), Federal
     Register, or the responsible agency's `.gov` site (IRS, OSHA, DOL, FMCSA,
     FAA, EPA…).
   - Texas: Texas statutes (`statutes.capitol.texas.gov`), Texas Administrative
     Code (via `texreg.sos.state.tx.us` / SOS), or the responsible state
     agency's official site (TABC `tabc.texas.gov`, DPS `dps.texas.gov`, TDLR
     `tdlr.texas.gov`, DSHS `dshs.texas.gov`, TxDMV `txdmv.gov`, Comptroller
     `comptroller.texas.gov`).
   - Secondary sources (blogs, law firms, compliance vendors) may guide the
     search but are NEVER the citation of record.
2. **Web-verify in-session.** Training data is presumed stale. `verified`
   requires that the researcher fetched and read the live primary source during
   this research session and records the verified-date.
3. **Confidence is first-class:**
   - `verified` — primary source fetched and read in-session; citation, section
     and effective date recorded; **AND the operative sentence(s) quoted
     verbatim in the entry's `Operative text` field** (see below). Only
     `verified` rules ship.
   - `probable` — strong secondary corroboration or a primary source that could
     not be fetched live (paywall, fetch failure); goes to founder review queue.
   - `uncertain` — conflicting sources, ambiguous applicability, or
     unverifiable cadence; review queue, never shipped.

   **Quote-the-text rule (2026-07-07, compensating control after the Fable→Opus
   pivot):** because we can no longer rely on a frontier model's judgment as the
   correctness backstop, a `verified` confidence REQUIRES that you quote the
   controlling statutory/regulatory sentence(s) verbatim. If you cannot fetch
   and quote the operative text, the entry is at most `probable`. Do not upgrade
   confidence to move faster — a wrong `verified` rule is the worst outcome in
   this project.

   **Source-access reality & the `provenance` qualifier (2026-07-07, confirmed
   by the orchestrator by direct fetch).** Live-tooling access to primary
   sources is uneven, so every entry ALSO records a `Provenance` value and
   `verified` is defined against it:
   - **Federal CFR/USC:** an official primary path EXISTS even when
     `ecfr.gov` anti-bot-redirects — use **`govinfo.gov` (GPO)**; Cornell LII is
     a strong secondary cross-check. Federal `verified` ⇒ `provenance: official`.
   - **Texas statutes:** `statutes.capitol.texas.gov` is an Angular SPA that
     serves ONLY its JS shell to flat WebFetch AND curl (confirmed 3×), and
     `web.archive.org` is blocked by our tooling. **BUT the official section
     text IS reachable via the Playwright browser** (a real browser renders the
     SPA — confirmed 2026-07-07). So prefer Playwright for the citation-of-record
     read; faithful full-text reproductions (`texas.public.law`, Cornell LII,
     FindLaw) are the fast corroboration path. Same for `ecfr.gov`: flat WebFetch
     302-redirects to an anti-bot host, but Playwright renders it.
   - **`provenance` values:** `official` (read on a `.gov`/GPO primary source) |
     `reproduction-corroborated` (verbatim text confirmed across **≥2
     independent** faithful reproductions, citation-of-record still the official
     statute) | `secondary` (single reproduction, or interpretive source).
   - **A `verified` entry REQUIRES `provenance ∈ {official,
     reproduction-corroborated}`.** A single reproduction, any cross-reproduction
     disagreement, or reliance on a down agency site (e.g. dps.texas.gov) ⇒
     `probable` at most.
   - **Human-gate caveat:** `reproduction-corroborated` Texas rules are NOT
     official-read. They are flagged for the founder to spot-confirm against the
     official site (which renders fine in a human browser) BEFORE their
     rule-set is enabled — especially load-bearing numbers (insurance minimums,
     license terms). This is recorded in RULES-REVIEW.md and is part of sign-off.
4. **Never fabricate.** No invented thresholds, dates, fee amounts, cadences or
   citations. If a fact can't be established from a fetched source, the entry
   says so and takes the lower confidence.
5. **Regulatory vs contractual.** Every insurance entry states whether the
   requirement comes from statute/regulation (`regulatory`) or is merely
   customary/contract-driven (`contractual` — noted for context, never encoded
   as a legal obligation).
6. **Absence is a finding.** "No state license exists for X in Texas" is a
   first-class, sourced entry (cite the agency page or statute scope that
   demonstrates the absence). The engine must know the difference between "no
   obligation" and "not researched".

## Entry format

Each obligation is a section in the entity file with this exact field list:

```markdown
### OBL-<jurisdiction>-<entity>-<nnn>: <short name>

- **Applies to:** <entity type + precise applicability conditions: activity
  triggers, headcount/revenue thresholds, seating capacity, for-hire status…>
- **Obligation:** <what document/credential/filing must exist>
- **Issuing/enforcing authority:** <agency>
- **Cadence:** <initial + renewal period, anchored how (issue date, calendar
  year, birth date…); grace period if any, with source>
- **Penalty for lapse:** <summary, sourced>
- **Category:** license | permit | worker-certification | insurance | filing
- **Basis:** regulatory | contractual-noted
- **Citation:** <statute/rule section> — <URL actually fetched>
- **Operative text:** "<verbatim quote of the controlling sentence(s) from the
  primary source — REQUIRED for confidence `verified`>"
- **Effective date of cited text:** <as shown on source, or "current as
  displayed">
- **Verified:** <YYYY-MM-DD> | **Confidence:** verified | probable | uncertain
- **Notes:** <ambiguities, local-level variation, edge cases, related
  obligations out of scope>
```

Numbering: `OBL-FED-CATERER-001`, `OBL-TX-SECURITY-003`, etc. Numbers are
stable once assigned; never reuse a retired number.

## File layout

```
docs/rules-research/
  METHODOLOGY.md          (this file)
  federal/<entity>.md     (federal obligations per entity type)
  texas/<entity>.md       (Texas obligations per entity type)
```

Each file **begins** with a status header and **ends** with two mandatory
sections:

Header (first line of the file, for interruption-durability):
```markdown
**Status:** PARTIAL | COMPLETE   <!-- COMPLETE only when every section is done -->
```
Write the file EARLY (after your first verified entry) and update it as you go,
so an interrupted session leaves clearly-labelled durable progress.

Trailing sections:
- **Local-level obligations (noted, not encoded)**
- **Open questions / review-queue candidates** — anything unresolved.

## Verification discipline for researchers

- Prefer fetching the statute/rule text itself over the agency's plain-language
  summary; when only the summary is fetchable, cite both and say which was read.
- Record the URL you actually fetched (deep link to the section, not the site
  root).
- When a cadence differs between statute and agency practice (e.g. statute says
  "two years", agency page says renewal windows), record both and cite both.
- Texas Administrative Code fetches: texreg URLs are stateful/fragile — if a
  TAC section can't be fetched directly, try the agency's published PDF of the
  rule chapter; if neither works, the entry is at most `probable`.
