# 02 — PROVENANCE MAP: every encoded rule → its primary source

This is the audit table. Data extracted directly from
`api/CompliDrop.Api/RuleData/**/*.json` (not transcribed from memory).

**How to use it.** Pick any row. The `obligationRef` is the join key into the
research dossier, where the **verbatim statutory quote** (`Operative text:`) lives.
The `citation` is what to open at the regulator. See §5 for the exact commands.

---

## 1. The counting, reconciled

| | Count |
|---|---|
| Rules encoded | **40** (Pass 5 added `tx-venue-wc-coverage-notice`, SPLIT-3) |
| `confidence: verified` | 37 |
| `confidence: probable` (never ships) | 3 |
| Review-gated | 0 (the TX security gate was lifted 2026-07-09 when G2 closed — evidence/g2/) |
| **Load in the production posture** | **37** |

Pinned by test `The_full_and_production_sets_have_the_expected_rule_counts`
(`api/CompliDrop.Api.Tests/RuleEngine/RealRuleDataLoadTests.cs`).

`Ships?` below means: loads under `RuleLoadOptions(VerifiedOnly: true,
IncludeReviewGated: false)` — the defaults.

---

## 2. Federal rules — `api/CompliDrop.Api/RuleData/us-fed/`

Dossier: `docs/rules-research/federal/<entity>.md`

| Rule id | obligationRef | Category | Conf. | Ships? | Citation |
|---|---|---|---|---|---|
| `fed-caterer-ttb-alcohol-dealer` | OBL-FED-CATERER-002 | filing | verified | ✅ | 27 CFR 31.31, 31.42, 31.111 |
| `fed-event-rental-forklift-operator-certification` | OBL-FED-EVENT-001 | worker-certification | verified | ✅ | 29 CFR 1910.178(l) |
| `fed-photographer-faa-part107-certificate` | OBL-FED-PHOTOGRAPHER-001 | worker-certification | verified | ✅ | 14 CFR 107.12; 107.61 |
| `fed-photographer-faa-part107-recency` | OBL-FED-PHOTOGRAPHER-002 | worker-certification | verified | ✅ | 14 CFR 107.65 |
| `fed-photographer-drone-registration` | OBL-FED-PHOTOGRAPHER-003 | permit | verified | ✅ | 14 CFR 48.15, 48.100, 48.30 |
| `fed-transportation-fmcsa-operating-authority` | OBL-FED-TRANSPORTATION-001 | license | verified | ✅ | 49 CFR 392.9a; 49 U.S.C. 13901-13902; 49 CFR part 365 |
| `fed-transportation-usdot-mcs150-update` | OBL-FED-TRANSPORTATION-002 | filing | verified | ✅ | 49 CFR 390.19T(b) |
| `fed-transportation-financial-responsibility-16plus` | OBL-FED-TRANSPORTATION-003 | insurance | verified | ✅ | 49 CFR 387.33T Schedule of Limits row (a); 387.31 |
| `fed-transportation-financial-responsibility-15orless` | OBL-FED-TRANSPORTATION-003 | insurance | verified | ✅ | 49 CFR 387.33T Schedule of Limits row (b); 387.31 |
| `fed-transportation-cdl-passenger-endorsement` | OBL-FED-TRANSPORTATION-004 | worker-certification | verified | ✅ | 49 CFR 383.23, 383.5, 383.93 |
| `fed-transportation-medical-examiner-certificate` | OBL-FED-TRANSPORTATION-005 | worker-certification | verified | ✅ | 49 CFR 391.41(a), 391.45(b) |
| `fed-transportation-clearinghouse-annual-query` | OBL-FED-TRANSPORTATION-006 | filing | verified | ✅ | 49 CFR 382.701(a),(b) |
| `fed-transportation-ucr-registration` | OBL-FED-TRANSPORTATION-007 | filing | verified | ✅ | 49 U.S.C. 14504a (fee-bracket CMV definition via 49 U.S.C. 31101(1)(B); no capacity gate — CONF-2 reversed CC-6) |
| `fed-venue-employer-identification-number` | OBL-FED-VENUE-001 | filing | verified | ✅ | IRC 6109; IRS EIN guidance |
| `fed-venue-osha-injury-log` | OBL-FED-VENUE-004 | filing | verified | ✅ | 29 CFR 1904.1, 1904.2, 1904.32 |
| `fed-venue-ttb-alcohol-dealer` | OBL-FED-VENUE-003 | filing | **probable** | ❌ held | 27 CFR 31.111 |

