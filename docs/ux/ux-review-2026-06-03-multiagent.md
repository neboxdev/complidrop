# CompliDrop — Fresh multi-agent UX review (2026-06-03)

> **Method.** Independent review run by 18 parallel agents, each reading the *real rendered code* (pages, hooks, components, empty/loading/error branches) for one surface or one cross-cutting journey, plus a dedicated Compliance-Rules redesign agent that also read the backend rule engine. A final synthesis agent deduped 232 raw findings into the themes, priorities, and redesign below. Agents were explicitly told **not** to read the prior `ux-review-2026-06-03.md`, so this is an uncontaminated second opinion. The persona throughout is **"Pat"** — a non-technical, 50-something office manager at a Texas event venue.
>
> **Companion doc:** [`ux-review-2026-06-03-comparison.md`](ux-review-2026-06-03-comparison.md) reconciles this pass with the prior 5-pass review and gives a single merged roadmap. **Read the comparison for the blind spots of this pass** (notably mobile/responsive and accessibility, which this pass under-covered).

## Verdict

**Intuitiveness for a non-technical SMB user: 3 / 10.**

Pat cannot complete the core job today, and the wall she hits is **structural, not cosmetic**. The engineering is genuinely strong — careful error/empty/loading states, jargon-free toasts, a warm vendor portal — but every owner-facing surface assumes Pat already understands the data model and speaks developer English. Two hard blockers stop the loop cold:

1. **The Compliance Rules page is a raw database query-builder** ("Templates", "Operator", "min_value", a free-text "Field" box demanding the secret token `general_liability_limit`) that Pat literally cannot fill in — so she never sets up "what a vendor must prove."
2. **Document upload never asks which vendor a file is for**, and a vendor is never nudged to pick a requirement set — so uploaded COIs land orphaned at "Vendor —" / Compliance "Pending" forever, with no explanation.

On top of that, the first-run dashboard is an all-zeros screen titled "compliance posture" with no "start here," and raw enums (`NonCompliant`, `ManualRequired`, `ExpiringSoon`) plus confidence percentages leak into the UI everywhere. The score is a 3 (not a 1) only because the underlying flows *do* work and the safety nets are above average — but on first run the app answers neither of Pat's questions: "are my vendors covered?" and "what do I do next?"

## The 8 recurring themes

