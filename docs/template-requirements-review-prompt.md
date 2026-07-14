# Deep review prompt — CompliDrop seeded vendor-insurance checklists (TX event venues)

> Paste everything below the line into Fable 5. It is self-contained.
> Companion to `docs/rule-engine/G1-LEGAL-RESEARCH.md` — same non-attorney memo discipline.

---

You are performing a **deep domain review of insurance-requirement defaults** for a compliance SaaS. Treat this as a research task with real consequences: these defaults decide whether a venue's vendor reads "Covered" or "Action needed."

## Your role and its hard limit

You are **not an attorney and not a licensed insurance broker**, and you must not present yourself as either. Produce a **non-attorney research memo**: cite primary sources, mark every uncertainty explicitly, and maintain a standing list of questions that genuinely require a licensed Texas attorney or a TX hospitality insurance broker to answer. **Never fabricate a citation, a form number, or a statute section.** If you cannot verify something, say "unverified" and explain what would verify it. Where you rely on industry custom rather than law, label it as custom, not law.

## The product, in one paragraph

CompliDrop is a $49/mo SaaS for SMBs. A venue uploads a vendor's certificate of insurance (COI), license, or permit. The document is OCR'd, an LLM extracts named fields into a flat `{field_name: value}` map, and a **deterministic rule engine** grades the document against a checklist. Each document gets a verdict — `Compliant`, `NonCompliant`, `ExpiringSoon`, `Expired`, `Pending` — and each vendor rolls up to `Covered`, `Action needed`, or `Missing`. **The verdict is the product.** A false "Covered" (fail-open) is the dangerous failure; a false "Action needed" (fail-closed) is merely friction the venue can override.

**Beachhead market: Texas event venues** — weddings and private events (caterers, bar service, event rentals, security, shuttles, photographers). Property management is a later market; optimize for TX event venues.

## The rule engine — what a requirement can physically express

Every requirement is a tuple:

```
(documentType, fieldName, operator, expectedValue)
```

- `documentType` ∈ `coi | license | permit | certification | contract | other`
- `operator` ∈ `required` (field present and non-empty) | `equals` (exact match) | `contains` (substring) | `min_value` (numeric ≥)
- `expectedValue` is a string (e.g. `"1000000"`, `"CDL"`), or null for `required`

**A rule can only test a field the extractor produces.** These are the fields the LLM is currently instructed to pull:

- **COI:** `policyholder_name`, `insurer_name`, `policy_number`, `effective_date`, `expiration_date`, `general_liability_limit`, `workers_comp_limit`, `auto_liability_limit`, `umbrella_limit`, `professional_liability_limit`, `liquor_liability_limit`, `certificate_holder`, `description_of_operations`, `additional_insured`
- **License:** `holder_name`, `license_number`, `license_type`, `issuing_authority`, `issue_date`, `expiration_date`, `state`
- **Permit:** `permit_number`, `permit_type`, `issuing_authority`, `issue_date`, `expiration_date`, `property_address`
- **Certification:** `holder_name`, `certification_name`, `certifying_body`, `issue_date`, `expiration_date`, `certification_number`

Note there is **one** `general_liability_limit` field — it does not distinguish per-occurrence from general aggregate. Same for every other `*_limit`.

## The five shipped checklists (verbatim, current proposal)

