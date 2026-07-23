# 0043. Stage the corrected additional-insured claim wording behind a default-OFF flag (CLM-1)

- **Status:** accepted
- **Date:** 2026-07-23
- **Deciders:** Ruben G. (founder), Claude (implementing #396)

## Context

The additional-insured requirement verifies "is the ADDL INSD box ticked, and does the venue name
appear in the certificate-holder box or description of operations" — a reasonable screen. But the UI
then asserts the **categorical** sentence *"Names 'Riverside Event Hall' as additional insured"*
(the `additional_insured` entry in `frontend/src/lib/requirements.ts`), and the failure copy says the
name *"was not found as an additional insured."* Both overstate what the document can prove.

An **ACORD 25 face confers no rights** and only *indicates* additional-insured status; the status
itself exists only via a policy endorsement (CG 20 26-class) or the policy provisions. A certificate
where a producer merely ticked the "Y" column — no endorsement attached — passes the screen, and the
product asserts additional-insured coverage that may not exist. Asserting coverage that may not exist
is a reliance/liability trap (TDI enforces the same line under Tex. Ins. Code §§1811.051/1811.152).

This is **counsel-gate item CLM-1** (`docs/rule-engine/G1-COUNSEL-BRIEF.md` §0, launch-blocking,
pending a licensed **Texas attorney's** sign-off). The template review that sourced the corrected
wording (`TEMPLATE-REQUIREMENTS-REVIEW.md` §3) is explicit that the existing engine **check**
(affirmative mark + name in holder/description) is *"a reasonable screen worth keeping."* So the fix
is **copy only** — no rule/verdict change, no endorsement-attachment detection, no negation guard.

The repo already stages counsel-gated corrections as **merged-but-inert** feature flags — the
regulatory rule engine (`RuleEngine:Enabled`, [ADR 0036] precursor) and the corrected system
checklists (`TemplateCorrections:Enabled`, [ADR 0036]). The founder chose the same posture here:
ship the corrected copy behind a new default-OFF flag, ready to flip the day CLM-1 clears.

## Decision

**Stage the corrected additional-insured claim wording behind a new, distinct, default-OFF flag —
`ComplianceClaims:CorrectedAdditionalInsuredWording` (settings class `ComplianceClaimsSettings`,
section `ComplianceClaims`, bool `CorrectedAdditionalInsuredWording`, default `false`).** It is
merged but inert; flipping it to `true` is a **copy-only** change and is the entire runbook for
CLM-1.

The wording, taken verbatim from `TEMPLATE-REQUIREMENTS-REVIEW.md` §3 (not invented here):

| Surface | Flag OFF (prod default, byte-identical to pre-#396) | Flag ON |
|---|---|---|
| Catalog sentence | `Names "{name}" as additional insured` | `Certificate indicates "{name}" as additional insured` |
| Failure / error message | `"{name}" was not found as an additional insured.` | `The certificate does not indicate "{name}" as an additional insured.` |
| Affirmative-flag (ACORD-checkbox fallback) check note | `The additional-insured box is checked…` | `The certificate indicates additional insured… A certificate only indicates this — request the endorsement (e.g. CG 20 26) to confirm coverage.` |

(Empty name → "your company" / "Your company", mirroring today's fallback.)

**Wiring** (mirrors exactly how `TemplateCorrections` surfaces `features.correctedChecklists`):

- The flag is surfaced on every me-shaped payload as
  `AuthFeatures.CorrectedAdditionalInsuredWording` → `/api/auth/me` `features.correctedAdditionalInsuredWording`.
- The frontend consumes it the same way it consumes `correctedChecklists` (`useMe()` → optional-chain
  through `features`, strict `=== true`, so a loading/undefined/old-backend `me` defaults to the
  **legacy** copy). It gates the read-view sentence (rules-page requirement rows + summary, and the
  document-detail **"What we checked"** additional-insured assertion) and the `errorMessage` stored on
  a new/edited additional-insured rule. The catalog copy is single-sourced through helpers whose
  flag-off branch is byte-identical to the pre-#396 literal.
- The backend threads the flag from `ComplianceCheckService`'s constructor (`IOptions<ComplianceClaimsSettings>`)
  through `ComputeOutcome` → `EvaluateRule` (a `bool` param, default `false`) so the affirmative-flag
  branch's **note** reframes when ON. The `bool` param default preserves today's behavior for the many
  direct-construction unit tests.

**Hard boundaries recorded as part of this decision:**

1. **Flag OFF is byte-identical to today, everywhere** — sentence, failure message, check note, and
   the pass/fail **verdict**. This is the merge-safety guarantee; prod is unchanged on merge (pinned by
   test on every surface).
2. **Copy only — the flag NEVER changes a verdict.** `EvaluateRule`'s `fallbackHit` (the pass/fail) is
   computed identically regardless of the flag; only the note string differs. A `[Theory]` grades the
   same inputs both ways and pins the verdict equal.
3. **Distinct from `TemplateCorrections`.** CLM-1 unlocks on a different sign-off (the additional-insured
   *claim wording* — an attorney/liability question) than TPL-A/B (the corrected *dollar minimums* — a
   broker/attorney question), so the two flags must be flippable independently. Do not fold them together.
4. **The engine check is unchanged.** No negation guard, no endorsement-attachment detection (a deferred
   founder+broker product decision, TRR §5/§3.3), no new `ExtractionField`.

## Consequences

### Positive
- Once CLM-1 clears, one config flip retires the categorical overclaim across the rules page, the
  document detail page, and the persisted check notes — with the honest "indicates, not grants —
  request the endorsement" framing (and the CG 20 26 pointer) in its place.
- Merge-safe: the deployed product is behaviorally identical to pre-#396 prod while the flag is off,
  pinned by tests on the note (both hit and miss), the me feature (default-off + on), and the catalog
  copy (byte-identical legacy strings).
- The flag is reversible (a compliance-claim can be re-gated), mirroring `TemplateCorrections`.

### Negative
- One more merged-inert flag to eventually retire after the sign-off (collapse the helper's legacy
  branch + the flag, like the `TemplateCorrections` cleanup step). Tracked with CLM-1.
- The `errorMessage` is **stored on the rule at author time**, so an additional-insured rule authored
  while the flag is off keeps its legacy "was not found" message until edited after a flip. Acceptable:
  the primary honest-framing surface is the **read-view sentence** (re-derived on every read and fully
  gated); the stored message is secondary, and the check **note** (also read-derived) reframes for all
  documents immediately on flip.

### Neutral
- **Marketing-site copy is OUT OF SCOPE here.** The public hero / how-it-works / FAQ lines that carry
  the same overclaim (e.g. "flagging anyone who listed you as certificate holder instead of additional
  insured", "your venue named as additional insured") are also CLM-1, but they live on public pages
  with **no `features` flag** and are addressed at flip time, not in this change (G1-COUNSEL-BRIEF §C,
  TRR §7).
- Test hosts leave the flag at its prod default (OFF); the flag-ON value is pinned by an isolated-host
  integration test, mirroring `TemplateCorrectionsFlagTests`.

## Alternatives considered

### Option A — Change the copy directly, no flag
Reword `requirements.ts` and the check note now. **Rejected**: CLM-1 is launch-blocking and pending a
TX-attorney sign-off; shipping the changed claim before sign-off defeats the whole G1 gate. The
merged-but-inert flag is the repo's established way to land the code early and flip it on sign-off.

### Option B — Reuse the existing `TemplateCorrections:Enabled` flag
Fold the wording into `features.correctedChecklists`. **Rejected**: CLM-1 (claim wording) and
TPL-A/B (dollar minimums) unlock on *different* professional sign-offs, so a single flag would
force-couple an attorney copy decision to a broker minimums decision — one could not ship without the
other. A distinct flag keeps them independent.

### Option C — Also change the check (negation guard / endorsement detection)
Make the screen stricter so it no longer passes a bare "Y". **Rejected / out of scope**: TRR §3 keeps
the screen as-is ("a reasonable screen worth keeping"); real additional-insured verification needs the
endorsement page, which is a deferred product decision (endorsement upload flow + an
`endorsement_forms_listed` extraction field, TRR §5), not a wording fix. This change touches **copy
only** — the verdict is provably identical flag-on vs flag-off.

## References

- Tickets: [#396](https://github.com/neboxdev/complidrop/issues/396) (bug + careful-review), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic)
- Gate: `docs/rule-engine/G1-COUNSEL-BRIEF.md` §0 (CLM-1) + §C; research basis `docs/rule-engine/TEMPLATE-REQUIREMENTS-REVIEW.md` §3 (the exact wording), §7 (marketing claims), §5 (endorsement gap)
- ADRs: [0036](0036-system-template-seed-convergence.md) (the `TemplateCorrections` merged-but-inert staging precedent this mirrors; Amendment 3 = the `features.correctedChecklists` me flag pattern)
- Code: `Configuration/ComplianceClaimsSettings.cs`, `Program.cs` (registration + boot line), `DTOs/Auth/AuthDtos.cs` (`AuthFeatures`), `Endpoints/AuthEndpoints.cs` (`ToMeResponse`), `Services/ComplianceCheckService.cs` (`EvaluateRule` note), `frontend/src/lib/requirements.ts`, `frontend/src/app/(dashboard)/rules/page.tsx`, `frontend/src/app/(dashboard)/documents/[id]/page.tsx`, `frontend/src/hooks/useAuth.ts`
