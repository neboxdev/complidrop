**Status:** COMPLETE   <!-- COMPLETE only when every section is done -->

# Federal regulatory obligations — TRANSPORTATION / SHUTTLE (for-hire passenger carriers)

Entity: for-hire passenger carriers (shuttle / charter buses / vans) serving event
venues. Scope of this file: **federal** obligations only. Texas (intrastate) is in
`../texas/transportation.md`.

**Applicability spine (read first — these conditions drive every entry below).**
The federal FMCSA layer attaches to a carrier when BOTH of two conditions hold:

1. **Interstate/foreign commerce.** Federal operating-authority + insurance rules
   attach to transportation "in interstate or foreign commerce." A purely
   **intrastate** Texas shuttle (both endpoints in TX, not part of a
   through-movement) is generally OUTSIDE FMCSA operating-authority/insurance
   jurisdiction and falls to the Texas file — BUT the **safety** regs (CDL,
   medical certificate, drug/alcohol) reach intrastate CMV drivers too, adopted by
   Texas (see Texas file). Interstate status is a fact question (crossing a state
   line, or carrying passengers in the flow of interstate commerce, e.g. airport
   connections to/from out-of-state trips).
2. **Vehicle passenger-capacity threshold.** Two federal thresholds recur, and
   they are DIFFERENT numbers, so encode them separately:
   - **CDL / safety trigger: "designed to transport 16 or more passengers,
     including the driver"** (a CMV). Below 16 (incl. driver) and under 26,001 lb
     and no hazmat → not a CMV → no CDL/medical/drug-testing by capacity alone.
   - **Insurance tier: 16-or-more vs 15-or-fewer** seating capacity (incl. driver)
     sets $5,000,000 vs $1,500,000.

Sources here are the GPO official CFR XML (`govinfo.gov`, 2024 title-49 vol.5
edition — parts 300-399) and the live eCFR renderer API, both primary. FMCSA's own
`.gov` explainer pages (fmcsa.dot.gov, csa.fmcsa.dot.gov) return HTTP 403 to
automated fetch and were used only as search-snippet corroboration — see Open
questions.

---

### OBL-FED-TRANSPORTATION-001: FMCSA operating authority (motor carrier registration)

- **Applies to:** A **for-hire** motor carrier of passengers operating in
  **interstate or foreign commerce**. Not required for purely intrastate operation
  (→ Texas file). Statutory exemptions exist for certain operations (e.g.
  taxicabs; some school/employer transportation) — verify per operation.
- **Obligation:** FMCSA-granted operating authority / motor carrier registration
  ("MC number" historically). A vehicle requiring operating authority may not be
  operated without it. Authority is not activated until the required BIPD
  insurance (OBL-003) is on file with FMCSA.
- **Issuing/enforcing authority:** Federal Motor Carrier Safety Administration
  (FMCSA), U.S. DOT. Application procedure: 49 CFR part 365.
- **Cadence:** One-time grant; stays active (no periodic renewal of the authority
  itself). BUT it depends on (a) the USDOT registration being updated biennially
  (OBL-002) and (b) insurance kept continuously on file (OBL-003). **Recency flag:
  effective ~Oct 1, 2025 FMCSA reportedly stopped issuing NEW standalone "MC
  numbers" and ties new operating authority to the USDOT number** (secondary
  corroboration only — see Notes / review-queue).
- **Penalty for lapse:** Operating without required operating authority → civil
  penalties (49 U.S.C. 14901 / 521); out-of-service.
- **Category:** license
- **Basis:** regulatory
- **Citation:** 49 CFR 392.9a (operating authority required); statutory basis 49
  U.S.C. 13901–13902; procedure 49 CFR part 365. —
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec392-9a.xml
  ; statute read at https://www.law.cornell.edu/uscode/text/49/13902
