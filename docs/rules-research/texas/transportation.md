**Status:** COMPLETE   <!-- COMPLETE only when every section is done -->

# Texas regulatory obligations — TRANSPORTATION / SHUTTLE (for-hire passenger carriers)

Entity: for-hire passenger carriers (shuttle / charter buses / vans) serving event
venues, operating **intrastate** in Texas. See `../federal/transportation.md` for
the federal layer. Two kinds of federal rule behave differently by
interstate/intrastate:
- **Interstate-only** (attach only in interstate/foreign commerce): FMCSA operating
  authority, USDOT operating authority, the $5M/$1.5M financial-responsibility
  minimums, and UCR.
- **Also intrastate** (attach to Texas-only CMV operations via Texas's adoption of
  the FMCSRs, Transp. Code ch. 644 / DPS 37 TAC ch. 4): **CDL + P endorsement,
  driver medical certificate, and the drug/alcohol Clearinghouse query.**
This file covers the **Texas intrastate** track and the TX-issued credentials.

**Three DIFFERENT passenger-capacity thresholds recur — keep them distinct when
encoding:**
- **TxDMV registration & TX insurance trigger: "more than 15 passengers, including
  the driver"** (= 16+) — the Transp. Code 548.001 CMV definition.
- **TX intrastate insurance $5M tier: "27 or more people, including the driver"**
  (current 43 TAC §218.16(a), 2024 amendment aligned to Transp. Code §548.001);
  "more than 15 but fewer than 27 people, including the driver" sits at
  **$500,000**. (The earlier "26+ not including the driver" phrasing was the
  superseded pre-2024 version — see OBL-TX-TRANSPORTATION-002.)
- **CDL trigger: "16 or more passengers, including the driver"** — Texas adopts the
  federal 49 CFR 383.5 CMV definition.

Primary sources used: **TxDMV Motor Carrier Handbook (May 2025)** (txdmv.gov,
official agency publication — downloaded and text-extracted in-session), and the
Texas Transportation Code (read via the `texas.public.law` faithful mirror because
`statutes.capitol.texas.gov` serves a JavaScript SPA shell to automated fetch — see
Open questions). The SOS TAC Figure PDF and DPS pages returned HTTP 403 / refused
connections this session.

---

### OBL-TX-TRANSPORTATION-001: TxDMV Motor Carrier Registration (Certificate of Registration / "TxDMV Number")

- **Applies to:** An **intrastate** motor carrier operating a **commercial motor
  vehicle** on Texas roads. A CMV (Transp. Code 548.001) includes a vehicle
  **"designed or used to transport more than 15 passengers, including the driver"**
  (= 16+) — so a for-hire event shuttle with 16+ seats operating intrastate must
  register. Also triggered by >26,000 lb weight, placardable hazmat, a farm vehicle
  ≥48,000 lb, a commercial school bus, or transporting household goods for
  compensation (any size). **NOT a CMV / not required:** a ≤15-passenger van under
  26,000 lb (no hazmat) — those fall to ordinary TX auto financial-responsibility +
  city licensing. **Statutory exemptions (643.002)** further exclude, among others,
  "a motor vehicle used to transport passengers operated by an entity whose primary
  function is not the transportation of passengers, such as a vehicle operated by a
  hotel, day-care center, public or private school, nursing home, or similar
  organization," governmental vehicles, and UCR carriers operating exclusively in
  interstate/international commerce.
- **Obligation:** A TxDMV Certificate of Registration ("TxDMV Number") issued by
  the Motor Carrier Division. Prerequisites: a valid **USDOT Number** first, then an
  insurer files **Form E** proof of insurance electronically; TxDMV then issues the
  number + an **insurance cab card** that must be carried in the vehicle.
- **Issuing/enforcing authority:** Texas Department of Motor Vehicles (TxDMV),
  Motor Carrier Division.