> The OSHA/TTB `obligationRef`s above were SWAPPED relative to the dossier until the Pass-5
> review (CONF-4): the dossier's OBL-FED-VENUE-003 is the TTB registration and -004 is the
> OSHA log. Fixed in the rule data and here — the trace now lands on the right entry.

> There is **no** `us-fed/security-service.json`. Every federal finding for guard
> companies is a sourced **absence** (no federal license/cert/insurance exists) and
> absences are not emitted as obligations. See `docs/rules-research/federal/security-service.md`.

---

## 3. Texas rules — `api/CompliDrop.Api/RuleData/us-tx/`

Dossier: `docs/rules-research/texas/<entity>.md`

| Rule id | obligationRef | Category | Conf. | Ships? | Citation |
|---|---|---|---|---|---|
| `tx-caterer-food-establishment-permit` | OBL-TX-CATERER-001 | permit | verified | ✅ | Tex. Health & Safety Code 437.0055; 25 TAC 228.2(14) |
| `tx-caterer-certified-food-manager` | OBL-TX-CATERER-002 | worker-certification | verified | ✅ | Tex. H&S Code 438.101-438.103; 25 TAC 228.31 |
| `tx-caterer-food-handler-training` | OBL-TX-CATERER-003 | worker-certification | verified | ✅ | 25 TAC 228.31(d),(e); 25 TAC 229.178(d)(1); Tex. H&S Code 438.046 |
| `tx-caterer-tabc-mixed-beverage` | OBL-TX-CATERER-004 | permit | verified | ✅ | Tex. Alco. Bev. Code 11.01, 28.01, 28.19, 25.01, 11.09, 11.11 (re-encoded Pass 5 as the neutral MB-or-W&MB retail permit, CONF-8) |
| `tx-caterer-mobile-food-vendor-license` | OBL-TX-CATERER-007 | license | verified | ✅ | Tex. H&S Code ch. 437B, 437B.051, 437B.055 |
| `tx-event-rental-amusement-ride-insurance` | OBL-TX-EVENT-001 | insurance | verified | ✅ | **Tex. Occ. Code 2151.1012** (general Class B: 2151.101(a)(3)) |
| `tx-event-rental-amusement-ride-inspection` | OBL-TX-EVENT-002 | permit | verified | ✅ | Tex. Occ. Code 2151.101; 28 TAC 5.9004 |
| `tx-event-rental-amusement-ride-injury-report` | OBL-TX-EVENT-003 | filing | verified | ✅ | Tex. Occ. Code 2151.103 |
| `tx-security-company-license` | OBL-TX-SECURITY-001 | license | verified | ✅ | Tex. Occ. Code 1702.102, 1702.108, 1702.301 |
| `tx-security-general-liability-insurance` | OBL-TX-SECURITY-003 | insurance | verified | ✅ | Tex. Occ. Code 1702.124(a), (c) — re-read on the OFFICIAL host 2026-07-08 (Pass 5) |
| `tx-security-noncommissioned-officer-license` | OBL-TX-SECURITY-004 | worker-certification | verified | ✅ | Tex. Occ. Code 1702.221, 1702.301 |
| `tx-security-officer-commission` | OBL-TX-SECURITY-005 | worker-certification | verified | ✅ | Tex. Occ. Code 1702.161, 1702.301 |
| `tx-security-personal-protection-officer-license` | OBL-TX-SECURITY-006 | worker-certification | verified | ✅ | Tex. Occ. Code 1702.201, 1702.202, 1702.301 |
| `tx-transportation-txdmv-motor-carrier-registration` | OBL-TX-TRANSPORTATION-001 | license | verified | ✅ | Tex. Transp. Code 643.051, 643.002, 548.001; 43 TAC ch. 218 |
| `tx-transportation-intrastate-insurance-27plus` | OBL-TX-TRANSPORTATION-002 | insurance | verified | ✅ | 43 TAC 218.16(a); Tex. Transp. Code 643.101 |
| `tx-transportation-intrastate-insurance-16to26` | OBL-TX-TRANSPORTATION-002 | insurance | verified | ✅ | 43 TAC 218.16(a); Tex. Transp. Code 643.101 |
| `tx-transportation-cdl-passenger-endorsement` | OBL-TX-TRANSPORTATION-003 | worker-certification | verified | ✅ | Tex. Transp. Code 522.011, 522.003, 522.051 (49 CFR 383.5 floor) |
| `tx-transportation-intrastate-medical-certificate` | OBL-TX-TRANSPORTATION-004 | worker-certification | **probable** | ❌ held | Tex. Transp. Code ch. 644; 37 TAC ch. 4; 49 CFR 391.45(b) |
| `tx-venue-dwc005-nonsubscriber-notice` | OBL-TX-VENUE-002 | filing | verified | ✅ | Tex. Labor Code 406.004; 28 TAC 110.101; DWC Form-005 |
| `tx-venue-wc-coverage-notice` | OBL-TX-VENUE-003 | filing | verified | ✅ | Tex. Labor Code 406.005 (added Pass 5, SPLIT-3 — reaches EVERY employer with employees) |
| `tx-venue-sales-use-tax-permit` | OBL-TX-VENUE-004 | permit | verified | ✅ | Tex. Tax Code 151.201-151.203 (lives in `us-tx/cross-cutting.json`; scoped to venue-org + event-rental + caterer, CONF-17) |
| `tx-venue-franchise-tax-report` | OBL-TX-VENUE-005 | filing | verified | ✅ | Tex. Tax Code ch. 171 |
| `tx-venue-tabc-retail-permit` | OBL-TX-VENUE-006 | permit | verified | ✅ | Tex. Alco. Bev. Code chs. 25, 28; 11.09 |
| `tx-venue-food-establishment-permit` | OBL-TX-VENUE-008 | permit | **probable** | ❌ held | Tex. H&S Code ch. 437; 25 TAC ch. 228 |

