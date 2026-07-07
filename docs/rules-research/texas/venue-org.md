**Status:** COMPLETE   <!-- COMPLETE only when every section is done -->

# Texas obligations — VENUE-ORG (Texas event venue business itself)

Entity: the event-venue business itself (CompliDrop's customer). A Texas event
venue that may serve/sell alcohol and food. Texas (state) jurisdiction only in
this file; federal obligations live in `../federal/venue-org.md`.

This file also carries two CANONICAL CROSS-CUTTING entries other entity files
reference:
- Texas workers' compensation status (Labor Code ch. 406) → OBL-TX-VENUE-001/002/003
- Texas sales & use tax permit (Tax Code ch. 151) → OBL-TX-VENUE-004

> **Sourcing note (read before trusting a citation):** the canonical Texas
> statute host `statutes.capitol.texas.gov` migrated to a client-rendered
> Angular SPA that serves only a JS shell to non-browser fetchers (curl and the
> research fetch tool both received the empty shell, not the statute text, for
> every `/Docs/**` path tried on 2026-07-07). Where a statutory sentence is
> quoted below, the verbatim text was obtained from an official **agency**
> reproduction (e.g. TDI's published Workers' Compensation Act PDF, a TABC page,
> a Comptroller page) — all acceptable primary sources under the methodology —
> and cross-checked against a secondary mirror (FindLaw/Justia) for currency.
> Statute sections I could quote only from a secondary mirror are marked
> `probable` and listed in the review queue.

---

### OBL-TX-VENUE-001: Workers' compensation coverage is ELECTIVE for private employers (non-subscriber status permitted)

- **Applies to:** Every private Texas employer, including an event venue, that
  has one or more employees. Texas is the only state where a private employer
  may legally decline workers' compensation insurance ("non-subscriber"). This
  entry records the *status finding* (coverage is generally NOT legally
  mandated); the document-shaped consequences of choosing non-subscription are
  OBL-TX-VENUE-002 (notice to the state) and OBL-TX-VENUE-003 (notice to
  employees).
- **Obligation:** None to *carry* coverage. Carrying workers' compensation
  insurance is optional. (If the venue DOES elect coverage, the resulting policy
  is a contractual/insurance artifact, not a state-mandated filing.)
- **Issuing/enforcing authority:** Texas Department of Insurance, Division of
  Workers' Compensation (DWC).
- **Cadence:** N/A — this is a standing legal status, not a periodic document.
- **Penalty for lapse:** None for declining coverage per se (it is lawful). A
  non-subscriber loses the common-law defenses (negligence, contributory
  negligence, assumption of risk, fellow-servant) in an employee injury suit
  (Labor Code §406.033) and takes on the notice duties in 002/003.
- **Category:** insurance
- **Basis:** regulatory
- **Citation:** Tex. Labor Code §406.002 (Coverage Generally Elective) —
  verbatim text from TDI's official published Texas Workers' Compensation Act,
  https://www.tdi.texas.gov/wc/act/documents/act81.pdf (p.123); currency
  cross-checked at https://codes.findlaw.com/tx/labor-code/lab-sect-406-002.html
  ("Current as of January 01, 2024", identical text).
- **Operative text:** "(a) Except for public employers and as otherwise provided
  by law, an employer may elect to obtain workers' compensation insurance
  coverage. (b) An employer who elects to obtain coverage is subject to this
  subtitle."
- **Effective date of cited text:** Acts 1993, 73rd Leg., ch. 269, §1, eff.
  Sept. 1, 1993; unchanged as of Jan. 1, 2024 (FindLaw currency label).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** WHO IS REQUIRED TO CARRY COVERAGE (the exceptions the venue engine
  should know about): (1) **Public employers** are excluded from the election
  (§406.002(a)). (2) A **contractor on a governmental building or construction
  contract** must certify in writing that it carries workers' comp for each
  employee on the public project — Labor Code §406.096(a): *"A governmental
  entity that enters into a building or construction contract shall require the
  contractor to certify in writing that the contractor provides workers'
  compensation insurance coverage for each employee of the contractor employed
  on the public project."* §406.096(d) clarifies that a **maintenance** employee
  of an employer whose primary business is not building/construction does not
  count — so an ordinary event venue is NOT swept in by §406.096 unless it takes
  a public construction contract. Both §406.096 quotes verbatim from the TDI Act
  PDF (p.139). Some commercial contracts and the venue's own insurers may
  *contractually* require coverage — that is contractual-noted, not a legal
  mandate.

### OBL-TX-VENUE-002: Non-subscriber notice to DWC (DWC Form-005)

- **Applies to:** Any Texas employer that does NOT carry workers' compensation
  insurance ("non-subscriber") AND has one or more employees who are not exempt
  from coverage. (Certain domestic, farm and ranch workers are exempt — if ALL
  the employer's employees are exempt, no filing.) Directly relevant to a venue
  that chooses non-subscription (OBL-TX-VENUE-001).
- **Obligation:** File **DWC Form-005 (Employer Notice of No Coverage or
  Termination of Coverage)** in writing with the Division. Document-shaped:
  the filed Form-005.
- **Issuing/enforcing authority:** TDI Division of Workers' Compensation.
- **Cadence:** Recurring. Per the form's own instructions, file: **within 30
  days of hiring your first employee**; within 10 days of terminating coverage;
  within 10 days of a DWC request; **and annually between February 1 and April
  30 of each calendar year** (a new Form-005 each year). Statute delegates the
  timing to commissioner rule (28 TAC §110.101); the Feb 1–Apr 30 annual window
  is stated on the official form.
- **Penalty for lapse:** Administrative violation. Labor Code §406.004(e): "An
  employer commits an administrative violation if the employer fails to comply
  with this section." (Administrative penalties up to the statutory cap under
  Labor Code ch. 415.)
- **Category:** filing
- **Basis:** regulatory
- **Citation:** Tex. Labor Code §406.004 (Employer Notice to Division) — verbatim
  from TDI Act PDF https://www.tdi.texas.gov/wc/act/documents/act81.pdf (p.123);
  cadence + who-must-file verbatim from the official DWC Form-005,
  https://www.tdi.texas.gov/forms/dwc/dwc005nocovst.pdf ; implementing rule 28
  TAC §110.101.
- **Operative text:** Statute — §406.004(a): "An employer who does not obtain
  workers' compensation insurance coverage shall notify the division in writing,
  in the time and as prescribed by commissioner rule, that the employer elects
  not to obtain coverage." Form-005 — "You must file this form if you are a
  non-subscriber and have one or more employees who are not exempt from workers'
  compensation coverage." / "When do I file the DWC Form-005? — Within 30 days
  of hiring your first employee. — Within 10 days of terminating your workers'
  compensation coverage. — Within 10 days of DWC asking you to file it. —
  Between February 1 and April 30 of each calendar year." / "I have already
  filed a DWC Form-005. Do I have to file another one? Yes. File a new DWC
  Form-005 between February 1 and April 30 of each calendar year."
- **Effective date of cited text:** §406.004 — Acts 1993, amended 2005; Form-005
  Rev. 01/25 (current form as displayed 2026-07-07).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The 28 TAC §110.101 rule text itself was not fetched live (the
  texreg host is fragile and capitol.texas.gov did not render); the annual
  Feb 1–Apr 30 cadence is quoted from the primary TDI form, which is
  authoritative for the filing window. A related but separate obligation sits on
  the *insurer*, not the employer: an insurance carrier must file notice of
  coverage with DWC within 10 days (Labor Code §406.006) — out of the venue's
  scope. Note the lead's hypothesis that "recent legislation changed" the cadence
  was NOT borne out: the statute has delegated timing to commissioner rule since
  2005 and the current form still states the Feb 1–Apr 30 annual window.

### OBL-TX-VENUE-003: Employer notice to employees of coverage status (posted + written)

- **Applies to:** Every Texas employer with employees — whether or not it carries
  workers' compensation — must tell its employees its coverage status. (This is
  not limited to non-subscribers, but it is most salient for them.)
