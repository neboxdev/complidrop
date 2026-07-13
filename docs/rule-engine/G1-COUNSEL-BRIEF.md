# CompliDrop — Legal & Insurance Confirmation Gate

**This is THE single go-live gate.** Nothing in CompliDrop that asserts a legal or
insurance conclusion to a customer goes live until the items in the **Master
Confirmation Checklist (§0)** are signed off by a licensed **Texas attorney** and —
for the insurance-specific items — a licensed **Texas insurance broker**. It spans
three surfaces:

- **A — Regulatory rule engine** (flag-off feature: "which laws apply to you").
- **B — Live vendor checklists & insurance defaults** (the seeded templates venues
  use today, and the dollar minimums they carry).
- **C — Marketing & exported-artifact claims** (what the site and the PDF assert).

> **Working method (founder philosophy, 2026-07-10).** An AI system (Fable / Claude)
> performs the deep, primary-source research an attorney or broker would do, and we
> **implement on that research as our working basis.** But research is not
> authorization: every item a professional should confirm is captured in this one
> document, and the corresponding surface stays **OFF / gated** until sign-off.
> *Do the deep research → record what a professional must confirm → ship only after
> they do.*

> **This document is non-attorney, non-broker work product. It is not legal advice
> and not insurance advice.** It exists to be *validated* by professionals, not to
> substitute for them. The detailed research it points to:
> [G1-LEGAL-RESEARCH.md](G1-LEGAL-RESEARCH.md) (rule engine) and
> [TEMPLATE-REQUIREMENTS-REVIEW.md](TEMPLATE-REQUIREMENTS-REVIEW.md) (templates &
> insurance) — both carry per-claim citations and a confidence tier; only
> "verified-primary" claims are treated as settled research, and even those need
> professional confirmation before go-live.

---

## §0. Master confirmation checklist

Every item a professional must confirm before the surface it gates can be shown to a
real customer. **Confirm by:** `A` = licensed TX attorney, `B` = licensed TX
insurance/hospitality broker. **Status:** ⬜ researched (Fable, cited) — pending
confirmation · ✅ confirmed. **Gate:** launch-blocking unless marked *(refine later)*.

