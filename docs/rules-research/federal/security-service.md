**Status:** COMPLETE   <!-- COMPLETE only when every section is done -->

# Federal regulatory obligations — SECURITY SERVICE (contract security / guard companies working events)

Entity type: `security-service`. Scope: contract security / guard companies that
supply watchmen, guards, or event security on a contractual basis to a private
party (e.g. an event venue). Jurisdiction level: **federal**. United States only.

**Headline finding: there is no federal license, permit, per-worker
certification, or federally-mandated insurance for an ordinary private contract
security / guard company doing event security.** Private security is regulated at
the STATE level (for Texas, see `docs/rules-research/texas/security-service.md`).
The entries below are sourced ABSENCE findings, plus scoped-out federal
touchpoints that are noted (not encoded) so the engine can say "no federal
obligation" rather than "not researched."

> Source-fetch note: `bls.gov` (the Occupational Outlook Handbook, the cleanest
> federal statement that guard licensing is a state matter) **returned HTTP 403
> to the fetcher on every attempt**, and `web.archive.org` was also unreachable
> from this tool. The BLS wording quoted below is therefore **search-surfaced,
> not live-fetched**, so the federal-absence entries are capped at `probable`
> under the methodology's quote-the-text rule. The absence is independently
> corroborated by U.S. DOJ / Office of Justice Programs research data (also
> search-surfaced). Nothing here is fabricated; where a live primary quote could
> not be obtained the confidence is lowered accordingly.

---

### OBL-FED-SECURITY-001: No federal business license or permit for contract guard companies

- **Applies to:** Every private contract security / guard company (including one
  supplying event security). Does NOT cover the niche of contract guards
  protecting *federal* facilities under a DHS Federal Protective Service contract
  (see Notes) — that is a procurement-contract regime, not a business license.
- **Obligation:** **None at the federal level.** No federal agency issues, and no
  federal statute or regulation requires, a business license or operating permit
  to run a private security / guard company. Licensing is a matter of state (and
  sometimes local) law.
- **Issuing/enforcing authority:** N/A federally. (State authority for Texas is
  DPS under Occupations Code ch. 1702.)
- **Cadence:** N/A (no federal credential to obtain or renew).
- **Penalty for lapse:** N/A federally.
- **Category:** license
- **Basis:** regulatory (absence finding)
- **Citation:** U.S. Bureau of Labor Statistics, Occupational Outlook Handbook,
  "Security Guards and Gambling Surveillance Officers,"
  https://www.bls.gov/ooh/protective-service/security-guards.htm (fetch blocked —
  see note); corroborated by U.S. DOJ Office of Justice Programs / NCJRS,
  "Licensing and the Regulation of Private Security,"
  https://www.ojp.gov/ncjrs/virtual-library/abstracts/licensing-and-regulation-private-security
- **Operative text:** BLS OOH (search-surfaced, not live-fetched): "Most states
  require that security guards be licensed... although licensing requirements vary
  by state... Guards who carry weapons usually must be licensed by the appropriate
  government authority." DOJ/OJP-sourced data (search-surfaced): "All but nine
  states require private security companies to be licensed... 35 States require
  licensing of private security agencies or businesses." No federal licensing
  scheme is identified in either source.
- **Effective date of cited text:** BLS OOH current edition as displayed (2024–34
  projection cycle referenced in the same page); not live-fetched.
- **Verified:** 2026-07-07 | **Confidence:** probable
- **Notes:** The confidence is `probable` ONLY because the BLS/archive pages could
  not be fetched live this session, not because the absence is in doubt — no U.S.
  Code chapter or CFR part licenses private security guard companies. The finding
  is that federal law imposes NO business license/permit on a private guard
  company; the operative obligations live in state law (Texas: ch. 1702).

---

### OBL-FED-SECURITY-002: No federal per-worker certification for private event guards

- **Applies to:** Individual security officers/guards employed by a private
  contract security company working private events (armed or unarmed).
- **Obligation:** **None at the federal level.** There is no federal license,
  registration, commission, or certification an individual must hold to work as a
  private security guard at a private event. Individual credentialing is a state
  matter (Texas: individual license / security officer commission / PPO license
  under ch. 1702).
