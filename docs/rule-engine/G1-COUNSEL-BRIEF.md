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
| **CLM-1** | Additional-insured copy: reword "Names you as additional insured" → "Certificate indicates…"; the certificate cannot prove AI status (endorsement needed). **UI/API copy now STAGED** behind `ComplianceClaims:CorrectedAdditionalInsuredWording` (default OFF, copy-only, ADR 0043); marketing-site copy still to do at flip. | A | Marketing + UI (#396) | ⬜ | §C, TRR §3/§7 |
| **CLM-2** | "Coverage dates that include the event" is currently unbacked (no event-date check exists); soften until the feature is built. | A | Marketing (#399 tier b) | ⬜ | §C, TRR §2.6/§7 |
| **CLM-3** | Exported audit PDF / vendor package carry no disclaimer while printing bare "Compliant". **A disclaimer now SHIPS on all three artifacts** (audit PDF, vendor package, CSV), on by default and behind no flag — ADR 0047. **Two answers needed:** (a) confirm/refine the exact sentence alongside CLM-1, and (b) rule on **prominence** — it currently renders in the same 8pt light-slate fine print as the branding line beneath it; is that conspicuous enough for an artifact handed to an insurer/broker/adjuster, or does it want weight, size, a rule, or the §V.1 all-caps formula? | A | Export (#402) | ⬜ | §C |
| **CLM-4** | FAQ "We don't sell or share your data" vs the 7 disclosed subprocessors (document contents to Google). **The corrected copy now SHIPS** (#403), on by default and behind no flag; the subprocessor list also gained the two compute hosts it had never named (Railway — the API servers every upload is posted to; Vercel — hosting the web app). **Copy to bless — items (a)–(d) below, all shipped, none reaching a verdict** (§C carries the same items with the reasoning; a new item is added in BOTH places or in neither): **(a)** the FAQ answer, *"We don't sell your data, and we share it only as described in our Privacy Policy — with the service providers that help us run CompliDrop, and where the law or the protection of rights and safety requires it."*, which is emitted verbatim as FAQPage JSON-LD and so is the exact string search engines and AI assistants quote; **(b)** the Privacy Policy's CCPA parenthetical, *"(we don't sell personal information, and we don't share it for targeted advertising)"*, which replaced an unqualified "(we don't sell or share it)" sitting in the same document as the vendor list; **(c)** the site-wide footer tagline *"Drop your docs. Stay compliant."* — §V.4's never-say list includes "keeps you compliant", and this is an imperative to the reader rather than an assertion about the product, which is why #403 left it standing; **(d)** the FAQ's *"won't slip through unnoticed"* — does it survive §V.4 given that a document whose expiration date was never extracted produces no reminder at all? Each of (c) and (d) needs a yes/no here, not a unilateral copy edit. | A | Marketing/privacy (#403) | ⬜ | §C |
| **CLM-5** | Public vendor portal has no privacy notice (CCPA notice-at-collection). **A notice now SHIPS** (#404, ADR 0054), on by default and behind no flag: it renders beside the dropzone before any upload, in every branch the route can render, and links `/privacy` — which gained a section written for the account-less reader it now sends there. **Copy to bless — (a) and (b) below, both shipped, neither reaching a verdict** (§C carries the same items with the reasoning; a new item is added in BOTH places or in neither): **(a)** the collection notice itself, read by a stranger who agreed to nothing, *"By uploading, you agree your document will be stored and processed — including automated reading by the AI services we use — as described in our Privacy Policy. This page sets no cookies and doesn't measure how it's used."*; **(b)** what the two branches with no upload affordance (dead link, transient failure) carry instead — a reader on a dead link is still owed the answer to *what does this page do with me* — *"This page sets no cookies and doesn't measure how it's used — see our Privacy Policy."* **Four rulings beyond the wording, none a unilateral copy edit:** **(i)** is NOTICE — no consent checkbox, no cookie banner — the right posture for a California vendor on this page, noting that the page no longer sets a cookie or measures the visit at all (round 2 of #404 stopped initialising PostHog on this route, ADR 0037 Amendment 2 / ADR 0054 Amendment 1), so both sentences now DISCLAIM a collection instead of disclosing one and (i) is asked about a smaller collection than when it was filed; **(ii)** may *"the AI services we use"* stand in for a named provider while `Extraction:Provider` is a config switch (the same question CLM-6 asks from the policy side); **(iii)** **prominence**, the same question CLM-3 routes for the export disclaimer — the notice renders in `text-xs text-slate-500`, byte-identical to the `Powered by CompliDrop` branding line beside it, so is fine print conspicuous enough for a disclosure given *at* collection to a stranger, or does it want weight, size, a rule, or the §V.1 all-caps formula; and **(iv)** do the Terms bind a portal uploader who was never shown them? `/terms` is deliberately not linked here (ADR 0054 Option E, rejected on relevance), but the shipped Terms accept on `By creating an account or using {SITE_NAME}, you agree to them` and their Acceptable-use clause governs `upload content you don't have the right to upload, or that is unlawful` — the act this reader performs. Either they do not reach the vendor, in which case that acceptance sentence overstates, or they do, in which case a link may be owed. | A | Portal (#404) | ⬜ | §C |
| **CLM-6** | "Documents not used to train AI models" holds only on the Vertex path; AI Studio + Anthropic are config-reachable and Anthropic isn't a disclosed subprocessor. | A | Privacy (#405) | ⬜ | §C |

**Launch-blocking:** RE-1..RE-5 (rule engine), TPL-A1..A4 + TPL-B1..B3 (templates &
dollar minimums), CLM-1..CLM-6 (copy/privacy). *Refine-later:* TPL-B4..B8.
`TRR` = [TEMPLATE-REQUIREMENTS-REVIEW.md](TEMPLATE-REQUIREMENTS-REVIEW.md).

**What sign-off unlocks (the three switches, all merged & inert today):**
- Rule engine → set `RuleEngine:Enabled=true` (+ `EnabledRuleSets`) after RE-1..RE-5.
- Template corrections → set `TemplateCorrections__Enabled=true` after TPL-A1..A4 +
  TPL-B1..B3 — full runbook in §B.3.
- Additional-insured wording (CLM-1) → set
  `ComplianceClaims__CorrectedAdditionalInsuredWording=true` after CLM-1 clears. Copy-only
  (UI sentence + failure message + the ACORD-checkbox check note); no verdict change. The
  marketing-site copy has no flag and is edited at the same time (§C, ADR 0043).

**No switch (already live):** the export disclaimer (CLM-3) is ON by default — a disclaimer
where none existed is one-directional risk reduction, so staging it OFF would have left the
defect live in prod (ADR 0047 §4). Sign-off here means *confirming or refining the sentence*,
a one-line edit to `ExportService.Disclaimer` (plus its verbatim test pin), not a flag flip —
**and**, if counsel wants the qualifier more conspicuous than today's fine print (ADR 0047 §5),
a second one-place edit to the footer styling in `ExportService.ApplyPageDefaults`. Neither
touches a verdict, so neither needs a runbook or a re-grade.

The privacy copy (CLM-4) is the same shape: it shipped in #403 with no flag, because copy
that contradicts our own policy is a defect either way round. Sign-off is a wording
confirmation, not a flip — the FAQ answer lives in `frontend/src/app/faq/page.tsx` and the
parenthetical in `frontend/src/app/privacy/page.tsx`. Both are pinned verbatim, as
whole-sentence string literals compared against the rendered page by
`frontend/src/app/marketing-content.test.tsx` → *"ships both CLM-4 sentences byte-for-byte
as the counsel brief quotes them"*: a reword of **any** word inside either sentence turns
the suite red, so what ships stays the string quoted above. (The other assertions on these
pages match key phrases only — they catch the old claim coming back, not a reword.) Neither
sentence reaches a verdict.

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
those are **merged into the codebase but disabled at runtime** behind the config
flag **`TemplateCorrections:Enabled` (default `false`)** — the same inert pattern
as `RuleEngine:Enabled` (PR #413, merged 2026-07-14; ADR 0036 Amendment 3). With
the flag off, the deployed product is behaviorally identical to the pre-correction
prod (pinned by test). The flag must NOT be flipped until TPL-A1..A4 and
TPL-B1..B3 are confirmed.

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

### B.3 What is *merged and disabled* pending this confirmation

The full five-template correction is **merged** (PR #413, 2026-07-14) and **inert
behind `TemplateCorrections:Enabled = false`**. The seeder holds two template
sets — `LegacyTemplates` (byte-exact the pre-correction prod set) and
`CorrectedTemplates` (the review's §4 set: Caterer + liquor $1M; Security + GL
$1M, minus the phantom `certification` rule; Transport auto $1M→$1.5M, minus the
`equals CDL` rule; Photographer GL $500k→$1M, minus E&O + the non-existent TX
license) — and converges the live system templates to whichever the flag selects
(ADR 0036; tenant clones never touched; durable watermarked cross-org re-grade;
sample-demo docs excluded). Flag **off** = byte-level no-op vs prod, pinned by
test. The gated UI (the liquor add-requirement option and the additional-insured
nudge) is hidden the same way, driven by `features.correctedChecklists` on
`/api/auth/me`. Deliberately **live** regardless of the flag: the extraction
improvements (liquor field + GL pinned to the ACORD "EACH OCCURRENCE" cell) and
the sample-COI liquor line — they read documents and assert no defaults.

**Flip runbook (after this gate clears):**
1. Attorney + broker sign off TPL-A1..A4 / TPL-B1..B3 in §0.
2. Set `TemplateCorrections__Enabled=true` in the Railway environment; redeploy.
3. Boot log prints `Template corrections: ENABLED`; the seed converges the five
   system templates and re-grades affected documents across orgs (watch the
   `re-graded {n}/{m}` log lines; an interrupted re-grade self-heals next boot).
4. Clear + re-create the sample demo in showcase orgs (old samples keep their
   verdict; the generator already emits liquor, so re-created ones pass).
5. Post-stability cleanup: collapse `LegacyTemplates` + the flag (ADR 0036 Am. 3).
The flag is reversible — setting it back to `false` converges to the legacy set
and re-grades again.

---

# Part C — Marketing & exported-artifact claims

These are copy/claims items where what the product *says* outruns what it can
*verify*. Each already has a tracking ticket; they are listed here so the single
gate is complete and the attorney can bundle them into one engagement.

| ID | Claim / gap | Ticket | Fix direction (from research) |
|---|---|---|---|
| CLM-1 | "Names your venue as additional insured" / "flagging anyone who listed you as certificate holder instead of additional insured" — a certificate cannot *prove* AI status (needs the endorsement). | #396 | Reword to "certificate **indicates** '{name}' as additional insured" + tell the venue to collect the CG 20 26-class endorsement. (TRR §3) |
| CLM-2 | "Coverage dates that include the event" — no event-date concept exists in the product; grades *today* only, and no COI can promise day-of validity (cancellation). | #399 (tier b) | Soften to "coverage dates you can see at a glance, expirations tracked"; build the event-date feature before re-claiming. (TRR §2.6/§7) |
| CLM-3 | Exported audit PDF & vendor package print bare "Compliant" with no disclaimer — the artifact most likely handed to an insurer or court. | #402 | **Done (ADR 0047)**: one shared `ExportService.Disclaimer` on the audit PDF, vendor package and CSV — *"Statuses reflect automated reading of documents as uploaded; certificates do not modify policies. Verify current coverage with the issuing carrier."* On by default (a disclaimer is one-directional risk reduction, so unlike CLM-1 it is not flag-staged). **Attorney to confirm or refine this exact sentence** — one edit, one constant, no flag flip. |
| CLM-4 | FAQ "We don't sell or share your data" contradicts 7 disclosed subprocessors (document contents to Google), emitted as JSON-LD. | #403 | **Done (#403)**: the FAQ answer now reads *"We don't sell your data, and we share it only as described in our Privacy Policy — with the service providers that help us run CompliDrop, and where the law or the protection of rights and safety requires it."* — qualified against the whole policy rather than against a list, because the policy introduces its vendors with "These include:" and reserves a rights-and-safety disclosure channel beside the legal one, so a narrower sentence would be a promise the policy does not keep. The policy's own unqualified "(we don't sell or share it)" became the CCPA terms of art *"(we don't sell personal information, and we don't share it for targeted advertising)"*, and its vendor list gained **Railway** (the API servers that receive and process uploads) and **Vercel** (hosting the web app) — compute hosts that were receiving the data while going unnamed. Copy only, no flag. **Attorney to confirm or refine sentences (a) and (b)** — the FAQ one is machine-quoted as FAQPage JSON-LD. **Two more phrases for the same pass, (c) and (d)** — the §0 register lists all four, and a new one is added in BOTH places or in neither. **(c)** The site-wide footer tagline *"Drop your docs. Stay compliant."* (`frontend/src/components/marketing/site-footer.tsx:88`). G1-LEGAL-RESEARCH's never-say list includes *"keeps you compliant"*; the tagline is an imperative addressed to the reader rather than an assertion about what the product does, so #403 deliberately left it standing rather than rewrite the brand tagline mid-fix — it needs a yes/no here, not a copy edit. **(d)** The FAQ's *"…and sends the reminders automatically — so the expiration buried in a row you forgot to check won't slip through unnoticed."* (`frontend/src/app/faq/page.tsx`, the spreadsheet-comparison answer). This is the string [#403](https://github.com/neboxdev/complidrop/issues/403) itself prescribed — *"soften 'can't slip through' to 'won't slip through unnoticed'"* — and the session implemented it verbatim rather than diverging from the ticket's stated fix. Round 2 of the review then asked whether it clears §V.4 anyway: the clause names the REMINDERS as its mechanism, and reminders are the one thing the Terms disclaim ("a helpful nudge, not a guaranteed notice"), while a document whose expiration date was never extracted matches no reminder window at all and a suppressed recipient is skipped (ADR 0031) — so for a customer who works from email there are reachable cases with no notice. The counter-argument, which is why it shipped: the dashboard surfaces the expiry independently of email, so "unnoticed" is arguably a claim about the product rather than about the mail. **Yes/no needed, not a unilateral copy edit** — the alternative on the table is anchoring the promise to the surface that keeps it ("so the expiration isn't buried in a row you have to remember to check"). |
| CLM-5 | Public vendor portal shows no privacy notice (CCPA notice-at-collection) to non-customer vendors whose uploads are stored + sent to Google AI + PostHog-tracked. | #404 | **Done (#404, ADR 0054)**: all three collection facts were verified against code before any copy was written — the file is stored in Azure Blob (`VendorPortalEndpoints` → `IBlobStorageService.UploadAsync` → the Azure `BlobContainerClient`), it is read by third-party AI (`ExtractionWorker` → Google Document AI OCR, then the LLM selected by `Extraction:Provider`), and the visit WAS measured by PostHog because the ROOT layout wraps every route in `Providers` and nothing opted the portal out. That third fact no longer holds and the copy moved with it: round 2 of #404 found that redaction could not reach every PostHog channel — `/flags` carries an ANONYMOUS visitor's raw `$initial_current_url` at init with no `identify()`, and the heatmaps buffer is keyed by `location.href` — so `Providers` now refuses to initialise analytics under `/portal/` at all (ADR 0037 Amendment 2). On this one route the URL IS the bearer credential, so it gets an invariant rather than a per-channel promise. Both sentences below therefore now DISCLAIM a collection rather than disclose one, and `/privacy`'s upload-link section moved in the same commit (ADR 0054 Amendment 1); the page setting no cookie is pinned by a test reading `document.cookie`, and the route issuing no analytics request by `frontend/src/lib/providers.test.tsx`. The notice renders **beside the dropzone, before the upload**, and in every branch the route can render — the loading shell carries the real sentence rather than a skeleton bar, the main return covers the form, the at-limit link and the post-upload state, and the two branches with no dropzone carry (b) instead, because "By uploading…" is false where nothing can be uploaded — a reader on a dead link is still owed the answer to *what does this page do with me*. It **names no AI vendor**: `Extraction:Provider` is a config switch, so a provider name in portal copy would go stale silently; the named subprocessor list stays in `/privacy`, one link away, and whether that list is complete on every configured path is **CLM-6's** question, deliberately not pre-empted here. Because a notice pointing at a policy that does not address its reader is not notice, `/privacy` gained **"If you were sent an upload link"** — no account needed, what is collected, that the file is stored and read automatically by the providers listed above, and that the business which sent the link controls the record. On by default and behind no flag, on ADR 0047's reasoning rather than ADR 0043's: notice where there was none is one-directional, so a default-OFF flag would ship the code and leave the reported gap live. **Attorney to confirm or refine sentences (a) and (b)** — a reword is a one-file edit — **and to rule on the four questions beyond the wording** carried in the §0 register (a new one is added in BOTH places or in neither). **(i) Notice vs consent** for a California vendor: CCPA has no opt-in for this and Options C/D (a consent checkbox, a cookie banner) were rejected as friction on the product's most abandonment-sensitive surface, but the posture is counsel's to confirm. **(ii) The unnamed provider**: may "the AI services we use" stand in for a named one while `Extraction:Provider` is a config switch — the same question CLM-6 asks from the policy side. **(iii) Prominence**, the question CLM-3 routes for the export disclaimer, and it applies here for the same reason plus one that is sharper: the notice renders in `text-xs text-slate-500`, the byte-identical Tailwind classes of the "Powered by CompliDrop" branding line directly beneath it (`frontend/src/app/portal/[token]/page.tsx`), so the disclosure and the logo credit carry equal visual weight. CLM-3's artifact is read by an insurer or broker who is looking for the fine print; this one is read — or not — by a stranger in a hurry, and "at or before collection" is a prominence claim as much as a placement one. Does it want weight, size, a rule, or the §V.1 all-caps formula? **(iv) Do the Terms bind a portal uploader who was never shown them?** ADR 0054 Option E rejects linking `/terms` on RELEVANCE — the reader's question is what happens to their file, and `/privacy` is that document — and it deliberately does not claim the Terms fail to reach them, because they are drafted to: the Lead accepts on "By creating an account **or using** CompliDrop, you agree to them" and the Acceptable-use clause governs "upload content you don't have the right to upload, or that is unlawful", which is precisely the act performed here. So either the acceptance sentence overstates its reach and wants narrowing, or it holds and a stranger is bound by an agreement this surface never showed them — in which case the link Option E declined may be owed after all. Yes/no, not a unilateral edit in either direction. |
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
