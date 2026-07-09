# Rule schema design — FROZEN v1 (2026-07-07; additive v1.2 revision 2026-07-08)

Status: **FROZEN** as of 2026-07-07, after the Phase-1 dossier + review gauntlet
completed and the founder said "go" to Phase 2. The applicability-fact registry
(§4) is locked against the dossier's actual triggers; the three open design
questions are resolved (§ Resolved decisions). The engine is built against THIS
file. Changing a frozen fact name / rule shape = a schema-version bump + a note
here, not a silent edit.

**v1.2 (2026-07-08, additive — the Pass-5 Fable re-review):** two new facts
(`providesUnarmedGuards`, CONF-19; `operatesIntrastate`, CONF-23 — see §4);
`insuranceMinimums` reshaped to represent EXACTLY what each statute states
(`kind`: combined-single-limit | split-limits; `coverageLine`: general-liability |
auto-liability; nullable `aggregate` — no field may carry a figure the cited
section does not contain, CONF-16); the insurance AMOUNT GATE closing A-2/CC-4
for general-liability floors (§6); a new status `below-stated-minimum`; cadence
`roundToMonthEnd` for "calendar months" recency language (CONF-0); and hardened
loader requirements (confidence/validFrom/citation-for-verified/minimums-shape
all mandatory — an omitted field can never silently default into the shipping
posture). See REVIEW-LOG § Pass 5.

Note on gates: FREEZE authorizes BUILDING the engine (nothing deploys; ships
feature-flag OFF). It does NOT clear G1 (counsel on user-facing framing) or G2
(founder browser spot-confirm of TX statutory figures) — those still gate
customer exposure, per RULES-REVIEW.md.

## 1. Storage & versioning model

- Rules live as **versioned JSON files in-repo** at `api/CompliDrop.Api/RuleData/`,
  organized `RuleData/{us-fed,us-tx}/<entity-type>.json`, compiled into the API
  as embedded resources and loaded/validated at boot (fail-fast on schema
  violation, like the migration drift guard).
- Every rule change is a PR: diffable, reviewable, testable. No runtime rule
  editing.
- A **rule** is identified by a stable slug id (e.g. `tx-security-company-license`).
  A rule id carries an array of **versions**, each with `validFrom`/`validTo`
  (date, US-Central civil dates — see §6). The engine evaluates against the
  version effective at the evaluation date. Versions are append-only; editing a
  shipped version's substance is forbidden (fix = new version + closing the old
  one's `validTo`), matching the ADR append-only convention.

## 2. Rule document shape

```jsonc
{
  "schemaVersion": 1,
  "rules": [
    {
      "id": "tx-security-company-license",
      "obligationRef": "OBL-TX-SECURITY-001",        // dossier cross-ref, mandatory
      "jurisdiction": "us-tx",                        // "us-fed" | "us-tx"
      "entityTypes": ["security-service"],
      "category": "license",                          // license|permit|worker-certification|insurance|filing
      "basis": "regulatory",                          // only regulatory rules ship; contractual stays in checklists
      "versions": [
        {
          "version": 1,
          "validFrom": "2026-07-07",
          "validTo": null,
          "confidence": "verified",                   // verified|probable|uncertain — engine SHIPS verified only
          "applicability": { /* condition tree, §4 */ },
          "obligation": {
            "name": "Security contractor company license",
            "documentType": "license",                // maps to existing Document.DocumentType values
            "documentSubType": "tx-dps-security-contractor",
            "authority": "Texas Department of Public Safety — Private Security Program",
            "perWorker": false                        // true for worker-certification rules
          },
          "cadence": { /* §5 */ },
          "insuranceMinimums": null,                  // only for category=insurance: {perOccurrence, aggregate, currency:"USD"}
          "citation": {
            "section": "Tex. Occ. Code §1702.102",
            "url": "https://statutes.capitol.texas.gov/...",
            "effectiveDateOfText": "…",
            "verifiedDate": "2026-07-07"
          },
          "rationale": "Texas law requires companies that provide security services to hold a security contractor license issued by the Texas DPS Private Security Program (Tex. Occ. Code §1702.102).",
          "userAction": "Ask this vendor for their DPS security contractor license and track its expiration.",
          "notes": null
        }
      ]
    }
  ]
}
```

