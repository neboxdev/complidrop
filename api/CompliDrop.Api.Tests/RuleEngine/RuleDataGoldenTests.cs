using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// GOLDEN-FILE tests over the REAL encoded rule data (loaded via <see cref="EmbeddedRuleData"/> in the
/// production, verified-only posture). Realistic entity fixtures are evaluated end-to-end through
/// <see cref="RegulatoryObligationEvaluator"/> and the emitted obligation SET + statuses are pinned. These
/// assert the encoded CONTENT behaves correctly (the interstate/intrastate insurance branch, the inflatable
/// amusement-ride trigger, satisfiesFederal suppression, and the "unknown ⇒ needs-profile-info, never
/// satisfied" safety rule) — complementing the synthetic-fixture mechanics tests.
/// </summary>
public class RuleDataGoldenTests
{
    private static readonly DateOnly Eval = new(2026, 8, 1); // after every rule's validFrom (incl. HB 2844, 2026-07-01)

    // Production posture: verified-only + review-gated rule-sets excluded (the DEFAULT), merged across all
    // files so cross-file satisfiesFederal resolves. The TX security set is HELD BACK here (A-5/CC-8).
    private static readonly RuleSet ProdRules = EmbeddedRuleData.LoadAll();

    // Verified + the review-gated TX security set included, for exercising the security behavioral goldens
    // (the security set does not ship in the default production load until its founder gate clears).
    private static readonly RuleSet GatedInclusiveRules =
        EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));

    private static ObligationResult? Find(ObligationReport report, string obligationRef) =>
        report.Obligations.FirstOrDefault(o => o.ObligationRef == obligationRef);

    // A pure-direction shuttle: interstate XOR intrastate. The TX rules gate on operatesIntrastate=true
    // (v1.2, CONF-23 — §643.002 exempts only EXCLUSIVELY-interstate carriers), so both facts are set.
    private static EntityProfile ShuttleProfile(bool interstate, int seats) =>
        EntityProfile.Builder().State("US-TX").EntityType("transportation")
            .OperatesVehiclesForHire(true)
            .OperatesInterstate(interstate)
            .OperatesIntrastate(!interstate)
            .MaxPassengerSeatingCapacity(seats)
            .Build();

    private static InsuranceMinimums MinimumsOf(string ruleId) =>
        ProdRules.Rules.Single(r => r.Id == ruleId).Versions[0].InsuranceMinimums!;

    // ---------------- (a) TX caterer serving alcohol + preparing food ----------------

    [Fact]
    public void Tx_caterer_serving_alcohol_and_food_gets_food_tabc_and_ttb_obligations()
    {
        var profile = EntityProfile.Builder()
            .State("US-TX").EntityType("caterer")
            .PreparesOrServesFood(true)
            .EmployeeCount(5) // food-handler training gates on having food EMPLOYEES (CONF-12)
            .ServesOrSellsAlcohol(true)
            .SellsTaxableGoodsOrServices(true) // the cross-cutting sales-tax permit reaches caterers (CONF-17)
            .OperatesFoodVendingVehicle(false) // a normal caterer, not a food truck
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.Covered);

        // Food obligations (Texas).
        Find(report, "OBL-TX-CATERER-001")!.Status.Should().Be(ObligationStatus.Missing); // food establishment permit
        Find(report, "OBL-TX-CATERER-002")!.Status.Should().Be(ObligationStatus.Missing); // certified food manager
        Find(report, "OBL-TX-CATERER-003")!.Status.Should().Be(ObligationStatus.Missing); // food handler training

        // Alcohol obligations: the Texas TABC retail permit AND the federal TTB registration.
        Find(report, "OBL-TX-CATERER-004")!.Status.Should().Be(ObligationStatus.Missing); // TABC retail alcohol permit (TX)
        Find(report, "OBL-FED-CATERER-002")!.Status.Should().Be(ObligationStatus.Missing); // TTB dealer registration (FED)

        // The cross-cutting Texas sales & use tax permit reaches the caterer entity type (CONF-17).
        Find(report, "OBL-TX-VENUE-004")!.Status.Should().Be(ObligationStatus.Missing);

        // The food-vending-vehicle license does not apply to a caterer who does not run a food truck.
        Find(report, "OBL-TX-CATERER-007")!.Status.Should().Be(ObligationStatus.NotApplicable);

        // Every relevant fact was supplied, so nothing is left unresolved.
        report.Obligations.Should().NotContain(o => o.Status == ObligationStatus.NeedsProfileInfo);
        report.OutstandingProfileFacts.Should().BeEmpty();

        // The report always carries the non-exhaustiveness notice (legal-req #2).
        report.Completeness.Text.Should().Contain("not a complete list");
    }

    [Fact]
    public void A_beer_wine_only_caterer_is_asked_for_the_neutral_tabc_retail_permit_not_specifically_mb()
    {
        // CONF-8: the required TABC permit TYPE depends on what is served (MB for spirits, BG for
        // beer/wine only) — the obligation must be the neutral retail permit, never MB-specifically.
        var rule = ProdRules.Rules.Single(r => r.Id == "tx-caterer-tabc-mixed-beverage");
        var obligation = rule.Versions[0].Obligation;

        obligation.Name.Should().Contain("Mixed Beverage or Wine & Malt Beverage");
        obligation.DocumentSubType.Should().Be("tabc-retail-alcohol");
        rule.Versions[0].Rationale.Should().Contain("beer/wine");
    }

    [Fact]
    public void A_food_preparing_venue_gets_the_cfm_and_food_handler_obligations()
    {
        // UNVER-0: the TFER worker credentials reach every food establishment, including a
        // food-preparing VENUE — not only caterers.
        var venue = EntityProfile.Builder()
            .State("US-TX").EntityType("venue-org")
            .PreparesOrServesFood(true)
            .EmployeeCount(12)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(venue, [], Eval, ProdRules);

        Find(report, "OBL-TX-CATERER-002")!.Status.Should().Be(ObligationStatus.Missing); // certified food manager
        Find(report, "OBL-TX-CATERER-003")!.Status.Should().Be(ObligationStatus.Missing); // food handler training
    }

    // ---------------- (b) interstate vs intrastate 20-seat shuttle (legal-req #1) ----------------

    private static EntityProfileBuilder Shuttle20() =>
        EntityProfile.Builder().State("US-TX").EntityType("transportation")
            .OperatesVehiclesForHire(true)
            .MaxPassengerSeatingCapacity(20);

    /// <summary>Pure interstate / pure intrastate 20-seat shuttle (both direction facts set).</summary>
    private static EntityProfile Shuttle20Pure(bool interstate) =>
        Shuttle20().OperatesInterstate(interstate).OperatesIntrastate(!interstate).Build();

    private static IEnumerable<ObligationResult> ApplicableTransportInsurance(ObligationReport report) =>
        report.Obligations.Where(o =>
            (o.ObligationRef == "OBL-FED-TRANSPORTATION-003" || o.ObligationRef == "OBL-TX-TRANSPORTATION-002")
            && o.Status != ObligationStatus.NotApplicable);

    [Fact]
    public void The_same_20_seat_shuttle_faces_different_insurance_floors_interstate_vs_intrastate()
    {
        var interstate = RegulatoryObligationEvaluator.Evaluate(Shuttle20Pure(interstate: true), [], Eval, ProdRules);
        var intrastate = RegulatoryObligationEvaluator.Evaluate(Shuttle20Pure(interstate: false), [], Eval, ProdRules);

        // Interstate: the only APPLICABLE passenger-liability floor is the $5,000,000 federal one.
        ApplicableTransportInsurance(interstate).Should().ContainSingle()
            .Which.Name.Should().Contain("$5,000,000");

        // Intrastate: the only applicable floor is the $500,000 Texas one — a DIFFERENT number on the same van.
        ApplicableTransportInsurance(intrastate).Should().ContainSingle()
            .Which.Name.Should().Contain("$500,000");

        // The encoded minimums back the names.
        MinimumsOf("fed-transportation-financial-responsibility-16plus").PerOccurrence.Should().Be(5_000_000m);
        MinimumsOf("tx-transportation-intrastate-insurance-16to26").PerOccurrence.Should().Be(500_000m);
    }

    [Fact]
    public void A_mixed_interstate_and_intrastate_carrier_owes_both_layers()
    {
        // CONF-23: §643.002 exempts only carriers operating EXCLUSIVELY in interstate commerce, so a
        // carrier doing BOTH owes the federal layer AND the TxDMV registration + TX insurance filing.
        var mixed = Shuttle20().OperatesInterstate(true).OperatesIntrastate(true).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(mixed, [], Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.Missing); // FMCSA authority
        Find(report, "OBL-TX-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.Missing);  // TxDMV registration
        ApplicableTransportInsurance(report).Should().HaveCount(2, "both the federal and the Texas insurance filings apply to a mixed carrier");
    }

    [Fact]
    public void Fmcsa_operating_authority_appears_only_for_the_interstate_shuttle()
    {
        var interstate = RegulatoryObligationEvaluator.Evaluate(Shuttle20Pure(interstate: true), [], Eval, ProdRules);
        var intrastate = RegulatoryObligationEvaluator.Evaluate(Shuttle20Pure(interstate: false), [], Eval, ProdRules);

        Find(interstate, "OBL-FED-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.Missing);
        Find(intrastate, "OBL-FED-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.NotApplicable);

        // TxDMV intrastate registration is the mirror image (for PURE-direction carriers).
        Find(intrastate, "OBL-TX-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.Missing);
        Find(interstate, "OBL-TX-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.NotApplicable);
    }

    [Fact]
    public void The_texas_cdl_suppresses_the_federal_cdl_so_it_is_not_double_emitted()
    {
        var report = RegulatoryObligationEvaluator.Evaluate(Shuttle20Pure(interstate: true), [], Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-004").Should().BeNull("the applicable Texas CDL implements the federal CDL floor");
        Find(report, "OBL-TX-TRANSPORTATION-003")!.Status.Should().Be(ObligationStatus.Missing);
    }

    // ---------------- (c) event-rental: inflatables vs not ----------------

    [Fact]
    public void Event_rental_renting_inflatables_gets_the_2151_1012_one_million_obligation()
    {
        var profile = EntityProfile.Builder().State("US-TX").EntityType("event-rental")
            .RentsInflatableAmusementDevices(true)
            .OperatesForklifts(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        Find(report, "OBL-TX-EVENT-001")!.Status.Should().Be(ObligationStatus.Missing);   // §2151.1012 insurance
        MinimumsOf("tx-event-rental-amusement-ride-insurance").PerOccurrence.Should().Be(1_000_000m);
        Find(report, "OBL-TX-EVENT-002")!.Status.Should().Be(ObligationStatus.Missing);   // inspection + AR-101 sticker

        // AR-800 is a conditional filing owed only on a reportable injury the profile can't assert, so with
        // no document on record it is NotApplicable (CC-1), NOT Missing — and its copy says "file only if…".
        var ar800 = Find(report, "OBL-TX-EVENT-003")!;
        ar800.Status.Should().Be(ObligationStatus.NotApplicable);
        ar800.UserAction.Should().Contain("injury");
    }

    [Fact]
    public void Event_rental_not_renting_inflatables_has_no_amusement_obligation()
    {
        var profile = EntityProfile.Builder().State("US-TX").EntityType("event-rental")
            .RentsInflatableAmusementDevices(false)
            .OperatesForklifts(false)
            .SellsTaxableGoodsOrServices(false) // the cross-cutting sales-tax permit reaches event-rental (CONF-17)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        Find(report, "OBL-TX-EVENT-001")!.Status.Should().Be(ObligationStatus.NotApplicable);
        Find(report, "OBL-TX-EVENT-002")!.Status.Should().Be(ObligationStatus.NotApplicable);
        Find(report, "OBL-TX-EVENT-003")!.Status.Should().Be(ObligationStatus.NotApplicable);
        report.Obligations.Should().OnlyContain(o => o.Status == ObligationStatus.NotApplicable,
            "nothing is actively required for an event-rental with no inflatables, no forklifts and no taxable sales");
    }

    [Fact]
    public void An_event_rental_selling_taxable_rentals_owes_the_sales_tax_permit()
    {
        // CONF-17: renting tables/chairs/tents/inflatables is a taxable activity — the cross-cutting
        // sales & use tax permit (canonical entry on venue-org) must reach event-rental entities.
        var profile = EntityProfile.Builder().State("US-TX").EntityType("event-rental")
            .RentsInflatableAmusementDevices(false)
            .OperatesForklifts(false)
            .SellsTaxableGoodsOrServices(true)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        Find(report, "OBL-TX-VENUE-004")!.Status.Should().Be(ObligationStatus.Missing);
    }

    // ---------------- (d) unset operatesInterstate ⇒ needs-profile-info, never satisfied ----------------

    [Fact]
    public void Unset_interstate_on_a_shuttle_makes_the_insurance_needs_profile_info_never_satisfied()
    {
        // operatesInterstate is UNSET. Even a present, unexpired COI must not launder the unresolved
        // interstate branch into a "Satisfied" (legal-req #1).
        var profile = EntityProfile.Builder().State("US-TX").EntityType("transportation")
            .OperatesVehiclesForHire(true)
            .MaxPassengerSeatingCapacity(20)
            .Build();
        var docs = new[] { new DocumentLike("coi-1", "coi", "fed-passenger-bipd", ExpirationDate: new DateOnly(2030, 1, 1)) };

        var report = RegulatoryObligationEvaluator.Evaluate(profile, docs, Eval, ProdRules);

        var fed5m = report.Obligations.Single(o => o.ObligationRef == "OBL-FED-TRANSPORTATION-003" && o.Name.Contains("$5,000,000"));
        fed5m.Status.Should().Be(ObligationStatus.NeedsProfileInfo);
        fed5m.MissingFacts.Should().Contain(FactNames.OperatesInterstate);

        var tx500 = report.Obligations.Single(o => o.ObligationRef == "OBL-TX-TRANSPORTATION-002" && o.Name.Contains("$500,000"));
        tx500.Status.Should().Be(ObligationStatus.NeedsProfileInfo);

        // No transportation insurance obligation may read Satisfied while the branch is unresolved.
        report.Obligations
            .Where(o => o.ObligationRef is "OBL-FED-TRANSPORTATION-003" or "OBL-TX-TRANSPORTATION-002")
            .Should().NotContain(o => o.Status == ObligationStatus.Satisfied);

        report.OutstandingProfileFacts.Should().Contain(FactNames.OperatesInterstate);
    }

    // ---------------- (e) transport insurance floor across every capacity/interstate edge (T-2) ----------------

    [Theory]
    [InlineData(15, true, "$1,500,000")]   // interstate ≤15 ⇒ federal $1.5M floor (fed $1.5M TRIGGERED)
    [InlineData(16, true, "$5,000,000")]   // interstate 16+ ⇒ federal $5M floor
    [InlineData(26, true, "$5,000,000")]
    [InlineData(27, true, "$5,000,000")]
    [InlineData(15, false, null)]          // intrastate ≤15 ⇒ not a CMV, no state insurance floor at all
    [InlineData(16, false, "$500,000")]    // intrastate 16–26 ⇒ Texas $500k floor
    [InlineData(26, false, "$500,000")]
    [InlineData(27, false, "$5,000,000")]  // intrastate 27+ ⇒ Texas $5M floor (TX $5M TRIGGERED)
    public void The_single_applicable_transport_insurance_floor_matches_capacity_and_interstate(
        int seats, bool interstate, string? expectedAmount)
    {
        // ShuttleProfile is pure-direction (interstate XOR intrastate), so exactly one floor may apply.
        var report = RegulatoryObligationEvaluator.Evaluate(ShuttleProfile(interstate, seats), [], Eval, ProdRules);

        var applicable = report.Obligations.Where(o =>
            (o.ObligationRef == "OBL-FED-TRANSPORTATION-003" || o.ObligationRef == "OBL-TX-TRANSPORTATION-002")
            && o.Status != ObligationStatus.NotApplicable).ToList();

        if (expectedAmount is null)
            applicable.Should().BeEmpty("a vehicle seating 15 or fewer, intrastate, is not a CMV with a state insurance floor");
        else
            applicable.Should().ContainSingle().Which.Name.Should().Contain(expectedAmount);
    }

    [Fact]
    public void The_texas_cdl_does_not_suppress_the_federal_cdl_when_capacity_is_unknown()
    {
        // T-1 on real data: with capacity UNSET the Texas CDL rule is Unknown, so it cannot suppress the
        // federal CDL floor — OBL-FED-TRANSPORTATION-004 must STILL be emitted (as NeedsProfileInfo).
        var profile = EntityProfile.Builder().State("US-TX").EntityType("transportation")
            .OperatesVehiclesForHire(true)
            .OperatesInterstate(true)
            .Build(); // maxPassengerSeatingCapacity UNSET

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        var fedCdl = Find(report, "OBL-FED-TRANSPORTATION-004");
        fedCdl.Should().NotBeNull("an Unknown Texas CDL rule must not suppress the federal CDL");
        fedCdl!.Status.Should().Be(ObligationStatus.NeedsProfileInfo);
    }

    [Fact]
    public void Unset_state_on_a_transportation_entity_yields_federal_only_and_state_outstanding()
    {
        // T-3 on real data: no state ⇒ Covered, only us-fed obligations, state outstanding — never TX by default.
        var profile = EntityProfile.Builder().EntityType("transportation")
            .OperatesVehiclesForHire(true)
            .OperatesInterstate(true)
            .MaxPassengerSeatingCapacity(20)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.Covered);
        report.Obligations.Should().NotBeEmpty();
        report.Obligations.Should().OnlyContain(o => o.ObligationRef.StartsWith("OBL-FED-"),
            "with an unknown state only federal obligations apply");
        report.OutstandingProfileFacts.Should().Contain(FactNames.State);
    }

    [Fact]
    public void Clearinghouse_attaches_to_an_intrastate_16plus_carrier_not_only_interstate()
    {
        // CC-2: the FMCSA Clearinghouse query follows CDL-driver employment (16+ seats) and attaches to
        // intrastate Texas carriers too — it is NOT gated on interstate.
        var report = RegulatoryObligationEvaluator.Evaluate(ShuttleProfile(interstate: false, seats: 40), [], Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-006")!.Status.Should().Be(ObligationStatus.Missing);
    }

    [Theory]
    [InlineData(8)]   // even a carrier with NO qualifying CMV registers (lowest fee bracket)
    [InlineData(11)]
    [InlineData(40)]
    public void Ucr_registration_applies_to_every_interstate_for_hire_carrier_regardless_of_capacity(int seats)
    {
        // CONF-2 (reverses CC-6): the >10-passenger figure is the UCR FEE CMV definition, not the
        // registration trigger — the dossier records that a small interstate shuttle with no qualifying
        // CMV still registers at the lowest fleet bracket.
        var report = RegulatoryObligationEvaluator.Evaluate(ShuttleProfile(interstate: true, seats: seats), [], Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-007")!.Status.Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void Ucr_registration_is_not_applicable_to_a_pure_intrastate_carrier()
    {
        var report = RegulatoryObligationEvaluator.Evaluate(ShuttleProfile(interstate: false, seats: 40), [], Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-007")!.Status.Should().Be(ObligationStatus.NotApplicable);
    }

    // ---------------- (f) drone photographer — Part 107 recency issueDate cadence (T-4/T-6) ----------------

    [Fact]
    public void Drone_photographer_part107_recency_tracks_the_24_month_clock_from_issue_date()
    {
        var photographer = EntityProfile.Builder().State("US-TX").EntityType("photographer-videographer")
            .OperatesDronesCommercially(true)
            .Build();

        // Recent recency training (issued 2025-06-01): "24 CALENDAR months" runs to the END of the 24th
        // month (14 CFR 107.65, CONF-0) ⇒ due 2027-06-30 ⇒ Satisfied with the month-end next-due date.
        var recent = new[] { new DocumentLike("rec-1", "certification", "faa-part107-recency", IssueDate: new DateOnly(2025, 6, 1)) };
        var recency = Find(RegulatoryObligationEvaluator.Evaluate(photographer, recent, Eval, ProdRules), "OBL-FED-PHOTOGRAPHER-002")!;
        recency.Status.Should().Be(ObligationStatus.Satisfied);
        recency.NextDueDate.Should().Be(new DateOnly(2027, 6, 30), "the 24-calendar-month recency window runs to the end of the 24th month after completion");

        // Stale recency (issued 2024-01-01) ⇒ current through 2026-01-31, already past at 2026-08-01 ⇒ Expired.
        var stale = new[] { new DocumentLike("rec-2", "certification", "faa-part107-recency", IssueDate: new DateOnly(2024, 1, 1)) };
        Find(RegulatoryObligationEvaluator.Evaluate(photographer, stale, Eval, ProdRules), "OBL-FED-PHOTOGRAPHER-002")!
            .Status.Should().Be(ObligationStatus.Expired);

        // A pilot completing recency mid-month stays legally current through month-end 24 months later:
        // issued 2024-08-10 ⇒ current through 2026-08-31, so at eval 2026-08-01 this is NOT Expired
        // (the day-precision anniversary 2026-08-10 alone would also not be expired yet — the month-end
        // distinction is pinned by the NextDueDate assertion above and the CadenceCalculator unit tests).
        var midMonth = new[] { new DocumentLike("rec-3", "certification", "faa-part107-recency", IssueDate: new DateOnly(2024, 8, 10)) };
        var midMonthResult = Find(RegulatoryObligationEvaluator.Evaluate(photographer, midMonth, Eval, ProdRules), "OBL-FED-PHOTOGRAPHER-002")!;
        midMonthResult.Status.Should().Be(ObligationStatus.Expiring, "due 2026-08-31 is within the 30-day window of 2026-08-01");
        midMonthResult.NextDueDate.Should().Be(new DateOnly(2026, 8, 31));

        // No issue date AND no expiry ⇒ can't determine currency ⇒ NeedsDocumentInfo (A-1), never Satisfied.
        var noDates = new[] { new DocumentLike("rec-3", "certification", "faa-part107-recency") };
        Find(RegulatoryObligationEvaluator.Evaluate(photographer, noDates, Eval, ProdRules), "OBL-FED-PHOTOGRAPHER-002")!
            .Status.Should().Be(ObligationStatus.NeedsDocumentInfo);
    }

    // ---------------- (g) null-expiry vs one-time (A-1) on real data ----------------

    [Fact]
    public void A_matched_renewal_insurance_with_no_readable_expiry_is_needs_document_info()
    {
        // A-1: the amusement-ride liability policy is a renewal anchored on documentExpiration. A matched COI
        // with no printed expiry can't be confirmed current ⇒ NeedsDocumentInfo, never a false Satisfied.
        var eventRental = EntityProfile.Builder().State("US-TX").EntityType("event-rental")
            .RentsInflatableAmusementDevices(true).OperatesForklifts(false)
            .Build();
        var docs = new[] { new DocumentLike("coi-noexp", "coi", "tx-amusement-ride-liability") }; // no expiry

        var report = RegulatoryObligationEvaluator.Evaluate(eventRental, docs, Eval, ProdRules);

        Find(report, "OBL-TX-EVENT-001")!.Status.Should().Be(ObligationStatus.NeedsDocumentInfo);
    }

    [Fact]
    public void A_one_time_permit_with_a_matched_document_and_no_expiry_is_satisfied()
    {
        // A held ONE-TIME credential (Texas sales & use tax permit — no expiration) reads Satisfied even with
        // no printed expiry: nothing to renew (contrast the renewal case above ⇒ NeedsDocumentInfo).
        var venue = EntityProfile.Builder().State("US-TX").EntityType("venue-org")
            .SellsTaxableGoodsOrServices(true)
            .Build();
        var docs = new[] { new DocumentLike("permit-1", "license", "tx-sales-use-tax") }; // no expiry

        var salesTax = Find(RegulatoryObligationEvaluator.Evaluate(venue, docs, Eval, ProdRules), "OBL-TX-VENUE-004")!;
        salesTax.Status.Should().Be(ObligationStatus.Satisfied);
        salesTax.MatchedDocumentId.Should().Be("permit-1");
    }

    // ---------------- (h) venue behavioral goldens — franchise fixedDate + DWC-005 gate (T-6/CC-5) ----------------

    [Fact]
    public void Venue_franchise_report_is_a_fixed_annual_may_15_filing()
    {
        var venue = EntityProfile.Builder().State("US-TX").EntityType("venue-org")
            .IsFranchiseTaxableEntity(true)
            .Build();

        var franchise = Find(RegulatoryObligationEvaluator.Evaluate(venue, [], Eval, ProdRules), "OBL-TX-VENUE-005")!;
        franchise.Status.Should().Be(ObligationStatus.Missing);
        franchise.NextDueDate.Should().Be(new DateOnly(2027, 5, 15), "May 15 has passed for 2026 by the Aug 1 evaluation date");
    }

    [Theory]
    [InlineData(false, 5, ObligationStatus.Missing)]        // non-subscriber WITH employees ⇒ owes DWC-005
    [InlineData(false, 0, ObligationStatus.NotApplicable)]  // non-subscriber with NO employees ⇒ not owed (CC-5)
    [InlineData(true, 5, ObligationStatus.NotApplicable)]   // carries WC ⇒ not a non-subscriber
    public void Venue_dwc005_requires_non_subscriber_and_at_least_one_employee(bool carriesWc, int employees, ObligationStatus expected)
    {
        var venue = EntityProfile.Builder().State("US-TX").EntityType("venue-org")
            .CarriesWorkersComp(carriesWc)
            .EmployeeCount(employees)
            .Build();

        Find(RegulatoryObligationEvaluator.Evaluate(venue, [], Eval, ProdRules), "OBL-TX-VENUE-002")!
            .Status.Should().Be(expected);
    }

    // ---------------- (i) security behavioral goldens — armed-guard vs close-protection gate (T-6/CC-3) ----------------

    [Fact]
    public void Security_ppo_fires_only_on_armed_close_protection_not_on_armed_guards_generally()
    {
        // CC-3: the PPO license gates on providesArmedCloseProtection, NOT the superset providesArmedGuards.
        var armedGuardsOnly = EntityProfile.Builder().State("US-TX").EntityType("security-service")
            .ProvidesArmedGuards(true)
            .ProvidesArmedCloseProtection(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(armedGuardsOnly, [], Eval, GatedInclusiveRules);

        Find(report, "OBL-TX-SECURITY-005")!.Status.Should().Be(ObligationStatus.Missing);        // armed officer commission applies…
        Find(report, "OBL-TX-SECURITY-006")!.Status.Should().Be(ObligationStatus.NotApplicable);  // …but PPO (close protection) does NOT

        var closeProtection = EntityProfile.Builder().State("US-TX").EntityType("security-service")
            .ProvidesArmedGuards(true)
            .ProvidesArmedCloseProtection(true)
            .Build();
        Find(RegulatoryObligationEvaluator.Evaluate(closeProtection, [], Eval, GatedInclusiveRules), "OBL-TX-SECURITY-006")!
            .Status.Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void Security_unarmed_company_owes_the_company_license_and_insurance_but_no_armed_credentials()
    {
        var unarmed = EntityProfile.Builder().State("US-TX").EntityType("security-service")
            .ProvidesArmedGuards(false)
            .ProvidesArmedCloseProtection(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(unarmed, [], Eval, GatedInclusiveRules);

        Find(report, "OBL-TX-SECURITY-001")!.Status.Should().Be(ObligationStatus.Missing);        // company license (all:[])
        Find(report, "OBL-TX-SECURITY-003")!.Status.Should().Be(ObligationStatus.Missing);        // GL insurance (all:[])
        Find(report, "OBL-TX-SECURITY-005")!.Status.Should().Be(ObligationStatus.NotApplicable);  // armed commission
        Find(report, "OBL-TX-SECURITY-006")!.Status.Should().Be(ObligationStatus.NotApplicable);  // PPO
    }

    // ---------------- (j) entity-type coverage + local obligations (A-3/CC-7) ----------------

    [Fact]
    public void An_unmodeled_entity_type_is_reported_not_covered_never_empty_compliant()
    {
        var florist = EntityProfile.Builder().State("US-TX").EntityType("florist").Build();

        var report = RegulatoryObligationEvaluator.Evaluate(florist, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.NotCovered);
        report.CoverageMessage.Should().Contain("florist");
        report.Obligations.Should().BeEmpty();
        report.Completeness.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_completeness_notice_surfaces_local_obligation_pointers_scoped_to_the_entity()
    {
        // CC-7: the venue's dossier "noted, not encoded" LOCAL obligations are unioned into the notice, and a
        // caterer does not inherit the venue's — the pointers are scoped to the rule-sets that applied.
        var venue = EntityProfile.Builder().State("US-TX").EntityType("venue-org")
            .SellsTaxableGoodsOrServices(true)
            .Build();
        var venueReport = RegulatoryObligationEvaluator.Evaluate(venue, [], Eval, ProdRules);
        venueReport.Completeness.LocalObligationPointers.Should().NotBeEmpty();
        venueReport.Completeness.LocalObligationPointers.Should().Contain(p => p.Contains("Certificate of occupancy"));

        var caterer = EntityProfile.Builder().State("US-TX").EntityType("caterer")
            .PreparesOrServesFood(true).ServesOrSellsAlcohol(false).OperatesFoodVendingVehicle(false)
            .Build();
        var catererReport = RegulatoryObligationEvaluator.Evaluate(caterer, [], Eval, ProdRules);
        catererReport.Completeness.LocalObligationPointers.Should().Contain(p => p.Contains("food establishment permit"));
        catererReport.Completeness.LocalObligationPointers.Should().NotContain(p => p.Contains("occupant-load"),
            "a caterer must not inherit the venue-org local obligations");
    }

    [Fact]
    public void A_matched_but_stale_document_reads_expired_via_rule_id_not_a_shadowing_sibling()
    {
        // A-4: OBL-FED-TRANSPORTATION-003 is shared by two rules (the $5M 16+ and the $1.5M ≤15 floors). For a
        // 20-seat interstate shuttle the ≤15 rule is NotApplicable; a stale COI must surface as Expired
        // (actionable, sorted first) and each result carries its own unique RuleId.
        var docs = new[] { new DocumentLike("coi-old", "coi", "fed-passenger-bipd", ExpirationDate: new DateOnly(2026, 1, 1)) };

        var report = RegulatoryObligationEvaluator.Evaluate(ShuttleProfile(interstate: true, seats: 20), docs, Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-003")!.Status.Should().Be(ObligationStatus.Expired,
            "the actionable Expired result sorts before its NotApplicable same-ref sibling");
        report.Obligations.Where(o => o.ObligationRef == "OBL-FED-TRANSPORTATION-003")
            .Select(o => o.RuleId).Should().OnlyHaveUniqueItems();
    }

    // ---------------- coverage guard ----------------

    [Fact]
    public void A_non_texas_state_is_reported_not_covered_never_empty_compliant()
    {
        var profile = EntityProfile.Builder().State("US-CA").EntityType("caterer").PreparesOrServesFood(true).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.NotCovered);
        report.CoverageMessage.Should().Contain("US-TX", "the message derives the covered set from the loaded rules (UNVER-7)");
        report.Completeness.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("US-TX")]
    [InlineData("us-tx")]
    [InlineData("tx")]
    [InlineData("TX")]
    [InlineData("Texas")]
    [InlineData("  texas  ")]
    public void Common_texas_state_spellings_all_resolve_to_covered(string state)
    {
        // A-7: "US-TX", "tx", "Texas" (case- and surrounding-space-insensitive) all normalize to us-tx and
        // are covered — not a NotCovered landmine.
        var profile = EntityProfile.Builder().State(state).EntityType("caterer").PreparesOrServesFood(true).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.Covered, $"\"{state}\" is a Texas spelling");
        Find(report, "OBL-TX-CATERER-001")!.Status.Should().Be(ObligationStatus.Missing);
    }

    [Theory]
    [InlineData("US-CA")]
    [InlineData("california")]
    [InlineData("FL")]
    public void A_non_texas_state_stays_not_covered(string state)
    {
        var profile = EntityProfile.Builder().State(state).EntityType("caterer").PreparesOrServesFood(true).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.NotCovered);
    }
}