- **Cadence:** Carrier chooses an **annual (1-year)** or **biennial (2-year)**
  certificate ($100 application; $10/vehicle for 1-yr, $20/vehicle for 2-yr).
  TxDMV mails/emails renewal notices ~30 days before expiration (renewal is the
  carrier's responsibility regardless). **Charter-bus operators may qualify for a
  NON-EXPIRING certificate** (a non-expiring cert is barred for carriers that
  transport waste, recyclables, household goods, or operate **non-charter** buses).
  7-day and 90-day temporary certificates are not renewable.
- **Penalty for lapse:** Administrative penalties, sanctions, or revocation of
  operating authority; cannot lawfully operate the CMV intrastate.
- **Category:** license
- **Basis:** regulatory
- **Citation:** Tex. Transp. Code §643.051 (registration required), §643.002
  (exemptions), §548.001 (CMV definition); TxDMV rules 43 TAC ch. 218; **TxDMV
  Motor Carrier Handbook (May 2025)** —
  https://www.txdmv.gov/sites/default/files/body-files/Motor_Carrier_Handbook.pdf
  ; statute read via https://texas.public.law/statutes/tex._transp._code_section_643.051
  and .../section_643.002 and .../section_548.001
- **Operative text:** "A motor carrier may not operate a commercial motor vehicle,
  as defined by Section 548.001, on a road or highway of this state unless the
  carrier registers with the department under this subchapter." (Tex. Transp. Code
  §643.051(a)). CMV trigger: a vehicle "designed or used to transport more than 15
  passengers, including the driver" (§548.001). Handbook (primary agency): "Motor
  carriers operating intrastate commercial motor vehicles on Texas roadways must
  register with the TxDMV's Motor Carrier Division (MCD) if they meet any of the
  following criteria: … Operate a vehicle designed to transport more than 15
  passengers, including the driver …". Exemption: "a motor vehicle used to transport
  passengers operated by an entity whose primary function is not the transportation
  of passengers, such as a vehicle operated by a hotel, day-care center, public or
  private school, nursing home, or similar organization" (§643.002(4)).
- **Effective date of cited text:** Handbook dated May 2025; statute current as
  displayed.
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The registration + criteria + exemptions are quoted from the primary
  TxDMV agency Handbook (fetched to disk, text-extracted with pdftotext) and
  corroborated by the statute text. Document to collect from a vendor: the TxDMV
  cab card / certificate of registration (shows the TxDMV Number + expiration).
  Distinguish "annual/biennial" (scheduled shuttle) vs "non-expiring" (charter
  event work) — the expiry cadence depends on the certificate type the carrier
  holds.

---

### OBL-TX-TRANSPORTATION-002: Texas intrastate insurance minimums (Form E filing)

- **Applies to:** Intrastate motor carriers required to register (OBL-001).
  Passenger tiers (by vehicle design/use), per the current 43 TAC §218.16(a)
  (2024 amendment, "including the driver"):
  - **More than 15 people, but fewer than 27 people, including the driver (16-26)
    → $500,000**
  - **27 or more people, including the driver → $5,000,000**
  - School bus (any capacity) → **$500,000**
  - For-hire/private carrier >26,000 lb (by weight) → **$500,000**
  - (Household-goods mover <26,000 lb → $300,000; placardable hazmat → $1,000,000
    or $5,000,000 by material; HHG cargo insurance $5,000/$10,000.)
- **Obligation:** Maintain continuous public-liability insurance at or above the
  applicable minimum; the insurer files **Form E** with TxDMV (a $100 filing fee
  applies). Coverage must stay active for the life of the registration.
- **Issuing/enforcing authority:** TxDMV Motor Carrier Division.
- **Cadence:** Continuous; Form E must remain on file (Form K cancellation notice
  ends it). Tied to the registration term.
- **Penalty for lapse:** "Failure to maintain active insurance coverage associated
  with your TxDMV Number may result in administrative penalties, sanctions, or the
  revocation of your operating authority."
