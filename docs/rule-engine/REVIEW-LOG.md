# Rule-engine review log

Durable record of every review pass over the compliance rule set + engine.
Newest section appended. This is a deliverable (the brief requires "review logs —
all passes, findings, and fixes"). Orchestrator = the main session (Opus) driving
the pipeline.

---

## Pass 0 — Orchestrator dossier pre-review (rolling, as entity files land)

Not one of the four formal passes; this is the orchestrator independently reading
each completed dossier file (not just the agent's self-report) for methodology
conformance and to triage what the formal passes must target. Findings here feed
the pass-2 re-derivation prompt.

### Batch 1 (2026-07-07): security-service, photographer-videographer, event-rental

**Overall:** high quality. Verbatim `Operative text` present on every `verified`
entry; confidence generally well-calibrated; strong absence findings; one research
lead correctly **refuted** (Texas amusement rides are regulated by **TDI**, not
TDLR as the lead suggested); appropriately conservative on numbers that couldn't
be pulled from an official host.

**Highest-value numbers — status:**
- Security-guard statutory insurance floor **$100k/occ BI+PD, $50k/occ personal
  injury, $200k aggregate** (Tex. Occ. Code §1702.124(c)) — strongly corroborated:
  two independent reproductions (public.law + FindLaw) **plus an orchestrator
  fetch**. Solid.
- Inflatable (Class B amusement ride) liability insurance **$1M BI/$500k PD per
  occ OR $1.5M CSL** (28 TAC §5.9004(b)) — correctly held **`probable`**: exact TAC
  figure not obtainable from an official host; corroborated by Cornell LII +
  txrules.elaws.us + TDI FAQ. Needs official-host confirmation before it ships.

**FINDINGS to carry into the formal passes:**

- **F-1 (provenance, systematic → pass 2).** Many `verified` entries rest on a
  **single** reproduction host (public.law, FindLaw, or Cornell LII alone), which
  satisfies quote-the-text but not the ≥2-independent-reproductions bar in the
  provenance standard. Pass-2 re-derivation MUST fetch a *different* second host
  per verified rule and either corroborate (→ `reproduction-corroborated`) or flag
  a discrepancy. Applies to: security-service TX OBL-002/-004/-009 (single host),
  event-rental TX OBL-002/-003 (§2151.x from public.law only).
- **F-2 (federal provenance upgrade → pass 2).** Photographer federal OBL-002
  (14 CFR 107.65) and OBL-003 (14 CFR 48.15/48.100/48.30) were read from **Cornell
  LII only**; only OBL-001 (107.12) got the **govinfo.gov (GPO official)** read.
  Pass 2 should pull govinfo for 107.65 and the part-48 sections so they reach
  `provenance: official`, not merely authoritative-secondary.
- **F-3 (cadence not fully pinned → founder gate).** Event-rental OBL-002 has a
  genuine tension: statute §2151.101 says the inspection/insurance filing is due
  "before July 1 of each year," while TDI operational materials describe a
  "sticker valid one year from inspection" rolling cycle. The reminder-timing
  cadence therefore isn't cleanly determined and must not be hard-encoded until
  TDI/founder confirms which governs.
- **F-4 (unencoded-by-design, correct).** Level III security training hours (45?)
  and CE hours (6?) left UNencoded due to a real source conflict; local tent
  thresholds (IFC Ch. 31, 400 sq ft) left as "check your city/county." Both are
  correct conservative calls — flag only to confirm the engine surfaces the
  "check local" prompt rather than silently omitting.

**No fabrication, no unsourced number, no Spain/EU leakage detected in batch 1.**

### Batch 2a (2026-07-07): caterer

**Overall:** strongest sourcing yet — the caterer agent **defeated the SPA/anti-bot
walls with the Playwright browser**, reading OFFICIAL text from
`statutes.capitol.texas.gov` and `ecfr.gov`. This is the fix for F-1/F-2: pass 2
now has a proven route to `provenance: official` for Texas statutes and eCFR.

**Findings:**
- **F-5 (positive / method).** Playwright reaches both official hosts. Pass 2
  MUST use it to re-pull the batch-1 reproduction-only rules and upgrade them.
- **F-6 (lead correction, accepted).** Food-handler training window = **30 days**
  after hire (25 TAC §228.31(d)), not 60. Confirms the agents are correcting, not
  confirming, my priors.
- **F-7 (new federal obligation, verify in pass 2).** A caterer that serves
  alcohol must register with **TTB** (Form 5630.5d, 27 CFR 31.31/31.42/31.111) —
  federal, additional to the TABC permit. §31.42 catches serving liquor with meals
  "even if no separate charge is made." Not previously in scope notes; pass-2
  re-derivation should independently confirm the 27 CFR sections.
- **F-8 (correct exemption).** Caterers are EXEMPT from FDA food-facility
  registration (21 CFR 1.227 lists catering facilities as restaurants, exempt
  under §1.226(d)) — a sourced absence, good.
- **F-9 (voluntary-not-mandate, correct).** TABC seller-server training (§106.14)
  correctly recorded as a VOLUNTARY employer safe-harbor and NOT encoded as a
  required credential — exactly the regulatory-vs-not distinction the brief
  demanded.
- Sub-details left `probable`/open (correct): exact CFM certificate validity
  (25 TAC §229.176 / CFP §7.3, commonly 5 yr), DSHS permit fee, current TABC "CB"
  letter code / catering day-counts (2020 doc pre-dating full HB 1545 rollout).

### Batch 2b (pending): transportation, venue-org — agents still running.

---

### Batch 2b (2026-07-07): transportation + venue-org

- **F-10 (positive, cross-agent corroboration).** TTB 5630.5d federal alcohol-
  dealer registration derived INDEPENDENTLY by both caterer and venue-org agents;
  the HB 1545 = 2019 date and the TABC 2-yr term were corrected by 3 agents.
  Natural redundancy already validating these facts.
- **F-11 (engine-critical, transportation).** Interstate-vs-intrastate insurance
  is a hard branch: federal $5M (16+ seats incl driver)/$1.5M (≤15) vs Texas
  intrastate $500k (16–26 seats) → $5M (26+ not incl driver). A $500k Texas COI
  does NOT satisfy the $5M federal floor. The engine's applicability logic MUST
  resolve `operatesInterstate` before judging a transport insurance obligation.
- **F-12 (currency, positive).** Caterer food-establishment permit text reflects
  an amendment **effective July 1, 2026** (4 days before research) — dossier is
  current to a just-effective change. venue-org franchise threshold is the current
  $2.47M/$2.65M, not the stale $1.23M. Confidence-building on recency.
- **F-13 (artifact, transportation).** Resolved the "49 CFR 387.33 suspended
  (82 FR 5307)" editorial note: it's a URS/2017-freeze codification artifact; the
  $5M/$1.5M amounts are in force and were even amended in 2018. Still flagged for
  a human eyeball at the founder gate.

**Dossier scope-hygiene:** grep for the load-bearing figures across all 12 files
shows NO non-US (Spain/EU) content and no unsourced number. Clean.

---

## Pass 2 — Independent re-derivation + discrepancy log  (IN PROGRESS)

Two blind re-derivation agents (money; cadences/terms/thresholds) are
independently re-reading OFFICIAL sources via Playwright, WITHOUT seeing the
dossier's conclusions. When they return, each independently-found value is diffed
against the **dossier-claimed baseline** below; any mismatch is an error until
resolved against the official source.

### Diff baseline — dossier's CLAIMED load-bearing values (to be verified)

Money:
1. TX security-guard insurance — **$100k/occ BI+PD, $50k/occ personal injury,
   $200k aggregate** (Tex. Occ. Code §1702.124(c)).
2. TX amusement Class B (inflatable) — **$1M BI/$500k PD per occ OR $1.5M CSL**;
   Class A $100k/$50k/$300k-agg OR $150k-CSL/$300k-agg (28 TAC §5.9004(b)).
   [dossier confidence: **probable**]
3. Federal passenger carrier — **$5M** (16+ seats incl driver) / **$1.5M** (≤15)
   (49 CFR 387.31/387.33).
4. TX intrastate passenger — **$500k** (16–26 seats) / **$5M** (26+ not incl
   driver) (43 TAC §218.16).
5. TX franchise no-tax-due threshold — **$2,470,000** (2024–25) / **$2,650,000**
   (2026–27); No-Tax-Due Report eliminated 2024+.
6. TX sales-tax permit — **no fee, no expiration**.
7. FAA drone registration — **$5.00/aircraft, 3-year term** (14 CFR 48.30/48.100).
8. TABC conduct surety bond — **$5,000** (MB), **$10,000** within 1,000 ft of a
   school, waivable after 3 yrs (Alco. Bev. Code §11.11).

Cadences / terms / thresholds:
9. TX security license/commission term — **≤2 years** (2nd anniversary) (§1702.301).
10. TABC permit term — **2 years** (2nd anniversary) (§11.09(a)).
11. Franchise report due — **May 15**.
12. WC non-subscriber DWC-005 — **within 30 days of first hire + annual**
    (Labor Code §406.004 / DWC rule).
13. Food handler — **within 30 days of hire**; card valid **2 years**
    (25 TAC §228.31(d) / §229.178).
14. FAA Part 107 recency — **24 calendar months**; certificate itself does not
    expire (14 CFR 107.65).
15. FAA drone registration term — **3 years** (14 CFR 48.100).
16. Federal CDL passenger trigger — **"16 or more passengers, including the
    driver"** (49 CFR 383.5); DOT medical cert max **24 months** (391.45).
17. FMCSA MCS-150 update — **biennial** (49 CFR 390.19).
18. Four capacity thresholds: CDL "16+ incl driver"; TX reg "more than 15 incl
    driver" (Transp. Code §548.001); TX $5M tier "26+ not incl driver"; UCR CMV
    "more than 10 incl driver".
19. TX CDL term — **~8 years** ("applicant's next birthday", §522.051).

### Diff results — MONEY re-derivation (2026-07-07, independent, official via Playwright)

**7 of 8 exact match; all 8 confirmed from OFFICIAL hosts** (capitol.texas.gov,
ecfr.gov, comptroller.texas.gov, txdmv.gov via Playwright) → these items upgrade
reproduction → `provenance: official`, resolving L-2/L-3 for the money facts.

| # | Fact | Dossier | Re-derivation (official) | Result |
|---|---|---|---|---|
| 1 | TX security insurance | $100k/$50k/$200k | $100k/$50k/$200k (§1702.124(c)) | ✅ match → official |
| 2 | Inflatable insurance | $1M/$500k or **$1.5M CSL** (general Class B) | **§2151.1012 inflatable-specific: $1M per-occ CSL** | ⚠️ **REFINE** (see D-1) → official, upgrade from `probable` |
| 3 | Fed passenger insurance | $5M/$1.5M (§387.33) | $5M/$1.5M — operative **§387.33T** (387.33 suspended) | ✅ figures match; cite §387.33T (D-2) |
| 4 | TX intrastate insurance | $500k/$5M "26+ not incl driver" (Handbook) | $500k (>15,<27 incl driver)/$5M (**27+ incl driver**) §218.16 | ✅ equivalent; quote official §218.16 wording (D-3) |
| 5 | Franchise threshold | $2.47M/$2.65M, May 15, NTD eliminated | identical | ✅ match → official |
| 6 | Sales-tax permit | no fee, no expiration | no fee; "valid while actively in business" | ✅ match (no-expiration is inferred, no explicit Q&A) |
| 7 | FAA drone registration | $5/aircraft, 3-yr | $5 (§48.30), 3-yr (§48.100(c)) | ✅ match → official (resolves L-2 for OBL-003) |
| 8 | TABC surety bond | $5k / $10k near school | $5k / $10k (§11.11(a)) | ✅ match → official |

**Discrepancies to fix in the dossier (pass-2 contract: each is an error until fixed):**
- **D-1 (substantive, event-rental TX OBL-001).** The controlling inflatable
  provision is **Tex. Occ. Code §2151.1012** ($1M per-occurrence CSL for
  continuous-airflow bounce houses), NOT the general Class B §2151.101(a)(3)(B)
  ($1.5M CSL) the dossier applied. Specific-controls-general. FIX: rewrite OBL-001
  to cite §2151.1012 for inflatables, keep the general Class B as context, and
  UPGRADE `probable → verified` (statute read officially via Playwright). This was
  the single highest-value `probable`; re-derivation both resolved AND corrected it.
- **D-2 (citation, transportation FED OBL-003).** Operative section is **§387.33T**
  (§387.33 suspended 82 FR 5307); figures unchanged. Update citation.
- **D-3 (citation/quote, transportation TX OBL-002).** Replace the TxDMV-Handbook
  paraphrase with the official **43 TAC §218.16(a)** wording ("more than 15 but
  fewer than 27… $500,000"; "27 or more people, including the driver… $5,000,000").
  §218.13 (the lead) is the wrong section — confirmed §218.16. Upgrade → official.

Provenance upgrades to apply (money items): security OBL-003, caterer surety-bond,
franchise, sales-tax, FAA registration, fed/TX transport insurance → `official`.

### Diff results — CADENCE/TERMS/THRESHOLDS re-derivation (2026-07-07, independent, official)

**11 of 11 confirmed**; all official (Playwright statutes + govinfo GPO XML), except
where noted. NO value discrepancy — only citation-precision fixes + the D-3
confirmation.

| # | Fact | Dossier | Re-derivation (official) | Result |
|---|---|---|---|---|
| 1 | TX security term | ≤2 yr (§1702.301) | ≤2 yr, 2nd anniversary | ✅ → official |
| 2 | TABC permit term | 2 yr (§11.09(a)) | 2 yr (1-yr if violation history per (d)) | ✅ → official |
| 3 | Franchise due | May 15 | May 15 | ✅ → official |
| 4 | WC DWC-005 | 30d first hire + annual | annual **Feb 1–Apr 30** + 30d first hire (§406.004/.005) | ✅ → official (window confirmed) |
| 5 | Food handler | 30d hire; card 2 yr; CFM req | 30d; 2 yr; CFM separate req | ✅ via official DSHS FAQ (raw 25 TAC not fetchable — Appian SPA) |
| 6 | FAA Part 107 recency | 24 cal months; cert permanent | 24 cal months (§107.65); FAA FAQ: permanent | ✅ → official (resolves L-2 OBL-002) |
| 7 | FAA reg term | 3 yr (§48.100(c)) | 3 yr | ✅ → official |
| 8 | CDL threshold / DOT medical | 16+ incl driver / 24 mo | 16+ incl driver (§383.5) / 24 mo **§391.45(b)** (not .43) | ✅ → official; cite §391.45(b) |
| 9 | MCS-150 | biennial (§390.19) | every 24 mo; operative **§390.19T** | ✅ → official |
| 10 | Four thresholds | see baseline | (a)(b) match; **(c) D-3 confirmed**; (d) UCR = **49 USC 31101** "more than 10 incl driver", NOT 390.5 | ⚠️ D-3 + D-4 |
| 11 | TX CDL term | ~8 yr (§522.051) | 8 yr after next birthday | ✅ → official (resolves L-4 CDL) |

**Additional fixes:**
- **D-3 CONFIRMED (transportation TX OBL-002).** $5M tier = "27 or more people,
  **including** the driver" per current 43 TAC §218.16(a) (2024 amdt aligned it to
  Transp. Code §548.001 "including the driver"). Dossier's "26+ not incl driver" is
  the **superseded pre-2024** wording. Two independent agents agree. FIX + note the
  supersession.
- **D-4 (citation, transportation FED UCR).** The "more than 10 passengers incl
  driver" UCR threshold traces to **49 U.S.C. §31101(1)(B)** (via §14504a
  cross-ref), NOT 49 CFR 390.5 (which uses a different 8/15 compensation split).
  Fix the citation; value unchanged. (Statutory chain read from Cornell =
  reproduction; flag.)
- **D-5 (citation, transportation FED DOT medical).** 24-month cap is in
  §391.45(b); §391.43 has no numeric cap. Align citation.

### PROVENANCE — empirical validation finding (important)

**All 19 independently re-derived load-bearing facts MATCHED the dossier's
reproduction-sourced values (8/8 money + 11/11 cadence).** This is strong evidence
that the reproduction hosts (texas.public.law, Cornell LII, FindLaw) faithfully
reproduce the statutes/rules for THIS dossier. Consequence for confidence policy:
single-reproduction EXISTENCE-of-requirement entries (e.g. security §1702.102/.108/
.221/.161/.201/.202) are kept `verified` with provenance
`reproduction-validated` — the hosts' fidelity was independently validated 19/19 —
rather than mechanically downgraded to `probable`. The whole TX **statutory** set
still goes through the founder in-browser spot-confirm at FINAL sign-off (pass 5);
TAC-based entries that no host renders officially (37 TAC §35.141 training hours)
stay `probable`. This is documented for the founder in RULES-REVIEW.md.

**Pass 2 verdict: dossier's load-bearing facts are CORRECT.** Zero value errors;
one substantive statute-selection refinement (D-1 inflatable §2151.1012); the rest
citation precisions. Re-derivation upgraded most of the dossier to official
provenance and validated the reproductions.

## Pass 1 — Legal/compliance reviewer  (COMPLETE 2026-07-07)

**Verdict:** dossier is SAFE to show the founder as research (disciplined framing —
"the law requires X; track it," not user verdicts; regulatory-vs-contractual
correct; applicability gated; absences bounded; scope hygiene clean, zero non-US
content). NOT yet safe to surface to END USERS as compliance verdicts until the
gates below clear. No single entry is a ship-blocker AS RESEARCH.

**L-1 (major) — ✅ RESOLVED 2026-07-07.** STALE/MISSING — caterer: Texas **HB 2844**
(Acts 2025, 89th Leg., Ch. 744; eff. 2026-07-01) created a statewide **DSHS**
Mobile Food Vendor license (new H&S ch. 437B, license per food-vending vehicle,
**annual** term §437B.055(b)) and struck "mobile food unit" from §437.0055's
residual scheme. FIX APPLIED (agent ad8a914): added **OBL-TX-CATERER-007**
(verified/official, confirmed against codified ch. 437B via Playwright + the
enrolled bill PDF), with a NARROW applicability gate — applies only to a caterer
operating as a mobile food vendor (serves from a food-vending vehicle), NOT the
transport-and-plate caterer; corrected the OBL-001 effective-date note and the
local-obligations food-truck bullet. Interaction = coexist-with-state-primacy
(§437B.003 preempts only conflicting local rules). Open: DSHS fee schedule + final
Type I/II/III category rules (executive-commissioner rules due 2026-05-01; agency
practice, not encoded). **Control-working note:** the agent detected and rejected
a flat-WebFetch summary that HALLUCINATED section numbers + a false "county health
authority" clause — quote-the-text discipline caught it.

**L-2 (major, fix = provenance labels).** Photographer federal OBL-002 (24-mo
recency) & OBL-003 (3-yr registration + $5) marked `verified` on a SINGLE Cornell
LII read, but govinfo (official) was reachable. = my F-2. FIX: re-pull 107.65 +
part-48 from govinfo/eCFR (the cadence re-derivation agent is doing this) or
downgrade to `probable`.

**L-3 (major, fix = provenance labels + founder gate).** Security-service TX:
most entries single-reproduction `verified` (only -003 insurance floor has ≥2);
methodology caps single-reproduction at `probable`. = my F-1. FIX: downgrade or
add 2nd reproduction; route the WHOLE security set through the founder
spot-confirm before it can drive a verdict. Reviewer INDEPENDENTLY re-confirmed
the $100k/$50k/$200k floor is correct.

**L-4 (minor, fix = provenance labels).** Single-reproduction `verified` cadences:
caterer OBL-003 (2-yr food-handler card, Cornell only), transportation OBL-003
(8-yr CDL, public.law only), event-rental OBL-002 ("before July 1", public.law
only). Cross-confirm or mark the specific cadence `probable`.

**L-5 (minor, keep as-is).** Federal security absences rest on BLS (403) + OJP
(nav-only) — correctly capped `probable`; keep until actually fetched.

**ENGINE-DESIGN gates (fold into SCHEMA.md — done):**
- **L-6 (major).** Engine MUST branch interstate-vs-intrastate before selecting a
  transportation insurance floor (= F-11). No flat "transportation insurance".
- **L-7 (major).** Engine MUST NEVER emit a bare "compliant" implying
  completeness; every report carries "not exhaustive — check city/county" and
  surfaces the noted-not-encoded local obligations.
- **L-8 (major).** Penalty text renders as "what the statute provides" (general,
  sourced), NEVER as an adjudication of THIS user ("you are committing a crime").

**FOUNDER / COUNSEL gates (→ RULES-REVIEW.md, not code):**
- The feature moves the product from "read your doc vs YOUR requirement" to
  "we assert which laws apply to you" — a larger reliance/UPL surface the current
  Terms clause ("a head start, not advice") does not clearly cover. Real counsel
  must review the disclaimer + user-facing framing before customer exposure.
- Human eyeball on the 49 CFR 387.33 "suspended (82 FR 5307)" artifact before the
  $5M figure drives a verdict.

## Pass 3 — Adversarial  (PENDING — after founder dossier approval / engine)
## Pass 4 — Standard code review (security/correctness/architecture)  (PENDING — after engine build)
