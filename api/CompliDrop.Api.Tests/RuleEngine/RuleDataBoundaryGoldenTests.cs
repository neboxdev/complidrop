using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Pass-5 boundary goldens over the REAL rule data (UNVER-24): every numerically-gated rule is asserted at
/// its exact statutory threshold edge, the previously-unasserted rules (MCS-150, DOT medical) get status
/// assertions, the security gate corrections (CONF-18/19) and the new WC coverage-notice rule (SPLIT-3) get
/// behavioral goldens, and EVERY numeric applicability leaf in the shipped data is pinned to its statutory
/// value so a one-off threshold edit can never pass the suite.
/// </summary>
public class RuleDataBoundaryGoldenTests
{
    private static readonly DateOnly Eval = new(2026, 8, 1);
    private static readonly RuleSet ProdRules = EmbeddedRuleData.LoadAll();
    private static readonly RuleSet GatedInclusiveRules =
        EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: true, IncludeReviewGated: true));

    private static ObligationResult? Find(ObligationReport report, string obligationRef) =>
        report.Obligations.FirstOrDefault(o => o.ObligationRef == obligationRef);

    private static EntityProfile Shuttle(bool interstate, int seats) =>
        EntityProfile.Builder().State("US-TX").EntityType("transportation")
            .OperatesVehiclesForHire(true)
            .OperatesInterstate(interstate)
            .OperatesIntrastate(!interstate)
            .MaxPassengerSeatingCapacity(seats)
            .Build();

    // ---------------- capacity threshold edges (UNVER-24) ----------------

    [Theory]
    [InlineData(15, ObligationStatus.NotApplicable)]
    [InlineData(16, ObligationStatus.Missing)]
    public void The_clearinghouse_query_turns_on_exactly_at_sixteen_seats(int seats, ObligationStatus expected)
    {
        var report = RegulatoryObligationEvaluator.Evaluate(Shuttle(interstate: false, seats: seats), [], Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-006")!.Status.Should().Be(expected,
            "49 CFR 382 follows CDL-driver employment: 16+ seats including the driver");
    }

    [Theory]
    [InlineData(15, ObligationStatus.NotApplicable)]
    [InlineData(16, ObligationStatus.Missing)]
    public void The_dot_medical_certificate_turns_on_exactly_at_sixteen_seats(int seats, ObligationStatus expected)
    {
        // Also the first status assertion for OBL-FED-TRANSPORTATION-005 anywhere (UNVER-24d).
        var report = RegulatoryObligationEvaluator.Evaluate(Shuttle(interstate: true, seats: seats), [], Eval, ProdRules);

        Find(report, "OBL-FED-TRANSPORTATION-005")!.Status.Should().Be(expected);
    }

    [Theory]
    [InlineData(15, ObligationStatus.NotApplicable)]
    [InlineData(16, ObligationStatus.Missing)]
    public void The_txdmv_registration_turns_on_exactly_at_sixteen_seats(int seats, ObligationStatus expected)
    {
        var report = RegulatoryObligationEvaluator.Evaluate(Shuttle(interstate: false, seats: seats), [], Eval, ProdRules);

        Find(report, "OBL-TX-TRANSPORTATION-001")!.Status.Should().Be(expected,
            "Transp. Code 548.001: designed to transport more than 15 passengers, including the driver");
    }

    [Fact]
    public void The_mcs150_biennial_update_applies_to_an_interstate_carrier_of_any_size()
    {
        // The first status assertion for OBL-FED-TRANSPORTATION-002 anywhere (UNVER-24d).
        var interstate = RegulatoryObligationEvaluator.Evaluate(Shuttle(interstate: true, seats: 8), [], Eval, ProdRules);
        Find(interstate, "OBL-FED-TRANSPORTATION-002")!.Status.Should().Be(ObligationStatus.Missing);

        var intrastate = RegulatoryObligationEvaluator.Evaluate(Shuttle(interstate: false, seats: 8), [], Eval, ProdRules);
        Find(intrastate, "OBL-FED-TRANSPORTATION-002")!.Status.Should().Be(ObligationStatus.NotApplicable);
    }

    [Theory]
    [InlineData(0, ObligationStatus.NotApplicable)]
    [InlineData(1, ObligationStatus.Missing)]
    public void The_dwc005_notice_turns_on_exactly_at_one_employee(int employees, ObligationStatus expected)
    {
        var venue = EntityProfile.Builder().State("US-TX").EntityType("venue-org")
            .CarriesWorkersComp(false)
            .EmployeeCount(employees)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(venue, [], Eval, ProdRules);

        Find(report, "OBL-TX-VENUE-002")!.Status.Should().Be(expected);
    }

    // ---------------- the WC coverage-notice rule (SPLIT-3) ----------------

    [Fact]
    public void Every_employer_owes_the_coverage_notice_regardless_of_workers_comp_election()
    {
        // Tex. Labor Code 406.005 reaches EVERY employer with employees — subscriber or not. This is what
        // the pre-fix fold under carriesWorkersComp=false silently lost for subscribers.
        var subscriber = EntityProfile.Builder().State("US-TX").EntityType("venue-org")
            .CarriesWorkersComp(true).EmployeeCount(5).Build();
        var subscriberReport = RegulatoryObligationEvaluator.Evaluate(subscriber, [], Eval, ProdRules);

        Find(subscriberReport, "OBL-TX-VENUE-003")!.Status.Should().Be(ObligationStatus.Missing,
            "a SUBSCRIBER venue still owes the posted coverage notice");
        Find(subscriberReport, "OBL-TX-VENUE-002")!.Status.Should().Be(ObligationStatus.NotApplicable,
            "the DWC-005 form itself is non-subscriber-only (406.004)");

        // The posted notice on record satisfies it (a standing maintain-and-post duty, one-time evidence).
        var withNotice = RegulatoryObligationEvaluator.Evaluate(subscriber,
            [new DocumentLike("notice", "other", "tx-wc-coverage-notice")], Eval, ProdRules);
        Find(withNotice, "OBL-TX-VENUE-003")!.Status.Should().Be(ObligationStatus.Satisfied);

        // No employees ⇒ no notice duty.
        var noEmployees = EntityProfile.Builder().State("US-TX").EntityType("venue-org")
            .CarriesWorkersComp(true).EmployeeCount(0).Build();
        Find(RegulatoryObligationEvaluator.Evaluate(noEmployees, [], Eval, ProdRules), "OBL-TX-VENUE-003")!
            .Status.Should().Be(ObligationStatus.NotApplicable);
    }

    // ---------------- security gate corrections (CONF-18/19), gated-inclusive ----------------

    [Fact]
    public void A_close_protection_only_company_owes_the_commission_and_the_ppo_together()
    {
        // CONF-18: a PPO is an armed credential issued UNDER a security officer commission (1702.301(c)),
        // so a bodyguard-only firm owes BOTH — the commission must not read NotApplicable.
        var bodyguards = EntityProfile.Builder().State("US-TX").EntityType("security-service")
            .ProvidesArmedGuards(false)
            .ProvidesArmedCloseProtection(true)
            .ProvidesUnarmedGuards(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(bodyguards, [], Eval, GatedInclusiveRules);

        Find(report, "OBL-TX-SECURITY-005")!.Status.Should().Be(ObligationStatus.Missing); // commission
        Find(report, "OBL-TX-SECURITY-006")!.Status.Should().Be(ObligationStatus.Missing); // PPO
        Find(report, "OBL-TX-SECURITY-004")!.Status.Should().Be(ObligationStatus.NotApplicable,
            "no unarmed guards ⇒ nobody needs the noncommissioned license (CONF-19)");
    }

    [Fact]
    public void An_armed_only_company_is_not_asked_for_unarmed_officer_licenses()
    {
        // CONF-19: the noncommissioned license reaches individuals working as UNARMED officers;
        // commissioned officers hold the commission instead.
        var armedOnly = EntityProfile.Builder().State("US-TX").EntityType("security-service")
            .ProvidesArmedGuards(true)
            .ProvidesArmedCloseProtection(false)
            .ProvidesUnarmedGuards(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(armedOnly, [], Eval, GatedInclusiveRules);

        Find(report, "OBL-TX-SECURITY-004")!.Status.Should().Be(ObligationStatus.NotApplicable);
        Find(report, "OBL-TX-SECURITY-005")!.Status.Should().Be(ObligationStatus.Missing);

        var unarmedToo = EntityProfile.Builder().State("US-TX").EntityType("security-service")
            .ProvidesArmedGuards(true)
            .ProvidesArmedCloseProtection(false)
            .ProvidesUnarmedGuards(true)
            .Build();
        Find(RegulatoryObligationEvaluator.Evaluate(unarmedToo, [], Eval, GatedInclusiveRules), "OBL-TX-SECURITY-004")!
            .Status.Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void The_security_gl_floor_grades_the_extracted_amount_against_the_bi_pd_component_only()
    {
        // v1.2 amount gate on the REAL 1702.124(c) split-limits floor: below $100k is a definite
        // shortfall; at-or-above never fully certifies (the $50k/$200k components are unreadable).
        var company = EntityProfile.Builder().State("US-TX").EntityType("security-service")
            .ProvidesArmedGuards(false).ProvidesArmedCloseProtection(false).ProvidesUnarmedGuards(true)
            .Build();

        DocumentLike Gl(decimal? amount) => new("coi", "coi", "tx-security-gl",
            ExpirationDate: new DateOnly(2030, 1, 1), GeneralLiabilityLimit: amount);

        Find(RegulatoryObligationEvaluator.Evaluate(company, [Gl(60_000m)], Eval, GatedInclusiveRules), "OBL-TX-SECURITY-003")!
            .Status.Should().Be(ObligationStatus.BelowStatedMinimum);

        Find(RegulatoryObligationEvaluator.Evaluate(company, [Gl(150_000m)], Eval, GatedInclusiveRules), "OBL-TX-SECURITY-003")!
            .Status.Should().Be(ObligationStatus.NeedsDocumentInfo,
                "the personal-injury sub-limit and aggregate cannot be verified from one extracted figure");

        Find(RegulatoryObligationEvaluator.Evaluate(company, [Gl(null)], Eval, GatedInclusiveRules), "OBL-TX-SECURITY-003")!
            .Status.Should().Be(ObligationStatus.NeedsDocumentInfo);
    }

    // ---------------- every numeric applicability leaf, pinned (UNVER-24 alternative) ----------------

    [Fact]
    public void Every_numeric_applicability_leaf_in_the_shipped_data_is_pinned_to_its_statutory_value()
    {
        // A data edit shifting ANY numeric gate (a capacity tier, an employee-count threshold) fails here,
        // independent of behavioral coverage. Signature: "ruleId fact op value", one per numeric leaf.
        var all = EmbeddedRuleData.LoadAll(new RuleLoadOptions(VerifiedOnly: false, IncludeReviewGated: true));

        var numericLeaves = all.Rules
            .SelectMany(r => r.Versions.Select(v => (r.Id, v.Applicability)))
            .SelectMany(x => CollectLeaves(x.Applicability).Select(l => (x.Id, Leaf: l)))
            .Where(x => x.Leaf.Op is ConditionOp.Gte or ConditionOp.Lte)
            .Select(x => $"{x.Id} {x.Leaf.Fact} {RuleTokens.ToToken(x.Leaf.Op)} {x.Leaf.Value.GetInt64()}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        numericLeaves.Should().BeEquivalentTo(new[]
        {
            "fed-transportation-cdl-passenger-endorsement maxPassengerSeatingCapacity gte 16",
            "fed-transportation-clearinghouse-annual-query maxPassengerSeatingCapacity gte 16",
            "fed-transportation-financial-responsibility-15orless maxPassengerSeatingCapacity lte 15",
            "fed-transportation-financial-responsibility-16plus maxPassengerSeatingCapacity gte 16",
            "fed-transportation-medical-examiner-certificate maxPassengerSeatingCapacity gte 16",
            "fed-venue-employer-identification-number employeeCount gte 1",
            "fed-venue-osha-injury-log employeeCount gte 11",
            "tx-caterer-food-handler-training employeeCount gte 1",
            "tx-transportation-cdl-passenger-endorsement maxPassengerSeatingCapacity gte 16",
            "tx-transportation-intrastate-insurance-16to26 maxPassengerSeatingCapacity gte 16",
            "tx-transportation-intrastate-insurance-16to26 maxPassengerSeatingCapacity lte 26",
            "tx-transportation-intrastate-insurance-27plus maxPassengerSeatingCapacity gte 27",
            "tx-transportation-intrastate-medical-certificate maxPassengerSeatingCapacity gte 16",
            "tx-transportation-txdmv-motor-carrier-registration maxPassengerSeatingCapacity gte 16",
            "tx-venue-dwc005-nonsubscriber-notice employeeCount gte 1",
            "tx-venue-wc-coverage-notice employeeCount gte 1",
        });
    }

    private static IEnumerable<LeafCondition> CollectLeaves(Applicability node) => node switch
    {
        LeafCondition leaf => [leaf],
        AllCondition all => all.Conditions.SelectMany(CollectLeaves),
        AnyCondition any => any.Conditions.SelectMany(CollectLeaves),
        NotCondition not => CollectLeaves(not.Inner),
        _ => [],
    };
}