`rationale` is user-facing and must follow the liability posture: states what
the law requires and cites it; never asserts that the entity is or isn't in
compliance ("required by", "track", "appears" — never "you are violating").

## 3. Federal/state layering

- An entity profile names its state (v1: only `US-TX` supported; any other
  state → engine returns "jurisdiction not covered", never an empty-equals-
  compliant result).
- Effective rule set = all `us-fed` rules whose applicability matches, PLUS all
  rules of the entity's state that match. Rules are **additive**.
- Where a state credential implements a federal floor (e.g. Texas-issued CDL
  implements 49 CFR 383), the rule is encoded ONCE at the level that issues the
  document (state), with `satisfiesFederal: ["fed-cdl-floor-ref"]` metadata so
  the dossier linkage stays explicit and no duplicate obligation is emitted.
  Deterministic rule: a federal rule listed in some matched state rule's
  `satisfiesFederal` is suppressed from output for that entity.

## 4. Applicability conditions (three-valued logic)

A small closed condition language over a typed **entity profile**:

```jsonc
{ "all": [
    { "fact": "operatesInterstate", "op": "eq", "value": true },
    { "fact": "maxPassengerSeatingCapacity", "op": "gte", "value": 16 }
] }
```

- Combinators: `all`, `any`, `not`. Operators: `eq`, `neq`, `gte`, `lte`, `in`.
- Facts are declared in a central registry (name, type, prompt-text for the UI,
  which entity types they apply to). Unknown/unset fact ⇒ condition evaluates
  **unknown**, not false. Kleene three-valued logic propagates through
  combinators; a rule whose applicability is `unknown` is emitted as
  `status: needs-profile-info` listing the missing facts — the engine NEVER
  silently drops an obligation because a question wasn't answered, and never
  asserts compliance from ignorance.
### FROZEN fact registry (v1)

Locked against the dossier's actual triggers. Each fact has a type; an unset fact
is `unknown` (Kleene), never `false`. `applies to` narrows which entity types
even surface the profile question.

| fact | type | applies to | drives (obligations) |
|---|---|---|---|
| `state` | enum `US-TX` (only value v1) | all | jurisdiction selection; non-TX ⇒ "jurisdiction not covered" |
| `entityType` | enum (the 6 types) | all | which rule files apply |
| `employeeCount` | int (≥0) | all | OSHA 300 log (≥11); "has employees" for WC/food-handler |
| `carriesWorkersComp` | bool | all w/ employees | WC-elective: `false` ⇒ DWC-005 non-subscriber notice obligation |
| `servesOrSellsAlcohol` | bool | caterer, venue-org | TABC permit(s); TTB alcohol-dealer registration |
| `preparesOrServesFood` | bool | caterer, venue-org | food-establishment permit; certified food manager; food-handler training |
| `operatesFoodVendingVehicle` | bool | caterer | DSHS Mobile Food Vendor license (HB 2844, ch. 437B) |
| `rentsInflatableAmusementDevices` | bool | event-rental | amusement-ride insurance (§2151.1012) + inspection/sticker + injury report |
| `operatesForklifts` | bool | event-rental | OSHA powered-industrial-truck operator certification |
| `providesArmedGuards` | bool | security-service | security officer commission (any-of with close-protection, CONF-18); Level III training |
| `providesArmedCloseProtection` | bool (v1.1 additive, CC-3) | security-service | PPO license; also the commission (a PPO presupposes one, CONF-18) |
| `providesUnarmedGuards` | bool (v1.2 additive, CONF-19) | security-service | noncommissioned (unarmed) officer individual license — commissioned officers hold the commission instead |
| `operatesVehiclesForHire` | bool | transportation | base gate for all transport obligations |
| `operatesInterstate` | bool | transportation | the federal branch: FMCSA authority / MCS-150 / UCR / §387 federal floor attach at `true`. Unknown ⇒ `needs-profile-info` |
| `operatesIntrastate` | bool (v1.2 additive, CONF-23) | transportation | the Texas branch: TxDMV reg + §218.16 TX floor attach at `true`. TWO facts because §643.002 exempts only EXCLUSIVELY-interstate carriers — a MIXED carrier (both true) owes BOTH layers; the old `operatesInterstate == false` gate silently dropped the TX layer for mixed carriers |
| `maxPassengerSeatingCapacity` | int (**including the driver**) | transportation | capacity thresholds: CDL/fed-$5M ≥16; fed-$1.5M ≤15; TX-reg >15; TX-$5M ≥27; TX-$500k 16–26. ALL normalized to seats-incl-driver. (UCR is deliberately NOT capacity-gated — the >10 figure is its FEE CMV definition, not the registration trigger; CONF-2 reversed CC-6.) |
| `operatesDronesCommercially` | bool | photographer-videographer | FAA Part 107 cert + 24-mo recency + drone registration |
| `sellsTaxableGoodsOrServices` | bool | all | Texas sales & use tax permit |
| `isFranchiseTaxableEntity` | bool | venue-org (+ any registered entity) | Texas franchise-tax annual report |