> The 5 TX security rules shipped behind `reviewGate: "founder-confirm-tx-security"` until
> 2026-07-09, when gate G2 closed under explicit founder delegation (live official-host
> verification with screenshot evidence — `evidence/g2/`). The gate marker is removed; the
> gating mechanism remains test-covered on synthetic fixtures (`RuleSetLoaderGuardTests`).

---

## 4. Provenance tiers — what "verified" actually rests on

| Tier | Meaning | Where it applies |
|---|---|---|
| `official` | Operative text read on a `.gov` / GPO primary host | All **federal** rules EXCEPT the UCR statutory chain (see `secondary`, below): govinfo GPO XML, eCFR API, agency `.gov`. Plus the Texas facts independently re-derived via the Playwright browser on `statutes.capitol.texas.gov`, `comptroller.texas.gov`, `txdmv.gov`, `tdi.texas.gov`, `dshs.texas.gov`. |
| `official-agency` | An official `.gov` AGENCY page/document citing or restating the rule (used where the raw TAC is unreachable) | e.g. the food-handler cadence (official DSHS TFER PDF / FAQ) — the tier RULES-REVIEW's legend calls `official-agency` |
| `reproduction-validated` | Verbatim statutory text taken from a faithful full-text reproduction (`texas.public.law`, Cornell LII, FindLaw) — **because the official Texas statute site serves only a JS shell to automated fetchers** | Texas statutory *existence-of-requirement* facts not covered by the 19 re-derived items |
| `secondary` | Single reproduction / interpretive source | The UCR statutory chain (49 U.S.C. 14504a → 31101) — govinfo's US-Code granule failed; a federal rule that SHIPS despite reproduction provenance because its threshold value was Pass-2 confirmed and its citation is a statute pointer, not a figure |

