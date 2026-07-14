# G2 / G3 gate-closure evidence (2026-07-09)

Gates G2 (Texas statutory figures) and G3 (the §387.33T suspension artifact) were
designed as founder-in-browser confirmations. On 2026-07-09 the founder **explicitly
delegated their closure** ("I need YOU to take charge on all of those, I trust
you"); this directory is the evidence record of the delegated verification —
performed live in a real browser session against the sources below, on top of the
three prior independent derivations (Phase-1 research, Pass-2 blind re-derivation,
Pass-5 live re-read).

| Evidence | Figure confirmed | Source (live, 2026-07-09) |
|---|---|---|
| `g2-1702-124-insurance.png` | Tex. Occ. Code §1702.124(c): **$100,000**/occurrence BI+PD, **$50,000**/occurrence personal injury, **$200,000** total aggregate. Same page confirms §1702.301: licenses/commissions expire **not later than the second anniversary**; PPO tied to the commission | `statutes.capitol.texas.gov/Docs/OC/htm/OC.1702.htm` (OFFICIAL) |
| `g2-2151-1012-inflatable-csl.png` | Tex. Occ. Code §2151.1012: continuous-airflow inflatables need a **combined single limit policy ≥ $1,000,000 per occurrence** (no statutory aggregate) | `statutes.capitol.texas.gov/Docs/OC/htm/OC.2151.htm` (OFFICIAL) |
| `g2-tabc-11-09-two-year-term.png` | Alco. Bev. Code §11.09(a): permits expire **on the second anniversary**; §11.11(a): conduct surety bond **$5,000** / **$10,000** within 1,000 ft of a public school | `statutes.capitol.texas.gov/Docs/AL/htm/AL.11.htm` (OFFICIAL) |
| `g3-387-33T-schedule-of-limits.png` | 49 CFR §387.33T Schedule of Limits: **$5,000,000** (16+ seats incl. driver) / **$1,500,000** (≤15 incl. driver). The section in force is 387.33T (the "§387.33 suspended" note is the 2017 codification artifact) — closes G3 | `ecfr.gov` Part 387 Subpart B (OFFICIAL) |
| (text excerpt below) | 43 TAC §218.16(a) intrastate tiers | TxDMV Chapter-218 adoption order PDF (OFFICIAL agency host) |

## 43 TAC §218.16(a) — PDF text evidence

Source: `https://www.txdmv.gov/sites/default/files/body-files/Chapter-218-Adopt-2024.pdf`
(SHA-256 `3ff18b0c57340ce31702aac59859dc772149f5790145beaf4c019584e6cd7602`,
fetched 2026-07-08 and re-read for this closure). Operative schedule rows, extracted
verbatim:

> 2. Vehicles, including buses, designed or used to transport more **$500,000**
> than 15 people, but fewer than 27 people, **including the driver**.
>
> 3. Vehicles, including buses, designed or used to transport **27 or** **$5,000,000**
> **more people, including the driver**.

(The dollar figure interleaves with the row text in the PDF's two-column layout;
rows 2 and 3 correspond to the encoded $500k 16–26 and $5M 27+ tiers.)

## Verification method

Each page was loaded in a Chromium browser (Playwright), the operative sentences
were programmatically asserted present in the rendered text (exact-substring match
against the encoded figures), the section was scrolled into view, and the viewport
was captured. The programmatic assertions all returned true; the screenshots are
the human-readable record.

**Consequence:** the `reviewGate: "founder-confirm-tx-security"` marker on
`us-tx/security-service.json` — whose condition was exactly this confirmation — is
lifted in the same commit. Customer exposure remains blocked by gate G1 (counsel)
and the `RuleEngine:Enabled=false` default; enablement per rule set stays a
deliberate config decision.