**Capacity convention (locked):** `maxPassengerSeatingCapacity` is the vehicle's
designed seating capacity **including the driver**. The dossier's superseded
pre-2024 phrasing "26 or more, not including the driver" was corrected at D-3 to
the current 43 TAC §218.16(a) wording, "27 or more people, including the driver"
(numerically the same population); every threshold in the rules is expressed
incl-driver so one fact serves all.

## 5. Cadence & deadline logic

```jsonc
{
  "kind": "renewal",              // renewal | fixed-annual | one-time | conditional-filing
  "periodMonths": 24,             // for renewal
  "anchor": "documentExpiration", // documentExpiration | issueDate | calendarDate
  "fixedDate": null,              // {month, day} for fixed-annual (e.g. franchise tax May 15)
  "gracePeriodDays": 0,
  "graceCitation": null           // grace periods must be independently sourced
}
```

- v1 evaluation leans on the tracked document's own `ExpirationDate` where the
  document carries one (licenses, permits, COIs); the cadence block is the
  fallback for documents without printed expiry and the basis for
  "renewal expected every N months" messaging. Deadline arithmetic is pure,
  date-only, in the org's IANA time zone (existing `Organization.TimeZone`),
  and must be property-tested across DST transitions, leap years, and
  month-length edges (Jan 31 + 1 month, etc.).

## 6. Engine contract (Phase 2, [OPUS] implements)

- Pure C#: `(EntityProfile, IReadOnlyList<Document>, LocalDate evaluationDate, RuleSet)`
  → `ObligationReport` with per-obligation status:
  `satisfied | expiring | expired | missing | below-stated-minimum |
  needs-profile-info | needs-document-info | not-applicable`.
  (`needs-document-info` added v1.1 — a matched document whose currency cannot be
  determined, e.g. no readable expiry on a renewing obligation. It must never read
  `satisfied`. `below-stated-minimum` added v1.2 — see the amount gate below.)
  No LLM calls, no clock reads (evaluation date injected), no DB access inside
  the evaluator.
