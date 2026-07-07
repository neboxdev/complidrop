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

    // Production posture: verified-only, merged across all files so cross-file satisfiesFederal resolves.
    private static readonly RuleSet ProdRules = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true));

    private static ObligationResult? Find(ObligationReport report, string obligationRef) =>
        report.Obligations.FirstOrDefault(o => o.ObligationRef == obligationRef);

    private static InsuranceMinimums MinimumsOf(string ruleId) =>
        ProdRules.Rules.Single(r => r.Id == ruleId).Versions[0].InsuranceMinimums!;

    // ---------------- (a) TX caterer serving alcohol + preparing food ----------------

    [Fact]
    public void Tx_caterer_serving_alcohol_and_food_gets_food_tabc_and_ttb_obligations()
    {
        var profile = EntityProfile.Builder()
            .State("US-TX").EntityType("caterer")
            .PreparesOrServesFood(true)
            .ServesOrSellsAlcohol(true)
            .OperatesFoodVendingVehicle(false) // a normal caterer, not a food truck
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.Covered);

        // Food obligations (Texas).
        Find(report, "OBL-TX-CATERER-001")!.Status.Should().Be(ObligationStatus.Missing); // food establishment permit
        Find(report, "OBL-TX-CATERER-002")!.Status.Should().Be(ObligationStatus.Missing); // certified food manager
        Find(report, "OBL-TX-CATERER-003")!.Status.Should().Be(ObligationStatus.Missing); // food handler training

        // Alcohol obligations: the Texas TABC permit AND the federal TTB registration.
        Find(report, "OBL-TX-CATERER-004")!.Status.Should().Be(ObligationStatus.Missing); // TABC Mixed Beverage (TX)
        Find(report, "OBL-FED-CATERER-002")!.Status.Should().Be(ObligationStatus.Missing); // TTB dealer registration (FED)

        // The food-vending-vehicle license does not apply to a caterer who does not run a food truck.
        Find(report, "OBL-TX-CATERER-007")!.Status.Should().Be(ObligationStatus.NotApplicable);

        // Every relevant fact was supplied, so nothing is left unresolved.
        report.Obligations.Should().NotContain(o => o.Status == ObligationStatus.NeedsProfileInfo);
        report.OutstandingProfileFacts.Should().BeEmpty();

        // The report always carries the non-exhaustiveness notice (legal-req #2).
        report.Completeness.Text.Should().Contain("not a complete list");
    }

    // ---------------- (b) interstate vs intrastate 20-seat shuttle (legal-req #1) ----------------

    private static EntityProfileBuilder Shuttle20() =>
        EntityProfile.Builder().State("US-TX").EntityType("transportation")
            .OperatesVehiclesForHire(true)
            .MaxPassengerSeatingCapacity(20);

    private static IEnumerable<ObligationResult> ApplicableTransportInsurance(ObligationReport report) =>
        report.Obligations.Where(o =>
            (o.ObligationRef == "OBL-FED-TRANSPORTATION-003" || o.ObligationRef == "OBL-TX-TRANSPORTATION-002")
            && o.Status != ObligationStatus.NotApplicable);

    [Fact]
    public void The_same_20_seat_shuttle_faces_different_insurance_floors_interstate_vs_intrastate()
    {
        var interstate = RegulatoryObligationEvaluator.Evaluate(Shuttle20().OperatesInterstate(true).Build(), [], Eval, ProdRules);
        var intrastate = RegulatoryObligationEvaluator.Evaluate(Shuttle20().OperatesInterstate(false).Build(), [], Eval, ProdRules);

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
    public void Fmcsa_operating_authority_appears_only_for_the_interstate_shuttle()
    {
        var interstate = RegulatoryObligationEvaluator.Evaluate(Shuttle20().OperatesInterstate(true).Build(), [], Eval, ProdRules);
        var intrastate = RegulatoryObligationEvaluator.Evaluate(Shuttle20().OperatesInterstate(false).Build(), [], Eval, ProdRules);

        Find(interstate, "OBL-FED-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.Missing);
        Find(intrastate, "OBL-FED-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.NotApplicable);

        // TxDMV intrastate registration is the mirror image.
        Find(intrastate, "OBL-TX-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.Missing);
        Find(interstate, "OBL-TX-TRANSPORTATION-001")!.Status.Should().Be(ObligationStatus.NotApplicable);
    }

    [Fact]
    public void The_texas_cdl_suppresses_the_federal_cdl_so_it_is_not_double_emitted()
    {
        var report = RegulatoryObligationEvaluator.Evaluate(Shuttle20().OperatesInterstate(true).Build(), [], Eval, ProdRules);

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
        Find(report, "OBL-TX-EVENT-003")!.Status.Should().Be(ObligationStatus.Missing);   // AR-800 injury report
    }

    [Fact]
    public void Event_rental_not_renting_inflatables_has_no_amusement_obligation()
    {
        var profile = EntityProfile.Builder().State("US-TX").EntityType("event-rental")
            .RentsInflatableAmusementDevices(false)
            .OperatesForklifts(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        Find(report, "OBL-TX-EVENT-001")!.Status.Should().Be(ObligationStatus.NotApplicable);
        Find(report, "OBL-TX-EVENT-002")!.Status.Should().Be(ObligationStatus.NotApplicable);
        Find(report, "OBL-TX-EVENT-003")!.Status.Should().Be(ObligationStatus.NotApplicable);
        report.Obligations.Should().OnlyContain(o => o.Status == ObligationStatus.NotApplicable,
            "nothing is actively required for an event-rental with no inflatables and no forklifts");
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

    // ---------------- coverage guard ----------------

    [Fact]
    public void A_non_texas_state_is_reported_not_covered_never_empty_compliant()
    {
        var profile = EntityProfile.Builder().State("US-CA").EntityType("caterer").PreparesOrServesFood(true).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, ProdRules);

        report.Coverage.Should().Be(JurisdictionCoverage.NotCovered);
        report.CoverageMessage.Should().Contain("Texas");
        report.Completeness.Text.Should().NotBeNullOrWhiteSpace();
    }
}