**Why `reproduction-validated` is not hand-waving:** the 19 load-bearing facts that
*were* independently re-derived (18 components on official hosts; the UCR chain via
Cornell — REVIEW-LOG D-4) **all matched** the reproduction-sourced dossier (19/19,
zero value errors). The Pass-5 Fable review added a THIRD independent pass: 12/12
highest-stakes facts re-read live, mostly on official hosts, zero value errors —
including §1702.124(c) and §1702.301 on `statutes.capitol.texas.gov` itself. That is
direct evidence the reproductions are faithful for this corpus. It is **not** a
substitute for gate G2.

### The 19 facts independently re-derived from official sources (Pass 2)

Money (8): security GL `$100k/$50k/$200k`; amusement Class A/B **and** the
inflatable-specific `$1M` CSL; federal passenger `$5M`/`$1.5M`; Texas intrastate
`$500k`/`$5M`; franchise no-tax-due `$2.47M`/`$2.65M`; sales-tax permit (no fee, no
expiry); FAA drone registration `$5`/3-year; TABC conduct surety bond `$5k`/`$10k`.

Cadence & thresholds (11): TX security term ≤2 yr; TABC permit term 2 yr; franchise
report due **May 15**; DWC-005 window **Feb 1–Apr 30** + 30 days of first hire;
food-handler **30 days** + card 2 yr + CFM required; FAA Part 107 recency **24
calendar months** (certificate itself permanent); FAA registration 3 yr; CDL
threshold "16 or more passengers **including the driver**" + DOT medical **24
months** (§391.45(b)); MCS-150 every 24 months (§390.19T); the four distinct
capacity thresholds; Texas CDL 8-year term.

Full diff table with quotes: `docs/rule-engine/REVIEW-LOG.md` § "Pass 2".

---

## 5. Trace commands (copy-paste)

```bash
# 1. Rule → its encoded values, citation, applicability, cadence
grep -rn -A 40 '"id": "tx-security-general-liability-insurance"' api/CompliDrop.Api/RuleData/

# 2. Rule → the research entry holding the VERBATIM statutory quote
grep -rn "OBL-TX-SECURITY-003" docs/rules-research/

# 3. Every verbatim quote in the dossier (this is the evidence base)
grep -rn "Operative text:" docs/rules-research/ | wc -l

# 4. Every rule that does NOT ship, and why
grep -rn '"confidence": "probable"' api/CompliDrop.Api/RuleData/
grep -rn '"reviewGate"'            api/CompliDrop.Api/RuleData/

# 5. Every insurance floor the engine carries
grep -rn -B 12 '"insuranceMinimums"' api/CompliDrop.Api/RuleData/ | grep -E '"id"|perOccurrence|aggregate'

# 6. Reconstruct the whole table from source (PowerShell)
#    (strips the // header comments, then projects id/ref/confidence/citation)
```

---

## 6. Obligations documented but deliberately NOT encoded

These appear in the dossier and are **intentionally absent** from the rule data.
An auditor should confirm each absence is *sourced*, not an oversight.