- **Insurance amount gate (v1.2 — closes A-2/CC-4 for general-liability floors).**
  For an `insurance` obligation whose `insuranceMinimums.coverageLine` is
  `general-liability`, a would-be `satisfied` must also clear the amount check
  against the document's extracted `GeneralLiabilityLimit`:
  - amount unreadable (null) ⇒ `needs-document-info` — never a Satisfied that
    certifies an amount nobody read;
  - amount **below** the comparable floor (the CSL `perOccurrence`, or the
    split-limits BI+PD component) ⇒ `below-stated-minimum` — a numeric comparison
    of the certificate against the cited statute's stated minimum, phrased as a
    tracked-obligation status, never an adjudication (this also demotes
    `expiring`: the shortfall exists now, not at renewal);
  - amount at/above a floor that ONE extracted figure cannot fully verify (split
    limits / a statutory aggregate, e.g. §1702.124(c)'s three limits) ⇒
    `needs-document-info` with a verify-sub-limits userAction — the engine never
    certifies adequacy it cannot read;
  - `expired` always stays `expired` (the stronger defect).
  An `auto-liability` floor (49 CFR 387.33T, 43 TAC 218.16) is NEVER compared
  against the extracted GENERAL-liability limit — wrong policy line; those keep
  presence+expiry semantics until extraction reads an auto-liability limit (see
  v1 KNOWN LIMITATIONS; tracks with product bug #397 on cell-pinning accuracy).
- **Fixed-annual proof semantics (v1.2, UNVER-13).** A matched document with NO
  printed expiry on a `fixed-annual` obligation reads `needs-document-info` —
  which annual cycle an undated proof covers is not generically inferable (a late
  filing for this cycle and an early one for the next look identical), so the
  engine never guesses `satisfied`. The next occurrence still surfaces as
  `NextDueDate` for reminders.
- **Grace-period scope (v1.2).** `gracePeriodDays` is honored uniformly on the
  printed-expiry and period-cadence paths (both classify via the same
  grace-aware timing). It is NOT honored on `fixedDate`/`calendarDate` anchors
  (the next-occurrence computation re-anchors on the evaluation date), so the
  loader REJECTS grace > 0 there until the engine supports it.
- **`validFrom` convention.** Versions carry the cited law's actual effective
  date where the dossier supplies one (ch. 437B → 2026-07-01; TABC 2-yr terms →
  2021-09-01; the franchise 2024+ regime → 2024-01-01); the remaining
  current-law versions use the 2020-01-01 dataset floor. The engine only
  evaluates at the current date in v1 — an as-of-history feature must first
  backfill true effective dates for the floor-dated versions.
- Only `confidence: verified` rule versions load in production; `probable`/
  `uncertain` versions are visible only behind the review flag.
- Feature flag per rule-set file (jurisdiction × entity type) so rollout is
  per-rule-set after founder sign-off. Implemented v1.2: `RuleEngine:Enabled`
  (default false) + `RuleEngine:EnabledRuleSets` ("us-fed/caterer", …) resolved
  once at boot into `RegulatoryRuleCatalog`, fail-fast like the migration guard.
  `VerifiedOnly`/`IncludeReviewGated` are deliberately NOT configurable — a
  probable or review-gated rule can never ship via config; gates lift by data PR.

## Codebase fit (recon 2026-07-07)

The existing `ComplianceCheckService.ComputeOutcome` /
`.EvaluateRule` (api/CompliDrop.Api/Services/ComplianceCheckService.cs) grades an
**uploaded document** against a **per-org CONTRACTUAL checklist**
(`ComplianceTemplate`/`ComplianceRule`; operators `required|equals|contains|
min_value`). It is a pure function over one loaded `Document`, uses injected
`TimeProvider` for date boundaries, and is unit-tested directly via
`InternalsVisibleTo`. **The new regulatory engine is a SEPARATE, upstream
layer** — it answers "which documents/credentials does this ENTITY need by law?"
from (entity profile × jurisdiction), which the contractual grader never asks.
Design consequences:
- New engine = its own pure service (`RegulatoryObligationEvaluator` or similar)
  mirroring `ComputeOutcome`'s purity + `TimeProvider` + `InternalsVisibleTo`
  test idiom. Do NOT entangle it with `ComplianceCheckService`.
- Composition: regulatory engine emits the OBLIGATION SET; each obligation's
  satisfied/expiring/expired status is then judged from the matching tracked
  `Document`(s) (presence + `ExpirationDate`), reusing the existing
  `ExpiringSoonWindowDays` constant so windows don't drift.
- `Document.DocumentType` today is a free-ish string (`coi`, `license`,
  `certification`, `other`, …). The rule schema's `documentType`/`documentSubType`
  must map onto this existing vocabulary — enumerate it before FREEZE.
- No entity-profile data exists yet (`Organization` has no address/state;
  `Vendor` has no entity-type/headcount facts). The migration adding those is
  the first [OPUS] step after FREEZE.

## Legal-review-mandated engine requirements (Pass 1, 2026-07-07 — HARD)

These are non-negotiable outputs of the legal/compliance review. The engine and
its UI must satisfy them; they are correctness requirements, not preferences.

1. **Interstate-vs-intrastate branch (transportation).** The evaluator MUST
   resolve the direction facts before selecting any transportation insurance
   floor. Federal for-hire passenger financial responsibility ($5M for 16+ seats
   / $1.5M for ≤15, gated on `operatesInterstate`) and Texas intrastate minimums
   ($500k for 16–26 seats / $5M for 27+ incl-driver per the 2024 §218.16(a)
   amendment, gated on `operatesIntrastate` since v1.2) are DIFFERENT floors on
   the same vehicle — and a MIXED carrier owes BOTH (CONF-23). If a direction
   fact is unknown ⇒ `needs-profile-info` for that layer, never a flat
   "compliant". FMCSA operating authority + MCS-150 + UCR are interstate-only.
2. **No completeness illusion.** The evaluator MUST NEVER emit a bare
   "you are compliant" that implies the tracked set is exhaustive. Every
   `ObligationReport` carries an explicit non-exhaustiveness notice and surfaces
   the entity's "noted, not encoded" LOCAL obligations (city/county health/food,
   fire-marshal, certificate of occupancy, tent/IFC, per-event permits). A
   fully-satisfied report reads "all TRACKED obligations are met — this is not a
   complete list of your legal obligations; check your city/county," never
   "compliant" full stop.
3. **Penalties are statutory-general, never a user adjudication.** Where a rule's
   rationale mentions a penalty, it is phrased as "what the statute provides"
   (general, sourced) and is NEVER paired with a definitive "you are
   non-compliant / you are committing a crime" for THIS user. The engine emits
   obligation STATUS (satisfied/expiring/expired/missing/needs-info), not legal
   conclusions about the user's conduct.
4. **Only `provenance ∈ {official, reproduction-corroborated}` + `confidence:
   verified` load in prod.** Single-reproduction entries (`probable`) stay behind
   the review flag. The Texas security rule-set's methodology human-gate (G2)
   closed 2026-07-09 via delegated official-host confirmation
   (audit `evidence/g2/`); its reviewGate is lifted — see METHODOLOGY provenance
   rule and REVIEW-LOG § Pass 5.

### v1 KNOWN LIMITATIONS (documented, founder-visible — updated v1.2, 2026-07-08)

- **Insurance amounts: CLOSED for general-liability floors (v1.2), open for
  auto-liability floors.** The A-2/CC-4 hole is closed where the extracted
  `GeneralLiabilityLimit` is a valid comparison input: GL floors (§2151.1012
  inflatables; §1702.124(c) security, review-gated) now gate `Satisfied` on the
  amount (see §6 amount gate; below-floor ⇒ `below-stated-minimum`; unreadable ⇒
  `needs-document-info`; split-limit sub-limits never certified from one figure).
  The v1.2 `insuranceMinimums` shape carries all statutory components
  machine-readably (the security $50k personal-injury sub-limit no longer lives
  only in prose). STILL OPEN: the transport floors (49 CFR 387.33T, 43 TAC
  218.16) are AUTO-liability — comparing them against the extracted
  GENERAL-liability limit would grade the wrong policy line, so they keep
  presence+expiry semantics until extraction reads an auto-liability limit.
  The comparison inherits extraction's cell-pinning accuracy (product bug #397
  stays open and tracks separately).