- **Issuing/enforcing authority:** N/A federally.
- **Cadence:** N/A.
- **Penalty for lapse:** N/A federally.
- **Category:** worker-certification
- **Basis:** regulatory (absence finding)
- **Citation:** Same sources as OBL-FED-SECURITY-001 (BLS OOH; DOJ/OJP).
- **Operative text:** BLS OOH (search-surfaced): licensing of guards, including
  the more stringent requirements for armed guards, is imposed by the states, not
  a federal authority ("Guards who carry weapons usually must be licensed by the
  appropriate government authority").
- **Effective date of cited text:** As displayed; not live-fetched.
- **Verified:** 2026-07-07 | **Confidence:** probable
- **Notes:** Do NOT invent a federal armed-guard credential. An armed guard is
  subject to the SAME general federal firearms law as any individual (Gun Control
  Act, 18 U.S.C. ch. 44; ATF), but that law imposes no guard-specific federal
  license and confers no guard-specific firearm privilege (e.g. no interstate
  carry authority by virtue of guard status). See Notes section below.

---

### OBL-FED-SECURITY-003: No federally-mandated insurance for guard companies

- **Applies to:** Private contract security / guard companies (event security).
- **Obligation:** **None at the federal level.** No federal statute or regulation
  mandates that a private security / guard company carry liability (or other)
  insurance. The only *law*-mandated insurance for a Texas guard company is the
  state general-liability requirement (Tex. Occ. Code § 1702.124 — see the Texas
  dossier, OBL-TX-SECURITY-003). Any higher coverage a venue demands is
  contractual, not federal.
- **Issuing/enforcing authority:** N/A federally.
- **Cadence:** N/A.
- **Penalty for lapse:** N/A federally.
- **Category:** insurance
- **Basis:** regulatory (absence finding)
- **Citation:** Absence — no U.S. Code / CFR insurance mandate for private guard
  companies. State requirement cross-ref: Tex. Occ. Code § 1702.124.
- **Operative text:** N/A (absence; no federal instrument to quote).
- **Effective date of cited text:** N/A.
- **Verified:** 2026-07-07 | **Confidence:** probable
- **Notes:** Distinguish law-mandated (Texas § 1702.124) from contractual COI
  requirements. Federal law adds nothing here for a private guard company.

---

## Local-level obligations (noted, not encoded)

These federal touchpoints exist but are OUT of scope for a private contract guard
company doing event security — noted so the engine does not treat them as
applicable, and so a user in an edge case is pointed to the right regime:

- **Federal Protective Service (FPS) Protective Security Officers (PSOs).**
  Contract guards who protect *federal* buildings/property work under DHS/FPS
  contracts with their own federal training and suitability requirements. This is
  a federal-procurement regime for guarding federal facilities — **not** event
  security for private venues. Not applicable to CompliDrop's entity; noted only.
- **General federal firearms law for armed guards.** An armed security officer
  possessing/carrying a firearm is subject to the Gun Control Act (18 U.S.C.
  ch. 44) and ATF regulations (27 CFR) like any person — e.g. prohibited-person
  rules, background checks on firearm *purchases*. There is **no** federal
  armed-guard license or federal firearm carry credential tied to guard status;
  the operative armed-guard credential is the STATE security officer commission
  (Texas § 1702.161). Do not encode any federal firearm credential for guards.
- **OSHA (workplace safety).** The employer owes a safe workplace under the OSH
  Act General Duty Clause and applicable standards, but that is not a license,
  permit, per-worker certification, or insurance — it is outside the four
  encoded categories. Noted for context only.
- **TWIC (Transportation Worker Identification Credential).** A federal credential
  required only for unescorted access to secure areas of maritime/port facilities
  and vessels (MTSA). Not triggered by event security at a private venue. Noted.
- **Workers' compensation / labor law (FLSA, NLRA).** Federal wage/hour and labor
  law applies to the employment relationship but is outside the license/permit/
  certification/insurance encoding scope. (Texas workers' comp is *optional* under
  Labor Code ch. 406 and is the employer's/venue-org's concern — see venue-org.)

## Open questions / review-queue candidates

- **Could not fetch live:** `bls.gov` Occupational Outlook Handbook "Security
  Guards" page (HTTP 403 on every attempt, incl. `?view=full` and `#tab-4`
  variants) — this is the cleanest federal source stating guard licensing is a
  state matter; its wording above is search-surfaced, not live-fetched.
  `web.archive.org` was also unreachable from the fetch tool, so an archived BLS
  snapshot could not substitute. `ojp.gov` returned only site navigation, not the
  NCJRS abstract text. Because of these, the federal-absence entries are `probable`
  rather than `verified` — re-run the BLS fetch from an unblocked context to
  upgrade. The underlying absence (no federal guard-company license) is not in
  genuine doubt.
- **FPS PSO edge case:** if CompliDrop ever needs to model a security vendor
  guarding a *federal* facility, the FPS contract training/suitability regime
  would need its own research pass. Out of scope for event venues today.
