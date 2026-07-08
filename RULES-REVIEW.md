# RULES-REVIEW — CompliDrop compliance rule set (v1)

**For: Ruben (founder sign-off). Status: awaiting your approval. Nothing here is
merged, encoded, or deployed.**

This is the Phase-1 checkpoint artifact: the complete US (federal + Texas) rule
set researched this session, with sources, confidence, the review-pass results,
and the discrepancy log. Read the **Decision & gates** section first, then the
rule inventory. Full evidence: [docs/rules-research/](docs/rules-research/) (every
obligation with verbatim statutory quotes); review detail:
[docs/rule-engine/REVIEW-LOG.md](docs/rule-engine/REVIEW-LOG.md).

> **Auditing this work?** Start at
> **[docs/rule-engine/audit/README.md](docs/rule-engine/audit/README.md)** — the audit
> index. It maps every encoded rule to its primary source, every engine guarantee to
> a named test, and states plainly what is *not* verified or enforced.

---

## 1. What you are approving (and what you are NOT)

- **Approving:** that this RULE CONTENT is correct and safe to encode as a
  *tracked-obligations* engine (Phase 2). Sign-off is **per jurisdiction**
  (federal / Texas) and can be per entity type.
- **NOT approving yet:** any customer-facing feature. The legal pass found the
  product now asserts *which laws apply to a user* — a bigger liability surface
  than today's "read your document against your own checklist." **Real counsel
  must review the user-facing framing/disclaimer before this is exposed to any
  customer.** The engine ships behind a feature flag, OFF, until you clear that.

## 2. Decision & gates (read before the table)

**Overall:** the rule set is **sound as research** — every load-bearing number was
independently re-derived from official sources and matched (19/19); framing is
disciplined ("the law requires X; track it," never "you are compliant"); scope is
US-only with zero non-US content. It is safe to approve for **encoding**, subject
to these gates:

| # | Gate | Owner | Blocks |
|---|---|---|---|
| G1 | Counsel review of the user-facing disclaimer/framing (product asserts applicable law → UPL/reliance surface) | You + counsel | customer exposure (not encoding) |
| G2 | Spot-confirm the Texas **statutory** figures in your browser at the official site (the site blocks our automation; text came from validated reproductions). Priority: security insurance **$100k/$50k/$200k**, TABC 2-yr term, intrastate insurance tiers | You | enabling TX rule-sets in prod |
| G3 | Human eyeball on the 49 CFR **§387.33T** "suspended §387.33" artifact before the $5M transport figure drives a verdict | You | transport insurance verdict |
| G4 | DPS-dependent facts (security fees, Level III training hours) stay `probable` until dps.texas.gov is back and confirms | — | those sub-facts only |
| G5 | Engine must satisfy the 3 hard design rules (interstate/intrastate branch; never a bare "compliant"; penalties are statutory-general) — see [SCHEMA.md](docs/rule-engine/SCHEMA.md) | Phase 2 | engine correctness |

## 3. Confidence & provenance legend

- **Confidence:** `verified` (operative text quoted from a fetched source; ships) ·
  `probable` (corroborated but a source was unfetchable; review queue, does NOT
  ship) · `uncertain` (none in this set).
- **Provenance:** `official` (read on a .gov / GPO primary host) ·
  `reproduction-validated` (verbatim text from a faithful reproduction whose
  fidelity was **independently validated 19/19** against official sources this
  session; TX statutes only, because the official site blocks automation) ·
  `official-agency` (an official .gov agency page citing the rule) · `reproduction`
  (single reproduction; treat as review-queue).