| ID | Item to confirm | By | Gates | Status | Detail |
|---|---|---|---|---|---|
| **RE-1** | Does presenting sourced, per-obligation regulatory requirements constitute/approach UPL or negligent-misrepresentation exposure in TX, and what disclaimer language/placement is required? | A | Rule engine | ⬜ | §A.3 Q1 |
| **RE-2** | Does the "head start, not advice" ToS clause need amendment/supplement for the rule engine? (draft in §A.4) | A | Rule engine | ⬜ | §A.3 Q2 |
| **RE-3** | Are the status labels acceptable — esp. `below-stated-minimum`, `missing`? | A | Rule engine | ⬜ | §A.3 Q3 |
| **RE-4** | May statutory penalty text appear in obligation rationale, and with what framing constraints? | A | Rule engine | ⬜ | §A.3 Q4 |
| **RE-5** | Currency/staleness Terms language; carry E&O insurance before enabling? | A | Rule engine | ⬜ | §A.3 Q5 |
| **TPL-A1** | Dram-shop reliance: does a venue with no TABC permit / no service role face Ch. 2 "provider" exposure when its caterer holds the permit? Does *requiring & archiving* vendor permits/COIs create assumed-duty / negligent-undertaking exposure for venues **or for CompliDrop**? | A | Templates | ⬜ | §B, TRR §8 A-1 |
| **TPL-A2** | Do the checklist verdict words ("Covered", "Compliant", "Certificate indicates…") hold the §81.101(c) posture now that checklists carry **dollar minimums** a venue may read as advice? | A | Templates | ⬜ | §B, TRR §8 A-2 |
| **TPL-A3** | May we ship **default dollar minimums** ($1M/$1.5M etc.) as editable *suggestions* without them being construed as insurance advice / UPL-adjacent? What disclaimer placement do template descriptions need? | A | Template dollar minimums | ⬜ | §B, TRR §8 A-3 |
| **TPL-A4** | The workers-comp rule vs TX non-subscribers: any problem with a default *contractual* demand for WC, and what red-badge copy avoids implying a legal violation? | A | Caterer WC rule | ⬜ | §B, TRR §8 A-4 |
| **TPL-B1** | Confirm the default limit set (GL $1M/occ + $2M agg; liquor $1M/occ; auto $1.5M small / $5M 16+; guard GL $1M) against what the 2026 TX wedding-venue market actually writes into vendor packets. | B | Template dollar minimums | ⬜ | §B, TRR §8 B-1 |
| **TPL-B2** | Guard A&B: how is it written in TX (excluded/sublimited/full)? What written evidence should a venue demand? Is $1M GL meaningful without an A&B confirmation? | B | Security template | ⬜ | §B, TRR §8 B-2 |
| **TPL-B3** | What do TX venues accept from lawful **non-subscriber** vendors instead of WC (occ-accident + indemnity/waiver)? | B | Caterer WC rule | ⬜ | §B, TRR §8 B-3 |
| **TPL-B4** | For one-day vendors, does AI status need **completed-operations** (CG 20 37-class) or is ongoing-ops enough? | B | AI guidance | ⬜ *(refine later)* | TRR §8 B-4 |
| **TPL-B5** | How do venues verify the **event vehicle** is on the shuttle policy (scheduled autos vs a generic CSL)? | B | Transport template | ⬜ *(refine later)* | TRR §8 B-5 |
| **TPL-B6** | Is a Damage-to-Rented-Premises minimum worth demanding, or is it better handled by contract/deposit? | B | Rental template | ⬜ *(refine later)* | TRR §8 B-6 |
| **TPL-B7** | Sanity-check dropping photographer **E&O** from the default — do any TX venue packets require it? | B | Photographer template | ⬜ *(refine later)* | TRR §8 B-7 |
| **TPL-B8** | How often does bar-service **liquor liability** appear on the main ACORD 25 vs a separate certificate? (calibrates the liquor rule's fail-closed rate) | B | Liquor rule tuning | ⬜ *(refine later)* | TRR §8 B-8 |
| **CLM-1** | Additional-insured copy: reword "Names you as additional insured" → "Certificate indicates…"; the certificate cannot prove AI status (endorsement needed). | A | Marketing + UI (#396) | ⬜ | §C, TRR §3/§7 |
| **CLM-2** | "Coverage dates that include the event" is currently unbacked (no event-date check exists); soften until the feature is built. | A | Marketing (#399 tier b) | ⬜ | §C, TRR §2.6/§7 |
| **CLM-3** | Exported audit PDF / vendor package carry no disclaimer while printing bare "Compliant". | A | Export (#402) | ⬜ | §C |
| **CLM-4** | FAQ "We don't sell or share your data" vs the 7 disclosed subprocessors (document contents to Google). | A | Marketing/privacy (#403) | ⬜ | §C |
| **CLM-5** | Public vendor portal has no privacy notice (CCPA notice-at-collection). | A | Portal (#404) | ⬜ | §C |
| **CLM-6** | "Documents not used to train AI models" holds only on the Vertex path; AI Studio + Anthropic are config-reachable and Anthropic isn't a disclosed subprocessor. | A | Privacy (#405) | ⬜ | §C |

**Launch-blocking:** RE-1..RE-5 (rule engine), TPL-A1..A4 + TPL-B1..B3 (templates &
dollar minimums), CLM-1..CLM-6 (copy/privacy). *Refine-later:* TPL-B4..B8.
`TRR` = [TEMPLATE-REQUIREMENTS-REVIEW.md](TEMPLATE-REQUIREMENTS-REVIEW.md).

---

# Part A — Regulatory rule engine

## A.1 What CompliDrop is, and what is changing

CompliDrop (complidrop.com) is a $49/mo SaaS for small event venues in Texas. As
shipped today it is a *document tracker*: a customer defines their own vendor
requirements ("caterers must upload a COI with $1M general liability"), vendors
upload documents, automated extraction reads them, and the product flags whether
the document appears to meet **the customer's own checklist**. The Terms of
Service frame this as:

> "**Automatic reading is a head start, not advice.** CompliDrop uses automated
> tools to read documents and flag whether they appear to meet your
> requirements. This is a convenience to save you time — it is **not** legal,
> insurance, or professional advice, and we do not guarantee that every
> extracted value or compliance result is accurate or complete. You are
> responsible for reviewing and confirming the results and for your own
> compliance decisions."

**The rule-engine feature is materially different.** A "regulatory rule engine"
tells the customer **which legal obligations apply to them and their vendors** —
e.g. "a Texas guard company must hold a DPS security-contractor license
(Tex. Occ. Code §1702.102) and carry general-liability insurance with minimum
limits of $100,000/$50,000/$200,000 (§1702.124(c)); your vendor's certificate is
missing / on file / below the stated minimum." The product moves from *"we read
your documents against YOUR rules"* to *"we assert which laws apply to you"* —
a larger reliance and unauthorized-practice-of-law (UPL) surface that the
current Terms clause was not written for.

**Current exposure status: NONE.** The feature is merged but inert — behind a
feature flag that defaults OFF, with no user interface or API endpoint. Nothing
ships to a customer until RE-1..RE-5 are confirmed.

## A.2 What the engine actually outputs (the framing discipline already built in)

Three requirements from our internal legal-review pass are enforced in the code
structure itself (each pinned by an automated test):

1. **No overall "compliant" verdict exists.** The report type has no
   is-compliant boolean; output is a list of per-obligation statuses
   (`satisfied / expiring / expired / missing / below-stated-minimum /
   needs-profile-info / needs-document-info / not-applicable`) plus a
   **mandatory non-exhaustiveness notice** on every report:

   > "This report lists only the regulatory obligations CompliDrop tracks for
   > the profile and documents you provided. It is not a complete list of your
   > legal obligations and is not legal advice. Local requirements (for example
   > city and county health, food, fire, occupancy, and per-event permits) and
   > other rules may also apply — check with your local authorities and a
   > qualified professional."

2. **Every rule states what the law requires and cites it, never adjudicating
   the user.** Statuses describe the DOCUMENT record ("no certificate on file"),
   not the entity's conduct ("you are operating illegally").

3. **Penalty text is statutory-general** — never paired with an assertion that
   THIS user is committing an offense.

Every encoded rule carries a citation (section + URL + verified date) to a US
primary source, a confidence tier (only "verified" ships), and a verbatim quote
of the operative statutory text in the research dossier.

## A.3 The questions for counsel (RE-1..RE-5)

1. **UPL / reliance (RE-1):** Does presenting sourced, per-obligation regulatory
   requirements (with the framing discipline in §A.2) constitute or approach the
   unauthorized practice of law in Texas, or create negligent-misrepresentation /
   detrimental-reliance exposure — and what disclaimer language, placement, and
   prominence would you require before launch?
2. **Terms of Service (RE-2):** Does the existing "head start, not advice" clause
   need to be amended or supplemented? Draft addition in §A.4 for markup.
3. **Status wording (RE-3):** Are the status labels acceptable, particularly
   `below-stated-minimum` and `missing` ("no document on record", not "you are
   violating the law")?
4. **Penalty text (RE-4):** May statutory penalty references appear in the
   obligation rationale, and with what framing constraints?
5. **E&O posture (RE-5):** What must the Terms say about currency/staleness of
   legal content, and should we carry E&O insurance for this feature before
   enabling it?

## A.4 Draft Terms addition (for markup, not final)

> **Regulatory obligation tracking is general information, not legal advice.**
> Where CompliDrop lists licenses, permits, filings, certifications, or
> insurance minimums that may apply to your business or your vendors, we are
> summarizing published federal and Texas requirements, with citations to the
> official source and the date we last verified it. These summaries are general
> information: they are not legal advice, they are not a complete list of your
> obligations, they may be out of date the day a law changes, and they do not
> account for your specific circumstances, local (city or county) requirements,
> or exemptions that may apply to you. A status like "missing" or "below stated
> minimum" describes the documents on record in CompliDrop — it is not a
> determination that you or your vendor has violated any law. Consult a licensed
> attorney for advice about your legal obligations.

---

# Part B — Live vendor checklists & insurance defaults

**Surface & current status.** These are the five **seeded system checklists** in
`ComplianceTemplateSeed.cs` that venues assign to vendors *today* — the contractual
tuple engine, live in production since #192 (June 2026). Unlike the rule engine,
this surface is already on. What is **gated** is the set of *corrections* the
template review recommends (new dollar minimums, rule removals, the liquor rule):
those are built but held (draft PR #413 + a planned migration) and must not deploy
until TPL-A1..A4 and TPL-B1..B3 are confirmed.

**Why an attorney AND a broker.** The rule engine was a pure legal (UPL) question.
The templates add a second axis: the *dollar minimums* ($1M GL, $1M liquor, $1.5M
auto, guard GL $1M) are **insurance judgments** — customary market practice, not
law — so they need a licensed TX hospitality/insurance broker, while the *framing*
of those minimums as suggestions (UPL/insurance-advice risk) needs the attorney.

**The research basis.** [TEMPLATE-REQUIREMENTS-REVIEW.md](TEMPLATE-REQUIREMENTS-REVIEW.md)
— a non-attorney/non-broker memo with a 34-row confidence table, primary Texas
statutes read live (Alco. Bev. Code Ch. 2 dram-shop; Labor Code §406 workers'-comp
non-subscriber regime; Occ. Code §1702 private security; Transp. Code + 49 CFR for
shuttles), ACORD 25 form mechanics, and ISO endorsement families. It is our working
basis; §8 of it is the professional-confirmation list, reproduced as TPL-* above.

### B.1 The dollar minimums the professionals must confirm (TPL-B1)

| Vendor type | Coverage | Proposed minimum | Research tier |
|---|---|---|---|
| All | General liability | **$1,000,000 / occurrence** (+ $2M aggregate once checkable) | custom (market) |
| Caterer / bar | Liquor liability | **$1,000,000 / occurrence** | custom (market) |
| Security guard | General liability | **$1,000,000 / occurrence** (statutory floor is only $100k/$50k/$200k) | custom (market) |
| Shuttle ≤15 seats | Auto liability | **$1,500,000** (federal for-hire floor, 49 CFR 387.33T) | verified-primary (floor) + custom |
| Shuttle 16+ seats | Auto liability | **$5,000,000** (inexpressible in a checklist — description note) | verified-primary |
| Photographer | General liability | **$1,000,000** (raised from $500k for consistency) | custom |

None of these is Texas law (only the shuttle federal floors are statutory). They
are what venues customarily demand. That is exactly why TPL-B1 needs a broker, and
TPL-A3 needs an attorney to bless shipping them as editable suggestions.

### B.2 The residual holes the review could not close (must-ask, not fixable in code)

- **Assault & battery (TPL-B2)** — the guard loss mode; commonly excluded/sublimited
  and invisible on an ACORD 25. A $1M GL rule can pass a vendor whose A&B is a $25k
  sublimit. Mitigation shipped: template description tells the venue to demand "A&B
  at full limits" in writing. Broker must confirm the ask.
- **Workers' comp vs TX non-subscribers (TPL-A4 / TPL-B3)** — Texas uniquely lets
  private employers opt out (Labor Code §406.002). A lawful non-subscriber caterer
  fails the WC rule (fail-closed friction, per-tenant deletable). Attorney confirms
  the copy can't imply a legal violation; broker confirms the accepted alternatives.
- **Dram-shop reliance / assumed duty (TPL-A1)** — does building a product that
  *checks* liquor liability create any assumed-duty exposure for venues or for
  CompliDrop? Attorney item; folds into the same engagement.

### B.3 What is *built and gated* on this confirmation

Draft PR #413 (held) ships the two additive, review-validated changes (Caterer
liquor $1M, Security GL $1M) plus the extraction field, sample-COI line, and UI
preset. A planned **data migration** (system-template rows only; tenant clones
untouched) would apply the value changes and rule removals the additive seeder
cannot: Photographer GL $500k→$1M, Transport auto $1M→$1.5M, and removing the
phantom Security `certification`, Photographer `license`+E&O, and Transport
`equals CDL` rules. **Neither deploys until this gate clears.**

---

# Part C — Marketing & exported-artifact claims

These are copy/claims items where what the product *says* outruns what it can
*verify*. Each already has a tracking ticket; they are listed here so the single
gate is complete and the attorney can bundle them into one engagement.

| ID | Claim / gap | Ticket | Fix direction (from research) |
|---|---|---|---|
| CLM-1 | "Names your venue as additional insured" / "flagging anyone who listed you as certificate holder instead of additional insured" — a certificate cannot *prove* AI status (needs the endorsement). | #396 | Reword to "certificate **indicates** '{name}' as additional insured" + tell the venue to collect the CG 20 26-class endorsement. (TRR §3) |
| CLM-2 | "Coverage dates that include the event" — no event-date concept exists in the product; grades *today* only, and no COI can promise day-of validity (cancellation). | #399 (tier b) | Soften to "coverage dates you can see at a glance, expirations tracked"; build the event-date feature before re-claiming. (TRR §2.6/§7) |
| CLM-3 | Exported audit PDF & vendor package print bare "Compliant" with no disclaimer — the artifact most likely handed to an insurer or court. | #402 | Add the same non-advice disclaimer the UI carries. |
| CLM-4 | FAQ "We don't sell or share your data" contradicts 7 disclosed subprocessors (document contents to Google), emitted as JSON-LD. | #403 | Reconcile copy with the actual subprocessor list. |
| CLM-5 | Public vendor portal shows no privacy notice (CCPA notice-at-collection) to non-customer vendors whose uploads are stored + sent to Google AI + PostHog-tracked. | #404 | Add notice-at-collection to the portal. |
| CLM-6 | "Documents not used to train AI models" holds only on the Vertex path; AI Studio + Anthropic are config-reachable and Anthropic isn't a disclosed subprocessor. | #405 | Pin the prod path or disclose; correct the claim. |

---

# What to read (for the professionals), in order

1. **This gate** (§0 checklist first).
2. Rule engine: [G1-LEGAL-RESEARCH.md](G1-LEGAL-RESEARCH.md) — the full non-attorney
   memo (its §VII lists the open items) — and
   [audit/04-LIMITATIONS-AND-GATES.md](audit/04-LIMITATIONS-AND-GATES.md).
3. Templates & insurance: [TEMPLATE-REQUIREMENTS-REVIEW.md](TEMPLATE-REQUIREMENTS-REVIEW.md)
   — §8 (must-ask list), §7 (claims audit), §9 (confidence table).
4. Optionally the audit index: [audit/README.md](audit/README.md).

---

## Draft engagement email — attorney (for Ruben to forward)

> Subject: Review request — TX SaaS: regulatory + insurance-checklist framing (UPL/disclaimer, ~1–2 hr)
>
> Hi [name],
>
> I run CompliDrop, a small SaaS that helps Texas event venues track vendor
> compliance documents (insurance certificates, licenses). Two things need a
> lawyer's eyes before I turn them on: (1) a feature that shows customers which
> Texas/federal requirements apply to them and their vendors, each with a
> statutory citation; and (2) the default vendor checklists we ship, which now
> carry suggested insurance dollar minimums. Both are built but gated off.
>
> I've attached a single gate document with a checklist of exactly what I need
> confirmed (UPL/reliance, disclaimer language, whether shipping editable dollar
> minimums as suggestions is safe, and a few copy items). The supporting research
> traces every legal claim to its primary source. I'd like: (a) a go/no-go on the
> framing, (b) final disclaimer/Terms language, (c) any wording changes.
>
> Thanks, Ruben

## Draft engagement note — insurance broker

> Subject: Sanity-check on default vendor-insurance minimums for TX wedding venues (~1 hr)
>
> Hi [name],
>
> CompliDrop helps Texas event venues track vendor COIs. We ship default
> checklists suggesting minimum coverage per vendor type and want a broker to
> confirm the numbers match what TX venues actually write into vendor packets in
> 2026: GL $1M/occ (+$2M agg) for all vendors, liquor liability $1M/occ for bar
> service, auto $1.5M for small shuttles / $5M at 16+ seats, guard GL $1M. Two
> specifics I especially need: how assault & battery is typically written for
> guard companies (and what evidence to demand, since it's invisible on an
> ACORD 25), and what venues accept from lawful non-subscriber vendors in place
> of workers' comp. Short attached memo has the detail. Thanks, Ruben

---

*Adjacent bundle: tickets #402–#405 (Part C) all touch legal/privacy copy and
can go in the same attorney engagement. Rule-engine flags stay OFF and template
corrections stay gated until §0 clears.*
