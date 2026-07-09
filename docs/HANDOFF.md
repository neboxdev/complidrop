# HANDOFF — Compliance rule engine session (started 2026-07-07)

## FOUNDER DELEGATION — gate closure + merge (2026-07-09, COMPLETE)

The founder explicitly delegated the remaining human steps ("I need YOU to take
charge on all of those, I trust you"). Executed this session:

- **G2 + G3 CLOSED (delegated):** all gate figures re-verified live in a real
  browser on the OFFICIAL hosts with screenshot evidence
  ([audit/evidence/g2/](rule-engine/audit/evidence/g2/README.md)) — the fourth
  independent pass. The TX security `reviewGate` is lifted; production posture
  is now the full verified set: **37 of 40 rules** (3 probable withheld).
- **Sign-off recorded** in RULES-REVIEW §8 (checkboxes ticked as delegated,
  founder's words quoted).
- **G1 REMAINS OPEN** — only a licensed attorney can close it. Prepared
  [G1-COUNSEL-BRIEF.md](rule-engine/G1-COUNSEL-BRIEF.md): a self-contained
  counsel package (feature description, exact output wording, five questions,
  draft Terms addition) + a draft engagement email for Ruben to forward.
  **Ruben's ONLY remaining action: send that email to a lawyer.** Flags stay
  OFF until counsel clears the framing; no endpoint/UI exists.
- **Merged (delegated, careful-review):** PR merged with a MERGE commit (audit
  docs reference branch SHAs — never squash this history). The deploy ships the
  engine inert (`RuleEngine:Enabled=false`) and also brings prod current with
  main (it had been behind by #340/#327/#323/#350).
- After G1: flip `RuleEngine:Enabled` + `EnabledRuleSets` per rule set, then
  build the evaluation endpoint/UI (normal /plan → /start work).

## PASS 5 — Fable re-review + remaining work (2026-07-08, COMPLETE)

Fable access returned; the founder ordered a thorough re-review of the
Opus-built pipeline plus completion of the remaining work. All done this
session (full record: [REVIEW-LOG § Pass 5](rule-engine/REVIEW-LOG.md)):

- **Re-review:** headline claims verified mechanically; **12/12 highest-stakes
  figures re-verified LIVE** (third derivation, mostly official hosts — incl.
  §1702.124(c)/§1702.301 on statutes.capitol.texas.gov, the G2 priority);
  16-finder review workflow → 75 findings → 3-lens adversarial verification
  (session limit killed ~125 verifiers mid-pass; the orchestrator personally
  ruled on every split/unverified finding per the review contract).
  **43 real findings + 29 confirmed = every one FIXED; 1 refutation upheld.**
- **Standout rule-content fixes:** UCR capacity gate removed (CC-6 reversed —
  fee definition ≠ registration trigger); `operatesIntrastate` fact (mixed
  carriers owe BOTH layers, §643.002 "exclusively"); security commission fires
  for close-protection too; `providesUnarmedGuards` gates the noncommissioned
  license; caterer TABC → neutral MB-or-W&MB permit; inspection fixedDate →
  Jun 30 ("before July 1"); OSHA/TTB obligationRef swap; EIN full trigger;
  NEW RULE `tx-venue-wc-coverage-notice` (§406.005 reaches every employer);
  CFM/food-handler scoped to venue-org; sales-tax → `us-tx/cross-cutting.json`
  (venue+event-rental+caterer); real effective dates (TABC 2021-09-01,
  franchise 2024-01-01); Part-107 recency `roundToMonthEnd` ("calendar months").
- **A-2/CC-4 insurance-amount hole CLOSED (v1.2)** for general-liability floors:
  reshaped `insuranceMinimums` (kind/coverageLine/nullable components — no
  fabricated figures), new `below-stated-minimum` status, amount gate in the
  evaluator. Auto-liability floors deliberately NOT compared against the
  extracted GL figure (wrong policy line; needs an extraction field — follow-up
  with #397).
- **Engine hardening:** omitted `confidence` can no longer default to Verified;
  `UnmappedMemberHandling.Disallow` (typo'd keys fail fast); validFrom/citation/
  minimums/subtype required; empty-`any` and impossible fixedDates rejected;
  grace unified (and rejected on fixed-date anchors until honored); document
  selection by effective deadline; conditional-filing proof reads Satisfied;
  fixed-annual undated proof never guesses Satisfied; federal-only-load state
  coverage; Neq fails closed; builder aliasing fixed.
- **Remaining work items DONE:** entity-profile persistence
  (`AddRegulatoryEntityProfileFields` — additive: Organization.State +
  RegulatoryFactsJson, Vendor.EntityType + RegulatoryFactsJson),
  `RegulatoryProfileMapper` (EF→engine adapters; Document.EffectiveDate →
  IssueDate; GeneralLiabilityLimit → the amount gate), and the per-rule-set
  feature flags (`RuleEngine:Enabled=false` default + `EnabledRuleSets`;
  `RegulatoryRuleCatalog` resolves fail-fast at boot; safe posture hard-coded,
  NOT configurable). No endpoint/UI — deliberately, pending G1.
- **Counts now:** 40 rules (37 verified + 3 probable; 5 review-gated) → **32 in
  the production posture**; 11 rule-data files; **225 rule-engine tests; full
  backend 1323/1323 green**.
- **Still open (founder):** **G1** (counsel on framing — blocks any customer
  exposure), **G2** (browser spot-confirm of TX figures — now the FOURTH pass
  over those numbers; blocks lifting the TX security reviewGate), G3 eyeball
  (§387.33T — orchestrator re-read it live this session), G4 (DPS facts stay
  probable). Merge decision is the founder's (`careful-review`: migration +
  Program.cs touched).



> **AUDIT TRAIL:** [docs/rule-engine/audit/](rule-engine/audit/README.md) — index,
> process log, provenance map (rule → primary source), verification guide
> (guarantee → test), and the limitations/gates register. Written 2026-07-08 for
> external audit.

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

**→ FOUNDER SAID "GO" (2026-07-07).** Interpreted as: commit deliverable + proceed
into Phase 2. Deliverable committed to branch **`feat/compliance-rule-engine`**
(6931d35, 17 files, no push/PR). **G1 (counsel on user-facing framing) + G2
(founder browser spot-confirm of TX statutory figures) REMAIN OPEN** — they gate
CUSTOMER EXPOSURE, not building; engine ships feature-flag OFF regardless.

### Phase 2 progress (2026-07-07)
- ✅ **SCHEMA.md FROZEN** — fact registry locked (§4); design questions resolved
  (RD-a filings as cadence-tracked; RD-b worker creds vendor-level v1; RD-c
  documentType → existing extraction vocab); build plan recorded.
- ✅ **Engine core** (commit 087c6e1) — orchestrator-verified: build clean, 110 tests.
  3 legal gates enforced structurally (no `IsCompliant` boolean exists).
- ✅ **Rule data encoded** (087c6e1) — 39 rules / 10 files; high-stakes figures
  spot-checked directly against the dossier by the orchestrator.
- ✅ **Pass 3 (adversarial) + Pass 4 (correctness / compliance-claims / test-quality)**
  — core logic independently certified correct; all findings fixed in **c53a975**;
  110 → **154 tests**, orchestrator-verified. Prod posture = **31 rules**
  (39 − 3 probable − 5 review-gated TX security).
- ✅ **Audit documentation** (2026-07-08): [docs/rule-engine/audit/](rule-engine/audit/README.md)
  — index, process log, provenance map, verification guide, limitations & gates.
- ⬜ **Remaining:** entity-profile persistence migration + per-rule-set feature flag
  (DB-schema, `careful-review`, flags default OFF); close the insurance-amount hole
  (top follow-up); founder gates **G1** (counsel on user-facing framing) and **G2**
  (browser spot-confirm of TX statutory figures) before ANY customer exposure.

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