- **Category:** insurance
- **Basis:** regulatory
- **Citation:** 43 TAC §218.16(a) (Figure — minimum insurance levels; 2024
  amendment adopted 12/12/2024, aligned to Transp. Code §548.001 "including the
  driver"); Tex. Transp. Code §643.101 et seq. Read on the TxDMV adopted-rule PDF —
  https://www.txdmv.gov/sites/default/files/body-files/Chapter-218-Adopt-2024.pdf
  (OFFICIAL agency source; independently confirmed by two re-derivation agents; the
  SOS TAC host blocks automated fetches). Corroborated by the **TxDMV Motor Carrier
  Handbook (May 2025)**, "Insurance Requirements" table —
  https://www.txdmv.gov/sites/default/files/body-files/Motor_Carrier_Handbook.pdf
- **Operative text:** 43 TAC §218.16(a) figure (verbatim, 2024 amendment):
  "**Vehicles, including buses, designed or used to transport more than 15 people,
  but fewer than 27 people, including the driver**" — **$500,000**; "**Vehicles,
  including buses, designed or used to transport 27 or more people, including the
  driver**" — **$5,000,000**; school buses (any capacity) — **$500,000**; private
  or for-hire motor carriers over 26,000 lb — **$500,000**. Handbook (corroborating,
  agency): "Under Texas law, all intrastate motor carriers are required to register
  with TxDMV and maintain continuous proof of insurance or other acceptable
  financial responsibility."
- **Effective date of cited text:** Handbook May 2025 (current TAC figure).
- **Verified:** 2026-07-07 | **Confidence:** verified | **Provenance:** official
  (43 TAC §218.16(a) figures from the TxDMV adopted-rule PDF Chapter-218-Adopt-2024,
  the official agency source; two re-derivation agents independently confirmed the
  2024 "including the driver" wording; the SOS TAC host blocks automated fetches)
- **Notes — CRITICAL interstate/intrastate divergence:** A 16-26-seat event shuttle
  needs only **$500,000** of liability **intrastate** in Texas, but the SAME
  vehicle in **interstate** commerce needs **$5,000,000** federally
  (OBL-FED-TRANSPORTATION-003). The rules engine MUST branch on interstate-vs-
  intrastate before picking a minimum, and must NOT treat a $500K Texas-intrastate
  COI as satisfying the federal floor. The authoritative rule text is the
  "Figure: 43 TAC §218.16(a)," now pinned to the TxDMV adopted-rule PDF
  (Chapter-218-Adopt-2024, official) rather than the Handbook summary; the SOS TAC
  host blocks automated fetches.
  **Correction (pass-2):** the current §218.16(a) (2024 amendment) reads "27 or
  more people, INCLUDING the driver" for the $5M tier; the earlier "not including
  the driver" phrasing was the superseded pre-2024 version. The 2024 amendment
  aligned the categories to Transportation Code §548.001. No vehicle tier exists
  for ≤15 passengers (outside this figure).

---

### OBL-TX-TRANSPORTATION-003: Texas Commercial Driver License (CDL) with Passenger (P) endorsement

- **Applies to:** A driver operating a **CMV** on Texas roads (intrastate OR
  interstate). Texas defines CMV by reference to **49 CFR 383.5** — i.e. a vehicle
  "designed to transport 16 or more passengers, including the driver" (also
  ≥26,001 lb or placarded hazmat). To carry passengers in such a vehicle the driver
  needs the **Passenger (P) endorsement**. A ≤15-passenger van under 26,001 lb does
  NOT require a CDL by capacity alone.
- **Obligation:** A valid Texas CDL of the appropriate class with the **P**
  endorsement; the driver also self-certifies interstate/intrastate + medical
  category (form CDL-5) with DPS.
- **Issuing/enforcing authority:** Texas Department of Public Safety (DPS).
- **Cadence:** "an original commercial driver's license expires eight years after
  the applicant's next birthday" (Tex. Transp. Code §522.051(a)) — i.e. **~8-year**
  term; the P endorsement rides with the CDL. (Medical certification is a separate,
  more frequent document — OBL-004.)
- **Penalty for lapse:** Driving a CMV without the proper CDL is a misdemeanor
  (§522.011) and an out-of-service condition.
- **Category:** worker-certification
- **Basis:** regulatory
- **Citation:** Tex. Transp. Code §522.011 (license required), §522.003 (CMV =
  meaning in 49 CFR 383.5), §522.051 (term); federal floor 49 CFR 383.23/383.93
  (verified in federal file). Statute read via
  https://texas.public.law/statutes/tex._transp._code_section_522.011 ,
  .../section_522.003 , .../section_522.051
- **Operative text:** "A person may not drive a commercial motor vehicle unless: …
  the person holds a valid commercial driver's license … " (Tex. Transp. Code
  §522.011(a)). "'Commercial motor vehicle' … has the meaning assigned by 49 C.F.R.
  Section 383.5." (§522.003(5)). "an original commercial driver's license expires
  eight years after the applicant's next birthday" (§522.051(a)).
