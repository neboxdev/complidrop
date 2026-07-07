# HANDOFF — Compliance rule engine session (started 2026-07-07)

Continuous handoff state for the rule-engine build. Updated after every
completed unit of work. If you are a weaker model (Opus) reading this
mid-session: do ONLY tasks tagged [OPUS]. Never derive/modify rule content,
change a confidence level, or run review passes 1–3. Rule questions go to the
FABLE-QUEUE section below, unresolved.

## Session brief (condensed)

Build CompliDrop's compliance rule engine: verified, jurisdiction-specific,
US-only rule data (federal + Texas) + a deterministic C# evaluation engine.
Correctness beats coverage beats speed. No rule without a live-verified primary
source. Confidence (`verified|probable|uncertain`) is first-class; only
`verified` ships. Founder signs off on rule CONTENT before anything merges.
Full brief: see the session transcript; methodology:
[docs/rules-research/METHODOLOGY.md](rules-research/METHODOLOGY.md).

## Phase 0 — scoping (DONE 2026-07-07)

Founder-approved scope:
- **Jurisdictions:** Federal + Texas.
- **Rule subjects:** BOTH the venue org itself AND vendor entity types
  (caterer, event-rental, security-service, transportation,
  photographer-videographer).
- **Categories:** licenses & permits; worker certifications; insurance where
  law-mandated (regulatory vs contractual explicitly marked). W-9 deferred.
- **Rule storage:** versioned in-repo data files (JSON/YAML), typed load at
  boot; every rule change is a diffable PR.