```csharp
// "Caterer" — "Typical insurance for a food & beverage caterer, including bar / alcohol service."
("coi", "general_liability_limit",      "min_value", "1000000")
("coi", "expiration_date",              "required",  null)
("coi", "workers_comp_limit",           "required",  null)
("coi", "liquor_liability_limit",       "min_value", "1000000")   // newly added

// "Event Rental Company" — "Coverage for table, tent, and equipment rental vendors."
("coi", "general_liability_limit",      "min_value", "1000000")
("coi", "expiration_date",              "required",  null)

// "Security Service" — "Licensing plus general-liability insurance for event security and guard services."
("license",       "license_number",     "required",  null)
("license",       "expiration_date",    "required",  null)
("certification", "expiration_date",    "required",  null)
("coi", "general_liability_limit",      "min_value", "1000000")   // newly added

// "Transportation / Shuttle" — "Auto coverage and a CDL for shuttle and transport vendors."
("coi",     "auto_liability_limit",     "min_value", "1000000")
("license", "license_type",             "equals",    "CDL")
("license", "expiration_date",          "required",  null)

// "Photographer / Videographer" — "General + professional (E&O) coverage for photo and video vendors."
("coi",     "general_liability_limit",       "min_value", "500000")
("coi",     "professional_liability_limit",  "min_value", "1000000")
("license", "expiration_date",               "required",  null)
```

An **additional-insured** requirement is deliberately *not* seeded, because the value is the venue's own legal name (per-tenant). The product nudges the user to add `("coi", "additional_insured", "contains", "<venue legal name>")` themselves.

## What the marketing site currently promises venues

Verbatim from the venue landing page:

- "General liability coverage"
- "Your venue named as an additional insured"
- "Liquor liability"
- "Coverage dates that include the event"
- FAQ: *"Most venues require each vendor to carry general liability coverage (**commonly $1M per occurrence / $2M aggregate**), to name the venue as an additional insured, and to add liquor liability if they serve alcohol. Vendors with employees are usually asked for workers' compensation."*
- FAQ: *"Being listed only as the certificate holder means you receive the certificate but aren't covered by it. Additional insured status — added by an endorsement on the vendor's policy — is what lets their insurer defend and pay a claim on your behalf."*

## A hard engineering constraint you must respect

The seeder reconciles rules on the natural key `(documentType, fieldName, operator)` — **the value is not part of the key, and an existing rule is never updated.** Once a rule ships with `expectedValue = "1000000"`, editing the seed to another number is silently inert in production. **Getting these numbers right before first deploy matters more than usual.** If you recommend a value that differs from the proposal, say so plainly and explain the consequence of shipping the wrong one first.

---

# Specific questions I need answered

Work through each. Where the answer differs for "law" vs "industry custom vs "what a venue can reasonably demand contractually," separate them.

