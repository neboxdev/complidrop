# 03 — VERIFICATION GUIDE: how to check this yourself

Nothing here asks you to trust a narrative. Every claim below maps to a command or
a named test.

---

## 1. Commands

```bash
# Full engine suite — expect: Passed! Failed: 0, Passed: 154
dotnet test api/CompliDrop.Api.Tests/CompliDrop.Api.Tests.csproj --filter "FullyQualifiedName~RuleEngine"

# Production code compiles clean
dotnet build api/CompliDrop.Api/CompliDrop.Api.csproj

# Scope hygiene: no non-US regulation anywhere in research or rule data.
#   Expect EXACTLY TWO hits, both benign and both verified by hand:
#     - docs/rules-research/METHODOLOGY.md      -> the scope-exclusion guardrail sentence
#     - docs/rules-research/texas/venue-org.md  -> "DWC publishes the exact posting
#          language (English/Spanish)" — that is a LANGUAGE, not a jurisdiction.
#   ZERO hits under api/CompliDrop.Api/RuleData/. No non-US regulation is cited anywhere.
grep -rniE "spain|spanish|españa|european union|\bGDPR\b" docs/rules-research/ api/CompliDrop.Api/RuleData/

# Every verified rule carries a verbatim statutory quote (expect 59 across 12 files)
grep -rc "Operative text:" docs/rules-research/federal docs/rules-research/texas
```

Test source: `api/CompliDrop.Api.Tests/RuleEngine/`
(`ApplicabilityEvaluatorTests`, `CadenceCalculatorTests`, `EntityProfileAndReportTests`,
`RuleSetLoaderTests`, `RegulatoryObligationEvaluatorTests`, `RealRuleDataLoadTests`,
`RuleDataGoldenTests`).

---

## 2. Guarantee → test mapping

Search the test name to find the proof. **This is the table an automated auditor
should walk.**

### The three hard legal requirements
| Guarantee | Test |
|---|---|
| Unknown interstate status can never become a pass, even with a document present | `Unknown_interstate_yields_needs_profile_info_never_satisfied_even_with_a_document` |
| Same vehicle, different jurisdictions ⇒ different insurance floors | `The_same_20_seat_shuttle_faces_different_insurance_floors_interstate_vs_intrastate` |
| The report exposes no overall "compliant" boolean | `Report_type_exposes_no_overall_compliant_boolean` |
| A report cannot exist without a non-exhaustiveness notice | `A_report_cannot_be_built_with_an_empty_completeness_notice` |
| Even a fully-satisfied report still carries the notice | `A_fully_satisfied_report_still_carries_the_non_exhaustiveness_notice` |
| Rule framing is verbatim, not an adjudication | `Every_report_carries_the_completeness_notice_and_verbatim_rule_framing` |
| Local (city/county) obligations are surfaced, per entity | `Local_obligation_pointers_are_populated_for_each_texas_entity` |

### Never assert from ignorance
| Guarantee | Test |
|---|---|
| An unset fact is Unknown, never False | `An_unset_fact_is_unknown_never_false` |
| A null entity type ⇒ needs-profile-info, never Missing or Satisfied | `A_null_entity_type_makes_type_scoped_rules_needs_profile_info_never_missing_or_satisfied` |
| An unmodeled entity type ⇒ not-covered, never an empty all-clear | `A_set_but_unmodeled_entity_type_reports_not_covered_never_an_empty_all_clear` |
| A non-covered state ⇒ not-covered, never an empty pass | `A_non_covered_state_reports_not_covered_never_an_empty_compliant` |
| An unset state evaluates federal rules only, and says so | `An_unset_state_evaluates_federal_rules_only_and_surfaces_state_as_outstanding` |
| A matched document with no readable expiry ⇒ needs-document-info, never Satisfied | `A_matched_renewal_document_with_no_readable_expiry_is_needs_document_info_never_satisfied` |
| …but a one-time credential with a document **is** Satisfied (correct) | `A_one_time_permit_with_a_matched_document_and_no_expiry_is_satisfied` |
| A document with a null subtype doesn't satisfy a subtyped obligation | `A_document_with_a_null_subtype_does_not_satisfy_a_subtyped_obligation` |

### Federal/state layering
| Guarantee | Test |
|---|---|
| A Texas credential that implements a federal floor suppresses it (no double-count) | `The_texas_cdl_suppresses_the_federal_cdl_so_it_is_not_double_emitted` |
| Suppression requires an **applicable** state rule, not merely a present one | `Suppression_requires_an_applicable_state_rule_not_merely_a_present_one` |
| **An Unknown state rule can NEVER suppress a federal obligation** | `An_unknown_state_rule_does_not_suppress_the_federal_obligation_it_would_implement` / `The_texas_cdl_does_not_suppress_the_federal_cdl_when_capacity_is_unknown` |