- **Obligation:** (a) Notify each new employee of the existence or absence of
  coverage at time of hire; (b) **post** a notice of coverage status at
  conspicuous locations at the workplace, on the DWC-prescribed form; (c) notify
  each employee within 15 days when coverage is obtained, terminated, or
  cancelled. Document-shaped: the posted workplace notice (DWC "Notice to
  Employees Concerning Workers' Compensation in Texas") and the hire-time notice.
- **Issuing/enforcing authority:** TDI Division of Workers' Compensation.
- **Cadence:** Standing (posted continuously; revised when status changes);
  event-driven at hire and on any coverage change.
- **Penalty for lapse:** Administrative violation. §406.005(e): "An employer
  commits an administrative violation if the employer fails to comply with this
  section."
- **Category:** filing
- **Basis:** regulatory
- **Citation:** Tex. Labor Code §406.005 (Employer Notice to Employees;
  Administrative Violation) — verbatim from TDI Act PDF
  https://www.tdi.texas.gov/wc/act/documents/act81.pdf (p.123–124); the posted
  form is DWC Notice-6/Notice-10, https://www.tdi.texas.gov/forms/dwc/notice10.pdf
- **Operative text:** "(b) The employer shall notify a new employee of the
  existence or absence of workers' compensation insurance coverage at the time
  the employee is hired. (c) Each employer shall post a notice of whether the
  employer has workers' compensation insurance coverage at conspicuous locations
  at the employer's place of business as necessary to provide reasonable notice
  to the employees. The commissioner may adopt rules relating to the form and
  content of the notice. The employer shall revise the notice when the
  information contained in the notice is changed. (d) An employer who obtains
  workers' compensation insurance coverage or whose coverage is terminated or
  canceled shall notify each employee that the coverage has been obtained,
  terminated, or canceled not later than the 15th day after the date on which
  the coverage, or the termination or cancellation of the coverage, takes
  effect."
- **Effective date of cited text:** Acts 1993, 73rd Leg., ch. 269, §1; amended
  Acts 2005, 79th Leg., ch. 265, §3.024, eff. Sept. 1, 2005.
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** DWC publishes the exact posting language (English/Spanish). This is
  a workplace-posting/record obligation, encodable as a "notice on file" the
  venue should hold, but there is no periodic *filing to the state* here — it is
  a maintain-and-post duty.

### OBL-TX-VENUE-004: Texas sales & use tax permit  [CROSS-CUTTING]

- **Applies to:** Any person "engaged in business in Texas" who sells, leases or
  rents taxable tangible personal property, or sells taxable services, in Texas
  (and remote sellers over the $500,000 economic-nexus threshold). An event
  venue that sells taxable items/services (e.g. tangible goods, certain taxable
  services) needs one; the permit is also required broadly of the vendor entity
  types (caterers, rental companies) this file cross-references. NOTE: a mixed
  beverage permittee remits **mixed beverage gross receipts/sales tax** under
  Tax Code ch. 183 separately — see Notes.
- **Obligation:** Hold a Texas Sales and Use Tax Permit (one per location or a
  consolidated permit), obtained via the Comptroller's online registration.
- **Issuing/enforcing authority:** Texas Comptroller of Public Accounts.
- **Cadence:** One-time application; **no fee**. The permit has **no fixed
  expiration and no periodic renewal** — it remains valid while the holder is
  actively engaged in business as a seller; the Comptroller may cancel it if the
  holder ceases business. (Ongoing *tax-return* filing obligations — monthly/
  quarterly/annual per assigned frequency — continue while the permit is active,
  but the permit document itself is not renewed.)
- **Penalty for lapse:** Engaging in business as a seller without a permit is a
  misdemeanor and subjects the seller to penalties/interest on unremitted tax
  (Tax Code §151.703, §151.708); operating without a permit can draw Comptroller
  enforcement.
- **Category:** permit
- **Basis:** regulatory
- **Citation:** Tex. Tax Code ch. 151 (Limited Sales, Excise, and Use Tax);
  permit requirement §151.201–.202, no fee §151.203. Verbatim from Comptroller
  FAQ https://comptroller.texas.gov/taxes/sales/faq/permit.php and the
  registration requirements page
  https://comptroller.texas.gov/help/sales-tax-registration/requirements.php
- **Operative text:** Comptroller — "Who is required to hold a Texas sales and
  use tax permit? You must obtain a … permit if you: Sell tangible personal
  property in Texas; Lease or rent tangible personal property in Texas; Sell
  taxable services in Texas; Sell or lease tangible personal property or taxable
  services to customers in Texas from an out-of-state business and have revenue
  from Texas of $500,000 or more in the past 12 months." / "Is there a fee for a
  permit? There is no fee for the permit, but you may be required to post a
  security bond." / "Your permit is valid only if you are actively engaged in
  business as a seller." / "the Comptroller's office may cancel your permit if it
  finds that you are no longer engaged in business as a seller."
- **Effective date of cited text:** Current as displayed 2026-07-07.
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The non-expiration cadence is now confirmed verbatim from the
  Comptroller FAQ (the permit is valid while the holder is "actively engaged in
  business as a seller"; the Comptroller may cancel it if the holder stops — no
  set term, no periodic renewal, no renewal fee). Cross-cutting:
  reference this entry from caterer/event-rental/other vendor files rather than
  duplicating. Distinct from the **mixed beverage** taxes (Tax Code ch. 183:
  6.7% gross receipts tax on the permittee + 8.25% sales tax on the customer)
  that a venue holding an MB permit also collects/remits — those are tax filings,
  not a separate permit, and can be added later if the engine needs them.

### OBL-TX-VENUE-005: Texas franchise tax annual report (+ Public/Ownership Information Report)

- **Applies to:** Every "taxable entity" formed or doing business in Texas —
  LLCs, corporations, LPs, PLLCs, professional associations, most partnerships
  with entity partners. **Sole proprietorships and general partnerships owned
  entirely by natural persons are NOT taxable entities** (they file nothing).
  So a venue organized as an LLC/corporation is in scope; a sole-prop venue is
  not.
- **Obligation:** File the annual Texas Franchise Tax Report. Even entities whose
  annualized revenue is **at or below the no-tax-due threshold owe no tax and
  (for 2024+) no longer file a "No Tax Due Report," but must still file the
  annual information report** — the Public Information Report (PIR, for corps/
  LLCs) or Ownership Information Report (OIR, for other entities).
- **Issuing/enforcing authority:** Texas Comptroller of Public Accounts.
- **Cadence:** Annual. **Due May 15** each year (next business day if May 15 is a
  weekend/holiday).
- **Penalty for lapse:** $50 late-filing penalty per report + 5% (then additional
  5%) of tax due if applicable, plus interest; continued non-filing leads to loss
  of the entity's right to transact business and forfeiture of corporate
  privileges/charter (Tax Code §171.251–.252, §171.362).
- **Category:** filing
- **Basis:** regulatory
- **Citation:** Tex. Tax Code ch. 171 (Franchise Tax). Verbatim from Comptroller
  https://comptroller.texas.gov/taxes/franchise/
- **Operative text:** "The annual franchise tax report is due May 15. If May 15
  falls on a weekend or holiday, the due date will be the next business day." /
  no-tax-due threshold: **"$2,470,000"** for report years **2024 and 2025** and
  **"$2,650,000"** for report years **2026 and 2027**; for entities "at or below
  the no tax due threshold, simply file your … Public Information Report or
  Ownership Report."
- **Effective date of cited text:** Threshold figures current as displayed
  2026-07-07 (report years 2024–2027).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The lead's caution about a stale threshold is correct and handled:
  the threshold is **$2.47M (2024–25) / $2.65M (2026–27)**, NOT the old
  $1.23M/$1.18M figures, and Texas **eliminated the standalone No Tax Due Report
  for 2024 and later** (below-threshold entities now file only the PIR/OIR). For
  the CURRENT (2026) report year the document-shaped deliverable for a typical
  small venue below $2.65M is the **Public Information Report**, due May 15.

### OBL-TX-VENUE-006: TABC Mixed Beverage Permit (MB)

- **Applies to:** A venue that sells/serves **distilled spirits** (liquor/mixed
  drinks) for on-premises consumption — the typical permit for a full-bar event
  venue. One of the two most likely venue permits (the other is BG, 007).
- **Obligation:** Hold a TABC Mixed Beverage Permit (MB). Authorizes on-premise
  sale of distilled spirits, wine and malt beverages, including holding events at
  a temporary location away from the primary premises.
- **Issuing/enforcing authority:** Texas Alcoholic Beverage Commission (TABC).
- **Cadence:** **Two-year term** — the permit "expires on the second anniversary
  of the date it is issued." Renew via TABC's AIMS: up to 30 days before
  expiration, or up to 30 days after by paying a late fee; the business must stop
  licensed activities after expiration unless a renewal application with fees is
  pending. (TABC may set a shorter/1-year term for violation history or workload
  proration — Alco. Bev. Code §11.09(d)–(e).)
- **Penalty for lapse:** Selling alcohol on an expired/lapsed permit is unlawful;
  the business must cease alcohol sales after expiration (subject to the pending-
  renewal grace). Operating without a valid permit exposes the venue to
  administrative penalties, permit cancellation, and criminal liability under the
  Alcoholic Beverage Code.
- **Category:** permit
- **Basis:** regulatory
- **Citation:** Tex. Alco. Bev. Code ch. 28 (Mixed Beverage Permit); term §11.09.
  Authorization text verbatim from TABC
  https://www.tabc.texas.gov/services/tabc-licenses-permits/tabc-license-permit-types/ ;
  two-year term verbatim from TABC FAQ
  https://www.tabc.texas.gov/faqs/tabc-license-permit-faqs/ ; renewal window from
  https://www.tabc.texas.gov/services/tabc-licenses-permits/tabc-license-permit-renewals/ ;
  §11.09(a) text corroborated at
  https://codes.findlaw.com/tx/alcoholic-beverage-code/alco-bev-sect-11-09/
- **Operative text:** Authorization (TABC) — "Authorizes the sale of distilled
  spirits, wine and malt beverages for on-premise consumption. It includes
  authority to transport alcoholic beverages from the place of purchase to the
  MB's licensed premises, provide guestroom minibars (hotels), and hold events at
  a temporary location away from the primary MB premises." Term (TABC FAQ) — "A
  license or permit is good for two years. It expires on the second anniversary
  of the date it is issued." Renewal (TABC) — "You may renew your license up to
  30 days before the license expiration date. A business may also renew up to 30
  days after the expiration date by paying a late fee." / "a business must stop
  licensed activities after the expiration date unless a renewal application with
  fees is pending with TABC." Statute (§11.09(a), FindLaw) — "A permit issued
  under this code expires on the second anniversary of the date it is issued,
  except as provided by Subsections (d) and (e) or another provision of this
  code."
- **Effective date of cited text:** TABC pages current as displayed 2026-07-07.
  The two-year term originated in HB 1545 (86th Leg., 2019 — the TABC Sunset
  bill), phased in on renewals (see Notes).
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** The lead's "2021 HB 1545" is a date correction: **HB 1545 passed in
  2019 (86th Legislature)**; the consolidated permit structure and two-year terms
  took practical effect as licenses came up for renewal on/after **Sept. 1, 2021**
  (hence the "2021" association). The §11.09(a) two-year sentence is quoted from
  FindLaw (secondary) because capitol.texas.gov would not render; the *same
  two-year fact* is independently verbatim from the primary TABC FAQ, so the rule
  is `verified`. Modern TABC issues the MB with an accompanying **Food and
  Beverage Certificate (FB)** in many cases; certificate/subordinate permits
  expire with the primary permit (§11.09(b)). A venue also needs TABC approval
  tied to local wet/dry status and, in practice, a signed original application
  with local certifications.

### OBL-TX-VENUE-007: TABC Wine and Malt Beverage Retailer's Permit (BG)

- **Applies to:** A venue that sells/serves **wine and malt beverages (beer)**
  but not distilled spirits — the lighter-weight alternative to the MB permit for
  a beer-and-wine-only event venue.
- **Obligation:** Hold a TABC Wine and Malt Beverage Retailer's Permit (BG).
  Authorizes on- and off-premise sale of wine and malt beverages, including
  holding events at a temporary location away from the primary premises.
- **Issuing/enforcing authority:** Texas Alcoholic Beverage Commission (TABC).
- **Cadence:** **Two-year term** ("expires on the second anniversary of the date
  it is issued"); same AIMS renewal window as MB (30 days before / 30 days after
  with late fee; stop activities after expiration absent a pending renewal).
- **Penalty for lapse:** Same as MB — unlawful to sell alcohol on a lapsed
  permit; administrative penalties, cancellation, criminal exposure.
- **Category:** permit
- **Basis:** regulatory
- **Citation:** Tex. Alco. Bev. Code ch. 25 (Wine and Malt Beverage Retailer's
  Permit); term §11.09. Authorization verbatim from TABC
  https://www.tabc.texas.gov/services/tabc-licenses-permits/tabc-license-permit-types/ ;
  term verbatim from TABC FAQ
  https://www.tabc.texas.gov/faqs/tabc-license-permit-faqs/
- **Operative text:** Authorization (TABC) — "Authorizes the sale of wine and
  malt beverages for on- and off- premise consumption. It also includes authority
  to hold events at a temporary location away from the primary BG premises."
  Term (TABC FAQ) — "A license or permit is good for two years. It expires on the
  second anniversary of the date it is issued."
- **Effective date of cited text:** Current as displayed 2026-07-07.
- **Verified:** 2026-07-07 | **Confidence:** verified
- **Notes:** HB 1545 (2019) consolidated the old beer ("BF") and wine/beer
  retailer permits and merged "beer" + "ale" into a single **malt beverage**
  category, which is why the modern permit is styled "Wine and Malt Beverage
  Retailer's Permit (BG)." A venue holds EITHER an MB (spirits) OR a BG (wine/
  beer only), not both, for the same premises — the engine should treat these as
  mutually-exclusive alternatives keyed to what the venue serves.