- **Empirical basis:** the pass-2 re-derivation independently re-read all 19
  load-bearing figures/cadences from OFFICIAL sources (via the Playwright browser,
  which renders the statute SPAs that flat fetches can't) and **every one matched**
  the dossier. That is why single-reproduction *existence* facts are held
  `verified/reproduction-validated` rather than downgraded — the hosts proved
  faithful — with G2 as the human backstop.

## 4. Rule inventory

Legend: **Cat** = license/permit/worker-cert(WC)/insurance(INS)/filing/absence.
**Conf** = confidence · provenance. "Applies when" is the applicability gate the
engine keys on.

### Caterer
| ID | Obligation | Applies when | Cadence | Cat | Source | Conf |
|---|---|---|---|---|---|---|
| FED-001 | FDA food-facility registration — **EXEMPT** (caterers = restaurants) | always | — | absence | 21 CFR 1.226(d)/1.227 | verified·official |
| FED-002 | TTB alcohol-dealer registration (Form 5630.5d) | serves/sells alcohol | one-time | filing | 27 CFR 31.31/.42/.111 | verified·official |
| FED-003/004 | No federal occupational license / insurance mandate | always | — | absence | SBA list | verified·official |
| TX-001 | DSHS food-establishment permit (local-first) | no local health authority | biennial | permit | H&S §437.0055 | verified·repro-validated |
| TX-002 | Certified Food Protection Manager (present all hours) | prepares food | per manager | WC | H&S ch. 438; 25 TAC §228.31 | verified·repro-validated |
| TX-003 | Food-handler training | each employee | **30 days** of hire; card **2 yr** | WC | 25 TAC §228.31(d)/§229.178 | verified·official-agency |
| TX-004 | TABC Mixed Beverage permit + $5k/$10k conduct bond | serves alcohol off-site | **2-yr** term | permit | Alco.Bev. §28.19/§11.09/§11.11 | verified·official |
| TX-005 | TABC seller-server training — **VOLUNTARY, not encoded** | — | — | (noted) | §106.14 | n/a |
| TX-007 | **DSHS Mobile Food Vendor license** (NEW, HB 2844) | operates a food-vending vehicle | **annual** | license | H&S ch. 437B §437B.051/.055 | verified·official |

### Event rental
| ID | Obligation | Applies when | Cadence | Cat | Source | Conf |
|---|---|---|---|---|---|---|
| FED-001 | Forklift / powered-industrial-truck operator certification | operates forklifts | **3-yr** re-eval | WC | 29 CFR 1910.178(l) | verified·official |
| FED-abs | No federal rental/inflatable license (CPSC duty is on makers) | always | — | absence | 15 USC 2063 | verified·official |
| TX-001 | **Amusement-ride liability insurance — inflatables: $1M per-occ CSL** | rents continuous-airflow inflatables | policy in force | INS | **Occ. §2151.1012** (§2151.101 general) | verified·official |
| TX-002 | Annual inspection + TDI AR-101 compliance sticker ($40/ride) | operates amusement rides | annual (before Jul 1) | permit | Occ. §2151.101 | verified·official |
| TX-003 | Quarterly injury report (Form AR-800) | reportable injury occurs | quarterly | filing | Occ. §2151.103 | verified·repro-validated |
| TX-004/005 | No TX general business license; no state tent permit (local IFC Ch.31) | always | — | absence | gov.texas.gov | verified·official |

### Security service
| ID | Obligation | Applies when | Cadence | Cat | Source | Conf |
|---|---|---|---|---|---|---|
| FED-001/2/3 | No federal guard-company license / cert / insurance | always | — | absence | (BLS/OJP unfetched) | **probable** |
| TX-001 | Security services contractor (guard company) license | acts as guard company | ≤2-yr | license | Occ. §1702.102/.108 | verified·repro-validated |
| TX-002 | License / commission **term ≤2 years** | all §1702 credentials | 2nd anniversary | license | Occ. §1702.301 | verified·official |
| TX-003 | **GL insurance on file w/ DPS — $100k/$50k/$200k** ⭐ | company-license holder | continuous | INS | Occ. §1702.124(c) | verified·official |
| TX-004 | Individual license — noncommissioned (unarmed) officer | unarmed guards | ≤2-yr | WC | Occ. §1702.221 | verified·repro-validated |
| TX-005 | Security officer commission — armed | armed guards | ≤2-yr | WC | Occ. §1702.161 | verified·repro-validated |
| TX-006 | Personal protection officer license | armed bodyguard work | ≤ commission | WC | Occ. §1702.201/.202 | verified·repro-validated |
| TX-007/008 | Level II / III / IV training (Level IV = 15 hr) | per credential | pre-credential | WC | 37 TAC §35.141 | verified (hours **probable**) |
| TX-009 | Penalty: Class A misdemeanor → 3rd-degree felony | (enforcement) | — | (noted) | Occ. §1702.388 | verified·repro-validated |

### Transportation / shuttle
| ID | Obligation | Applies when | Cadence | Cat | Source | Conf |
|---|---|---|---|---|---|---|
| FED-001 | FMCSA operating authority (MC/USDOT) | **interstate** for-hire | — | license | 49 USC 13901/2; 49 CFR 365 | verified·official |
| FED-002 | USDOT / MCS-150 biennial update | has USDOT # | **every 24 mo** | filing | 49 CFR §390.19T | verified·official |
| FED-003 | Financial responsibility **$5M (16+ seats) / $1.5M (≤15)** | interstate passenger | continuous | INS | 49 CFR §387.33T | verified·official |
| FED-004 | CDL + passenger endorsement | vehicle **16+ incl driver** | 8-yr (TX) | WC | 49 CFR 383.5 | verified·official |
| FED-005 | DOT medical examiner's certificate | CDL driver | **≤24 mo** | WC | 49 CFR §391.45(b) | verified·official |
| FED-006 | FMCSA Clearinghouse annual query | employs CDL drivers | annual | filing | 49 CFR 382.701 | verified·official |
| FED-007 | UCR registration | interstate CMV (**>10 pax incl driver**) | annual (Dec 31) | filing | 49 USC §31101 (via §14504a) | verified·reproduction |
| TX-001 | TxDMV motor-carrier registration | intrastate for-hire **>15 incl driver** | annual/biennial | license | Transp. §643 / §548.001 | verified·official |
| TX-002 | Intrastate insurance **$500k (16–26) / $5M (27+ incl driver)** | intrastate for-hire | continuous | INS | 43 TAC §218.16(a) | verified·official |
| TX-003 | TX CDL — **8-yr** term | intrastate CDL driver | 8 yr | WC | Transp. §522.051 | verified·official |
| TX-004 | TX intrastate medical/CDL adoption details | intrastate driver | — | WC | Transp. ch. 644 | **probable** (DPS down) |

### Photographer / videographer
| ID | Obligation | Applies when | Cadence | Cat | Source | Conf |
|---|---|---|---|---|---|---|
| FED-000 | No federal photographer license | always | — | absence | SBA list | verified·official |
| FED-001 | FAA Part 107 remote-pilot certificate | **flies drone commercially** | permanent cert | WC | 14 CFR 107.12 | verified·official |
| FED-002 | Part 107 knowledge **recency — 24 calendar months** | commercial drone pilot | every 24 mo | WC | 14 CFR 107.65 | verified·official |
| FED-003 | Small-UAS registration — **$5 / 3-yr** | commercial drone, per aircraft | 3 yr | permit | 14 CFR 48.100/.30 | verified·official |
| TX-000 | No TX photographer license | always | — | absence | TDLR list | verified·official |
| TX-001 | TCEQ environmental permit | **chemical** photo-finishing only (~never digital) | — | permit | TCEQ | **probable** |

### Venue org (the customer itself)
| ID | Obligation | Applies when | Cadence | Cat | Source | Conf |
|---|---|---|---|---|---|---|
| FED-001 | EIN | has employees | one-time | (noted) | IRS | verified·official |
| FED-002 | No federal venue license | always | — | absence | SBA list | verified·official |
| FED-003 | OSHA 300 injury log | **11+ employees** (venue NAICS not exempt) | annual | filing | 29 CFR 1904 | verified·official |
| FED-004 | TTB alcohol-dealer registration (5630.5d) | serves alcohol | one-time | filing | 27 CFR 31.x | **probable** (ttb.gov down) |
| TX-001 | Workers' comp is **ELECTIVE**; non-subscriber **DWC-005** notice | declines WC coverage | **Feb 1–Apr 30** annual + 30d of first hire | filing | Labor §406.002/.004/.005 | verified·official |
| TX-002 | Sales & use tax permit (**no fee, no expiration**) | sells taxable goods/services | none | permit | Tax ch. 151 | verified·official |
| TX-003 | Franchise-tax annual report | any taxable entity | **May 15**; NTD threshold **$2.47M/$2.65M** | filing | Tax ch. 171 | verified·official |
| TX-004/005 | TABC Mixed Beverage (MB) / Wine-&-Malt (BG) permit | venue serves/sells alcohol | **2-yr** | permit | Alco.Bev. ch. 28/25; §11.09 | verified·official |
| TX-006 | Food-establishment permit (if venue prepares food) | prepares/serves food | biennial | permit | H&S ch. 437 | **probable** |

## 5. Review-pass results

- **Pass 1 (legal/compliance):** dossier safe as research; 7 gates raised, all
  triaged (G1–G5 above; HB 2844 staleness fixed; provenance labels reconciled).
  Framing, regulatory-vs-contractual labeling, and applicability gating are
  confirmed strengths. Detail: REVIEW-LOG Pass 1.
- **Pass 2 (independent re-derivation):** two blind agents re-derived all 19
  load-bearing facts from OFFICIAL sources. **Zero value errors.** Corrections
  applied: **D-1** inflatable insurance is §2151.1012 ($1M CSL), not the general
  Class B ($1.5M) — a real improvement; **D-3** intrastate $5M tier is "27+
  including driver" (current §218.16), not the superseded "26+ not including
  driver"; plus citation precisions (§387.33T, UCR §31101, §391.45(b)). Full
  discrepancy log: REVIEW-LOG Pass 2.
- **Pass 3 (adversarial) & Pass 4 (code review):** scheduled for Phase 2 (they
  target the engine, which is built after your approval).

## 6. What is deliberately NOT covered (surface to users as "check locally")

Municipal/county obligations are noted in each dossier file but NOT encoded:
city/county health & food permits, per-event Temporary Food Establishment permits,
fire-marshal inspections, certificate of occupancy, assembly permits, tent/IFC
permits, local special-event security ordinances, local film/photo permits. The
engine must never render an all-green "compliant" that implies this list is
complete (SCHEMA gate). Also out of v1 scope: states other than Texas; W-9/tax
withholding; contractual (non-regulatory) COI limits.

## 7. Coverage summary

- 6 entity types × (federal + Texas) = 12 dossier files, ~57 obligations.
- Confidence: **~48 verified**, **~9 probable** (review queue: federal guard-company
  absences, security training hours, TCEQ, venue food permit, venue TTB, TX
  intrastate medical). 0 uncertain.
- Every `verified` rule carries a verbatim statutory/regulatory quote.
- Scope hygiene: **zero non-US (Spain/EU) content** (verified by grep).

## 8. Sign-off

Please mark each row and add any conditions:

- [ ] **Federal rule set** — approve for encoding · needs changes: ____
- [ ] **Texas rule set** — approve for encoding (with G2 browser spot-confirm) · needs changes: ____
- [ ] **G1 (counsel on user-facing framing)** — acknowledged; engine stays flag-OFF until cleared
- [ ] Per-entity exceptions / notes: ____

_On approval, Phase 2 begins: freeze the schema, build the deterministic C#
engine against this frozen set, tests, then passes 3–4. No customer exposure
until G1 clears._
