# Rule engine — AUDIT INDEX

**Purpose.** This directory is the audit trail for CompliDrop's regulatory
compliance rule engine. It exists so a reviewer (human or automated) can trace
**every encoded legal claim back to a primary source**, and **every engine
guarantee back to a test**, without trusting any narrative.

**Scope of the work audited:** US federal + Texas regulatory obligations for six
entity types, encoded as versioned data, evaluated by a deterministic engine.
Built 2026-07-07 → 2026-07-08 on branch `feat/compliance-rule-engine`.

**Status:** built, reviewed, fixed, verified; merged 2026-07-09 (founder-delegated).
**Ships feature-flag OFF — inert in production.** One human gate remains open: G1,
counsel review of the user-facing framing (see `04-LIMITATIONS-AND-GATES.md`).

---

## 1. Read these in order

| Doc | Answers |
|---|---|
| `README.md` (this file) | What exists, where it lives, how to verify in 2 minutes |
| [`01-PROCESS-LOG.md`](01-PROCESS-LOG.md) | Every step taken, in order, with dates, decisions, and rationale — including a mid-session model downgrade and the controls added to compensate |
| [`02-PROVENANCE-MAP.md`](02-PROVENANCE-MAP.md) | **The audit table.** Every one of the 40 encoded rules → its statutory citation → the dossier entry holding the verbatim quote → confidence + provenance tier |
| [`03-VERIFICATION-GUIDE.md`](03-VERIFICATION-GUIDE.md) | How to independently re-verify: commands, test-name → guarantee mapping, the 4 review passes and what each found |
| [`04-LIMITATIONS-AND-GATES.md`](04-LIMITATIONS-AND-GATES.md) | What is **not** verified, **not** enforced, and **not** shipped — plus residual risk and the open human gates |

---

## 2. Headline numbers (all independently verified, see §4)

| Metric | Value | Where to check |
|---|---|---|
| Encoded rules | **40** | `api/CompliDrop.Api/RuleData/**/*.json` (Pass 5 added `tx-venue-wc-coverage-notice`) |
| …`verified` confidence | 37 | field `versions[].confidence` |
| …`probable` (do NOT ship) | 3 | filtered by `RuleLoadOptions.VerifiedOnly` (default `true`) |
| …review-gated | 0 | the TX security gate was lifted 2026-07-09 when G2 closed — see `evidence/g2/` |
| **Rules that load in prod posture** | **37** | test `The_full_and_production_sets_have_the_expected_rule_counts` |
| Rule-data files | 11 | `RuleData/us-fed/` (5), `RuleData/us-tx/` (6, incl. `cross-cutting.json`) |
| Research dossier files | 12 | `docs/rules-research/{federal,texas}/*.md` |
| Engine tests | **225 pass / 0 fail** | `dotnet test --filter RuleEngine` |
| Load-bearing facts independently re-derived (Pass 2) | **19 / 19 matched** | `docs/rule-engine/REVIEW-LOG.md` § Pass 2 (18 components on official hosts; the UCR statutory chain via Cornell — see D-4) |
| Highest-stakes facts re-verified live by Fable (Pass 5) | **12 / 12 matched** | `docs/rule-engine/REVIEW-LOG.md` § Pass 5 |

Every `verified` rule carries a **verbatim quote of the controlling statutory
text** in its dossier entry (`Operative text:` field). No quote ⇒ not `verified`.

---

## 3. Artifact map — where everything lives

### The legal research (the source of truth for every number)
| Path | What it is | Audit it for |
|---|---|---|
| `docs/rules-research/METHODOLOGY.md` | The rules the research had to follow | Confidence rubric, `provenance` tiers, the quote-the-text requirement |
| `docs/rules-research/federal/<entity>.md` | Federal obligations, 6 files | `Operative text:` verbatim quotes, `Citation:` URLs, `Verified:` dates |
| `docs/rules-research/texas/<entity>.md` | Texas obligations, 6 files | same; plus "Local-level obligations (noted, not encoded)" |

Entities: `caterer`, `event-rental`, `security-service`, `transportation`,
`photographer-videographer`, `venue-org`.

### The encoded rules (what the engine actually executes)
| Path | What it is |
|---|---|
| `api/CompliDrop.Api/RuleData/us-fed/*.json` | Federal rule data (5 files) |
| `api/CompliDrop.Api/RuleData/us-tx/*.json` | Texas rule data (6 files, incl. cross-cutting.json) |

Each rule carries `obligationRef` (→ dossier entry), `citation.section`,
`citation.url`, `citation.verifiedDate`, `confidence`, `applicability`, `cadence`,
`insuranceMinimums`, `rationale`, `userAction`.

### The engine
| Path | What it is |
|---|---|
| `api/CompliDrop.Api/RuleEngine/RegulatoryObligationEvaluator.cs` | The pure evaluator |
| `api/CompliDrop.Api/RuleEngine/ApplicabilityEvaluator.cs` | Kleene 3-valued logic |
| `api/CompliDrop.Api/RuleEngine/CadenceCalculator.cs` | Pure date-only deadline math |
| `api/CompliDrop.Api/RuleEngine/RuleSetLoader.cs` | JSON load + fail-fast validation + confidence/gate filtering |
| `api/CompliDrop.Api/RuleEngine/FactRegistry.cs` | The frozen applicability-fact vocabulary |
| `api/CompliDrop.Api/RuleEngine/ObligationReport.cs` | Output types (no `IsCompliant` boolean exists) |

### The specs and review records
| Path | What it is |
|---|---|
| `docs/rule-engine/SCHEMA.md` | **FROZEN v1** rule schema + the 3 hard legal requirements + v1 known limitations |
| `docs/rule-engine/REVIEW-LOG.md` | All review passes, every finding, every fix, the discrepancy log |
| `RULES-REVIEW.md` (repo root) | The founder sign-off artifact: rule table, gates, coverage |
| `docs/HANDOFF.md` | Continuous session state / decision record |