- **A matched document with no readable expiry** on a renewing obligation yields
  `NeedsDocumentInfo` (not `Satisfied`) — the engine will not certify currency it
  can't determine. v1.2 extends this to fixed-annual obligations with undated
  proof (see §6) and to conditional filings WITH proof reading `Satisfied`
  (UNVER-5 — uploading proof must never read worse than having none).
- **Conditional filings** (amusement AR-800 injury report) surface as
  `NotApplicable` until a triggering-event fact exists; the rationale states "file
  only if a reportable injury occurs."
- **CMV weight/hazmat prongs are not modeled (CONF-24).** The transportation
  gates encode only the passenger-capacity prong of the §548.001 / 49 CFR 383.5
  CMV definitions; the >26,000 lb weight, hazmat, school-bus and household-goods
  prongs have no frozen fact. A heavy (>26,000 lb) vehicle seating ≤15 is out of
  v1 scope — recorded in the rule files' headers and local-obligations wording.
- **Charter non-expiring TxDMV certificates (CONF-25).** The charter/scheduled
  service-type split has no frozen fact, so the TxDMV registration cadence is
  `renewal`: a lawful non-expiring charter certificate with no printed expiry
  reads `NeedsDocumentInfo` (never a wrong verdict, never Satisfied) pending the
  charter-definition confirm (dossier Open question 4).
- **`employeeCount` is a current-headcount proxy (CONF-7).** 29 CFR 1904.1's
  exemption turns on headcount "at any time during the LAST calendar year"; the
  registry has no last-year fact, so the OSHA gate uses current headcount and the
  rationale/userAction carry the correct statutory test in prose.
- **The TFER minimal-risk exemption is not encodable (CONF-9).** 25 TAC
  228.31(c) exempts prepackaged-only / no-exposed-TCS operations from the CFM
  requirement; no TCS/prepackaged fact exists, so a minimal-risk operation
  answering `preparesOrServesFood=true` still sees the obligation — the
  userAction states the exemption.