- **Effective date of cited text:** current as displayed.
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The underlying obligation (CDL + P endorsement for a 16+-passenger CMV)
  rests on federal primary text already fetched (49 CFR 383.5/383.23/383.93 —
  federal file). The Texas-specific statute sentences (522.011/522.003/522.051, incl.
  the 8-year term) were read via the `texas.public.law` mirror because
  `statutes.capitol.texas.gov` serves an unparseable SPA shell — a human should
  re-confirm the 8-year term against the capitol primary. Document to collect: the
  driver's Texas CDL showing class + "P" endorsement.

---

### OBL-TX-TRANSPORTATION-004: Intrastate driver medical examiner's certificate (Texas adoption of 49 CFR Part 391)

- **Applies to:** Drivers operating a CMV in **intrastate** Texas commerce (drivers
  in interstate commerce are already covered by OBL-FED-TRANSPORTATION-005). Texas
  applies the federal 49 CFR Part 391 physical-qualification standard to intrastate
  CMV drivers, with limited Texas variances.
- **Obligation:** A current Medical Examiner's Certificate (the "med card") plus CDL
  medical self-certification on file with DPS; a driver selecting "Non-Excepted
  Intrastate" (CDL-5 Section B) must provide a current medical examiner's
  certificate.
- **Issuing/enforcing authority:** Texas DPS (adopting FMCSA Part 391); certificate
  issued by a certified medical examiner.
- **Cadence:** Maximum **24 months** (mirrors federal 49 CFR 391.45(b)); shorter for
  certain conditions. Texas provides intrastate medical **waivers/variances** for
  some conditions that would disqualify interstate (e.g. via DPS form CDL-37), and
  does not adopt 49 CFR 391.11(b)(2) for intrastate drivers.
- **Penalty for lapse:** Not medically qualified → out-of-service; CDL downgrade if
  the certification lapses.
- **Category:** worker-certification
- **Basis:** regulatory
- **Citation:** Tex. Transp. Code ch. 644 (Texas adoption of federal motor-carrier
  safety regulations); 37 TAC ch. 4 (DPS motor-carrier safety rules); 49 CFR 391.41,
  391.45; DPS forms CDL-5 / "A Texas Motor Carrier's Guide to Highway Safety"
  (MCS-9). (DPS `dps.texas.gov` refused connection this session — see Open
  questions.)
- **Operative text:** Federal anchor already quoted (49 CFR 391.45(b): re-exam
  within "the preceding 24 months"; 391.41(a)(1)(i) must be medically certified).
  Texas intrastate application confirmed via DPS CDL-5/medical-certification
  guidance (search corroboration; DPS primary not fetchable this session).
- **Effective date of cited text:** federal text current (2024 GPO edition); TX
  adoption current.
- **Verified:** 2026-07-07 | **Confidence:** probable
- **Notes:** Downgraded to **probable** because the DPS primary pages
  (`dps.texas.gov`) and 37 TAC ch. 4 could not be fetched live this session
  (ECONNREFUSED / SPA). The 24-month cadence and the med-card obligation are
  federally verified; what needs a human primary confirm is the exact Texas
  intrastate variance set and the ch. 644 / 37 TAC ch. 4 adoption citation. For a
  document-collection product this is largely the SAME med card as the federal
  entry, applied to intrastate drivers. **Cross-reference:** the federal drug/alcohol
  **Clearinghouse pre-employment + annual query** (OBL-FED-TRANSPORTATION-006) and
  the CDL rules also reach **intrastate** Texas CDL drivers — part 382/383 apply to
  any driver required to hold a CDL (16+-passenger CMV), and Texas adopts the FMCSRs
  for intrastate operations (ch. 644 / 37 TAC ch. 4) — so those federal entries are
  not interstate-only for a Texas shuttle with 16+-seat vehicles.