### Commits (branch `feat/compliance-rule-engine`)
| SHA | Contents |
|---|---|
| `6931d35` | Research dossier + methodology + schema draft + review log + RULES-REVIEW |
| `087c6e1` | Engine core + encoded rule data + 110 tests |
| `c53a975` | Every review finding fixed; 110 → 154 tests |
| `096fa7c` | This audit trail (index, process log, provenance map, verification guide, gates) |
| *(Pass 5)* | Fable re-review: every finding fixed, insurance amount gate (v1.2), entity-profile persistence + feature flags; 154 → 225 tests — see REVIEW-LOG § Pass 5 |

---

## 4. Two-minute independent verification

```bash
# 1. Engine compiles and every guarantee holds (expect: 225 passed, 0 failed)
dotnet test api/CompliDrop.Api.Tests/CompliDrop.Api.Tests.csproj --filter "FullyQualifiedName~RuleEngine"

# 2. Every rule file loads and passes fail-fast validation
#    (test: Every_embedded_rule_data_file_loads_and_validates)

# 3. Prod posture ships exactly 37 rules (40 - 3 probable; no review gate since G2 closed 2026-07-09)
#    (test: The_full_and_production_sets_have_the_expected_rule_counts)

# 4. No non-US regulation anywhere. Expect EXACTLY TWO benign hits, both verified:
#      docs/rules-research/METHODOLOGY.md  -> the scope-exclusion guardrail sentence
#      docs/rules-research/texas/venue-org.md -> "DWC publishes the exact posting
#         language (English/Spanish)" — a LANGUAGE, not a jurisdiction.
#    Zero hits in api/CompliDrop.Api/RuleData/. No non-US regulation is cited anywhere.
grep -rniE "spain|spanish|españa|european union|\bGDPR\b" docs/rules-research/ api/CompliDrop.Api/RuleData/
```

Evidence base size: **58 verbatim `Operative text:` statutory quotes** across the 12
dossier files (`grep -rc "Operative text:" docs/rules-research/federal docs/rules-research/texas`;
a repo-wide grep returns 59 — the extra hit is METHODOLOGY.md's entry-format template line,
not a statutory quote).

---

## 5. Auditing a single legal claim — the standard trace

To check any encoded number (e.g. *"a Texas guard company must carry $100,000
per-occurrence liability insurance"*):

1. **Find the rule.** `grep -rn "tx-security-general-liability-insurance" api/CompliDrop.Api/RuleData/`
   → gives `insuranceMinimums`, `citation.section`, `obligationRef`.
2. **Find the research entry.** Take the `obligationRef` (`OBL-TX-SECURITY-003`)
   → `grep -rn "OBL-TX-SECURITY-003" docs/rules-research/`
   → the dossier entry contains **`Operative text:`**, the verbatim statutory quote.
3. **Check the primary source.** Open `citation.url` / the section
   (`Tex. Occ. Code 1702.124(c)`) and compare against the quoted `Operative text`.
4. **Check the provenance tier.** See [`02-PROVENANCE-MAP.md`](02-PROVENANCE-MAP.md) —
   `official` (read on a .gov/GPO host) vs `reproduction-validated` (verbatim text from a
   faithful reproduction; the official Texas statute site blocks flat fetching — though the
   Pass-5 review re-read the load-bearing sections on the official host via a real browser).
5. **Check it's actually enforced the way it reads.** See
   [`03-VERIFICATION-GUIDE.md`](03-VERIFICATION-GUIDE.md) for the test that pins it, and
   [`04-LIMITATIONS-AND-GATES.md`](04-LIMITATIONS-AND-GATES.md) — since v1.2, GENERAL-liability
   floors ARE compared against the extracted amount (below ⇒ `below-stated-minimum`; unreadable
   ⇒ `needs-document-info`); AUTO-liability floors (the transport rules) are deliberately NOT
   compared against the general-liability figure (wrong policy line) and keep presence+expiry.

---

## 6. The three hard requirements the engine must satisfy

These came out of the legal/compliance review and are enforced **structurally**,
not by convention. An auditor should confirm each:

| # | Requirement | How it's enforced | Test |
|---|---|---|---|
| 1 | Interstate vs intrastate must be resolved before any transport insurance floor is selected; unknown ⇒ `needs-profile-info`, never a pass | Applicability is resolved before satisfaction is computed; a present document cannot launder an Unknown | `Unknown_interstate_yields_needs_profile_info_never_satisfied_even_with_a_document` |
| 2 | No bare "compliant" that implies the tracked set is exhaustive | `ObligationReport` has **no** `IsCompliant` boolean; the notice is mandatory — the **ObligationReport constructor** rejects an empty notice text, so a report cannot be built without one | `Report_type_exposes_no_overall_compliant_boolean`, `A_report_cannot_be_built_with_an_empty_completeness_notice` |
| 3 | Penalties are stated as what the statute provides — never an adjudication of *this* user | `ObligationStatus` has no "violation"/"illegal" value; rationale copy reviewed rule-by-rule | `Every_report_carries_the_completeness_notice_and_verbatim_rule_framing` |

---

## 7. What this engine deliberately does NOT claim

Read [`04-LIMITATIONS-AND-GATES.md`](04-LIMITATIONS-AND-GATES.md) before relying on any output.
Summary: it verifies insurance **amounts** only for general-liability floors (auto-liability
floors keep presence+expiry until extraction reads that policy line); it does not cover
municipal/county obligations; it covers **only** Texas among the states; and 8 of
the 40 rules (3 `probable` + 5 review-gated) do **not** load in the production
posture.
