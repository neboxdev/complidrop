# G1 counsel brief — regulatory rule engine, user-facing framing review

**Prepared 2026-07-09 for outside counsel. This is the ONE open gate before the
feature can be shown to any customer.** Everything below is self-contained; the
supporting evidence lives in this repo under `docs/rule-engine/audit/`.

---

## 1. What CompliDrop is, and what is changing

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

**The new feature is materially different.** A "regulatory rule engine" tells
the customer **which legal obligations apply to them and their vendors** —
e.g. "a Texas guard company must hold a DPS security-contractor license
(Tex. Occ. Code §1702.102) and carry general-liability insurance with minimum
limits of $100,000/$50,000/$200,000 (§1702.124(c)); your vendor's certificate is
missing / on file / below the stated minimum." The product moves from *"we read
your documents against YOUR rules"* to *"we assert which laws apply to you"* —
a larger reliance and unauthorized-practice-of-law (UPL) surface that the
current Terms clause was not written for.

**Current exposure status: NONE.** The feature is merged but inert — it is
behind a feature flag that defaults OFF, and no user interface or API endpoint
exists. Nothing ships to a customer until this review concludes.

## 2. What the engine actually outputs (the framing discipline already built in)

Three requirements from our internal legal-review pass are enforced in the code
structure itself (each is pinned by an automated test):

1. **No overall "compliant" verdict exists.** The report type has no
   is-compliant boolean; output is a list of per-obligation statuses
   (`satisfied / expiring / expired / missing / below-stated-minimum /
   needs-profile-info / needs-document-info / not-applicable`) plus a
   **mandatory non-exhaustiveness notice** that renders on every report:

   > "This report lists only the regulatory obligations CompliDrop tracks for
   > the profile and documents you provided. It is not a complete list of your
   > legal obligations and is not legal advice. Local requirements (for example
   > city and county health, food, fire, occupancy, and per-event permits) and
   > other rules may also apply — check with your local authorities and a
   > qualified professional."

2. **Every rule states what the law requires and cites it, never adjudicating
   the user.** Example rationale text: *"Texas law requires a company that
   provides guard or security-officer services on a contractual basis to hold a
   security services contractor (guard company) license from the Texas DPS
   Private Security Program before operating (Tex. Occ. Code 1702.102,
   1702.108)."* Statuses describe the DOCUMENT record ("no certificate on
   file"), not the entity's conduct ("you are operating illegally").

3. **Penalty text is statutory-general.** Where a rationale mentions a penalty
   ("operating without the required license is an offense under 1702.388"), it
   states what the statute provides and is never paired with an assertion that
   THIS user is committing it.

Every encoded rule carries a citation (section + URL + verified date) to a US
primary source, a confidence tier (only "verified" ships), and a verbatim quote
of the operative statutory text in the research dossier. The audit trail
(`docs/rule-engine/audit/README.md`) lets a reviewer trace any encoded number to
its statute in about a minute.

## 3. The questions for counsel

1. **UPL / reliance:** Does presenting sourced, per-obligation regulatory
   requirements (with the framing discipline in §2) constitute or approach the
   unauthorized practice of law in Texas, or create negligent-misrepresentation
   /detrimental-reliance exposure — and what disclaimer language, placement,
   and prominence would you require before launch?
2. **Terms of Service:** Does the existing "head start, not advice" clause
   (§1) need to be amended or supplemented for this feature? We have a draft
   addition in §4 for you to mark up.
3. **Status wording:** Are the status labels acceptable, particularly
   `below-stated-minimum` (a numeric comparison of an extracted certificate
   amount against a cited statutory floor) and `missing` (defined as "no
   document on record", not "you are violating the law")? Should any be
   renamed or carry inline qualifiers?
4. **Penalty text:** May statutory penalty references (e.g. "Class A
   misdemeanor") appear in the obligation rationale at all, and if so with what
   framing constraints? (Our internal rule: never adjacent to a red/negative
   status badge in the UI.)
5. **Errors-and-omissions posture:** Given that rules change (every rule
   carries a verified-date and we plan a re-verification cadence), what does
   the Terms need to say about currency/staleness of legal content, and should
   we carry E&O insurance for this feature before enabling it?

## 4. Draft Terms addition (for markup, not final)

> **Regulatory obligation tracking is general information, not legal advice.**
> Where CompliDrop lists licenses, permits, filings, certifications, or
> insurance minimums that may apply to your business or your vendors, we are
> summarizing published federal and Texas requirements, with citations to the
> official source and the date we last verified it. These summaries are
> general information: they are not legal advice, they are not a complete list
> of your obligations, they may be out of date the day a law changes, and they
> do not account for your specific circumstances, local (city or county)
> requirements, or exemptions that may apply to you. A status like "missing"
> or "below stated minimum" describes the documents on record in CompliDrop —
> it is not a determination that you or your vendor has violated any law.
> Consult a licensed attorney for advice about your legal obligations.

## 5. What to read (in order, ~45 minutes)

1. This brief.
2. [`RULES-REVIEW.md`](../../RULES-REVIEW.md) — the rule inventory with sources
   and confidence, and the sign-off record.
3. [`docs/rule-engine/audit/04-LIMITATIONS-AND-GATES.md`](audit/04-LIMITATIONS-AND-GATES.md)
   — what the engine deliberately does NOT claim (written to be uncomfortable
   rather than reassuring).
4. Optionally: [`docs/rule-engine/audit/README.md`](audit/README.md) — the full
   audit index (every legal claim → primary source; every guarantee → test).

Adjacent items you may want to bundle into the same engagement: open tickets
#402 (exported PDFs carry no disclaimer), #403 (marketing copy vs disclosed
data sharing), #404 (vendor-portal privacy notice / CCPA notice-at-collection),
#405 (subprocessor disclosure accuracy) — all touch legal copy.

---

## Draft engagement email (for Ruben to forward)

> Subject: Review request — SaaS feature that lists regulatory obligations (UPL/disclaimer review, ~1 hr)
>
> Hi [name],
>
> I run CompliDrop, a small SaaS that helps Texas event venues track vendor
> compliance documents (insurance certificates, licenses). We've built a new
> feature that goes a step further: it shows customers which federal and Texas
> requirements apply to them and their vendors — each with a citation to the
> statute or regulation — and whether a matching document is on file. It is
> fully built but switched off, and I won't enable it until you've reviewed the
> user-facing framing.
>
> I've attached a short brief that contains: what the feature asserts and the
> exact output wording, the five specific questions I need answered (mainly
> UPL/reliance exposure and required disclaimer language), our current Terms
> clause, and a draft Terms addition for you to mark up. The supporting
> research (every requirement traced to its primary source) is available if
> you want to sample it.
>
> Could you review and give me: (a) a go/no-go on the framing approach,
> (b) final disclaimer/Terms language, and (c) any wording changes to the
> status labels? Happy to walk through it on a call first.
>
> Thanks,
> Ruben

*Attach: this file (rendered to PDF is fine), RULES-REVIEW.md, and
04-LIMITATIONS-AND-GATES.md.*