- **Operative text:** "A motor vehicle providing transportation requiring
  operating authority must not be operated—(1) Without the required operating
  authority or (2) Beyond the scope of the operating authority granted." (49 CFR
  392.9a(a)). Statutory: "the Secretary of Transportation shall register a person
  to provide transportation subject to jurisdiction under subchapter I of chapter
  135 as a motor carrier using self-propelled vehicles the motor carrier owns,
  rents, or leases only if the Secretary determines that the person—(A) is willing
  and able to comply with—(i) this part and the applicable regulations of the
  Secretary and the Board" (49 U.S.C. 13902(a)(1)).
- **Effective date of cited text:** current as displayed (2024 GPO edition / live
  eCFR).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** Operative regulatory sentence (392.9a) fetched from GPO primary. The
  statute (13902) was read via Cornell LII because uscode.house.gov was
  unreachable (ECONNREFUSED) and the govinfo USCODE granule 404'd this session —
  primary re-fetch recommended. Part 365 (application procedure) currently runs on
  the **transitional "T" sections** (`365.101` non-T shows "suspended indefinitely
  as of Oct 14, 2021" — the Unified Registration System freeze); this is codifi-
  cation mechanics, the requirement to register is unchanged. The Oct-2025
  MC→USDOT-number change is FMCSA registration-modernization; the underlying
  obligation ("have FMCSA operating authority to run for-hire interstate passenger
  service") is unchanged. Document to collect from a vendor: FMCSA operating-
  authority / MC certificate or the carrier's active USDOT/MC record.

---

### OBL-FED-TRANSPORTATION-002: USDOT number registration + biennial update (MCS-150)

- **Applies to:** Any motor carrier operating a CMV in interstate commerce must
  have a USDOT number. For a 16+-passenger interstate shuttle this always applies;
  it also reaches some intrastate carriers (hazmat, or by Texas adoption — see TX
  file).
- **Obligation:** Obtain a USDOT number before operating (file Form MCS-150 /
  online application), and **update the registration at least every 24 months**
  (biennial update), even if nothing changed.
- **Issuing/enforcing authority:** FMCSA.
- **Cadence:** Initial before operating; then **every 24 months** — the MONTH is
  set by the last digit of the USDOT number, the YEAR (odd/even) by the
  next-to-last digit.
- **Penalty for lapse:** Civil penalties under 49 U.S.C. 521(b)(2)(B) **and
  deactivation of the USDOT number** (which halts lawful operation).
- **Category:** filing
- **Basis:** regulatory
- **Citation:** 49 CFR 390.19T(b) (the currently-operative transitional section;
  the non-T 49 CFR 390.19 shows "suspended indefinitely" as of Nov 17, 2023 under
  the URS freeze). —
  https://www.ecfr.gov/api/renderer/v1/content/enhanced/current/title-49?section=390.19T
- **Operative text:** "If the next-to-last digit of its USDOT Number is odd, the
  motor carrier or intermodal equipment provider shall file its update in every
  odd-numbered calendar year. … if the next-to-last digit of the USDOT Number is
  even, the motor carrier or intermodal equipment provider shall file its update
  in every even-numbered calendar year." Filing month by last digit ("USDOT No.
  ending in 1" → "by last day of January"; 2→February; … 0→October). Penalty
  clause (b)(4): "A person that fails to complete biennial updates … is subject to
  the penalties prescribed in 49 U.S.C. 521(b)(2)(B) … and deactivation of its
  USDOT Number." (49 CFR 390.19T(b)).
- **Effective date of cited text:** current as displayed (live eCFR).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** Fetched from the live eCFR renderer API (primary). The "biennial
  update" is the recurring, document-shaped obligation; the MCS-150 form is the
  filing artifact. Distinguish from OBL-001 (operating authority) — a carrier can
  hold authority yet be deactivated for a missed biennial update.

---

### OBL-FED-TRANSPORTATION-003: Public-liability (BIPD) insurance — minimum levels for passenger carriers

- **Applies to:** For-hire motor carriers of passengers operating in interstate or
  foreign commerce. Minimum is set by the **highest-seating-capacity vehicle** in
  the fleet:
  - **16 passengers or more, including the driver → $5,000,000**
  - **15 passengers or less, including the driver → $1,500,000**
- **Obligation:** Maintain public-liability (bodily injury & property damage)
  insurance at or above the minimum, and file evidence with FMCSA (Form **BMC-91**
  or **BMC-91X**) plus carry the **Form MCS-90B** endorsement on the policy.
  Operating authority is not activated/retained without it on file.
- **Issuing/enforcing authority:** FMCSA (financial-responsibility program).
- **Cadence:** Continuous; must remain on file. Cancellation requires advance
  written notice (35 days) to FMCSA.
- **Penalty for lapse:** Operating authority not granted / revoked; operating
  without required financial responsibility → civil penalties, out-of-service.
- **Category:** insurance
- **Basis:** regulatory
- **Citation:** 49 CFR 387.31 (financial responsibility required — the schedule
  reference) & **49 CFR 387.33T** (minimum levels — the operative in-force section;
  the non-T § 387.33 reorganization was suspended at 82 FR 5307, Jan 17 2017),
  subpart B. Figures are identical under § 387.33T. Operative § 387.33T read on
  ecfr.gov via Playwright (official); GPO 2024 edition corroborates § 387.31 / the
  $5M/$1.5M amounts —
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec387-31.xml
  and
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec387-33.xml
- **Operative text:** "No motor carrier shall operate a motor vehicle transporting
  passengers until the motor carrier has obtained and has in effect the minimum
  levels of financial responsibility as set forth in § 387.33 of this subpart."
  (49 CFR 387.31(a) — the cross-reference reads "§ 387.33"; the operative in-force
  schedule is the identically-numbered **§ 387.33T**). Schedule (49 CFR
  **387.33T(a)**, "For-hire motor carriers of passengers operating in interstate or
  foreign commerce"): "(1) Any vehicle with a seating capacity of 16 passengers or
  more, including the driver — $5,000,000"; "(2) Any vehicle with a seating capacity
  of 15 passengers or less, including the driver — 1,500,000".
- **Effective date of cited text:** [80 FR 63709, Oct. 21, 2015, as amended at 83
  FR 22876, May 17, 2018]; amounts long-standing (statutory floor since the 1980s
  Bus Regulatory Reform Act era).
- **Verified:** 2026-07-07 | **Confidence:** verified | **Provenance:** official
  (operative § 387.33T read on ecfr.gov via Playwright; § 387.31 and the $5M/$1.5M
  amounts corroborated on the GPO 2024 CFR edition)
- **Notes — the "suspended" editorial artifact (READ THIS):** Both the 2024 GPO
  CFR and the live eCFR display 387.33 as active text carrying the $5M/$1.5M
  amounts, AND carry an Effective Date Note: "At 82 FR 5307, Jan. 17, 2017, §
  387.33 was suspended, effective Jan. 14, 2017." Investigation: this is the
  2017 regulatory-freeze / URS codification freeze (the same 82 FR 5307 also
  froze 387.303); it suspended the 2015 **reorganization**, not the dollar
  amounts. The $5M/$1.5M figures are identical under both the current codified
  text and the pre-2015 fallback text, are unchanged, and are the amounts FMCSA
  currently enforces and requires on file to grant passenger operating authority
  (corroborated by FMCSA's own passenger licensing/insurance pages via search
  snippet, and by the section's later 2018 amendment). The amount is therefore not
  in genuine doubt; the codification note is flagged in the review-queue for a
  belt-and-suspenders human confirmation. Paragraph (b) sets a higher "highest
  State minimum" rule for multi-state §5307/5310/5311 transit providers (unlikely
  for a private event shuttle). Distinguish from the venue's *contractual* COI
  demands (contractual, handled by the checklist feature) — this entry is the
  legally-mandated floor. **Correction (pass-2):** the operative section is
  **§ 387.33T** (non-T § 387.33 suspended at 82 FR 5307); the $5M/$1.5M figures are
  identical under § 387.33T — flagged for a human eyeball at the founder gate.

---

### OBL-FED-TRANSPORTATION-004: Commercial Driver's License (CDL) with Passenger (P) endorsement

- **Applies to:** Any **driver** operating a **CMV**. For this entity the trigger
  is a vehicle **"designed to transport 16 or more passengers, including the
  driver"** (also triggered independently by ≥26,001 lb GVWR/GCWR or placarded
  hazmat). To carry passengers in such a vehicle the driver additionally needs the
  **Passenger (P) endorsement**. A ≤15-passenger van (under 26,001 lb, no hazmat)
  does NOT require a CDL by capacity alone.
- **Obligation:** A valid CDL issued by the driver's State of domicile, bearing the
  **P** endorsement (obtained via a knowledge + skills test). (School-bus service
  additionally needs **S**; not typical for event shuttles.)
- **Issuing/enforcing authority:** Driver's State DMV/DPS under the federal 49 CFR
  part 383 floor; FMCSA sets the standard. (Texas: DPS, Transp. Code ch. 522 — see
  TX file.)
- **Cadence:** CDL renewal term is set by the issuing State (see TX file). The
  endorsement travels with the CDL. (Medical certification is a separate document
  — OBL-005.)
- **Penalty for lapse:** Driver operating a CMV without the proper CDL/endorsement
  → driver and carrier civil penalties; driver placed out-of-service.
- **Category:** worker-certification
- **Basis:** regulatory
- **Citation:** 49 CFR 383.23 (CDL required), 49 CFR 383.5 (definition of
  "Commercial motor vehicle"), 49 CFR 383.93 (endorsements). —
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec383-23.xml
  ,
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec383-5.xml
  ,
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec383-93.xml
- **Operative text:** "No person may legally operate a CMV unless such person
  possesses a CDL which meets the standards contained in subpart J of this part,
  issued by his/her State or jurisdiction of domicile" (49 CFR 383.23(a)(2)).
  CMV-by-capacity: "[a commercial motor vehicle] Is designed to transport 16 or
  more passengers, including the driver" (49 CFR 383.5). Endorsement: "An operator
  must obtain State-issued endorsements to his/her CDL to operate commercial motor
  vehicles which are: … (2) Passenger vehicles"; "(2) Passenger—a knowledge and a
  skills test" (49 CFR 383.93(a),(b)).
- **Effective date of cited text:** current as displayed (2024 GPO edition).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The 16-passenger-including-driver figure is the key machine-evaluable
  applicability threshold and it is verbatim in 383.5. Document to collect: the
  driver's CDL showing class + a "P" endorsement code. **Prerequisite (one-time,
  not separately collected):** since Feb 7, 2022, a driver obtaining an INITIAL
  Passenger (P) endorsement must first complete FMCSA Entry-Level Driver Training
  (ELDT, 49 CFR part 380) from a provider on the Training Provider Registry — but
  holding the P endorsement already evidences ELDT completion, so encode the CDL/P
  endorsement (this entry), not ELDT as a separate renewable credential.

---

### OBL-FED-TRANSPORTATION-005: DOT medical examiner's certificate (driver physical qualification)

- **Applies to:** Any driver operating a CMV subject to part 391 (interstate CMV
  drivers; Texas adopts the standard for intrastate CMV drivers — see TX file). For
  this entity: drivers of 16+-passenger vehicles (and other CMVs).
- **Obligation:** The driver must be medically certified as physically qualified by
  a medical examiner listed on FMCSA's National Registry, evidenced by a current
  **Medical Examiner's Certificate** (MEC, Form MCS-6 / the "med card").
- **Issuing/enforcing authority:** FMCSA (National Registry of Certified Medical
  Examiners); certificate issued by the certified ME.
- **Cadence:** Maximum **24 months**; shorter intervals (commonly 12 months, or
  less) apply for certain medical conditions (e.g. insulin-treated diabetes,
  certain vision, hypertension) per 391.45(c)-(h).
- **Penalty for lapse:** Driver not physically qualified → out-of-service; carrier
  penalties for using a medically-unqualified driver.
- **Category:** worker-certification
- **Basis:** regulatory
- **Citation:** 49 CFR 391.41(a) (must be qualified/carry cert), 49 CFR 391.45(b)
  (24-month re-exam — the section that states the numeric cap), 49 CFR 391.43
  (exam & certificate; prescribes the exam, states no numeric interval cap). —
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec391-41.xml
  and
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec391-45.xml
- **Operative text:** "A person subject to this part must not operate a commercial
  motor vehicle unless he or she is medically certified as physically qualified to
  do so, and, except as provided in paragraph (a)(2) of this section, when on-duty
  has on his or her person the original, or a copy, of a current medical examiner's
  certificate …" (49 CFR 391.41(a)(1)(i)). Cadence: "Any driver who has not been
  medically examined and certified as qualified to operate a commercial motor
  vehicle during the preceding 24 months, unless the driver is required to be
  examined and certified in accordance with paragraph (c), (d), (e), (f), (g), or
  (h) of this section" (49 CFR 391.45(b)).
- **Effective date of cited text:** current as displayed (2024 GPO edition).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** **Recency:** on/after **June 23, 2025**, a CDL/CLP holder's medical
  certification is electronically transmitted by the ME to FMCSA and merged into
  the driver's CDL record, so CDL holders "no longer need to carry on … person the
  medical examiner's certificate" (49 CFR 391.41(a)(2)(i)(B)) — the certificate
  still exists and the 24-month cadence is unchanged, but for CDL drivers the
  authoritative record is the state driving record, not a paper card. For a
  document-collection product this means a CDL shuttle driver's med status may be
  verifiable via the CDL/CDLIS record rather than a scanned med card. The 24-month
  max is the encodable expiry cadence.

---

### OBL-FED-TRANSPORTATION-006: FMCSA Drug & Alcohol Clearinghouse — pre-employment + annual query

- **Applies to:** Employers of CDL drivers subject to part 382 controlled-substance
  & alcohol testing (i.e. anyone employing a CDL driver of a CMV — includes 16+-pax
  shuttle drivers). An owner-operator is treated as an employer.
- **Obligation:** Register in the FMCSA Clearinghouse; run a **pre-employment full
  query** before using a driver in a safety-sensitive function, and run at least
  one **query per driver per year** (annual limited query at minimum).
- **Issuing/enforcing authority:** FMCSA (Clearinghouse).
- **Cadence:** Pre-employment (once, before use) + **annually** (≥ every 12 months)
  for each CDL driver.
- **Penalty for lapse:** Civil penalties for failing to query; the driver may not
  perform safety-sensitive functions if a query/record shows a prohibited status.
- **Category:** filing (query record)
- **Basis:** regulatory
- **Citation:** 49 CFR 382.701(a) (pre-employment query), 382.701(b) (annual
  query). —
  https://www.govinfo.gov/content/pkg/CFR-2024-title49-vol5/xml/CFR-2024-title49-vol5-sec382-701.xml
- **Operative text:** "Employers must not employ a driver subject to controlled
  substances and alcohol testing under this part to perform a safety-sensitive
  function without first conducting a pre-employment query of the Clearinghouse"
  (49 CFR 382.701(a)). "Employers must conduct a query of the Clearinghouse at
  least once per year for information for all employees subject to controlled
  substance and alcohol testing under this part" (49 CFR 382.701(b)).
- **Effective date of cited text:** current as displayed (2024 GPO edition).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The broader DOT drug/alcohol **testing program** (pre-employment,
  random, post-accident, reasonable-suspicion tests; 49 CFR part 382 subparts B-C
  and part 40 procedures) is PROCESS, noted-not-encoded — no single per-vendor
  document. The Clearinghouse annual query is the recurring, record-shaped
  obligation. Employer Clearinghouse **registration** is a one-time setup step
  (process).

---

### OBL-FED-TRANSPORTATION-007: Unified Carrier Registration (UCR) — annual

- **Applies to:** Motor carriers (incl. passenger carriers), brokers, freight
  forwarders and leasing companies that operate in **interstate/international
  commerce** and have a USDOT number. A purely intrastate carrier is not subject.
- **Obligation:** Annual UCR registration and fee, paid to the carrier's base
  State under the UCR Agreement; fee bracket by number of power units (vehicles).
- **Issuing/enforcing authority:** Unified Carrier Registration Plan / base-State
  (federal program, 49 U.S.C. 14504a); Texas participates. Enforced by FMCSA & the
  states.
- **Cadence:** **Annual.** Registration period opens Oct 1; fees due by **Dec 31**
  for the upcoming registration year (e.g. 2026 year due by Dec 31, 2025).
- **Penalty for lapse:** Fines and roadside/operational enforcement; may be placed
  out-of-service in enforcing states.
- **Category:** filing
- **Basis:** regulatory
- **Citation:** 49 U.S.C. 14504a (UCR fee; its "commercial motor vehicle" fee
  threshold cross-references **49 U.S.C. §31101(1)(B)** — "more than 10 passengers,
  including the driver"); UCR Plan (plan.ucr.gov); operative annual
  requirement stated in the **TxDMV Motor Carrier Handbook (May 2025)** (primary
  agency, txdmv.gov). — Handbook:
  https://www.txdmv.gov/sites/default/files/body-files/Motor_Carrier_Handbook.pdf
  ; statute read at https://www.law.cornell.edu/uscode/text/49/14504a and
  https://www.law.cornell.edu/uscode/text/49/31101 (Cornell LII — govinfo USC
  granule failed; the §31101(1)(B) statutory-chain citation is `reproduction`
  provenance) ; fee schedule https://plan.ucr.gov/fee-brackets/
- **Operative text:** "Under the federal Unified Carrier Registration (UCR) Act,
  individuals and companies operating commercial vehicles in interstate or
  international commerce must register at www.UCR.gov and pay an annual fee based on
  fleet size." and "UCR registrations expire on December 31 of each calendar year.
  Roadside enforcement of UCR requirements begins on January 1 of the following
  year." (TxDMV Motor Carrier Handbook, May 2025). Statutory backing: "Motor
  carriers, motor private carriers, leasing companies, brokers, and freight
  forwarders shall pay all fees required under this section to their base-State
  pursuant to the UCR Agreement" (49 U.S.C. 14504a(f)(4)); fee basis "the number of
  commercial motor vehicles owned or operated" (49 U.S.C. 14504a(f)(1)(A)).
- **Effective date of cited text:** Handbook May 2025; 2026 fee schedule set by UCR
  Plan (unchanged from 2025 per corroboration).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** Upgraded to **verified**: the operative annual-registration sentence +
  Dec 31 cadence are quoted from a primary state agency source (TxDMV Handbook,
  fetched and text-extracted in-session); the federal statute 49 U.S.C. 14504a is
  the citation of record (read via Cornell LII — re-fetch the primary USC when
  uscode.house.gov is reachable). **Applicability nuance:** UCR reaches carriers
  operating in interstate/international commerce; the UCR "commercial motor vehicle"
  fee definition (49 U.S.C. §31101(1), incorporated by §14504a's cross-reference)
  is ≥10,001 lb GVWR/GVW, OR — per **49 U.S.C. §31101(1)(B)** — "designed to
  transport more than 10 passengers, including the driver," OR placardable hazmat
  (a THIRD threshold, distinct from the 16-passenger CDL trigger and the
  15-passenger TX-registration trigger). **Citation source (pass-2):** this "more
  than 10 passengers, including the driver" UCR threshold traces to **49 U.S.C.
  §31101(1)(B)** (via §14504a's cross-reference), NOT to 49 CFR 390.5 — whose
  general CMV definition uses a DIFFERENT passenger split ("more than 8 passengers
  including the driver for compensation; or more than 15 not for compensation") and
  is not the UCR threshold. Provenance of this statutory-chain citation is
  **reproduction** (read via Cornell LII; the govinfo US-Code granule failed this
  session), so this specific citation is `reproduction`, not official. A small
  interstate shuttle with no qualifying CMV still registers at the lowest fleet
  bracket. This is a legitimate recurring compliance artifact (a UCR
  receipt) for interstate shuttle operators; note the Handbook states proof need not
  be carried in the vehicle.

---

## Local-level obligations (noted, not encoded)

- **USDOT number display on the vehicle** (49 CFR 390.21) — a *marking* requirement
  (the number must appear on both sides of the CMV), not a collectible per-vendor
  document. Noted.
- **ADA vehicle accessibility** (49 CFR parts 37/38, DOT) — applies to certain
  service structures; not a per-vendor document/credential. Noted.
- **Airport / municipal ground-transportation permits** — many airports and cities
  require a separate shuttle/limo ground-transportation permit or decal to pick up
  on their property. These are municipal/airport-authority obligations, OUT of
  federal/state encoding scope — the product should say "check the specific airport
  or city." Noted (see Texas file for TX-specific city examples).
- **Hours-of-Service logs / ELD, vehicle inspection & maintenance (parts 395, 396),
  driver qualification file (391.51)** — federal PROCESS obligations, not a single
  collectible credential. Noted, not encoded.

## Open questions / review-queue candidates

1. **387.33 "suspended" editorial note (82 FR 5307).** Confirmed the $5M/$1.5M
   amounts are current & enforced under both the codified and fallback text, but a
   human should eyeball the live eCFR 387.33 page once to confirm no live change.
   (Amounts corroborated; codification note flagged.)
2. **FMCSA `.gov` pages return HTTP 403 to automated fetch** — could NOT live-fetch:
   `fmcsa.dot.gov/registration/insurance-filing-requirements`,
   `fmcsa.dot.gov/safety/passenger-safety/licensing-and-insurance-requirements-hire-motor-carriers-passengers-parts`,
   `csa.fmcsa.dot.gov/safetyplanner/...`. Their content (insurance form names
   BMC-91/BMC-91X/MCS-90B, exemptions list) was taken from search snippets +
   the CFR text; a human with browser access should confirm the BMC/MCS-90B form
   names and the taxicab/school exemptions verbatim.
3. **`uscode.house.gov` unreachable this session (ECONNREFUSED); govinfo USCODE
   granule 404'd.** 49 U.S.C. 13901/13902/14504a were read via Cornell LII
   (accurate reproduction, but secondary). Re-fetch the primary USC for verbatim
   statutory quotes when the House server is reachable.
4. **MC-number → USDOT-number change (~Oct 1, 2025).** Secondary-sourced only;
   confirm the exact effective date and mechanics from an FMCSA primary notice.
5. **Interstate-vs-intrastate determination for event shuttles.** Whether a given
   venue shuttle run is "interstate" (triggering OBL-001/003/007) is fact-specific;
   the engine should treat FMCSA operating authority + $5M/$1.5M as
   *conditional-on-interstate*, with the TX intrastate track as the alternative.
6. **Part 365 / 390.19 "T" transitional sections (URS).** Operative requirements
   confirmed via the "T" sections and eCFR; the long-running Unified Registration
   System freeze means citations should track the "T" variants until URS is fully
   implemented.
