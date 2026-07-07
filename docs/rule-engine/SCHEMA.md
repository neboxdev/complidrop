# Rule schema design — FROZEN v1 (2026-07-07)

Status: **FROZEN** as of 2026-07-07, after the Phase-1 dossier + review gauntlet
completed and the founder said "go" to Phase 2. The applicability-fact registry
(§4) is locked against the dossier's actual triggers; the three open design
questions are resolved (§ Resolved decisions). The engine is built against THIS
file. Changing a frozen fact name / rule shape = a schema-version bump + a note
here, not a silent edit.

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
    { "fact": "maxVehicleSeatingCapacity", "op": "gte", "value": 16 }
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
| `providesArmedGuards` | bool | security-service | security officer commission; PPO license; Level III/IV training |
| `operatesVehiclesForHire` | bool | transportation | base gate for all transport obligations |
| `operatesInterstate` | bool | transportation | **the critical branch**: FMCSA authority/UCR/§387 federal floor (true) vs TxDMV reg + §218.16 TX floor (false). Unknown ⇒ `needs-profile-info` |
| `maxPassengerSeatingCapacity` | int (**including the driver**) | transportation | capacity thresholds: CDL/fed-$5M ≥16; fed-$1.5M ≤15; TX-reg >15; TX-$5M ≥27; TX-$500k 16–26; UCR >10. ALL normalized to seats-incl-driver. |
| `operatesDronesCommercially` | bool | photographer-videographer | FAA Part 107 cert + 24-mo recency + drone registration |
| `sellsTaxableGoodsOrServices` | bool | all | Texas sales & use tax permit |
| `isFranchiseTaxableEntity` | bool | venue-org (+ any registered entity) | Texas franchise-tax annual report |

**Capacity convention (locked):** `maxPassengerSeatingCapacity` is the vehicle's
designed seating capacity **including the driver**. The dossier's "27 or more not
including the driver" was normalized at D-3 to "27 or more including the driver";
every threshold in the rules is expressed incl-driver so one fact serves all.

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
  `satisfied | expiring | expired | missing | needs-profile-info | not-applicable`.
  No LLM calls, no clock reads (evaluation date injected), no DB access inside
  the evaluator.
- Only `confidence: verified` rule versions load in production; `probable`/
  `uncertain` versions are visible only behind the review flag.
- Feature flag per rule-set file (jurisdiction × entity type) so rollout is
  per-rule-set after founder sign-off.

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
   resolve the `operatesInterstate` fact before selecting any transportation
   insurance floor. Federal for-hire passenger financial responsibility ($5M for
   16+ seats / $1.5M for ≤15) and Texas intrastate minimums ($500k for 16–26
   seats / $5M for 26+ not-incl-driver) are DIFFERENT floors on the same vehicle.
   If `operatesInterstate` is unknown ⇒ `needs-profile-info`, never a flat
   "compliant". FMCSA operating authority + UCR are interstate-only obligations.
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
   verified` load in prod.** Single-reproduction entries (`probable`) and the
   whole Texas security rule-set (methodology human-gate) stay behind the review
   flag until the founder confirms — see METHODOLOGY provenance rule.

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
