# Rule schema design — DRAFT (pending Phase 1 dossier)

Status: **DRAFT**, authored 2026-07-07 during Phase 1 research. [FABLE]-tier
design decisions. The condition vocabulary (§4) will be finalized against the
completed research dossier before the schema freezes. Opus: implement against
this file only after it says FROZEN.

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
- Draft fact vocabulary (to finalize from dossier): `state`, `entityType`,
  `hasEmployees`, `employeeCount`, `servesOrSellsAlcohol`, `preparesFood`,
  `operatesDronesCommercially`, `providesArmedGuards`, `operatesForklifts`,
  `rentsInflatableAmusementDevices`, `operatesVehiclesForHire`,
  `maxVehicleSeatingCapacity`, `operatesInterstate`, `carriesWorkersComp`.

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

## Open design questions (resolve before FREEZE)

1. Whether `filing`-category obligations (workers'-comp non-subscriber notice,
   franchise tax report) fit v1's document-centric UX or ship as
   reminders-only — decide after dossier shows their real shapes.
2. Per-worker credentials (guard registration, food handler): v1 likely tracks
   at vendor level ("evidence that workers hold X"), not a per-employee roster
   — confirm framing with founder at dossier checkpoint.
3. Exact `DocumentType`/`DocumentSubType` mapping to the extraction pipeline's
   existing vocabulary.