---

## Local-level obligations (noted, not encoded)

- **Municipal for-hire / limousine / shuttle permits.** A for-hire passenger
  vehicle **at or below 15 passengers** (under 26,000 lb) is NOT a CMV and is NOT
  subject to TxDMV motor-carrier registration — but Texas cities commonly license
  ground transportation (taxi/limousine/shuttle) at the **municipal** level (e.g.
  city vehicle-for-hire permits, airport ground-transportation permits/decals).
  These are city/airport-authority obligations, OUT of state/federal encoding scope
  — the product should say "check your city and any airport you serve." Noted.
- **Transportation Network Companies (TNCs / rideshare).** Regulated at the STATE
  level under Tex. Occupations Code ch. 2402 (a TxDMV permit for the TNC), but that
  is app-based rideshare, not a typical charter/event shuttle — noted for
  completeness, not encoded for this entity.
- **Texas workers' compensation — OPTIONAL (Notes only, not encoded).** Texas is
  the one state where private employers may **elect not to carry** workers'
  compensation insurance (Tex. Labor Code ch. 406; a non-subscriber files a
  DWC-005 notice). Whether a shuttle company carries WC is an employer/venue-org
  concern, not a per-vendor licensing/insurance document mandated for the activity —
  so it is noted, never encoded as a transportation obligation. (Labor Code
  §406.002 election text not fetched verbatim this session; optionality is
  well-established via TDI Division of Workers' Compensation.)
- **State vehicle inspection / registration, fuel tax (IFTA), IRP apportioned
  plates.** Operational/vehicle-level Texas obligations (IRP/IFTA apply mainly to
  interstate or heavy vehicles); not per-vendor credentials collected by a venue.
  Noted, not encoded.

## Open questions / review-queue candidates

1. **`statutes.capitol.texas.gov` serves a JavaScript SPA shell to automated
   fetch** — the direct `Docs/TN/htm/TN.643.htm` and `TN.522.htm` URLs returned the
   site navigation, not chapter text. All Texas statute quotes here (643.051,
   643.002, 548.001, 522.011, 522.003, 522.051) were read via the faithful
   `texas.public.law` mirror. A human with browser access should re-confirm these
   against the capitol primary (especially the §522.051 8-year CDL term and the
   §643.002 exemption list).
2. **SOS TAC Figure PDF (`sos.state.tx.us/texreg/.../202405997-1.pdf`) and the
   `txrules.elaws.us` §218.16 mirror could not be fetched** (403 / timeout). The
   §218.16(a) insurance amounts here are quoted from the primary TxDMV Handbook
   (May 2025); re-pin against the official §218.16(a) figure when reachable.
3. **`dps.texas.gov` refused connection (ECONNREFUSED)** — the intrastate medical
   certificate adoption (ch. 644 / 37 TAC ch. 4), the exact Texas medical variances,
   and the CDL P-endorsement issuance details were taken from search corroboration +
   DPS form references (CDL-5, CDL-37, MCS-9). OBL-004 is `probable` pending a DPS
   primary fetch.
4. **Non-expiring vs annual/biennial certificate boundary for event shuttles.** The
   Handbook bars a non-expiring certificate for carriers operating "non-charter
   buses." Whether a given venue-shuttle service is "charter" (event-hire, likely
   eligible for non-expiring) or "non-charter" (scheduled, must renew
   annually/biennially) is a service-type determination the engine should surface as
   a cadence branch. Confirm the precise TxDMV definition of "charter."
5. **≤15-passenger for-hire boundary.** Confirmed a ≤15-passenger (under 26,000 lb)
   for-hire van is outside TxDMV motor-carrier registration and TX §218.16 — its
   licensing is municipal. Worth a human confirm that no other TX state permit
   (e.g. a residual chauffeured-transportation or limousine registration) attaches;
   Texas repealed its separate state limousine/bus authority in favor of TxDMV MC
   registration, but confirm no successor state credential.
6. **43 TAC §218.13 (the original research lead) is the "Application for Motor
   Carrier Registration" section — the INSURANCE amounts live in §218.16.** Recorded
   so the lead isn't chased to the wrong section.