### The money (insurance floors and capacity thresholds)
| Guarantee | Test |
|---|---|
| Exactly one insurance floor applies, and it matches capacity + interstate status — tested at the **exact** boundaries 15/16/26/27 | `The_single_applicable_transport_insurance_floor_matches_capacity_and_interstate` |
| Interstate selects the federal floor; Texas floor becomes not-applicable | `Interstate_true_selects_the_federal_floor_and_marks_the_texas_floor_not_applicable` |
| Intrastate selects the Texas floor; federal becomes not-applicable | `Interstate_false_selects_the_texas_floor_and_marks_the_federal_floor_not_applicable` |
| FMCSA operating authority is interstate-only | `Fmcsa_operating_authority_appears_only_for_the_interstate_shuttle` |
| The Clearinghouse attaches to intrastate carriers too (not interstate-only) | `Clearinghouse_attaches_to_an_intrastate_16plus_carrier_not_only_interstate` |
| UCR applies only above the 10-passenger threshold | `Ucr_applies_only_above_the_ten_passenger_threshold` |
| Inflatables get the §2151.1012 `$1M` obligation; non-inflatable renters get none | `Event_rental_renting_inflatables_gets_the_2151_1012_one_million_obligation` / `Event_rental_not_renting_inflatables_has_no_amusement_obligation` |
| Insurance minimums appear only on insurance rules and are positive | `Insurance_minimums_appear_only_on_insurance_rules_and_are_positive` |