### OBL-TX-VENUE-008: Retail food establishment permit (if the venue prepares/serves food)

- **Applies to:** A venue that prepares, stores, serves, or sells food to the
  public (in-house catering, a prep kitchen, plated service). A venue that only
  rents space and lets outside caterers bring food generally does NOT itself need
  this permit (the caterer does — see caterer file). JURISDICTION SPLIT: whether
  the permit comes from the **state (DSHS)** or a **local (city/county) health
  authority** depends on location — see Notes.
- **Obligation:** Hold a Retail Food Establishment permit from the health
  authority with jurisdiction, operating under the Texas Food Establishment Rules
  (TFER).
- **Issuing/enforcing authority:** Texas DSHS **where no local health department
  has jurisdiction**; otherwise the city or county health department (which
  issues most food permits in populated areas).
- **Cadence:** Annual in practice (DSHS retail food permits are issued for a
  ~1-year term and renewed; local terms vary). NOT verified verbatim here — see
  Notes and review queue.
- **Penalty for lapse:** Operating a food establishment without a valid permit is
  prohibited and subject to enforcement (permit suspension/revocation, penalties)
  under H&S Code ch. 437 and local ordinance.
- **Category:** permit
- **Basis:** regulatory
- **Citation:** Tex. Health & Safety Code ch. 437 (Regulation of Food Service
  Establishments…); Texas Food Establishment Rules, 25 TAC ch. 228. Agency hub:
  https://www.dshs.texas.gov/retail-food-establishments/permitting-information-retail-food-establishments/starting-a-new-retail
  and https://www.dshs.texas.gov/retail-food-establishments/statutes-laws-retail-food-establishments
