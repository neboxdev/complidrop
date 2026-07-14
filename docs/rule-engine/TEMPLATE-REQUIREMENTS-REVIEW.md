# Template requirements review — insurance defaults for the five system checklists

> **NON-ATTORNEY, NON-BROKER WORK PRODUCT — NOT LEGAL ADVICE AND NOT INSURANCE
> ADVICE.** Researched and written by an AI system (Claude) on 2026-07-10 at the
> founder's direction, mirroring the posture of
> [G1-LEGAL-RESEARCH.md](G1-LEGAL-RESEARCH.md): it is structured for a licensed
> Texas attorney and a Texas hospitality insurance broker to **validate rather
> than research from scratch**. Every claim carries a verification tier in §9;
> anything not verified against a primary source this session (or against the
> in-repo rules-research dossier, whose citations were fetched live from
> official hosts on 2026-07-07/08) is marked. Industry custom is labeled
> custom, never dressed as law. Feeds tickets
> [#400](https://github.com/neboxdev/complidrop/issues/400),
> [#396](https://github.com/neboxdev/complidrop/issues/396),
> [#397](https://github.com/neboxdev/complidrop/issues/397).

Scope: the five seeded **checklist templates** in
[ComplianceTemplateSeed.cs](../../api/CompliDrop.Api/Data/Seed/ComplianceTemplateSeed.cs)
(the contractual tuple engine venues use today) — NOT the flag-off regulatory
rule engine (`RuleData/`), which already encodes the conditional statutory
obligations this memo repeatedly notes as "inexpressible in a checklist."

---

## 0. What the engine can physically see (grounding for every verdict below)

Three facts about the current implementation bound everything this memo can
recommend:

1. **A rule only grades a document of its own `documentType`**
   ([ComplianceCheckService.cs:284](../../api/CompliDrop.Api/Services/ComplianceCheckService.cs)).
   The vendor rollup treats every distinct `documentType` in the checklist as a
   mandatory upload: a required type with no document reads **Missing**
   (fail-closed, [VendorEndpoints.cs:97](../../api/CompliDrop.Api/Endpoints/VendorEndpoints.cs)).
   So *which document types a template mentions* is itself a requirement, and a
   spurious type (Security's `certification`, Photographer's `license`) creates
   a permanent false "Missing".
2. **`min_value` on a missing field fails closed** ("Field missing.",
   [ComplianceCheckService.cs:373](../../api/CompliDrop.Api/Services/ComplianceCheckService.cs)).
   A rule on a field the extractor never emits fails **every** vendor. The live
   extraction prompt (v2, [ExtractionPrompts.cs](../../api/CompliDrop.Api/Services/Extraction/ExtractionPrompts.cs))
   has **no `liquor_liability_limit` field** — the proposed liquor rule ships
   dead-on-arrival unless the extractor learns the field in the same change.
3. **A COI is not the policy.** ACORD 25's own text disclaims conferring
   rights and states that additional-insured status requires policy provisions
   or endorsement (§3, §9). Every "Covered" this product emits is at best
   "the certificate *shows*/says X as of its issue date" — never "the vendor
   *is* covered". This ceiling is why several marketing claims need softening
   (§7) regardless of which rules we seed.

### The seeder-inertness constraint, corrected against current code

The stated constraint (reconcile on `(documentType, fieldName, operator)`,
values never updated) describes the in-flight #400 seeder. **Current `main` is
more inert still**: `EnsureAsync` skips an entire template when its *name*
already exists ([ComplianceTemplateSeed.cs:60](../../api/CompliDrop.Api/Data/Seed/ComplianceTemplateSeed.cs)),
so for a database that has already booted once — **production has: these five
templates shipped with #192 in early June** — every change below (added rules,
removed rules, changed values) is silently inert via the seed alone.

Consequences, stated plainly:

- **Photographer GL `500000` and Transportation auto `1000000` are already
  frozen in prod.** Editing the seed to `1000000`/`1500000` fixes only fresh
  databases. Shipping the right values requires a **data migration** that
  updates the existing system-template rows (precedent:
  `RenameSystemTemplatesToVenueTypes`, `DedupeAndGuardSystemTemplates`), and
  the migration must dodge [#412](https://github.com/neboxdev/complidrop/issues/412)
  (no unique index on the rule natural key → concurrent double-boot duplicates).
- Rule **removals** (Security's `certification`, Photographer's `license` and
  E&O, Transportation's `equals CDL`) likewise need the migration, and each
  removal must trigger the existing template-mutation re-grade fan-out
  (`ReevaluateForTemplateAsync`) so stale checks don't linger.
- Per-tenant **clones** of system templates are user data. The migration should
  touch only `IsSystemTemplate=true` rows; existing clones keep whatever the
  venue chose (documented, not silently rewritten).
- **New template names are safe to add** any time (name-level reconcile inserts
  unknown names).

---

## 1. Executive summary — the five changes that most reduce a false "Covered", ranked

1. **Pin `general_liability_limit` to the "Each Occurrence" ACORD cell (#397).**
   Today the extractor is told only "general_liability_limit"; an ACORD 25
   displays six GL dollar cells, and if the model returns the $2,000,000
   General Aggregate, a $500,000-per-occurrence policy passes every
   `min_value 1000000` rule. This is the highest-probability fail-open in the
   product and it silently poisons *all four* templates that check GL plus the
   auto rule. **Immediate fix (no schema surgery): one sentence in the
   extraction prompt pinning the field to the EACH OCCURRENCE cell + version
   bump. Durable fix: split fields (§5) and migrate rules.**
2. **Stop asserting additional-insured coverage the certificate cannot prove
   (#396).** The ADDL INSD "Y" column is an agent's representation; ACORD 25's
   IMPORTANT box says AI status exists only if the policy is endorsed. The
   engine's check ("box checked + name appears") is a reasonable *screen* but
   the UI sentence "Names 'X' as additional insured" and the marketing line
   "flagging anyone who listed you as certificate holder instead of additional
   insured" overstate it. Reword to "certificate indicates…" and tell the venue
   to collect the endorsement page (CG 20 26-class, §3). This is a wording fix
   with liability weight, not a rule change.
3. **Give Security Service an insurance requirement and delete the phantom
   `certification` requirement.** As shipped, a guard company with **zero
   insurance** reads Covered once its license is on file — the only template
   whose dangerous gap is *absence of any insurance rule*. Add
   GL ≥ $1,000,000 + COI expiration. Simultaneously accept and document the
   residual hole this memo cannot close: assault & battery, the very loss mode
   guards exist for, is commonly excluded or sublimited by endorsement and is
   invisible to the extractor (§5, broker question B-2).
4. **Raise the shuttle auto floor to $1,500,000 and drop `license_type equals
   "CDL"`.** $1,000,000 sits *below* the federal financial-responsibility floor
   for even the smallest for-hire interstate passenger vehicle ($1.5M for ≤15
   seats, $5M for 16+ — 49 CFR 387.33T, verified). Meanwhile the CDL rule fails
   every lawful ≤15-seat shuttle driver (CDL attaches at 16+ seats incl.
   driver — 49 CFR 383.5, verified) — pure fail-closed noise that trains venues
   to override red badges, while catching nobody the auto rule misses. The 16+
   → $5M / CDL-P conditional is inexpressible in a checklist; it already lives
   in the regulatory engine and goes in the gap table + template description.
5. **Ship liquor liability as a working check, not a dead rule (#400).** Add
   `liquor_liability_limit` to the extraction prompt (v3 + version bump), the
   `min_value 1000000` rule on Caterer, a liquor line on the sample COI
   (`SampleCertificateGenerator` — otherwise the one-click demo flips
   NonCompliant), and a "Liquor liability" preset in
   [requirements.ts](../../frontend/src/lib/requirements.ts). Expect honest
   fail-closed friction: liquor liability has no dedicated ACORD 25 section and
   often rides an OTHER row, the description box, or a separate certificate
   (§1.5) — a red "no liquor liability found" that prompts a phone call is the
   correct behavior for a wedding-venue product whose worst case is a
   dram-shop death claim.

Ranked-out but load-bearing: the Photographer template's spurious `license`
requirement (no Texas photographer license exists — verified absence) and its
$500k GL are #6; they mostly generate false "Action needed", but chronic false
red is how users learn to stop believing red.

---

## 2. Per-template findings

### 2.1 Caterer ("Typical insurance for a food & beverage caterer, including bar / alcohol service.")

| Current rule | Verdict | Reasoning |
|---|---|---|
| `("coi","general_liability_limit","min_value","1000000")` | **KEEP** (value) / **CHANGE** (field semantics) | $1M per-occurrence is the standard venue ask (custom, §9 C-1). But the field must be pinned to EACH OCCURRENCE (#397, §4) or the check is unsound. |
| `("coi","expiration_date","required",null)` | **KEEP** | Foundation of the whole expiry pipeline. |
| `("coi","workers_comp_limit","required",null)` | **KEEP, with honest framing** | Not TX law — see below. Defensible as a contractual demand; fail-closed for lawful non-subscribers. Copy must never say "required by law". |
| `("coi","liquor_liability_limit","min_value","1000000")` *(proposed)* | **ADD — with the 4-part companion change** | See liquor analysis below. Dead-on-arrival without the extractor field; demo-breaking without the sample-COI line. |

**Liquor liability — the analysis (question 1).**

- *Host liquor vs. liquor liability (form text verified this session from a
  hosted CG 00 01 04 13 specimen).* The ISO CGL exclusion c. (Liquor
  Liability) removes coverage for causing/contributing to intoxication,
  furnishing to minors or the intoxicated, and statutory liquor liability —
  but, verbatim: "However, this exclusion applies only if you are **in the
  business of manufacturing, distributing, selling, serving or furnishing
  alcoholic beverages**." An insured *not* in that business retains so-called
  host liquor coverage inside its CGL — and the 04 13 edition adds, verbatim,
  that "permitting a person to bring alcoholic beverages on your premises …
  is not by itself considered the business of selling, serving or furnishing"
  — i.e. the base form *protects* a BYOB venue's host-liquor coverage (only
  the optional CG 21 50 / CG 21 51 endorsements strip it). A caterer or bar
  service that serves alcohol as part of its offering **is** in the business —
  its CGL excludes the loss, and it needs a separate liquor liability policy
  (ISO CG 00 33 occurrence / CG 00 34 claims-made) or carrier equivalent. So
  for a bar-service caterer at a TX wedding, **host liquor is not enough; the
  checklist must look for a liquor liability line.**
- *Texas dram-shop law (verified this session, official statute text).*
  Tex. Alco. Bev. Code **Chapter 2** is the Dram Shop Act. §2.02(b): providing
  is actionable on proof that "at the time the provision occurred it was
  apparent to the provider that the individual … was obviously intoxicated to
  the extent that he presented a clear danger to himself and others" and that
  the intoxication proximately caused the damages. §2.03(c): the chapter is
  "the exclusive cause of action for providing an alcoholic beverage to a
  person 18 years of age or older." §2.02(c): a narrow social-host action
  against adults 21+ who knowingly furnish (or knowingly allow on premises
  they own/lease) alcohol to minors **under 18**. **"Provider" (§2.01,
  verbatim):** "a person who sells or serves an alcoholic beverage under
  authority of a license or permit … **or who otherwise sells** an alcoholic
  beverage to an individual." So a *licensed* person is a provider whether it
  sells or serves; an *unlicensed* one only if it **sells** — and TABC treats
  alcohol bundled into a package price as a sale (next bullet). The
  **§106.14(a) safe harbor**: an employee's unlawful service is not attributed
  to the employer when (1) the *employer requires* TABC-approved seller
  training, (2) the employee actually attended, and (3) the employer didn't
  directly or indirectly encourage the violation. The Texas Supreme Court
  applies §106.14 directly to Chapter 2 dram-shop claims and lets plaintiffs
  pierce it with evidence of the provider's own negligence (*20801, Inc. v.
  Parker*, 249 S.W.3d 392 (Tex. 2008) — secondary-hosted opinion text) — which
  is why venues customarily ask that bar staff be "TABC-certified" even though
  the training is statutorily voluntary.
- *Who's exposed when the caterer holds the permit.* The permitted caterer is
  the "provider." A venue that neither holds a permit, nor sells, nor serves
  is outside §2.01's provider definition for adult guests (the only
  premises hook in the chapter is the §2.02(c) minors-under-18 provision) —
  but plaintiffs routinely name the venue and host anyway
  (premises/negligence theories; labeled secondary), so the vendor's liquor
  policy + additional-insured status is exactly the protection stack the
  marketing FAQ describes. The allocation question is attorney item A-1 (§8).
- *TABC permits — corrected against current law this session.* The old
  **Caterer's Permit (CB), ABC ch. 31, was repealed effective 2021-09-01**
  (HB 1545 §410(a), 86th Leg. 2019); no "Caterer's Certificate" exists in the
  current code, and legacy TABC packets that cite it (Form L-CCFP) are stale.
  Today a caterer that itself sells/serves spirits holds a **Mixed Beverage
  Permit (ch. 28)** and reaches off-site private events through **§28.19
  temporary-location sales**, administered via TABC's **File and Use
  Notification (FUN)** — no pre-approval for a private event with ≤500
  attendance and <$10,000 wholesale alcohol value — or **Temporary Event
  Approval (TEA)** for everything else; nonprofits use the **NT** permit
  (ABC §30.01). Beer/wine-only → the consolidated Wine & Malt Beverage
  Retailer's permit (ch. 25). Two-year terms (§11.09(a)). **No sale → no
  permit**: TABC's FAQ, verbatim — "It is legal to provide free alcoholic
  beverages without a permit" — provided there is genuinely no expectation of
  money; TABC's own examples make a **cash bar, ticketed entry with drinks,
  a tip jar, or alcohol bundled into a package price a SALE** requiring a
  permit. A true host-paid open bar or BYOB needs none. **Recommendation: do
  NOT seed a required TABC `permit` document on the Caterer checklist.** Many caterers lawfully
  serve under the venue's or a bar service's permit (dossier-verified custom),
  so a required permit doc would mass-fail compliant vendors; the conditional
  "if the caterer provides the alcohol → ask for its TABC permit" already
  lives in the regulatory engine (`tx-caterer-tabc-mixed-beverage`,
  `servesOrSellsAlcohol` gate). A venue that knows its caterer pours can add a
  `("permit","expiration_date","required",null)` rule to its clone in the UI.
- *Amount.* **$1,000,000 per occurrence** is the customary venue ask for liquor
  liability (custom — broker confirm B-1). Liquor policies typically state an
  each-common-cause limit plus an aggregate; $1M/occ is the number to gate on.
- *Can extraction even see it?* ACORD 25 (2016/03) has **no dedicated liquor
  section** — GL, Auto, Umbrella, WC, then blank rows. Brokers evidence liquor
  liability by typing it into a blank OTHER row with its limit, noting it in
  Description of Operations, or issuing a second certificate. Expect the
  extractor to find a number **most but not all** of the time (medium
  reliability, §5). The failure direction is the safe one: `min_value` on a
  missing field fails closed → "Action needed" → the venue asks the vendor for
  the liquor certificate. That friction is acceptable for the loss mode with
  the fattest tail (a dram-shop death claim); what would NOT be acceptable is
  green with no liquor check at all — which is what ships today.
- *One template or two?* **For:** a separate "Bar / Alcohol Service" template
  spares food-only caterers (drop-off lunch caterers, dessert vendors) a
  permanent red and protects the venue's trust in red badges. **Against:** it
  makes the safe outcome depend on the venue *correctly re-assigning* the
  vendor — the exact human step this product exists to remove; a venue that
  leaves a pouring caterer on plain "Caterer" gets a silent false Covered.
  At TX wedding venues the caterer running the bar is the modal case, not the
  edge. **Recommendation: keep the liquor rule on the default Caterer template
  (fail-closed default), say "including bar / alcohol service" in its
  description, and let food-only venues delete the one rule from their clone —
  a one-click, per-tenant loosening the UI already supports.** Splitting the
  template is the tunable alternative if support tickets show food-only
  caterers dominate; revisit with usage data, not now.

**Workers' compensation — the analysis (question 2).**

- *The law, verified (this session, official statute text + TDI):* Tex. Labor
  Code **§406.002(a)** — "Except for public employers and as otherwise
  provided by law, an employer **may elect** to obtain workers' compensation
  insurance coverage." Texas-unique per the regulator itself (TDI/DWC:
  "Today, Texas is the only state that allows private employers to choose…").
  A **non-subscriber** must notify DWC (§406.004; form **DWC005**, annual
  cadence set by commissioner rule/TDI guidance, not the statute's text) and
  its employees at hire + by posted notice (§406.005). When sued by an
  injured employee it **loses the common-law defenses** — §406.033(a)
  verbatim: "it is not a defense that: (1) the employee was guilty of
  contributory negligence; (2) the employee assumed the risk…; or (3) the
  injury or death was caused by the negligence of a fellow employee" — with
  pre-injury waivers void (§406.033(e)) and compensatory damages uncapped
  (only the generally applicable exemplary-damages cap, CPRC §41.008,
  applies). §406.096 (mandatory coverage on governmental construction
  contracts) is the exception proving the elective default; a *private*
  customer imposing WC by contract rests on ordinary freedom of contract —
  nothing prohibits it, nothing codifies it. §406.097 additionally lets sole
  proprietors / partners / 25%+ officers exclude *themselves* by policy
  endorsement even when the business subscribes — which is what the ACORD 25
  "ANY PROPRIETOR/PARTNER/EXECUTIVE OFFICER/MEMBER EXCLUDED?" box surfaces.
- *So is requiring `workers_comp_limit` "wrong"?* As a statement of law, yes —
  a lawful non-subscriber caterer fails the rule. As a **contractual demand it
  is standard and sensible** (custom): the venue's exposure when a vendor's
  uninsured employee is hurt on site is (a) the employee sues everyone,
  venue included, in ordinary negligence — no exclusive-remedy bar protects
  the venue (it isn't the employer), and (b) an uninsured vendor facing a
  defense-stripped §406.033 suit is judgment-thin, making the venue the
  deepest pocket standing. Requiring WC (or an occupational-accident
  alternative, broker item B-3) is precisely how venues shift that.
- *What the engine can know:* it cannot know "has employees" — no such field,
  and `description_of_operations` sniffing would be brittle. The options are
  require-for-all (fail-closed friction for sole proprietors and lawful
  non-subscribers) or drop-to-nothing (fail-open for the staffed caterer, the
  modal case). **Keep the rule** (over-requiring is the safe direction); the
  venue overrides or deletes per-tenant for a known solo operator.
- *What a non-subscriber's COI shows (Texas agents-association practice,
  verified):* IIAT's certificate best-practices instruct agents, verbatim,
  "Do not use this section for occupational accident and employers' liability
  written for non-subscribers. Use the blank section instead." So a correctly
  issued non-subscriber certificate has a **blank WC section** (any
  occupational-accident program appears as a named policy in a blank OTHER
  row) — extraction emits nothing → `required` fails → Action needed → human
  conversation. That is the correct fail-closed flow. A **subscriber's** COI
  shows the "PER STATUTE" checkbox plus Employer's-Liability cells (E.L. EACH
  ACCIDENT / E.L. DISEASE - EA EMPLOYEE / E.L. DISEASE - POLICY LIMIT —
  captions verified against 2016/03 specimens); today's `workers_comp_limit`
  in practice captures the E.L. dollar figure, since "statutory" isn't a
  number — presence (`required`) is therefore the right operator, and a
  `min_value` on this field would be semantically confused until an
  `employers_liability_each_accident` field exists (§5).
- *Copy check:* the error message "Workers comp coverage is required." is
  contract-framed — acceptable. Nothing in the product may say Texas law
  requires it. The venue landing page already hedges ("where required by your
  state") — fine, and worth a TX-specific footnote eventually since the
  beachhead state is the one state where it's elective.

### 2.2 Event Rental Company

| Current rule | Verdict | Reasoning |
|---|---|---|
| `("coi","general_liability_limit","min_value","1000000")` | **KEEP** (same #397 caveat) | $1M/occ standard (custom). |
| `("coi","expiration_date","required",null)` | **KEEP** | — |

- **Tents:** no Texas state tent permit exists; membrane structures are a
  **local fire-marshal matter** (IFC-based, permits typically >400 sq ft, NFPA
  701 flame certs) — verified absence, in-repo dossier. Conditional and local →
  not seedable; noted in the gap table and suited to the regulatory engine's
  `localObligations` surfacing.
- **Inflatables are the exception with a real statutory floor:** Tex. Occ. Code
  **§2151.1012** mandates amusement-ride liability insurance — $1,000,000
  per-occurrence CSL for continuous-airflow inflatables — filed with TDI
  (verified, in-repo dossier). A venue whose rental vendor brings bounce
  houses should demand that COI; conditional (not every rental vendor) → gap
  table + description note, regulatory engine already encodes it.
- **Inland marine / equipment floaters:** protects the *vendor's own* gear; the
  venue has no insurable interest. Don't require (custom).
- **"Damage to Rented Premises":** verified form mechanics (CG 00 01 04 13
  specimen): a separate limit covering (a) non-fire property damage to
  premises "rented to you for a period of seven or fewer consecutive days"
  and (b) **fire** damage to premises "rented to you or temporarily occupied
  by you with permission of the owner" — so for a day-of vendor its realistic
  trigger is fire damage while occupying the venue. Other vendor damage to
  venue property in the vendor's care/custody/control can fall in the CGL's
  CCC exclusion; real protection there is contractual (damage deposit,
  indemnity), not COI-checkable. Venues customarily set no vendor DTRP
  minimum (verified custom). No seeded minimum; broker refinement item B-6.
- **Workers' comp for tent crews** (installation is the injury-heavy part):
  legitimate optional add, same §406.002 analysis as the caterer. Left off the
  default to keep non-subscriber friction on the one template where staffing
  is near-universal (Caterer); venues with rigging-heavy vendors add it
  per-tenant.

### 2.3 Security Service

| Current rule | Verdict | Reasoning |
|---|---|---|
| `("license","license_number","required",null)` | **KEEP** | The DPS **company** license (security services contractor — DPS "Class B", or C for combined — Tex. Occ. Code §1702.102; DPS administers per §1702.005) is exactly the document a venue collects; it bears a number and expiry, and the number is **publicly verifiable in TOPS** (tops.portal.texas.gov — verified live), a natural future product hook. |
| `("license","expiration_date","required",null)` | **KEEP** | License term ≤ 2 years (§1702.301). |
| `("certification","expiration_date","required",null)` | **REMOVE** | Maps to no document a guard *company* hands a venue. Individual credentials — commissioned officer (§1702.161), noncommissioned officer (§1702.221), personal protection officer **license** (§1702.201; post-SB 616 a license, not an endorsement) — are **per-worker DPS pocket cards** with photo + expiration (§§1702.165(b), 1702.232), verified this session. The checklist engine has no per-worker docs (the regulatory engine models them). As shipped this rule makes every security vendor read "Missing: certification" forever → override training. |
| `("coi","general_liability_limit","min_value","1000000")` *(proposed)* | **ADD** | See below. |
| `("coi","expiration_date","required",null)` *(proposed)* | **ADD** | Every template that requires a COI must require its expiry; without it the expiry pipeline has nothing to run on. |

- **The statutory floor is far below the venue ask.** §1702.124(a),(c): a
  license precondition is GL insurance of **$100,000/occ bodily injury &
  property damage, $50,000/occ personal injury, $200,000 aggregate**
  (verified on the official host 2026-07-08, screenshot evidence in
  `audit/evidence/g2/`). So "licensed" implies only token coverage — requiring
  the license alone (the shipped template!) certifies a vendor whose statutory
  minimum coverage wouldn't cover one serious injury. **$1M/occ GL is the
  customary venue demand** (custom, broker confirm B-1).
- **Assault & battery — the honest hole.** Industry custom, verified this
  session against three broker/industry sources (IRMI itself was
  inaccessible; broker item B-2 stands): guard-company CGL policies commonly
  exclude or sublimit A&B ("Standard Commercial General Liability (CGL)
  policies frequently contain Assault & Battery exclusions" — Leavitt Group),
  restoring it by endorsement, often at a sublimit below the CGL limit with
  defense costs sometimes eroding it. A&B is *the* guard loss mode. **No
  ACORD 25 cell carries it** (verified against the complete 2016/03 specimen
  text); if visible at all it's free text in Description of Operations or a
  named policy in a blank OTHER row. A bare `general_liability_limit ≥ 1M`
  rule can therefore certify a vendor whose relevant coverage is a $25k
  sublimit — fail-open, inexpressible today (§5, §6). Mitigations: gap-table field
  (`assault_battery_limit`, low extraction reliability), template description
  telling the venue to ask for "A&B included at full limits" in writing, and
  the broker question.
- **Armed vs unarmed:** armed service adds per-officer commissions (§1702.161)
  and customarily higher insurance expectations — per-worker + conditional →
  regulatory engine territory, description note only.
- **Any-license-passes caveat:** nothing pins the uploaded license to a DPS
  §1702 contractor license — a food-handler card would satisfy both license
  rules (fail-open, §6). A `("license","issuing_authority","contains","Public
  Safety")` rule is *expressible* today and worth considering, flagged brittle
  (OCR/abbreviation variance produces false reds). Recommended as a fast
  follow after observing real uploads, not in the first seed.

### 2.4 Transportation / Shuttle

| Current rule | Verdict | Reasoning |
|---|---|---|
| `("coi","auto_liability_limit","min_value","1000000")` | **CHANGE value → `1500000`** | $1M is below any applicable federal for-hire floor: 49 CFR **387.33T** Schedule of Limits, verbatim rows verified on eCFR this session — "16 passengers or more, including the driver — **$5,000,000**"; "15 passengers or less, including the driver — **$1,500,000**" (applies to for-hire passenger carriers in interstate commerce, §387.27(a); taxicab exception = seating <7, no regular route). TX intrastate tiers (43 TAC 218.16(a), Figure verified against the 2024 adopted rule): **$500,000** for >15-but-<27 people incl. driver, **$5,000,000** for 27+; **≤15 seats intrastate has NO TxDMV minimum at all** — only the general Texas 30/60/25 floor (Transp. Code §601.072(a-1), verified) plus any municipal ordinance. So $1M already exceeds every *intrastate* floor; $1.5M is the lowest number that isn't below a *federal* floor the vendor may be subject to, and it's the figure event-contract custom uses for small shuttles (e.g. the CAPTA vendor packet: "$5,000,000 limit required. $1,500,000 for limousines with 15 or less passengers"). The 16+ → $5M case is inexpressible (no seating-capacity input) — description note + gap table; residual fail-open acknowledged in §6. **Value change requires the data migration (§0) — shipping 1.5M in the seed alone fixes nothing in prod.** |
| `("license","license_type","equals","CDL")` | **REMOVE** | CDL attaches at a **design capacity of 16+ passengers including the driver** (49 CFR 383.5 Group C, verbatim-verified; P endorsement per §383.93(b)(2) *read with* §383.5). Texas doesn't restate the number — Transp. Code §522.003(5) incorporates 49 CFR 383.5 **by reference** (verified). A 14-passenger shuttle driver lawfully holds a regular license; this rule permanently fails the modal wedding-shuttle vendor. It also protects nothing: a venue can't verify from a license *document* that the *person driving that night* is the license holder. The conditional CDL-P rule already exists in the regulatory engine (`tx-transportation-cdl-passenger-endorsement`). |
| `("license","expiration_date","required",null)` | **KEEP** | A current driver credential from the operator is a reasonable contractual ask. |
| `("license","license_number","required",null)` *(proposed)* | **ADD** | Presence-shaped replacement for the CDL rule; keeps the license upload meaningful. |
| `("coi","expiration_date","required",null)` *(proposed)* | **ADD** | Same rationale as Security. |

- **USDOT / TxDMV registration as a required `permit`:** recommend **not
  seeding**. TxDMV registration attaches intrastate only when a vehicle is
  designed/used for **more than 15 passengers including the driver**
  (§643.051(a) → §548.001(1)(B), verbatim-verified; exemptions in §643.002 —
  notably entities "whose primary function is not the transportation of
  passengers" and exclusively-interstate carriers), and TxDMV's own bus-operator
  guidance requires those carriers to hold **both** a USDOT number and a TxDMV
  certificate, with the insurance **cab card** carried in the vehicle. FMCSA
  operating authority applies only interstate — though note the 49 CFR
  **390.5T trap** (verified): a **9–15-passenger for-hire vehicle in
  interstate commerce** is still an FMCSR-regulated CMV (USDOT registration,
  driver rules) even with no CDL needed. Charter operators may hold
  **non-expiring** TxDMV certificates (dossier-recorded limitation) — a
  required permit doc would fail lawful small operators and confuse the
  compliant big ones. Regulatory engine handles the conditionals; venues with
  big-bus vendors add a permit rule per-tenant.
- **Scheduled vs hired/non-owned auto:** the ACORD auto section shows
  checkboxes (ANY/OWNED/SCHEDULED/HIRED/NON-OWNED) plus one CSL figure. Whether
  the *event vehicle* is on the policy is invisible on the certificate.
  Description note; broker item B-5.
- **HNOA for caterers/rental vendors** (they drive to the venue): real but
  second-order; expressible only as `auto_liability_limit required` (the
  checkboxes aren't fields). Not seeded by default — friction for vendors who
  genuinely don't drive (photographers ride-share, some rentals deliver via
  third parties); venues can add per-tenant. Gap-table row for a
  `hired_nonowned_auto` flag field.

### 2.5 Photographer / Videographer

| Current rule | Verdict | Reasoning |
|---|---|---|
| `("coi","general_liability_limit","min_value","500000")` | **CHANGE value → `1000000`** | Internally inconsistent with every other template and with the venue's own customary master-policy vendor requirement ($1M/occ — custom). A photographer's GL loss (tripod trips a grandmother, lighting rig falls) is not smaller than a florist's. **Already frozen at 500000 in prod → needs the migration; shipping the seed edit alone is inert.** |
| `("coi","professional_liability_limit","min_value","1000000")` | **REMOVE** | E&O covers *bad photographs* — lost files, missed shots, ruined footage. The injured party is the couple, not the venue; the venue has no insurable interest in it, and venue vendor packets do not customarily demand photographer E&O (custom — broker sanity-check B-7). Over-requiring here isn't "safe friction": it's a permanent red for the many legitimately GL-only photographers, which trains the venue to override reds — the exact habit that later swallows a real liquor or GL failure. Template description drops "professional (E&O)". |
| `("license","expiration_date","required",null)` | **REMOVE — spurious** | **Texas issues no occupational license for photography or videography** (verified ABSENCE, in-repo dossier `texas/photographer-videographer.md`: no TDLR program, no issuer). The rollup therefore shows every photographer "Missing: license" forever. The only adjacent credential is the FAA Part 107 remote-pilot certificate *if* they fly a drone — conditional and federal → regulatory engine / per-tenant. |
| `("coi","expiration_date","required",null)` *(proposed)* | **ADD** | Consistency; the template previously leaned on the license expiry it required spuriously. |

### 2.6 Coverage dates (question 9 — cross-template)

- What ships grades **in force today** (with the 30-day ExpiringSoon overlay
  and the #399 "covered through" horizon). There is **no event-date concept
  anywhere in the product** (verified: zero hits for any event-date
  identifier), so "coverage dates that include the event" is not currently a
  checkable claim.
- A correct event check needs both ends: `effective_date ≤ event date` (a
  policy bound next month is as useless as one expiring next week — and
  effective dates *after* the certificate's issue date are common on renewals)
  and `expiration_date ≥ event date`. The engine's operators can't compare a
  field to a per-vendor date; this is an engine/product feature (event date on
  the vendor or portal link), not a seedable rule → gap table.
- **Mid-term cancellation makes even a true snapshot unreliable.** ACORD 25
  (2016/03) cancellation box: notice "will be delivered in accordance with the
  policy provisions" — the pre-2010 "endeavor to mail __ days" language is
  gone, and the certificate holder is typically owed **no** notice absent a
  notice-of-cancellation endorsement. A COI verified in March proves nothing
  about the policy's existence in June.
- **Defensible claim from an ACORD 25 alone:** "the certificate shows a policy
  period that includes the event date" — nothing stronger. Marketing copy
  currently implies the stronger thing (§7); soften now, build the event-date
  feature when it earns its keep.

---

## 3. Additional insured — the honesty question in full (question 3)

- **Certificate holder** = the entity the certificate is addressed to. ACORD
  25's face: the certificate "confers no rights upon the certificate holder"
  and "does not affirmatively or negatively amend, extend or alter the
  coverage afforded by the policies below." Holder status is a mailing label.
- **Additional insured** = a party the vendor's policy itself covers, via
  policy provision or endorsement. The relevant ISO endorsement family
  (numbers per ISO's commercial lines program; industry-standard, tier §9):
  - **CG 20 26** — Designated Person or Organization (scheduled): the natural
    form for a venue–vendor relationship that isn't a construction contract.
  - **CG 20 10** — Owners, Lessees or Contractors (scheduled, **ongoing
    operations** only in current editions) and **CG 20 37** (its **completed
    operations** counterpart): the construction pair; for one-day event
    vendors, ongoing-ops coverage is usually the operative need, but caterers'
    food-borne claims can surface post-event — completed-ops is the broker
    nuance (B-4).
  - **CG 20 33 / CG 20 38** — the ISO blanket "automatic status when required
    in a written agreement" pair — are by their verified titles **construction
    agreement** forms (CG 20 33 additionally limited to direct-privity
    parties, ongoing ops only; CG 20 38 extends to no-privity upstream
    parties). For non-construction vendor relationships, blanket AI usually
    rides **carrier-proprietary** endorsement wording in the small
    event-vendor programs caterers and photographers actually buy (custom).
    Either way, blanket forms respond only if the venue actually has a signed
    vendor agreement requiring AI status — a *contract hygiene* prerequisite
    CompliDrop can remind venues about but never verify.
  - Companions venues customarily also ask for: **waiver of subrogation**
    (CG 24 04; evidenced on ACORD 25 only by the SUBR WVD column) and
    **primary & non-contributory** wording (CG 20 01; **no ACORD cell at
    all** — free text in Description of Operations if anywhere).
- **What ACORD 25 actually evidences:** the ADDL INSD column is a **Y/N mark
  the issuing agent types**. ACORD 25's IMPORTANT box (2016/03, verbatim from
  a specimen this session): if the holder is an additional insured, "the
  policy(ies) must have ADDITIONAL INSURED provisions or be endorsed. …
  A statement on this certificate does not confer rights to the certificate
  holder in lieu of such endorsement(s)." TDI's own COI FAQ tells agents to
  check the box only "if the policy includes an endorsement that names the
  certificate holder as an additional insured" — and agent-training material
  warns that ticking it without the endorsement creates an agent E&O claim,
  not coverage. Texas statutorily enforces the same line: **Tex. Ins. Code
  §1811.051(a)** — a certificate may not be issued that "alters, amends, or
  extends the coverage or terms" of the referenced policy; **§1811.152** —
  "A certificate of insurance is not a policy of insurance and does not
  amend, extend, or alter the coverage afforded by the referenced insurance
  policy" (both verbatim-verified; TDI enforces with cease-and-desist and
  civil penalties up to $1,000 per infraction). Brokers still err and still
  accommodate. The checkbox is a *screen*, not proof.
- **Blunt answer to the blunt question:** AI status **cannot be verified from
  an ACORD 25 alone.** Verification requires the endorsement page (or policy
  provisions). Consequences:
  1. The current engine check (affirmative mark + venue name in
     holder/description — the #272 logic) is a reasonable screen and worth
     keeping.
  2. The UI sentence must become **"Certificate indicates '{name}' as
     additional insured"** (requirements.ts:185) and the failure message
     adjusted symmetrically; the marketing "flagging anyone who listed you as
     certificate holder instead of additional insured" and the hero's "your
     venue named as additional insured" need "certificate shows/indicates"
     framing (§7).
  3. The durable product answer is an **endorsement-document ask**: guidance
     (and later a checklist rule) for uploading the AI endorsement itself;
     `documentType` would be `coi`/`other` today — a
     `endorsement_forms_listed` extraction field is the gap-table entry.
  4. What CompliDrop may honestly claim: it reads what the certificate says,
     flags what's absent, and tells the venue what to demand — that's
     genuinely valuable; it just isn't coverage verification, and the copy
     must not blur that line (same posture G1 imposed on "Compliant").

---

## 4. Proposed template set (copy-pasteable, real seed shape)

Matches the `RuleSeed`/`TemplateSeed` records in
[ComplianceTemplateSeed.cs](../../api/CompliDrop.Api/Data/Seed/ComplianceTemplateSeed.cs).
Reminder: for existing databases (prod included) this seed is inert — the
same PR must carry the system-template data migration (§0) and the
re-grade fan-out, plus the four companion changes: extraction prompt v3
(liquor field + Each-Occurrence pinning + version bump), sample-COI liquor
line + fixture updates, the frontend liquor preset, and the #396 wording fix.

```csharp
new(SampleVendorTemplateName, // "Caterer"
    "Typical insurance for a food & beverage caterer, including bar / alcohol service.",
    [
        new("coi", "general_liability_limit", "min_value", "1000000",
            "General liability must be at least $1,000,000 per occurrence.", 1),
        new("coi", "liquor_liability_limit", "min_value", "1000000",
            "Liquor liability of at least $1,000,000 is required for alcohol service. If this caterer doesn't serve alcohol, remove this rule from your checklist.", 2),
        new("coi", "workers_comp_limit", "required", null,
            "Workers' compensation coverage is required.", 3),
        new("coi", "expiration_date", "required", null,
            "Expiration date is required.", 4)
    ]),
new("Event Rental Company",
    "Coverage for table, tent, and equipment rental vendors. (Bounce-house / inflatable vendors: Texas law also mandates a $1M amusement-ride policy — ask for that certificate.)",
    [
        new("coi", "general_liability_limit", "min_value", "1000000",
            "General liability must be at least $1,000,000 per occurrence.", 1),
        new("coi", "expiration_date", "required", null,
            "Expiration date is required.", 2)
    ]),
new("Security Service",
    "DPS guard-company licensing plus general-liability insurance. (Ask in writing that assault & battery is covered at full limits — certificates don't show it.)",
    [
        new("license", "license_number", "required", null,
            "License number is required.", 1),
        new("license", "expiration_date", "required", null,
            "License expiration date is required.", 2),
        new("coi", "general_liability_limit", "min_value", "1000000",
            "General liability must be at least $1,000,000 per occurrence.", 3),
        new("coi", "expiration_date", "required", null,
            "Expiration date is required.", 4)
    ]),
new("Transportation / Shuttle",
    "Auto liability and a current driver credential for shuttle and transport vendors. (Vehicles seating 16+ including the driver: require $5,000,000 and a CDL with passenger endorsement.)",
    [
        new("coi", "auto_liability_limit", "min_value", "1500000",
            "Auto liability must be at least $1,500,000 (the federal floor for small for-hire passenger vehicles).", 1),
        new("coi", "expiration_date", "required", null,
            "Expiration date is required.", 2),
        new("license", "license_number", "required", null,
            "Driver license number is required.", 3),
        new("license", "expiration_date", "required", null,
            "License expiration date is required.", 4)
    ]),
new("Photographer / Videographer",
    "General liability coverage for photo and video vendors.",
    [
        new("coi", "general_liability_limit", "min_value", "1000000",
            "General liability must be at least $1,000,000 per occurrence.", 1),
        new("coi", "expiration_date", "required", null,
            "Expiration date is required.", 2)
    ])
```

Diffs vs the proposal in one glance: liquor rule kept on Caterer (with an
opt-out sentence in its error message); Security loses `certification`, gains
COI expiry; Transportation loses `equals CDL`, gains license-number + COI
expiry, auto floor 1.0M → **1.5M**; Photographer loses E&O and the license
requirement, GL 0.5M → **1.0M**; every COI-bearing template requires
`expiration_date`. Rules NOT added because the fields don't exist go to §5 —
none were invented.

---

## 5. Extraction gaps table

Every check this review wanted and could not express. "Reliability" = how
consistently a current-generation LLM could populate the field from OCR'd
ACORD 25 text.

| Desired check | New field name | Where it appears on ACORD 25 (2016/03) | LLM reliability | Fail-open risk if omitted |
|---|---|---|---|---|
| GL ≥ $1M **per occurrence** specifically (#397) | `gl_each_occurrence_limit` | GL section, "EACH OCCURRENCE" cell | High (labeled cell) | **HIGH** — aggregate read passes a $500k/occ policy. Interim: pin the existing field to this cell in the prompt. |
| GL aggregate ≥ $2M | `gl_general_aggregate_limit` | "GENERAL AGGREGATE" (+ "GEN'L AGGREGATE LIMIT APPLIES PER: POLICY/PROJECT/LOC") | High | Medium — a $1M/$1M policy exhausts on the first claim. |
| Products/completed-ops ≥ $2M (food claims often land here) | `gl_products_completed_ops_aggregate_limit` | "PRODUCTS - COMP/OP AGG" | High | Low–Medium |
| Damage to rented premises minimum | `gl_damage_to_rented_premises_limit` | "DAMAGE TO RENTED PREMISES (Ea occurrence)" | High | Low (venue's real remedy is contractual — §2.2) |
| Personal & advertising injury | `gl_personal_adv_injury_limit` | "PERSONAL & ADV INJURY" | High | Low |
| Auto CSL pinned (vs split limits) | `auto_combined_single_limit` (+ split-limit trio) | "COMBINED SINGLE LIMIT (Ea accident)" / BI-per-person / BI-per-accident / PD cells | High | Medium — same ambiguity class as #397. |
| Umbrella per-occ vs aggregate | `umbrella_each_occurrence_limit`, `umbrella_aggregate_limit` | UMBRELLA/EXCESS LIAB section | High | Low |
| Employers' liability (the number the WC rule actually reads) | `employers_liability_each_accident` | "E.L. EACH ACCIDENT" | High | Low (current rule is presence-only, fail-closed) |
| Real AI verification (#396) | `endorsement_forms_listed` (e.g. "CG 20 26 04 13") — plus an endorsement-document upload flow | DoO box sometimes lists form numbers; truly on the endorsement pages, NOT the cert | Low from COI alone | **HIGH** as a claim; mitigated by the §3 wording fix regardless |
| Waiver of subrogation | `subrogation_waived` | "SUBR WVD" column (Y/N per coverage row) | Medium | Medium |
| Primary & non-contributory | `primary_noncontributory` | No cell — free text in DoO if anywhere | Low | Medium |
| Liquor liability limit | `liquor_liability_limit` *(this review's add)* | No dedicated section — blank OTHER rows, DoO text, or a separate certificate | Medium | **HIGH** until added (marketed, unchecked) |
| A&B included at full limits (guards) | `assault_battery_limit` | No standard cell — DoO or endorsement schedule | Low | **HIGH** for Security — a $1M GL with a $25k A&B sublimit passes |
| Seating capacity → $5M tier + CDL-P | — (not on any insurance document) | n/a | n/a | Medium — conditional floors stay in the regulatory engine / description |
| Hired & non-owned auto present | `hired_nonowned_auto` (boolean) | AUTO section checkboxes | Medium | Low |
| Coverage on the *event date* | — (product feature: event date input + date-compare operator) | n/a — and the cancellation box guarantees no notice | n/a | **HIGH for the marketing claim** — §7 |
| License is actually a DPS §1702 / relevant license | `license_type` normalization or `issuing_authority` matching | On the license document | Medium | Medium — any license satisfies `required` today |

Recommended sequencing: prompt-pin `general_liability_limit` + add
`liquor_liability_limit` now (prompt v3); split GL/auto cells as the next
extraction version with rule migration; endorsement/event-date items are
product features, not fields.

---

## 6. Fail-open vs fail-closed audit (proposed set)

Systemic fail-opens first — they apply to every rule below: (1) a COI is
unverified hearsay about a policy that may be cancelled tomorrow (§2.6, §3);
(2) verdicts are computed from low-confidence extractions
([#401](https://github.com/neboxdev/complidrop/issues/401), open); (3) the
supersession/verdict pipeline is out of scope here.

| Rule | Fail-open (non-compliant passes)? | Fail-closed (compliant fails)? | Net |
|---|---|---|---|
| Caterer/Rental/Security/Photographer GL ≥ $1M | **YES until #397 pinning** (aggregate read); post-pin: residual broker-error/forgery only | Unreadable/odd-format limit → red | Fix via prompt pin → acceptable |
| Caterer liquor ≥ $1M | Venue mis-assignment can't dodge it (it's on the default); residual: liquor on a *separate cert* the venue never uploads reads as missing → red, i.e. fail-closed not open | **Common**: OTHER-row/DoO extraction miss → red for a truly covered vendor | Deliberately fail-closed; the friction is the feature |
| Caterer WC required | E.L.-line presence passes without scrutiny of limits (minor) | **Lawful TX non-subscriber / sole proprietor → permanent red** | Fail-closed by design; per-tenant delete is the relief valve |
| All `expiration_date required` | No | Rarely (unreadable date — #383 class) | Safe |
| Security license number+expiry | **YES — any license document passes** (nothing pins to DPS §1702); mitigable with a brittle `issuing_authority contains` rule (§2.3) | Rare | Accept + fast-follow |
| Security GL ≥ $1M | **YES — A&B exclusion/sublimit invisible** (the guard-specific loss mode); also the generic #397 case | Standard | Biggest *residual* fail-open in the set; broker B-2 + description warning |
| Transport auto ≥ $1.5M | **YES for 16+-seat vehicles** (federal floor is $5M; capacity unknowable from the COI) | Small intrastate operators lawfully carrying less than $1.5M → red (they can buy up; friction) | Halves the old gap; residual documented |
| Transport license rules | Any license passes (CDL not verified — deliberate: the equals-CDL rule was noise, §2.4) | No longer fails lawful ≤15-seat drivers | Net improvement |
| Photographer GL ≥ $1M | Generic #397 only | GL-only-photographer at $500k → red (intended raise) | Fine |
| **Removed rules** (E&O, photographer license, security certification, equals-CDL) | Their removal opens nothing: none of the four ever verified a real venue-protective fact | Removes three permanent false-red generators | The override-training win |

**Pre-launch fixes demanded by this table:** the #397 prompt pin (kills the
dominant fail-open in four templates at once), the Security insurance addition
(closes the only no-insurance template), and the liquor field+rule (closes the
marketed-but-unchecked gap). The A&B and 16+-seat residuals cannot be closed
by this engine — they are must-ask items (§8) and description-copy warnings.

---

## 7. Claims audit — marketing promises vs what the proposed engine verifies

| Promise (verbatim source) | Can the proposed engine verify it? | Action |
|---|---|---|
| "General liability coverage" (venue page REQUIREMENTS list) | Partially: limit **as printed**, per-occurrence only after the #397 pin; never the policy's actual status | Keep; the page already hedges ("practical guidance, not legal advice") |
| "Commonly $1,000,000 per occurrence and $2,000,000 aggregate" (detail line + FAQ) | The $1M/occ half after the pin; the $2M aggregate **not until the field split** (§5) | Keep as education (it's "what venues ask", custom-framed); don't imply the product checks the aggregate |
| "Your venue named as an additional insured" + hero "…your venue named as additional insured" + "flagging anyone who listed you as certificate holder instead of additional insured" (How-it-works) | **No.** Certificate *indications* only; endorsement never seen (§3). The flagging sentence is the closest to a false feature claim — the engine flags what the certificate shows, not who "is" covered | **Soften now** (#396): "checks that certificates show your venue as additional insured — and tells you to collect the endorsement that actually grants it." Fix requirements.ts sentence in the same PR |
| "Liquor liability. For any vendor serving or selling alcohol" | After this change: yes, as printed-limit presence ≥ $1M, with known misses when liquor rides a separate certificate | Ship the 4-part liquor change; until merged this promise is unbacked (#400) |
| "Workers' compensation … where required by your state" (list) / "Vendors with employees are usually asked for workers' compensation" (FAQ) | Presence on the certificate: yes. The FAQ framing ("usually asked") is custom-accurate | Keep; never let any surface say TX *requires* it (§2.1). TX-footnote nice-to-have |
| "Coverage dates that include the event" (list) / "by the day of the event you're looking at a clean list" | **No.** The engine grades *today*, no event date exists in the product, and no COI can promise day-of validity (cancellation, §2.6) | **Soften now**: "Coverage dates you can see at a glance — with expirations tracked and chased automatically." Event-date checking becomes roadmap, not copy |
| FAQ additional-insured explainer ("Being listed only as the certificate holder means you receive the certificate but aren't covered by it…") | It's education, not a feature claim — and it is *accurate* | Keep as-is; it's the best paragraph on the page |

(The privacy-adjacent claims are tracked separately: #403/#404/#405.)

---

## 8. Must-ask-a-human list

**For a licensed Texas attorney (blocks launch — fold into the existing G1
counsel session):**

- **A-1.** Review this memo's dram-shop reading: does a venue with no TABC
  permit and no service role face Chapter 2 "provider" exposure when its
  caterer holds the permit? Does *requiring and archiving* vendors' TABC
  permits or COIs create any assumed-duty / negligent-undertaking exposure for
  CompliDrop's customers (venues) or for CompliDrop?
- **A-2.** Confirm the checklist verdict wording ("Covered", "Compliant",
  "Names you as additional insured" → "Certificate indicates…") satisfies the
  §81.101(c) posture G1 established, now that checklists carry dollar minimums
  the venue may read as advice.
- **A-3.** May the product ship default dollar minimums (the $1M/$1.5M figures
  above) as *suggestions* without them being construed as insurance advice /
  UPL-adjacent, given they're editable and labeled custom? What disclaimer
  placement do the template descriptions need?
- **A-4.** The workers-comp rule vs Texas non-subscribers: any problem with a
  default contractual demand for WC, and what should the red-badge copy say to
  avoid implying a legal violation?

**For a Texas hospitality/events insurance broker (blocks launch):**

- **B-1.** Confirm the default set: GL $1M/occ ($2M aggregate once checkable)
  for all vendor types; liquor liability $1M/occ for bar service; auto $1.5M
  CSL small shuttles / $5M at 16+ seats; guard GL $1M/occ. Which numbers does
  the TX wedding-venue market actually write into vendor packets in 2026?
- **B-2.** Guard companies: how is A&B typically written in TX (excluded,
  sublimited, full limits)? What written evidence should a venue demand, given
  ACORD 25 shows nothing? Is $1M GL meaningful without an A&B confirmation?
- **B-3.** What do TX venues accept from lawful non-subscriber vendors in
  place of workers' comp (occupational-accident policy + indemnity, waiver)?
- **B-4.** For one-day event vendors, does AI status need completed-operations
  (CG 20 37-class) coverage — e.g. caterer food-borne-illness claims surfacing
  days later — or is ongoing-ops (CG 20 26/20 10-class) sufficient?
- **B-5.** Shuttle vendors: how do venues verify the *event vehicle* is on the
  policy (scheduled autos vs a generic CSL line)? Is asking for the schedule
  page standard?
- **B-6.** Is a Damage-to-Rented-Premises minimum worth demanding from
  non-tenant vendors, or is the CCC-exclusion reality better handled purely by
  contract/deposit? *(refine later)*
- **B-7.** Sanity-check dropping photographer E&O from the default: do any TX
  venue packets actually require it? *(refine later)*
- **B-8.** How often does TX bar-service liquor liability appear on the main
  ACORD 25 (OTHER row) vs a separate certificate? (Calibrates the
  fail-closed rate of the liquor rule.) *(refine later)*

**Blocks-launch vs refine-later:** A-1..A-4 and B-1..B-3 gate turning these
defaults on for real customers; B-4..B-8 tune copy and follow-on rules.

---

## 9. Confidence table

Tiers: **verified-primary** (read on the official source, either this session
by the verification agents or in the in-repo dossier with live-fetch evidence)
· **verified-secondary** (consistent reputable secondary sources) · **custom**
(industry practice, not law) · **unverified** (assumption; what would verify
it is stated).

| # | Claim | Tier | Citation |
|---|---|---|---|
| 1 | Dram Shop Act = Alco. Bev. Code ch. 2; §2.02(b) obvious-intoxication standard; §2.03(c) exclusivity (18+); §2.02(c) social host = adult 21+ / minor under 18 | **verified-primary** (this session) | statutes.capitol.texas.gov `AL.2.htm`, quoted verbatim (current through 89th Leg. 2nd C.S. 2025) |
| 2 | §2.01 "provider" = sells/serves under license or permit "**or who otherwise sells**" — the unlicensed prong turns on *selling*, not on being "required to hold" a permit | **verified-primary — corrected this session** | Same source, §2.01 verbatim |
| 3 | §106.14(a) safe harbor: employer *requires* training + employee attended + no encouragement; applies to ch. 2 dram-shop claims, pierceable by provider negligence | **verified-primary** (statute) + verified-secondary (case) | `AL.106.htm` verbatim; *20801, Inc. v. Parker*, 249 S.W.3d 392 (Tex. 2008) via hosted opinion text |
| 4 | **Caterer's Permit (CB), ch. 31, repealed eff. 2021-09-01**; current mechanism = Mixed Beverage Permit + §28.19 temporary-location sales via TABC FUN/TEA; NT for nonprofits | **verified-primary — corrects the in-repo dossier's `probable`-tier CB note** | HB 1545 §410(a) enrolled text (capitol.texas.gov); `AL.28.htm` §28.19; tabc.texas.gov temporary-event page |
| 5 | No sale → no TABC permit ("It is legal to provide free alcoholic beverages without a permit"); cash bar / ticket / tip jar / package-price bundling = sale | **verified-primary (agency FAQ)** | tabc.texas.gov License & Permit FAQs, quoted |
| 6 | Texas law does NOT mandate caterer GL/liquor insurance (sourced absence); only the ch. 28 conduct surety bond (§11.11) | **verified-primary** | In-repo dossier OBL-TX-CATERER-006 (official text 2026-07-07) |
| 7 | CG 00 01 04 13 exclusion c. applies "only if you are in the business of…"; BYOB sentence *preserves* host-liquor coverage; CG 21 50/21 51 are exclusion-**tightening** endorsements; liquor coverage = CG 00 33/00 34 | **verified-specimen** (hosted full-text form) + verified-secondary (form roles) | Argo-hosted CG 00 01 04 13 specimen, quoted; Marsh ISO-filing briefing; forms databases |
| 8 | Labor Code §406.002 elective; §406.033 defense-stripping + void waivers; §406.004/005 notices (form DWC005; *annual* cadence per rule/TDI, not statute text); §406.096 exception; §406.097 owner exclusion | **verified-primary** (this session, rendered official pages + TDI) | `LA.406.htm` verbatim; tdi.texas.gov forms + cb030 |
| 9 | Texas = only state with broad private opt-out | **verified-primary (regulator's own statement)** | TDI/DWC "What is workers' compensation?" quoted |
| 10 | Non-subscriber COI: WC section left **blank**; occupational-accident programs go in a blank OTHER row | **custom — Texas agents-association guidance** | IIAT *Best Practices — Certificates of Insurance*, quoted |
| 11 | Non-subscriber compensatory damages uncapped (exemplary capped by CPRC §41.008 generally) | **verified-primary** (statutes) | `LA.406.htm`; CPRC §41.008(b) |
| 12 | Ch. 1702 = Private Security Act; DPS administers (§1702.005 post-SB 616; board → advisory committee); company license §1702.102; DPS classes A/B/C; ≤2-yr terms §1702.301 | **verified-primary** (statute) + agency page (archived Dec 2025) | `OC.1702.htm` verbatim; DPS license-types page |
| 13 | §1702.124(a),(c): $100k/occ BI+PD, $50k/occ PI, $200k aggregate GL precondition | **verified-primary** (twice: this session + G2 evidence) | `OC.1702.htm` §1702.124 verbatim; screenshots `audit/evidence/g2/` |
| 14 | Individual credentials = DPS pocket cards w/ photo + expiry (§§1702.165(b), 1702.232); commissioned §1702.161, noncommissioned §1702.221, PPO **license** §1702.201–.202; company license publicly searchable in TOPS | **verified-primary** | Same source; tops.portal.texas.gov (live) |
| 15 | Guard CGL A&B commonly excluded/sublimited, endorsed back at sublimits; not visible on ACORD 25 | **custom — 3 labeled industry sources** (IRMI inaccessible) | Leavitt Group; ReShield; Total CSR — quoted in verification report |
| 16 | CDL Group C = designed for 16+ passengers **incl. driver** (49 CFR 383.5); P endorsement §383.93(b)(2) read with §383.5; Transp. Code §522.003(5) incorporates 383.5 **by reference** | **verified-primary — citation mechanism corrected** | eCFR §383.5/§383.93 verbatim; `TN.522.htm` §522.003(5) verbatim |
| 17 | 49 CFR **387.33T**: $5,000,000 (16+ incl. driver), $1,500,000 (15 or less incl. driver); §387.27(a) interstate for-hire; taxi exception <7 seats | **verified-primary** | eCFR §387.33T Schedule of Limits verbatim; FMCSA filing-requirements chart |
| 18 | TxDMV registration: §643.051(a) → §548.001(1)(B) "more than 15 passengers, including the driver"; exemptions §643.002; 43 TAC 218.16(a) Figure: $500k (16–26 incl. driver), $5M (27+); **no tier for ≤15**; TxDMV guidance: USDOT number + TxDMV certificate + cab card for 16+ | **verified-primary — §643.002/§548.001 pinpoints corrected** | `TN.643.htm`/`TN.548.htm` verbatim; TxDMV Chapter-218 adoption (2024) Figure verbatim; TxDMV Smart-Bus-Operators guidance |
| 19 | ≤15-seat intrastate floor = Texas 30/60/25 only (Transp. Code §601.072(a-1)) + municipal ordinance | **verified-primary** | `TN.601.htm` §601.072 verbatim |
| 20 | 49 CFR 390.5T: 9–15-passenger for-hire **interstate** vehicles are FMCSR-regulated CMVs (USDOT registration) even without CDL | **verified-primary** | eCFR §390.5T |
| 21 | ACORD 25 (2016/03) cell captions — GL six cells + POLICY/PROJECT/LOC/**OTHER** aggregate boxes; ADDL INSD + SUBR WVD columns; auto CSL/BI/PD cells + ANY/OWNED/SCHEDULED/HIRED/NON-OWNED boxes; umbrella cells; WC "PER STATUTE / OTH-ER" + 3 E.L. cells + proprietor-excluded box; **no liquor section; no P&NC cell; no A&B cell** | **verified-specimen** (two independent blank 2016/03 specimens, full text) | Allegany Group + NY DFS specimens; TDI-hosted filed specimen; City of Chicago blank |
| 22 | ACORD 25 disclaimer, IMPORTANT box (AI requires endorsement), cancellation "in accordance with the policy provisions"; "endeavor to mail" removed in the 2009/2010 revisions | **verified-specimen** (language) + verified-secondary (revision history) | Specimens above; industry commentary (JCJ, Insurance Advocate, Graham Co.) |
| 23 | AI endorsement family: CG 20 26 (designated person/org — the venue-apt "catch-all"); CG 20 10 (ongoing ops; **no written-contract requirement** — 04 13 added contract-*linked limits*, not a contract condition) / CG 20 37 (completed ops); CG 20 33/20 38 blanket = **construction-agreement** forms (20 33 privity-limited); CG 24 04 waiver of subrogation; CG 20 01 primary & non-contributory (04 13) | **verified-specimen** (IIAT/FMIC/AmRisk-hosted full texts) + verified-secondary (roles/history) | IIAT-hosted CG 20 26/20 10/20 37/20 33/20 38/20 01 specimens; AmRisk CG 24 04; Amwins advisory; Sonoma County guide |
| 24 | Tex. Ins. Code §1811.051(a),(b) + §1811.152: a COI may not alter/amend/extend coverage, conveys no contractual right, "is not a policy of insurance"; TDI enforces (≤$1,000/infraction); TDI FAQ: check the AI box only when the endorsement exists | **verified-primary** (statute + regulator FAQ) | texas.public.law §§1811.051/.152 verbatim; tdi.texas.gov/certificates/faq.html |
| 25 | Liquor liability appears on ACORD 25 only via blank OTHER rows / DoO / separate certificate | **verified-specimen (absence)** + custom (practice, incl. a municipal sample cert) | Specimen text; Town of Burlington MA sample liquor ACORD |
| 26 | No Texas photographer/videographer occupational license | **verified-primary (absence, twice)** | TDLR program list (live, this session); dossier OBL-TX-PHOTOGRAPHER-000 |
| 27 | No Texas state tent permit; local fire-marshal, IFC >400 sq ft | **verified-primary (absence)** | Dossier `texas/event-rental.md` OBL-TX-EVENT-005 |
| 28 | Inflatables: Occ. Code §2151.1012 mandatory $1M/occ CSL amusement-ride insurance | **verified-primary** | Dossier `texas/event-rental.md` OBL-TX-EVENT-001 |
| 29 | Venue customary limits: GL $1M/occ (usually $2M agg); liquor $1M/occ; shuttle asks mirroring $1.5M/$5M federal tiers; venues do NOT customarily require photographer E&O, inland marine, or vendor DTRP minimums | **custom** — multiple venue packets + broker guidance (CAPTA, NAMM, LACC, Seattle, Portland, Insurance Canopy, FLIP, Logrock, CoverMyConfetti) | Verification reports, URLs quoted therein |
| 30 | DTRP mechanics: fire-legal + the 7-consecutive-days exception to exclusion j; separate limit per CG 00 01 §III ¶6 | **verified-specimen** | Argo CG 00 01 04 13, quoted |
| 31 | Sample COI has no liquor line; adding the Caterer liquor rule flips the demo NonCompliant without the generator change | **verified-code** | [SampleCertificateGenerator.cs:71-97](../../api/CompliDrop.Api/Services/SampleCertificateGenerator.cs) |
| 32 | Extraction prompt v2 lacks `liquor_liability_limit`; a rule on it fails everyone | **verified-code** | [ExtractionPrompts.cs:25-28](../../api/CompliDrop.Api/Services/Extraction/ExtractionPrompts.cs) |
| 33 | Seeder skips existing template names entirely; prod values frozen; migration required | **verified-code** | [ComplianceTemplateSeed.cs:60](../../api/CompliDrop.Api/Data/Seed/ComplianceTemplateSeed.cs) |
| 34 | Whether DPS officers must carry the pocket card **on duty**; whether the TxDMV certificate number must be marked on the vehicle exterior | **unverified** — settle via 37 TAC ch. 35 / ch. 4 in the relocated SOS TAC portal | Flagged in verification reports |

### Corrections this session surfaced for the in-repo dossier

`docs/rules-research/texas/caterer.md` OBL-TX-CATERER-004's `probable`-tier
note ("implemented by TABC as the 'Caterer's Permit' plus a per-event
'Catering Certificate'") is **refuted**: ch. 31 was repealed eff. 2021-09-01
(HB 1545 §410(a)) and the current administration is the FUN/TEA process; the
legacy L-CCFP packet TABC still hosts is stale. The dossier's verified §28.19
core is unaffected; only the implementation note (and any UI copy that ever
says "Caterer's Permit") should be updated. Filed as
[#414](https://github.com/neboxdev/complidrop/issues/414).