### Rule data integrity
| Guarantee | Test |
|---|---|
| Every rule file loads and passes fail-fast validation | `Every_embedded_rule_data_file_loads_and_validates` |
| Prod posture = 31 rules (39 − 3 probable − 5 gated) | `The_full_and_production_sets_have_the_expected_rule_counts` |
| Every rule has an `obligationRef` and `basis: regulatory` | `Every_rule_carries_a_mandatory_obligation_ref_and_regulatory_basis` |
| Every `satisfiesFederal` reference resolves | `Satisfies_federal_references_resolve_against_loaded_federal_rules` |
| Only the TX security set is review-gated | `Only_the_tx_security_rule_set_is_review_gated` |
| The modeled entity types match the canonical constant | `The_modeled_entity_types_match_the_canonical_constant` |
| Each result carries a unique rule id (so a same-ref decoy can't shadow it) | `Each_result_carries_its_unique_rule_id` |
| Actionable statuses sort before non-actionable | `Obligations_are_ordered_actionable_first_then_by_ref` |

### Date / cadence math (pure, `DateOnly`, injected evaluation date)
| Guarantee | Test |
|---|---|
| Month-length and leap-year clamping (Jan 31 + 1mo; Feb 29 + 12mo) | `AddMonthsClamped_handles_month_length_and_leap_year_edges` (+ a property test) |
| Fixed-annual dates clamp at Feb 29 | `NextAnnualOccurrence_finds_the_next_calendar_date_with_feb29_clamp` |
| Grace-period boundary is exact | `ClassifyTiming_places_the_evaluation_date_around_due_plus_grace` / `ClassifyTiming_with_zero_grace_is_overdue_the_day_after_due` |
| DST cannot shift a deadline | `Arithmetic_is_date_only_and_unaffected_by_dst_transitions` |
| An issue-date-anchored recency clock (FAA Part 107, 24 months) works end-to-end | `Drone_photographer_part107_recency_tracks_the_24_month_clock_from_issue_date` |
| Franchise tax is a fixed-annual May 15 filing | `Venue_franchise_report_is_a_fixed_annual_may_15_filing` |
| DWC-005 requires non-subscriber **and** ≥1 employee | `Venue_dwc005_requires_non_subscriber_and_at_least_one_employee` |

### Kleene three-valued logic (exhaustive, not sampled)
`ApplicabilityEvaluatorTests` walks the full truth tables over every operand pair
**and triple** (`All_and_any_match_the_reference_over_every_operand_triple`), plus
missing-fact precision (`An_undecided_all_reports_only_the_load_bearing_unknown_facts`).

---

## 3. The review passes — what each one actually did

Full record with file:line evidence: `docs/rule-engine/REVIEW-LOG.md`.

| Pass | Who | Method | Outcome |
|---|---|---|---|
| **0** | Orchestrator | Read the completed dossier files directly (not agent summaries) | Found the single-reproduction provenance gap (F-1/F-2), routed to Pass 2 |
| **1** | Legal/compliance reviewer (isolated) | Rule-by-rule scrutiny of sources, framing, liability | Cleared framing; found **HB 2844** staleness (a law effective 6 days before the research), provenance overstatements |
| **2** | **Two blind re-derivation agents** | Told the *question*, not the answer. Re-derived every load-bearing figure/cadence from official sources via Playwright, then diffed | **19/19 matched.** Produced 5 corrections (D-1…D-5) including the controlling inflatable statute and a superseded Texas threshold |
| **3** | Adversarial agent | Actively tried to break the engine; each confirmed break proven with a throwaway test against the **real** rule data | Found the null-expiry false pass, the entity-type escapes, the gate conflation. **Cleared**: capacity boundaries, Kleene, version selection, cadence math |
| **4** | correctness / compliance-claims / test-quality reviewers (isolated) | Standard code review, claim-vs-code-vs-dossier divergence, and test rigor | Correctness independently certified the core logic; compliance-claims confirmed **zero numeric drift**; test-quality found mutation-surviving gaps |

**Every finding from all four passes was fixed** (commit `c53a975`), except one
explicitly scoped-and-documented item: insurance-amount enforcement (§4 below).

---

## 4. How the *tests themselves* were audited

The test-quality reviewer was asked to find, for each guarantee, **a plausible
code mutation that the existing suite would not catch**. Three survived and were
closed:

| Mutation that used to survive | Now caught by |
|---|---|
| `satisfiesFederal` suppression firing on Kleene-**Unknown** as well as True (would silently hide a federal obligation from ignorance). The old test used capacity 10 → a *decided False*, not Unknown | `An_unknown_state_rule_does_not_suppress_the_federal_obligation_it_would_implement` |
| Shifting a capacity threshold by one (e.g. Texas `$5M` tier to `gte 26`) — no test evaluated a 15-, 26- or 27-seat vehicle, and the fed `$1.5M` and TX `$5M` floors were never triggered at all | `The_single_applicable_transport_insurance_floor_matches_capacity_and_interstate` |
| Defaulting an unknown/unset state to Texas (applying TX law to a non-TX vendor) | `An_unset_state_evaluates_federal_rules_only_and_surfaces_state_as_outstanding` |
| `expiry <= evaluationDate` (making "expires today" already expired) | boundary row added to `License_status_follows_the_matched_documents_expiry` |

---

## 5. What the orchestrator verified personally (vs. delegated)

Delegated work was **never** accepted on its own report:

| Claim | Independent check performed |
|---|---|
| "Engine builds, 110 tests pass" | Orchestrator ran `dotnet test` itself |
| "Rule data encoded correctly" | Orchestrator read `us-tx/security-service.json`, `us-fed/transportation.json`, `us-tx/transportation.json` directly and compared each figure and applicability gate against the dossier |
| "Security insurance is $100k/$200k" | Orchestrator fetched Tex. Occ. Code §1702.124 independently and confirmed the text |
| "Texas statutes site is unreachable" | Orchestrator fetched it directly (got the JS shell), and confirmed `web.archive.org` is blocked |
| "All review findings fixed, 154 tests pass" | Orchestrator ran the suite itself and re-read the Clearinghouse/UCR gates in the JSON |
| "39 rules, 3 probable, 5 gated, 31 ship" | Extracted programmatically from the JSON (see `02-PROVENANCE-MAP.md` §1) |

---

## 6. Re-running an adversarial probe yourself

The engine is pure and takes an injected evaluation date, so a probe is a plain
unit test — no database, no clock, no network:

```csharp
var rules   = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true));
var profile = EntityProfile.Builder()
    .State("US-TX").EntityType("transportation")
    .OperatesVehiclesForHire(true)
    // .OperatesInterstate(...) deliberately UNSET
    .MaxPassengerSeatingCapacity(20)
    .Build();

var report = RegulatoryObligationEvaluator.Evaluate(
    profile, documents: [], evaluationDate: new DateOnly(2026, 8, 1), rules);

// Expected: the insurance obligation is NeedsProfileInfo — never Satisfied,
// and never silently dropped. report.Completeness is always populated.
```

Useful invariants to attack: try to produce a `Satisfied` from an Unknown fact; try
to make `report.Obligations` empty while `Coverage == Covered`; try to get a
federal obligation suppressed by a state rule that isn't Kleene-True.