1. **The Compliance Rules page is a developer tool, not a Pat tool — and it's where the whole product's value lives.** To express the one thing she bought CompliDrop for, Pat must invent the internal token `general_liability_limit`, type `1000000` with no `$`/commas, pick `min_value`, and realize "not expired" can't be built here at all. Nav says "Compliance rules"; the page says "Templates." Most Pats abandon at the empty grid → no rules → every vendor stays unchecked → nothing is ever judged compliant.
2. **Raw enums & machine internals leak into the UI as labels.** `NonCompliant`/`ManualRequired`/`ExpiringSoon` badges, snake_case field labels uppercased into what look like error codes, naked `· 87%` confidence, and an activity feed that garbles `complianceTemplate.created` into **"Compliancetemplate · Created"**. These read as *bugs* to a non-technical user. One shared display-map fixes ~8 findings across four pages.
3. **There is no "what do I do next."** The core loop (add vendor → set requirements → get the document → read the result) is never stated. The dashboard's only CTA ("Drop a document") is the *wrong* first step. Empty states ("No vendors yet.") name an absence but never the next click or the payoff.
4. **Upload captures no vendor or document type**, so the compliance answer can never appear (the hook & API already accept both — the dropzone just doesn't pass them).
5. **Insider vocabulary on every page** ("compliance posture", "Awaiting extraction", "Expiry pipeline", "0-30d", "Re-extract", "LLM spend MTD", "subcontractors") signals "this wasn't built for you."
6. **Confidence percentages create anxiety; the "why" is missing.** "72% confident" reads as "wrong 28% of the time," while a *non-compliant* doc shows a red badge but never the stored per-rule reason ("General liability must be at least $1,000,000").
7. **Self-serve dead ends:** no "Forgot password?" anywhere, a lockout message with no duration/escape, and "Generate upload link" that only copies to clipboard with no "email it to the vendor" action.
8. **Trust gaps on the money & marketing surfaces:** no social proof or support link on the marketing site, raw Stripe `past_due` leaking into plan prose, and an Annual card that shows `$39/mo` but hides the **$468 charged today**.

## Top 25 prioritized findings

Severity: 🔴 blocker · 🟠 major · 🟡 minor. Effort: S/M/L.

| # | Sev | Surface | Issue | Fix (short) | Eff |
|---|----|---------|-------|-------------|-----|
| 1 | 🔴 | Rules | Raw query-builder Pat can't use | Rebuild as plain-English requirement checklist (see redesign below) | L |
| 2 | 🔴 | Documents (upload) | No vendor/type at upload → orphaned, never evaluated | After drop, ask "Which vendor?" + "Type?"; pass existing `vendorId`/`documentType`; inline "Assign" on orphan rows | M |
| 3 | 🔴 | Dashboard | All-zeros first run, only CTA is wrong step | When 0 vendors & 0 docs, hide stats, show 3-step "Get started" card | M |
| 4 | 🔴 | Docs list/detail, Export | Raw status enums as badge text | One shared enum→English map applied everywhere incl. ExportService | S |
| 5 | 🔴 | Vendor detail/list | Requirement set defaults to "— none —" with no warning it disables all checking | Relabel "What this vendor must prove"; amber "No requirements set" note; list column → "Set requirements" link | S |
| 6 | 🟠 | Document detail | Field labels render raw `snake_case` uppercased like codes | Field display-name map + drop `uppercase` CSS | S |
| 7 | 🟠 | Doc detail/list | Per-field confidence % creates anxiety | Remove naked `· 87%`; tiered "Double-check this" / "Please verify" prompt | S |
| 8 | 🟠 | Document detail | No "why is this not compliant?" — failed-rule messages computed but never shown | Render `ComplianceCheck` failure notes + "Email {vendor} to fix this" | L |
| 9 | 🟠 | Vendor detail | "Generate upload link" copies silently — no way to send it | Primary "Email this link to {vendor}" via existing Resend; fix copy toast | M |
| 10 | 🔴 | Login / Auth | No "Forgot password?" + dead-end lockout | Add reset link (point to support@ until flow ships); return + show unlock time | M |
| 11 | 🟠 | Dashboard | Activity feed garbles camelCase ("Compliancetemplate · Created") | Replace `prettyAction` regex with a plain-English lookup map | S |
| 12 | 🟠 | Reminders | No empty/loading/error state — blank table reads as broken | Add the three states (mirror the history card) | S |
| 13 | 🟠 | Reminders | Column labels hide who gets the email | "Email me (and my team)" / "Email the vendor" / "Send reminder" / "On" | S |
| 14 | 🟠 | Reminders (history) | Raw `bounced`/`complained` with no meaning | Map to "Bad email address" / "Marked as spam" + tooltips; add a "Vendor" column | M |
| 15 | 🔴 | Settings (billing) | Raw Stripe `past_due` leaks into prose | Map status to a friendly banner; never interpolate the raw value | S |
| 16 | 🟠 | Settings/pricing | Annual `$39/mo` hides `$468` charged today | Show `annualBilledLabel` (already in `plans.ts`): "billed $468/year · save $120" | S |
| 17 | 🟠 | Document detail | "Re-extract" is unexplained; risks overwriting edits | Rename "Read again" + tooltip warning; disable while processing | S |
| 18 | 🟠 | Dashboard | Chrome jargon + hardcoded chart max=10 | Rename "compliance posture"/"Expiry pipeline"/"0-30d"; dynamic chart max; clickable buckets | S |
| 19 | 🟠 | Document detail | Processing error card shows raw internal error string | "We couldn't read this document" + map known codes; hide raw behind "Details for support" | M |
| 20 | 🟠 | Document detail | `ManualRequired` has no call-to-action | Amber card "Check the amber fields, fix any, then Save"; highlight low-confidence fields | M |
| 21 | 🟠 | Landing | Zero social proof, no support/contact link | Add a testimonial block + footer "Questions? support@…"; de-jargon pricing cards | M |
| 22 | 🟠 | Export | PDF prints raw user GUIDs; silently capped at 500 events | Join `AuditLog`→`Users` for names; print "(showing 500 most recent)"; clarify date-range scope | M |
| 23 | 🟠 | Vendors/Rules | "Template"/"subcontractors" don't match Pat's model | Standardize on "Requirements"/"Requirement checklist"; venue-native copy & placeholders | S |
| 24 | 🟠 | Settings | "LLM spend MTD" is pure engineering jargon | "AI reading cost this month (included in your plan)" or remove the tile | S |
| 25 | 🟡 | Docs/Vendor/Rules | Native `confirm()` deletes with no consequence context | Styled confirmation naming the consequence; aria-label the revoke X | M |

> Full per-surface detail (232 findings) is in `.claude/ux-surfaces.json`; the structured synthesis in `.claude/ux-synthesis.json`.

## Centerpiece: Compliance Rules redesign

**Verdict: rebuild required.** This is the single biggest blocker. Pat literally cannot author the one rule she came for, and is actively *misled* into thinking she configured "not expired" when she did not. The good news: **it's almost entirely a frontend change** against the existing rule engine.

### The reframe
Stop calling it "Templates" (a database query-builder). It's **"Vendor requirements"** — a plain-English checklist Pat already owns from her printed vendor packet. **One checklist = one vendor type** (Caterer, DJ, Florist, Photographer, Rental company). Each requirement is a sentence she'd say out loud — *"Carries at least $1,000,000 in general liability insurance"*, *"Insurance has not expired"* — built by **picking from a small menu, never by typing field names or operators**.

### Proposed UI (in order)
- **Header/intent line** — "Vendor requirements" / "Set what each kind of vendor must prove. We check every document you upload against this list automatically." (Sidebar nav label changes too.)
- **Left rail** = checklists labeled by vendor type, count line "4 requirements · used by 3 vendors". Ready-made ones under "Suggested checklists" with a "Use this" button (clones an *editable* copy — drop the read-only "system" badge).
- **"+ New checklist"** opens "What kind of vendor is this for?" with a type picker and "Start from our suggested checklist" preselected.
- **Requirement lines as sentences** (read view) with green check + Edit/Remove. No doc-type/field/operator/message columns ever shown.
- **"+ Add a requirement" menu** — grouped **Insurance / Dates / Licenses & permits**. Each item is a sentence with at most one fill-in. `min_value` reads "must be at least" (implied by wording — no operator shown).
- **Money input** that displays `$1,000,000` (commas + `$`, presets $500k/$1M/$2M/custom) but stores the bare integer `1000000`. Pat never learns the no-commas rule.
- **"Document must not be expired" one-click toggle** with *honest* helper text: "We mark the document Expired the day its expiration date passes, and warn you 30 days before."
- **Live summary** — "A caterer is compliant when: they carry at least $1,000,000 general liability and their insurance has not expired."
- **Bridge to vendors** — checklist shows "Vendors using this checklist"; the vendor edit screen's dropdown is relabeled "Requirement checklist" so both screens speak the same words.

### Worked example (Pat sets up "$1M liability, not expired" for caterers)
1. Click **Vendor requirements** → see suggested "Caterers".
2. **+ New checklist** → pick "Caterer" → "Start from suggested" → Create.
3. **+ Add a requirement** → Insurance → "Minimum coverage amount" → type "General liability", amount "$1,000,000". UI assembles *"Carries at least $1,000,000 in general liability insurance"* and POSTs `{documentType:"coi", fieldName:"general_liability_limit", operator:"min_value", expectedValue:"1000000", errorMessage:"General liability is below the $1,000,000 you require."}`.
4. **+ Add a requirement** → Dates → tick "Document must not be expired" → POSTs `{fieldName:"expiration_date", operator:"required"}`.
5. Summary reads back what she built. She clicks **Assign to vendors** (or picks "Caterers" on the vendor screen).
6. Next caterer COI uploaded → engine checks `general_liability_limit ≥ 1000000` and that an expiration date exists, and auto-marks Expired if the date passed. **Pat never typed a field name, operator, or unformatted number.**

### Honesty note the redesign verified in the backend
`ComplianceCheckService` has **no "date in the future" operator**. A `required` rule on `expiration_date` only checks a date was *found*; the Expired/ExpiringSoon status is computed *separately and automatically* from `doc.ExpirationDate`. So the "not expired" toggle must be sold as (a) it guarantees a missing-date COI is flagged + (b) the automatic expiry engine does the date math — **not** as a rule that compares to today. A vendor with no/zero rules lands at **Pending** (never Compliant). The toggle copy above is worded to stay truthful.

### Migration cost
**Almost entirely frontend.** Every plain-language requirement maps onto the existing `UpsertRuleRequest` shape that `POST /api/compliance/templates/{id}/rules` already accepts. The money formatter and sentence-assembly are pure client logic; `errorMessage` is generated client-side. **Two small backend items are optional:**
1. A `clone` endpoint for suggested checklists — *or* fake it for MVP by POSTing a new template and replaying each suggested rule through existing endpoints (no migration).
2. Vendor *type* is currently free-text `Vendor.Category`, not linked to checklist selection — treat type as a display label for MVP; a nullable `VendorType` column can come later.

### Open questions for Ruben
- One checklist per vendor, or can a vendor need several (a caterer that also rents equipment)? Today `Vendor.ComplianceTemplateId` is a single FK.
- For "names your venue as additional insured," what exact `expectedValue` string — the venue legal name (risk of false negatives) or a generic token?
- Real `clone` endpoint vs frontend-replay for MVP?
- Reframe the seeded checklist names ("General Sub Contractor", "Property Vendor"…) to venue types (Caterer, DJ/Band, Florist…)?
- Surface per-vendor "why not compliant" notes (finding #8) as a fast-follow?

## Quick wins (high impact, each well under a day)
1. Shared enum→English display map applied everywhere (kills ~8 findings).
2. Remove naked `· 87%`; swap per-field % for a tiered "Double-check this" prompt.
3. Rename dashboard copy ("compliance posture", "Awaiting extraction", "Expiry pipeline", "0-30d") + dynamic chart max.
4. Field display-name map + drop uppercase CSS on document-detail labels.
5. Rename "Re-extract" → "Read again" with overwrite-warning tooltip.
6. Replace `prettyAction` regex with a plain-English activity-feed lookup map.
7. Map raw Stripe status to a friendly sentence; render `annualBilledLabel` under the Annual price.
8. Add "Forgot your password?" under the login Password field (point to support@) + footer "Questions? support@…".
9. Amber "No requirements set" note when a vendor's checklist is "— none —"; Vendors column empty value → "Set requirements" link.
10. Rewrite dead-end empty states into one-line guides stating the next click and payoff.

## Biggest churn risks
1. **Rules page unusable** → no rules → nothing ever judged compliant → product delivers zero value → churn in the first session.
2. **Silent orphaned uploads** → core question "is this vendor covered?" never answered → "the app is broken."
3. **First-run dead end** → non-technical user stalls in the first two minutes.
4. **False sense of security** → a vendor left at "— none —" is never evaluated, yet nothing says so — a real liability exposure for a *compliance* product.
5. **Jargon-as-bugs** erodes trust even where flows work.
6. **Broken self-serve escapes** (password reset; "email the vendor the link").
7. **Conversion/billing trust gaps** (no social proof, hidden $468, raw `past_due`).

## Known blind spots of THIS pass — now covered by the gap pass
This review optimized for copy, information architecture, flow, and jargon, and **under-covered mobile, accessibility, performance, forms behavior, and functional escape-hatches** (0 mobile findings, 3 a11y findings here). A dedicated **gap pass** has since covered exactly those dimensions — **104 net-new findings (16 blockers)**, including the confirmed mobile P0 (inline `240px` sidebar, `overflow-hidden` tables), the reminder-toggle a11y failure, sub-44px touch targets, AA contrast failures, and silent data-loss one-way doors (email never verified, write-once timezone, missing pagination, valid-session eviction).

➡️ See [`ux-review-2026-06-03-gap-pass.md`](ux-review-2026-06-03-gap-pass.md) for those findings and [`ux-review-2026-06-03-comparison.md`](ux-review-2026-06-03-comparison.md) for the full reconciliation + final merged roadmap.