### Founder/counsel gates (NOT code — carry to RULES-REVIEW.md)

- The feature asserts *which laws apply to the user*, a larger reliance/UPL
  surface than "read your document against your own requirement." Real legal
  counsel must review the disclaimer + user-facing framing before ANY customer
  exposure. Feature-flag OFF until then.

## Resolved design decisions (at FREEZE, 2026-07-07)

**RD-a — `filing`-category obligations are tracked as cadence-driven obligations
with optional proof, not forced through document extraction.** WC DWC-005,
franchise-tax report, TTB registration, MCS-150 update, Clearinghouse query, and
amusement injury reports don't fit "upload → extract → grade." The engine emits
them as obligations whose status comes from (a) an optional tracked document /
manual "filed on <date>" attestation and (b) the cadence's next-due date, surfaced
via reminders. No extraction dependency. Status vocabulary is the same
(`satisfied|expiring|expired|missing|needs-profile-info|not-applicable`);
`missing` for a filing means "no proof/attestation on record," never a legal
verdict.

**RD-b — per-worker credentials are tracked at VENDOR level in v1, not a
per-employee roster.** Guard registration, food-handler cards, CDLs, and Part 107
certs set `perWorker: true` (drives UI copy: "each worker performing X must hold
…"), but v1 tracks a single obligation/evidence item per credential type per
vendor. Per-employee rosters are deferred (Phase 2+). Rationale: matches how the
product tracks vendors today; avoids building an employee-management surface for
v1. Flagged for founder awareness.

**RD-c — `documentType` maps onto the EXISTING `Document.DocumentType` vocabulary**
(`coi`, `license`, `certification`, `other`) so tracked documents reconcile with
the extraction pipeline; specificity lives in `documentSubType`:
| rule category | Document.DocumentType | example documentSubType |
|---|---|---|
| insurance | `coi` | `tx-security-gl`, `tx-amusement-ride-liability`, `fed-passenger-mcs90` |
| license / permit | `license` | `tx-dps-security-contractor`, `tabc-mixed-beverage`, `dshs-mobile-food-vendor` |
| worker-certification | `certification` | `tx-food-handler`, `faa-part107`, `tx-cdl-passenger` |
| filing | `other` | `dwc-005`, `tx-franchise-report`, `ttb-5630-5d` |
The engine matches a tracked `Document` to an obligation by
`(DocumentType, documentSubType)` where set, else `DocumentType` + the vendor's
entity context. Unmapped/legacy `other` documents never auto-satisfy a specific
obligation.

### Mechanical interpretations resolved in the engine build (2026-07-07, verified)

Engine core built + 96 synthetic-fixture tests green. Non-rule-content decisions:
- **`validTo` is inclusive** (last valid day); on version overlap, latest
  `validFrom` wins.
- **Cadence anchors**: `documentExpiration | issueDate | calendarDate | fixedDate`;
  `calendarDate`/`fixedDate` both mean "next annual occurrence of `{month,day}`".
- **`satisfiesFederal` suppression fires only on a Kleene-TRUE state match** — an
  Unknown/unresolved state rule can NEVER suppress a federal floor. (Safety.)
- **Unset `state`** ⇒ evaluate federal rules only, skip state rules, surface
  `state` in `OutstandingProfileFacts`; never assume TX.
- **`IDocumentLike.IssueDate`** exists for the `issueDate` anchor; the EF
  `Document` has no issue-date column yet — unset until the persistence step.

## Build plan (Phase 2 — order)

1. **Engine core** (this drop): `RuleData` types, JSON loader + boot validation,
   Kleene applicability evaluator, cadence date-math (pure, `TimeProvider`),
   `ObligationReport` DTOs, the interstate/intrastate + no-completeness-illusion +
   penalty-framing gates. Unit + property tests on SYNTHETIC fixtures (no real
   rule content) so mechanics are proven independently.
2. **Rule data**: encode the verified dossier rules into
   `RuleData/{us-fed,us-tx}/<entity>.json`; golden-file tests with realistic
   entity fixtures. Only `verified` ships; `probable` behind the review flag.
3. **Entity-profile persistence**: migration adding the §4 facts
   (Organization.state, Vendor entity-type + fact columns/JSON); feature-flag
   wiring (per rule-set file).
4. **Pass 3 (adversarial) + Pass 4 (code review)**; fix all findings.