### 1. Liquor liability — the highest-stakes item
- Distinguish **host liquor liability** (commonly *included* in a CGL policy for parties who do not sell alcohol) from **liquor liability / dram shop coverage** (for those "in the business of" manufacturing, selling, serving, or furnishing alcohol — commonly *excluded* by CGL exclusion **and** requiring a separate policy or endorsement). Which one does a bar-service caterer at a TX wedding actually need?
- Texas statutory dram-shop liability: identify the governing chapter and section of the Texas Alcoholic Beverage Code, who it reaches (licensee/permittee vs social host), and the TX safe-harbor/"Trained Server" provisions. Does a *venue* have exposure when the caterer holds the TABC permit, and vice versa?
- Which TABC permit types matter for a private event (e.g. caterer's permit, temporary event permits, BYOB scenarios)? Should the checklist require a **TABC permit document** (`documentType = permit`) at all, in addition to liquor liability insurance?
- Is `$1,000,000` the right minimum, and per-occurrence or aggregate?
- **Does liquor liability appear on a standard ACORD 25 at all?** If it usually appears only in the "Description of Operations" free-text box or on a separate certificate, then a `min_value` rule on `liquor_liability_limit` will frequently see *nothing* and fail closed for compliant vendors. Assess how often extraction can actually find this value, and recommend accordingly.
- Should the liquor rule live on the general **"Caterer"** template (flagging food-only caterers as non-compliant — "safe friction"), or on a **separate "Bar Service / Alcohol"** template the venue assigns only when alcohol is served? Argue both, then recommend.

### 2. Workers' compensation — I suspect the current rule is wrong
- Texas is, as I understand it, unusual in that private employers may elect **not** to carry workers' compensation ("non-subscribers"). Verify this, cite the Labor Code provision, and explain the non-subscriber regime.
- The Caterer checklist currently requires `workers_comp_limit` to be **present**. Is that defensible in Texas, where a lawful non-subscriber caterer would fail it? What is the correct rule — require it, require it only if the vendor has employees (can the engine even know that?), or drop it to a warning?
- A COI for a non-subscriber, or a sole proprietor with no employees, typically shows what in the workers-comp section of an ACORD 25? Would extraction produce an empty `workers_comp_limit`, a `"$0"`, or an exclusion note?
- What does the venue's *own* exposure look like if a vendor's uninsured employee is injured on site?

### 3. Additional insured — is the current check honest?
- Explain, with ISO endorsement form numbers, the difference between **certificate holder** and **additional insured**, and between the common AI endorsement forms (ongoing operations vs completed operations vs scheduled-vs-blanket). Which forms should a venue insist on?
- On an ACORD 25, what actually evidences AI status? Is the "ADDL INSD" column a reliable indicator, or is it a checkbox that a broker can tick without an endorsement actually existing? **This is the crux of open issue #396: our rule passes on a checkbox and then asserts to the user "Names you as additional insured."**
- Should the product also demand **waiver of subrogation** and **primary & non-contributory** wording? Are those separate ACORD cells, separate endorsements, or free text?
- Can any of this be verified from an ACORD 25 alone, or does it strictly require the endorsement document itself? If the latter, say so bluntly — it changes what we may claim.

### 4. Per-occurrence vs aggregate — open issue #397
- On an ACORD 25, name the exact cells that carry "Each Occurrence," "General Aggregate," "Products-Comp/Op Agg," "Damage to Rented Premises," "Personal & Adv Injury."
- Our single `general_liability_limit` field could capture *any* of these. If the model reads the **$2M aggregate** and the rule is `min_value 1000000`, an inadequate **$500k per-occurrence** policy passes. Confirm this is a real failure mode.
- What should we extract instead? Propose the exact new field names and which ACORD cell each maps to.
- What limits should a TX event venue require for each cell?

### 5. Security services
- Texas private security licensing: cite the governing Occupations Code chapter and the regulator. What licence classes exist (guard company vs individual commissioned officer vs non-commissioned), and **which document would a venue actually receive** — a company licence, an individual registration, or both? Our rule requires a `license_number` plus a `certification` expiration; is that the right shape?
- **Assault & battery** exposure: is A&B typically excluded from a guard company's CGL and added back by endorsement, or sublimited? If so, a bare `general_liability_limit ≥ $1M` rule may certify a guard vendor whose A&B is excluded or sublimited to a token amount. What should the rule be, and is it expressible?
- Are armed vs unarmed guards materially different for a venue's requirements?
- Recommended GL limit for a guard vendor at a private event.

### 6. Transportation / shuttle — I suspect the CDL rule is wrong
- Under FMCSA rules, when is a **CDL with a passenger (P) endorsement** actually required? My understanding is it turns on vehicle design capacity (commonly ≥16 passengers including the driver) or GVWR — so a 14-passenger shuttle driver may lawfully need **no CDL**. Verify and cite.
- If so, `("license", "license_type", "equals", "CDL")` fails a lawful 14-passenger shuttle operator. What is the correct requirement?
- For-hire passenger carriers: what auto-liability minimum applies (federal minimums differ by seating capacity; intrastate TX may differ)? Is `$1,000,000` adequate for a passenger shuttle, or is $1.5M/$5M the real number?
- Should we require USDOT / TxDMV motor-carrier registration as a `permit` document? Does the vehicle need to appear as scheduled vs hired/non-owned auto?
- **Hired & non-owned auto** — relevant for caterers and rental vendors who drive to the venue. Should other templates carry it?

### 7. Event rental companies
- Is `general_liability_limit ≥ $1M` + expiry sufficient? What about **tents** (wind load, anchoring, permits), installation/rigging, and damage to the venue's premises?
- Do rental vendors typically carry inland marine / equipment coverage, and does the venue care?
- Should "Damage to Rented Premises" have its own minimum?

### 8. Photographer / videographer
- Is **professional liability (E&O) at $1M** genuinely customary for wedding photographers, or is this over-required? (Over-requiring fails closed — safe, but it trains users to override red statuses, which is its own hazard.)
- Is `general_liability_limit ≥ $500,000` internally inconsistent with the $1M we require everywhere else, given the venue's own insurer likely mandates $1M from all vendors?
- Why does this template require a `license` expiration at all? Photographers are not licensed in Texas as far as I know. Verify; if it's spurious, say so.

### 9. Coverage dates that include the event
- We currently grade "is the certificate in force **today**," not "is it in force **on the event date**." The marketing page promises the latter. Beyond the obvious (compare `expiration_date` to the event date), what should a venue check — is `effective_date` ≤ event date equally important? What about mid-term cancellation, and does an ACORD 25 give any cancellation-notice guarantee (the "should endeavor to" language)?
- Is there any defensible way to assert "coverage dates include the event" from an ACORD 25 alone?

---

# Deliverables

Produce a markdown memo with these sections:

1. **Disclaimer** — non-attorney, non-broker; not legal advice; mirrors `docs/rule-engine/G1-LEGAL-RESEARCH.md`.
2. **Executive summary** — the 5 changes that most reduce the chance of a false "Covered," ranked.
3. **Per-template findings** — one section per checklist. For each current rule: *keep / change / remove*, with reasoning and citation. For each proposed new rule, give the exact tuple `(documentType, fieldName, operator, expectedValue)`.
4. **Proposed template set** — a complete, copy-pasteable replacement for the five `TemplateSeed` definitions above, in the same C# tuple shape. Where a needed rule is **not expressible** with the current fields/operators, put it in section 5 instead of inventing a field.
5. **Extraction gaps table** — every requirement you'd want that the current field set cannot express. Columns: `desired check | new field name | where it appears on an ACORD 25 (form cell / section) | how reliably an LLM could read it | fail-open risk if omitted`.
6. **Fail-open vs fail-closed audit** — for every rule in your proposed set, classify: could a **non-compliant vendor pass** (fail-open, dangerous), or could a **compliant vendor fail** (fail-closed, friction)? Fail-open items are the ones I must fix before launch.
7. **Claims audit** — for each marketing promise quoted above, state whether the proposed engine can actually verify it. Flag any promise we should soften or remove. (We already know "coverage dates include the event" is not truly checked.)
8. **Must-ask-a-human list** — the specific questions a licensed TX attorney or a TX hospitality insurance broker must answer, phrased so I can hand the list to them directly. Distinguish "blocks launch" from "refine later."
9. **Confidence table** — every material claim, with `verified (primary source) | industry custom | unverified assumption`, and the citation.

## Rules of engagement

- **Jurisdiction: Texas**, private-event venues. Note where federal law governs (FMCSA) or where a rule is national industry custom (ISO forms, ACORD 25 layout).
- Cite **primary sources** where they exist: Texas Alcoholic Beverage Code, Texas Occupations Code, Texas Labor Code, Texas Insurance Code, FMCSA regulations (49 CFR), ISO form numbers, ACORD 25 (2016/03) layout. Quote the operative language where it decides the answer.
- Where you're inferring from custom, **say so** — do not dress custom as law.
- Prefer "this rule cannot be verified from an ACORD 25" over inventing a field the extractor could never populate.
- **Over-requiring is safe; under-requiring is dangerous.** When genuinely uncertain, recommend the fail-closed option and flag it as friction to be tuned.
- Be concrete about numbers. "Adequate limits" is useless; `$1,000,000 per occurrence / $2,000,000 general aggregate` is useful.