- **Liability framing:** adopt the existing terms-page posture ("not legal,
  insurance, or professional advice") — engine output = tracked obligations
  with sources, never legal conclusions. Terms language at
  `frontend/src/app/terms/page.tsx` ("Automatic reading is a head start, not
  advice" section).

Codebase facts that shape the design:
- Existing `ComplianceTemplate`/`ComplianceRule` (api/CompliDrop.Api/Entities/Compliance.cs)
  encode per-org CONTRACTUAL checklists — the new engine is a separate layer,
  not a rewrite.
- No jurisdiction/entity-profile data exists yet: `Organization` has no
  state/address; `Vendor` has no entity type/state/headcount. Engine needs a
  new entity-profile model.
- Spain/EU scope check: CLEAN — only product-privacy GDPR mentions, nothing to
  clean up.
- Open issues #396–#405 are compliance-claim overreach bugs; this work must
  keep verdict framing conservative.

## Phase 1 — research dossier (RE-RUN on Opus, IN PROGRESS 2026-07-07)

First fan-out (Fable) died mid-research with ZERO files written. Re-launched all
six on Opus with the compensating controls + durable incremental writes.
Progress (3 of 6 complete):

| Entity | Status | Fed (v/p/u) | TX (v/p/u) | Notes |
|---|---|---|---|---|
| security-service | ✅ COMPLETE | 0/3/0 | 9/0/0 | TX verified are `reproduction-corroborated` (official .gov unreachable); DPS-dependent fee/renewal facts held `probable`; fed = 3 sourced absences capped `probable` (BLS/archive unreachable). Key: §1702.124 insurance **$100k/$50k/$200k** (orchestrator-confirmed via independent repro). |
| photographer-videographer | ✅ COMPLETE | 4/0/0 | 1/1/0 | Fed verified via **govinfo.gov (official)** + Cornell — Part 107 cert / 24-mo recency / part-48 registration, all drone-gated. TX = 1 absence verified + 1 `probable` (TCEQ photo-finishing, near-nil applicability). |
| event-rental | ✅ COMPLETE | 2/0/0 | 4/1/0 | **Refuted my lead:** amusement rides = **TDI**, not TDLR. Inflatables = Class B amusement ride (mandatory insurance + annual inspection + AR-101 sticker + quarterly AR-800 injury reports). Insurance amount §5.9004(b) **$1M BI/$500k PD or $1.5M CSL** held `probable` (exact TAC figure not on approved primary host). Forklift cert verified via osha.gov. |
| caterer | ✅ COMPLETE | 4/0/0 | 6/0/0 | **Playwright breakthrough** (see below): pulled OFFICIAL text from capitol.texas.gov + ecfr.gov. **Refuted lead:** food-handler window is **30 days** after hire (25 TAC §228.31(d)), not 60. TABC seller-server correctly NOT encoded (voluntary safe-harbor §106.14). HB 1545 → **2-yr** permit term. **New federal find:** caterer serving alcohol must register with **TTB** (Form 5630.5d, 27 CFR 31.x). Caterers **exempt** from FDA food-facility registration (21 CFR 1.227). |
| venue-org | ✅ COMPLETE | 3/1/0 | 7/1/0 | Owns the two cross-cutting entries: **WC elective** (§406.002 from TDI official Act PDF) + document duties (annual **DWC-005** Feb1–Apr30 & within 30d of first hire; §406.005 notices; §406.096 who-IS-required); **sales-tax permit** (no fee, **no expiration**). TABC **2-yr** MB/BG. Franchise tax due **May 15**, threshold **$2.47M (24-25)/$2.65M (26-27)**, No-Tax-Due Report eliminated 2024+. Fed: EIN, no-fed-license absence, **OSHA 300 log at 11+ emp** (venue NAICS NOT exempt; restaurants ARE). TTB `probable`. **Corrected lead:** HB 1545 = **2019**, 2-yr terms phased from Sept 1 2021. |
| transportation | ✅ COMPLETE | 7/0/0 | 3/1/0 | Densest layer. **Fed passenger insurance $5M (16+ seats incl driver) / $1.5M (≤15)** (49 CFR 387.31/387.33; resolved the "§387.33 suspended" artifact — amounts unchanged/enforced). **TX intrastate insurance $500k (16–26 seats) → $5M (26+ not incl driver)** (43 TAC §218.16) — ENGINE MUST branch interstate-vs-intrastate. Four capacity thresholds quoted verbatim (16 incl driver / 15 / 26 not incl driver / 10). Cadences: MCS-150 biennial, DOT medical ≤24mo, Clearinghouse annual, CDL 8-yr. **Corrected leads:** TX insurance = §218.16 not §218.13; FMCSA stopped new MC numbers ~Oct 2025. Added **UCR** (annual, due Dec 31). Fed via eCFR API + govinfo GPO XML (primary). |

**DOSSIER COMPLETE (2026-07-07).** 12 files, ~57 obligations. Totals: **Federal
20 verified / 4 probable**; **Texas 30 verified / 3 probable**; **grand total 50
verified / 7 probable / 0 uncertain**. Cross-agent corroboration observed (TTB
5630.5d found independently by caterer + venue-org; HB 1545 date + TABC 2-yr term
corrected by 3 agents). Now in the review gauntlet (pass 2 re-derivation + pass 1
legal) before the founder Phase-1 checkpoint.

### Source-access reality (updated 2026-07-07 — PLAYWRIGHT BREAKTHROUGH) → drives `provenance`

METHODOLOGY.md carries the full standard. Summary:
- **Federal CFR/USC:** official path via **govinfo.gov (GPO)**, and now also
  **ecfr.gov via Playwright browser** (the caterer agent rendered it). eCFR
  anti-bot-blocks flat WebFetch/curl; a real browser gets through.
- **Texas statutes:** `statutes.capitol.texas.gov` serves only its JS shell to
  WebFetch AND curl, but **the caterer agent read the OFFICIAL section text via
  the Playwright browser** (the SPA renders for a real browser). So
  `provenance: official` for TX statutes IS achievable — via browser, not HTTP.
  `web.archive.org` remains blocked by our tooling; reproductions (public.law,
  Cornell LII, FindLaw) remain the fast path for corroboration.
- **CONSEQUENCE for pass 2:** the re-derivation pass should use **Playwright to
  re-pull official text** for the load-bearing rules that batch-1 read only from
  reproductions (F-1/F-2 in REVIEW-LOG.md), upgrading them
  reproduction-corroborated → official. This shrinks the founder-gate burden.
- **dps.texas.gov** was DOWN this run (ECONNREFUSED) — agency-practice facts
  (fees, renewal windows) held `probable`. Playwright can't fix a down origin;
  retry live later.

### ⚠️ FOUNDER-GATE ITEM (carry into RULES-REVIEW.md)

Batch-1 Texas **statutory** `verified` rules were read from reproductions, NOT
the official host (WebFetch couldn't render the SPA). Pass 2 will re-pull the
load-bearing ones officially via Playwright; whatever still hasn't reached
`provenance: official` by sign-off, the founder should spot-confirm in-browser —
especially **security-guard insurance $100k/$50k/$200k (§1702.124)**, **license
terms**, and the **inflatable insurance $1M/$500k/$1.5M CSL (28 TAC §5.9004(b),
still `probable`)**. Federal rules via govinfo are already official-grade.

Checkpoint unchanged: founder approves the dossier before Phase 2 encoding.

## Rule schema — DRAFT authored (2026-07-07)

[docs/rule-engine/SCHEMA.md](rule-engine/SCHEMA.md): in-repo versioned JSON
(`api/CompliDrop.Api/RuleData/{us-fed,us-tx}/<entity>.json`), append-only rule
versions with validFrom/validTo, three-valued (Kleene) applicability logic over
a typed entity profile (unknown fact ⇒ `needs-profile-info`, never a silent
pass), additive federal+state layering with `satisfiesFederal` suppression,
verified-only rules load in prod, feature flag per rule-set file. NOT frozen —
condition vocabulary finalizes against the completed dossier.

## Decisions made

- 2026-07-07 Scope as above (Phase 0 interview).
- 2026-07-07 Municipal/county obligations NOTED in dossier, not encoded in v1;
  product should surface "check your city/county" rather than imply
  completeness.
- 2026-07-07 "Absence is a finding": unlicensed activities (if verified) get a
  sourced no-obligation entry so the engine distinguishes "no obligation" from
  "not researched".

## GOVERNING DECISION — Opus runs the whole pipeline (2026-07-07)

Founder decision (verbatim intent): *"We don't have Fable anymore, so everything
that was aimed at Fable should now be executed by Opus, with the highest amount
of effort possible… maybe duplicate the review part. We need to make it work
with Opus, there is no other option."*

**The [FABLE]/[OPUS] tier split is SUPERSEDED.** Opus now performs rule
derivation, confidence judgments, schema design, and all review passes. The tags
below remain only as a record of what was *originally* deemed frontier-only.

### Compensating controls (replace the lost "frontier-model-judges" safeguard)

The tier split existed because rule derivation on a weaker model can produce
plausible-but-wrong rules no test catches. With Opus doing derivation, that
single safeguard is replaced by **process- and redundancy-level controls that do
not depend on model tier**:

1. **Quote-the-text discipline.** A rule is `verified` ONLY if the researcher
   fetched the primary source AND quotes the operative statutory/regulatory
   sentence verbatim in a new **Operative text** field (METHODOLOGY.md updated).
   No quote ⇒ not verified. Careful reading of eCFR/Tex. statutes is
   model-tier-robust in a way that unsupported judgment is not.
2. **Full independent re-derivation (up from ≥30% to 100% of verified rules).**
   A second agent that never saw the first dossier re-derives every verified
   rule from primary sources; results are diffed; each discrepancy is an error
   until resolved against the quoted source. This is the founder's "duplicate
   the review", maximized.
3. **Confidence conservatism.** Bias toward `probable`/`uncertain` on any
   ambiguity or unfetchable source. Only quoted-`verified` rules ship.
4. Legal/compliance persona pass + adversarial pass + standard code-review pass,
   all unchanged, plus the **founder human sign-off gate** on rule content.
5. **Durable incremental writes.** Research agents write their dossier file
   early with a `**Status:** PARTIAL|COMPLETE` header and update as they go, so
   an Opus-limit interruption leaves clearly-labelled durable progress (last
   time, write-at-end lost everything).

Resulting trade: we lose "trust the strong model's judgment" and gain
"quote the source + re-derive everything independently" — at higher token cost,
which the founder authorized.

## Next tasks (updated 2026-07-07 — everything on Opus; tags now historical)

Pipeline state: dossier COMPLETE ✅ · Pass 1 legal COMPLETE ✅ · Pass 2
re-derivation COMPLETE ✅ (money + cadence; 19/19 facts matched, zero value
errors) · HB 2844 caterer refresh COMPLETE ✅ (L-1 fixed) · 5 pass-2 corrections
APPLIED ✅ (D-1 inflatable §2151.1012; D-3 §218.16 wording + header; D-2 §387.33T;
D-4 UCR §31101; D-5 §391.45(b)) · **RULES-REVIEW.md COMPLETE ✅**.

**→ AT FOUNDER CHECKPOINT (2026-07-07). Presented [RULES-REVIEW.md](../RULES-REVIEW.md).
Awaiting per-jurisdiction sign-off. This BLOCKS Phase 2 (engine). STOP for human.**

Provenance status: load-bearing numbers/terms upgraded to `official` (re-derived
via Playwright); single-reproduction existence facts held
`verified/reproduction-validated` (hosts validated 19/19) with G2 founder
browser-confirm as backstop; DPS-dependent + TAC-hours + a few absences stay
`probable`. All captured in RULES-REVIEW.md §4 + REVIEW-LOG.

--- after founder approval (Phase 2; likely fresh budget/session) ---

--- after founder approval (Phase 2; likely a fresh budget/session) ---
6. FREEZE SCHEMA.md condition vocabulary against the approved dossier (honor the
   3 legal-mandated engine requirements now in SCHEMA.md).
7. C# evaluation engine (pure, no LLM) against FROZEN rule data + schema.
8. Tests: unit per rule (boundary dates, leap years, grace edges), property-based
   date logic, golden-file entity fixtures.
9. Migrations for entity-profile fields + feature-flag wiring (flag OFF until
   counsel clears user-facing framing).
10. Pass 3 adversarial (contradictory rules, multi-match applicability, DST/TZ
    deadlines, rule-version boundary, malformed extraction) + Pass 4 standard
    code review. Fix all findings.
11. RULES-REVIEW.md final sign-off; per-jurisdiction feature-flag enablement.
12. [FABLE] RULES-REVIEW.md for founder sign-off (rule table + discrepancy log
    + coverage summary).

## FABLE-QUEUE

(No rule questions raised — Opus wrote no rules and no engine code this turn.
Opus: append rule questions here in future, never resolve them yourself.)