| Item | Why not encoded | Where documented |
|---|---|---|
| **Absence findings** (no federal guard-company license; no TX photographer license; no TX general business license; no state tent permit) | "No obligation exists" is a finding, not an obligation to emit | each dossier's absence entries |
| **TABC seller-server training** | Statute makes it a **voluntary employer safe-harbor**, not a mandate — encoding it as required would be a false legal claim | `texas/caterer.md` OBL-TX-CATERER-005 |
| **TCEQ photo-finishing permit** | `probable` **and** un-gateable (no "develops film chemically" fact). Surfacing it to a digital event photographer would be a false positive | `texas/photographer-videographer.md`; no `us-tx/photographer-videographer.json` exists |
| **Security Level II/III/IV training** | Evidenced by holding the license/commission/PPO; emitting separately would double-count | `us-tx/security-service.json` header comment |
| **Criminal-penalty sections** (§1702.388, §2151.153) | Stated as statutory-general context inside `rationale`; never emitted as a trackable obligation or as a user adjudication | rule `rationale` fields |
| **Municipal / county obligations** (city health permits, fire marshal, certificate of occupancy, tent/IFC permits, film permits) | Out of v1 encoding scope by design — but surfaced to users via `localObligations` → the report's completeness notice | each dossier's "Local-level obligations (noted, not encoded)"; test `Local_obligation_pointers_are_populated_for_each_texas_entity` |

---

## 7. Known encoding compromises (each is a deliberate, recorded trade-off)

| Rule | Compromise | Recorded in |
|---|---|---|
| `tx-security-general-liability-insurance` | ~~Single `perOccurrence` field; the $50k sub-limit lived in prose~~ **RESOLVED Pass 5:** the v1.2 split-limits shape carries all three statutory limits machine-readably | rule `notes`; `SCHEMA.md` v1.2 |
| `tx-event-rental-amusement-ride-inspection` + `-injury-report` | Gated on `rentsInflatableAmusementDevices` although §2151.101/.103 reach amusement-ride operators GENERALLY — a non-inflatable ride renter (mechanical bull, trackless train) is out of v1 scope, because encoding its inspection duty without the matching §2151.101(a)(3) insurance floor would half-cover it (UNVER-29) | rule `notes`; RULES-REVIEW TX-002 row |
| `tx-event-rental-amusement-ride-injury-report` | Event-conditional (owed only after a reportable injury). No "injury occurred" fact exists, so it is `conditional-filing` and surfaces as `NotApplicable`, never `Missing` (proof on record reads `Satisfied` since v1.2, UNVER-5) | rule `notes`; test `…conditional…` in `RuleDataGoldenTests` |
| `tx-venue-tabc-retail-permit` / `tx-caterer-tabc-mixed-beverage` | The entity holds **either** an MB or a W&MB (BG) permit; no fact distinguishes spirits from beer/wine-only service, so one neutral obligation represents both alternatives (the caterer rule was MB-specific until Pass 5, CONF-8) | rule `notes` |
| `fed-transportation-ucr-registration` | UCR also attaches via a ≥10,001 lb GVWR threshold (an untracked fact). ~~Only the >10-passenger threshold is gated~~ **CORRECTED Pass 5 (CONF-2):** the passenger figure is the FEE CMV definition, not the registration trigger — no capacity gate at all now | rule `notes`; REVIEW-LOG Pass 5 |
| `tx-caterer-food-establishment-permit` | Fires for every food-preparing caterer although §437.0055's statutory trigger is the no-local-authority residual case — deliberate broadening: SOME food permit (local-first, DSHS residual) is required either way (UNVER-37) | rule `notes`; RULES-REVIEW TX-001 row |
| Cross-cutting venue obligations (franchise, EIN, OSHA, DWC-005, WC notice) | Scoped to `entityTypes: [venue-org]` — the customer's own obligations — matching the dossier's "canonical owner" convention. The sales-tax permit moved to `us-tx/cross-cutting.json` (venue-org + event-rental + caterer, CONF-17) | `us-tx/venue-org.json`, `us-fed/venue-org.json`, `us-tx/cross-cutting.json` |