- **Operative text:** *Not captured verbatim.* The two DSHS pages returned an
  HTTP 403 / navigation-only shell on 2026-07-07, and H&S Code §437 / 25 TAC 228
  sit on the unrenderable capitol.texas.gov + fragile texreg hosts. The
  jurisdiction split and statutory framework are corroborated by DSHS index
  pages and the search index (H&S ch. 437 "allows counties and cities with health
  departments as well as the state to issue permits, conduct inspections, and
  take enforcement actions").
- **Effective date of cited text:** 25 TAC 228 / TFER 2021 edition is the current
  ruleset.
- **Verified:** 2026-07-07 | **Confidence:** probable
- **Notes:** Downgraded to `probable` because no primary operative sentence could
  be fetched (DSHS 403; statute hosts unrenderable). The important, reliable
  finding for the product: **this permit is usually LOCAL** — an SMB venue should
  be told to check its city/county health department first, with DSHS as the
  fallback authority only where there is no local health jurisdiction. A
  **Certified Food Manager** credential and **Food Handler** cards (per-worker,
  H&S ch. 437 subch. G/H; 25 TAC 228 Subchapter K) attach to food-preparing
  staff — those are per-worker certifications better captured in the caterer
  file; flagged for cross-reference.

---

## Local-level obligations (noted, not encoded)

These are real obligations for a Texas event venue but are **municipal/county**,
outside the federal+state encoding scope. Surface them as "check your
city/county," never as satisfied/complete:

- **Certificate of Occupancy (CO):** Issued by the city (building/permitting
  dept). Required before occupying/operating the space; assembly occupancies
  (event venues) get an occupant-load limit. No statewide CO for private venues
  in Texas — it is local.
- **Fire marshal inspection / assembly permit / occupant-load posting:** Local
  fire marshal (city or county). Assembly occupancies are inspected for exits,
  capacity, extinguishers, alarms. Texas has a **State Fire Marshal** (within TDI)
  but routine inspection of private assembly venues is delegated to local
  authorities; the state does not issue a per-venue operating permit. Verify
  locally.
- **Retail food establishment permit (city/county health dept):** As in
  OBL-TX-VENUE-008, most food permits are issued and inspected locally.
- **Certified Food Manager registration (local):** Some jurisdictions register
  CFMs locally in addition to the state accreditation.
- **Local alcohol certifications on the TABC application:** TABC requires local
  official sign-offs (city secretary/county) and conformance with local wet/dry
  status and hours; the venue's TABC packet includes these local certifications.
- **Noise / amplified-sound permit, special-event permit, parking/valet, sign
  permit:** Commonly municipal for venues; verify locally.
- **Local health permit for temporary food events** (if the venue hosts one-off
  food events): typically a city/county temporary food event permit.

## Open questions / review-queue candidates

Sources that could not be fetched live, or facts needing a crisper primary quote:

1. **statutes.capitol.texas.gov is unrenderable to fetchers (2026-07-07).** The
   site is a client-rendered Angular SPA; curl and the fetch tool receive only a
   250 KB JS shell for every `/Docs/**/*.htm` and `/Docs/SDocs/*.pdf` path. All
   Texas statute *text* quoted here came from agency reproductions or secondary
   mirrors. A future run with a headless browser (or the SPA's backing API) could
   upgrade the statutory quotes. Affected: Alco. Bev. Code §11.09 & chs. 25/28,
   Tax Code chs. 151/171, H&S Code ch. 437, Labor Code ch. 406 (the last fully
   mitigated via the TDI Act PDF).
2. **28 TAC §110.101** (DWC-005 rule) — cadence quoted from the primary TDI form,
   not the rule text; texreg host not fetched. Low risk (form is authoritative).
3. **Sales tax permit non-expiration — RESOLVED 2026-07-07.** The Comptroller
   FAQ verbatim ("Your permit is valid only if you are actively engaged in
   business as a seller"; "the Comptroller's office may cancel your permit if it
   finds that you are no longer engaged in business as a seller") confirms no
   fixed term / no renewal. Entry 004 is `verified`.
4. **Food establishment permit (OBL-TX-VENUE-008)** — DSHS pages 403'd / were
   nav-only; no verbatim permit-requirement sentence or exact term/fee could be
   captured. DSHS does publish a "Texas Retail Food Establishments Jurisdiction
   Interactive Map" (confirms the state-vs-local split exists). Still needed for
   a `verified` upgrade: DSHS permit term (believed ~1 year) and fee, plus a
   verbatim permit-required + "no local health authority → DSHS" sentence
   (H&S §437.003/437.0055 or 25 TAC 228). Currently `probable`.
5. **Mixed beverage gross receipts tax (Tax Code ch. 183)** — noted under 004 but
   not written as its own filing entry; add if the engine needs venue tax filings
   beyond the sales/use and franchise reports.
6. **TABC Food & Beverage Certificate (FB)** and other subordinate permits —
   confirm whether to encode separately or as attributes of the MB/BG term.
