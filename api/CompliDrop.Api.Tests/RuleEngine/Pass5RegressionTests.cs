using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Pass-5 regression tests over SYNTHETIC rules built in code: the insurance amount gate (A-2/CC-4 closure,
/// v1.2), document selection by effective deadline (UNVER-4), conditional-filing with proof (UNVER-5),
/// fixed-annual undated-proof semantics (UNVER-13), federal-only-set state coverage (UNVER-7), the Neq
/// fail-closed contract (UNVER-8), builder aliasing (UNVER-10), and month-end cadence rounding (CONF-0).
/// </summary>
public class Pass5RegressionTests
{
    private static readonly DateOnly Eval = new(2026, 8, 1);

    private static Rule MakeRule(
        string id = "test-rule",
        string jurisdiction = "us-tx",
        string category = "license",
        Applicability? applicability = null,
        Obligation? obligation = null,
        Cadence? cadence = null,
        InsuranceMinimums? minimums = null) => new()
        {
            Id = id,
            ObligationRef = "OBL-" + id.ToUpperInvariant(),
            Jurisdiction = jurisdiction,
            EntityTypes = ["test-widget"],
            Category = category,
            Versions =
        [
            new RuleVersion
            {
                Version = 1,
                ValidFrom = new DateOnly(2020, 1, 1),
                Confidence = RuleConfidence.Verified,
                Applicability = applicability ?? Condition.All(),
                Obligation = obligation ?? new Obligation { Name = id, DocumentType = "license", DocumentSubType = id },
                Cadence = cadence,
                InsuranceMinimums = minimums,
                Citation = new Citation { Section = "Synthetic §1" },
                Rationale = "Synthetic.",
                UserAction = "Synthetic.",
            },
        ],
        };

    private static RuleSet SetOf(params Rule[] rules) => new() { Rules = rules };

    private static EntityProfile Widget() => EntityProfile.Builder().State("US-TX").EntityType("test-widget").Build();

    private static ObligationResult SingleResult(RuleSet rules, IReadOnlyList<IDocumentLike> docs) =>
        RegulatoryObligationEvaluator.Evaluate(Widget(), docs, Eval, rules).Obligations.Single();

    // ---------------- the insurance amount gate (A-2/CC-4, v1.2) ----------------

    private static Rule GlInsuranceRule(InsuranceMinimums minimums) => MakeRule(
        id: "test-gl-insurance",
        category: "insurance",
        obligation: new Obligation { Name = "GL floor", DocumentType = "coi", DocumentSubType = "test-gl" },
        cadence: new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.DocumentExpiration },
        minimums: minimums);

    private static readonly InsuranceMinimums GlCsl1M = new()
    {
        Kind = InsuranceFloorKind.CombinedSingleLimit,
        CoverageLine = InsuranceCoverageLine.GeneralLiability,
        PerOccurrence = 1_000_000m,
    };

    private static DocumentLike Coi(decimal? glLimit, DateOnly? expires = null) =>
        new("coi-1", "coi", "test-gl", ExpirationDate: expires ?? new DateOnly(2030, 1, 1), GeneralLiabilityLimit: glLimit);

    [Fact]
    public void An_unreadable_coverage_amount_reads_needs_document_info_never_satisfied()
    {
        var result = SingleResult(SetOf(GlInsuranceRule(GlCsl1M)), [Coi(glLimit: null)]);

        result.Status.Should().Be(ObligationStatus.NeedsDocumentInfo,
            "a bare Satisfied would certify an amount nobody read (A-2/CC-4)");
    }

    [Fact]
    public void A_coverage_amount_below_the_statutory_floor_reads_below_stated_minimum()
    {
        var result = SingleResult(SetOf(GlInsuranceRule(GlCsl1M)), [Coi(glLimit: 500_000m)]);

        result.Status.Should().Be(ObligationStatus.BelowStatedMinimum);
        result.InsuranceMinimums.Should().NotBeNull("the UI needs the floor next to the status");
    }

    [Fact]
    public void A_coverage_amount_meeting_a_fully_verifiable_csl_floor_reads_satisfied()
    {
        SingleResult(SetOf(GlInsuranceRule(GlCsl1M)), [Coi(glLimit: 1_000_000m)])
            .Status.Should().Be(ObligationStatus.Satisfied, "the floor is met exactly (>= comparison)");

        SingleResult(SetOf(GlInsuranceRule(GlCsl1M)), [Coi(glLimit: 2_000_000m)])
            .Status.Should().Be(ObligationStatus.Satisfied);
    }

    [Fact]
    public void A_split_limits_floor_is_never_fully_certified_from_one_extracted_figure()
    {
        // §1702.124(c)-shaped: the $50k personal-injury sub-limit and $200k aggregate cannot be read from
        // the single extracted GL figure, so at-or-above the BI+PD component reads NeedsDocumentInfo.
        var split = new InsuranceMinimums
        {
            Kind = InsuranceFloorKind.SplitLimits,
            CoverageLine = InsuranceCoverageLine.GeneralLiability,
            PerOccurrenceBodilyInjuryAndPropertyDamage = 100_000m,
            PerOccurrencePersonalInjury = 50_000m,
            Aggregate = 200_000m,
        };

        SingleResult(SetOf(GlInsuranceRule(split)), [Coi(glLimit: 150_000m)])
            .Status.Should().Be(ObligationStatus.NeedsDocumentInfo, "sub-limits remain unverifiable");

        SingleResult(SetOf(GlInsuranceRule(split)), [Coi(glLimit: 60_000m)])
            .Status.Should().Be(ObligationStatus.BelowStatedMinimum, "below the BI+PD component is a definite shortfall");
    }

    [Fact]
    public void An_auto_liability_floor_is_never_compared_against_the_extracted_general_liability_limit()
    {
        // Wrong policy line: the extracted figure is the GENERAL-liability limit; comparing it against an
        // auto-liability floor (49 CFR 387.33T / 43 TAC 218.16) would grade the wrong coverage.
        var auto = new InsuranceMinimums
        {
            Kind = InsuranceFloorKind.CombinedSingleLimit,
            CoverageLine = InsuranceCoverageLine.AutoLiability,
            PerOccurrence = 5_000_000m,
        };

        SingleResult(SetOf(GlInsuranceRule(auto)), [Coi(glLimit: 1_000m)])
            .Status.Should().Be(ObligationStatus.Satisfied,
                "presence + expiry semantics remain for auto floors (documented v1.2 limitation)");
    }

    [Fact]
    public void An_expired_certificate_stays_expired_regardless_of_its_amount()
    {
        var expired = Coi(glLimit: 5_000_000m, expires: new DateOnly(2026, 1, 1));

        SingleResult(SetOf(GlInsuranceRule(GlCsl1M)), [expired])
            .Status.Should().Be(ObligationStatus.Expired, "expiry is the stronger defect");
    }

    [Fact]
    public void An_expiring_certificate_below_the_floor_reads_below_stated_minimum()
    {
        // The shortfall exists NOW, not at renewal — it outranks the expiring-soon signal.
        var expiring = Coi(glLimit: 500_000m, expires: Eval.AddDays(10));

        SingleResult(SetOf(GlInsuranceRule(GlCsl1M)), [expiring])
            .Status.Should().Be(ObligationStatus.BelowStatedMinimum);
    }

    [Fact]
    public void An_expiring_certificate_with_an_unreadable_amount_keeps_the_expiring_deadline_signal()
    {
        var expiring = Coi(glLimit: null, expires: Eval.AddDays(10));

        SingleResult(SetOf(GlInsuranceRule(GlCsl1M)), [expiring])
            .Status.Should().Be(ObligationStatus.Expiring, "the renewal deadline is the actionable signal; the renewal upload gets re-checked");
    }

    // ---------------- document selection by effective deadline (UNVER-4) ----------------

    [Fact]
    public void A_fresh_issue_dated_renewal_outranks_a_stale_sibling_listed_first()
    {
        var recencyRule = MakeRule(
            id: "test-recency",
            category: "worker-certification",
            obligation: new Obligation { Name = "Recency", DocumentType = "certification", DocumentSubType = "test-recency" },
            cadence: new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.IssueDate, PeriodMonths = 24 });

        // The STALE document comes first (the natural CreatedAt-ascending order) — both have null expiry.
        var docs = new IDocumentLike[]
        {
            new DocumentLike("stale", "certification", "test-recency", IssueDate: new DateOnly(2023, 5, 1)),
            new DocumentLike("fresh", "certification", "test-recency", IssueDate: new DateOnly(2026, 6, 1)),
        };

        var result = SingleResult(SetOf(recencyRule), docs);

        result.Status.Should().Be(ObligationStatus.Satisfied, "the fresh renewal is the operative credential");
        result.MatchedDocumentId.Should().Be("fresh");
    }

    [Fact]
    public void An_expired_printed_expiry_document_does_not_shadow_a_fresh_issue_dated_one()
    {
        var recencyRule = MakeRule(
            id: "test-recency",
            category: "worker-certification",
            obligation: new Obligation { Name = "Recency", DocumentType = "certification", DocumentSubType = "test-recency" },
            cadence: new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.IssueDate, PeriodMonths = 24 });

        var docs = new IDocumentLike[]
        {
            new DocumentLike("old-expired", "certification", "test-recency", ExpirationDate: new DateOnly(2025, 1, 1)),
            new DocumentLike("fresh", "certification", "test-recency", IssueDate: new DateOnly(2026, 6, 1)),
        };

        var result = SingleResult(SetOf(recencyRule), docs);

        result.MatchedDocumentId.Should().Be("fresh",
            "selection compares EFFECTIVE deadlines (printed expiry vs cadence-computed due), not printed expiry alone");
        result.Status.Should().Be(ObligationStatus.Satisfied);
    }

    // ---------------- conditional filing with proof on record (UNVER-5) ----------------

    [Fact]
    public void Proof_of_a_conditional_filing_reads_satisfied_never_a_permanent_needs_document_info()
    {
        var conditionalRule = MakeRule(
            id: "test-injury-report",
            category: "filing",
            obligation: new Obligation { Name = "Injury report", DocumentType = "other", DocumentSubType = "test-injury" },
            cadence: new Cadence { Kind = CadenceKind.ConditionalFiling, Anchor = CadenceAnchor.DocumentExpiration });

        var withoutProof = SingleResult(SetOf(conditionalRule), []);
        withoutProof.Status.Should().Be(ObligationStatus.NotApplicable, "no trigger fact is assertable (CC-1)");

        var withProof = SingleResult(SetOf(conditionalRule),
            [new DocumentLike("ar800", "other", "test-injury")]);
        withProof.Status.Should().Be(ObligationStatus.Satisfied,
            "uploading proof of a one-off filing must never read WORSE than having no document");
    }

    // ---------------- fixed-annual with an undated proof (UNVER-13) ----------------

    [Fact]
    public void A_fixed_annual_obligation_with_an_undated_proof_reads_needs_document_info_all_year()
    {
        // Which annual cycle an undated proof covers is not generically inferable (a late filing for this
        // cycle and an early one for the next look identical), so the engine never guesses Satisfied.
        var fixedAnnual = MakeRule(
            id: "test-annual-filing",
            category: "filing",
            obligation: new Obligation { Name = "Annual filing", DocumentType = "other", DocumentSubType = "test-annual" },
            cadence: new Cadence { Kind = CadenceKind.FixedAnnual, Anchor = CadenceAnchor.FixedDate, FixedDate = new MonthDay(4, 30) });

        var undatedProof = new IDocumentLike[] { new DocumentLike("proof", "other", "test-annual") };

        // The day AFTER the deadline — the old behavior read Satisfied here (the false all-clear).
        RegulatoryObligationEvaluator.Evaluate(Widget(), undatedProof, new DateOnly(2026, 5, 1), SetOf(fixedAnnual))
            .Obligations.Single().Status.Should().Be(ObligationStatus.NeedsDocumentInfo);

        // Mid-cycle too: the proof's cycle coverage is unknowable either way.
        RegulatoryObligationEvaluator.Evaluate(Widget(), undatedProof, new DateOnly(2026, 2, 1), SetOf(fixedAnnual))
            .Obligations.Single().Status.Should().Be(ObligationStatus.NeedsDocumentInfo);

        // A proof with a PRINTED expiry still classifies normally.
        var datedProof = new IDocumentLike[] { new DocumentLike("proof", "other", "test-annual", ExpirationDate: new DateOnly(2027, 4, 30)) };
        RegulatoryObligationEvaluator.Evaluate(Widget(), datedProof, new DateOnly(2026, 5, 1), SetOf(fixedAnnual))
            .Obligations.Single().Status.Should().Be(ObligationStatus.Satisfied);
    }

    // ---------------- federal-only set + a known state (UNVER-7) ----------------

    [Fact]
    public void A_set_state_against_a_federal_only_rule_set_reads_not_covered_never_a_partial_federal_report()
    {
        var federalOnly = SetOf(MakeRule(id: "test-fed-only", jurisdiction: "us-fed"));

        var report = RegulatoryObligationEvaluator.Evaluate(Widget(), [], Eval, federalOnly);

        report.Coverage.Should().Be(JurisdictionCoverage.NotCovered,
            "a known state whose rules are not loaded must never yield a silent federal-only report");
        report.CoverageMessage.Should().Contain("US-TX");
    }

    // ---------------- Neq fails closed on a type mismatch (UNVER-8) ----------------

    [Fact]
    public void Neq_fails_closed_to_false_on_a_fact_value_type_mismatch()
    {
        // Hand-built tree (the loader rejects this shape): int fact vs string literal.
        var profile = EntityProfile.Builder().EmployeeCount(5).Build();

        ApplicabilityEvaluator.EvaluateValue(
                Condition.Leaf(FactNames.EmployeeCount, ConditionOp.Neq, "5"), profile)
            .Should().Be(Kleene.False, "an incomparable pair must not fail OPEN to true");

        ApplicabilityEvaluator.EvaluateValue(
                Condition.Leaf(FactNames.EmployeeCount, ConditionOp.Eq, "5"), profile)
            .Should().Be(Kleene.False);

        // Comparable pairs still negate normally.
        ApplicabilityEvaluator.EvaluateValue(
                Condition.Leaf(FactNames.EmployeeCount, ConditionOp.Neq, 6), profile)
            .Should().Be(Kleene.True);
    }

    // ---------------- builder aliasing (UNVER-10) ----------------

    [Fact]
    public void Reusing_a_builder_after_build_does_not_mutate_the_earlier_profile()
    {
        var builder = EntityProfile.Builder().State("US-TX");
        var first = builder.Build();

        builder.EmployeeCount(5);
        var second = builder.Build();

        first.IsSet(FactNames.EmployeeCount).Should().BeFalse("the first profile must stay immutable");
        second.IsSet(FactNames.EmployeeCount).Should().BeTrue();
    }

    // ---------------- month-end rounding (CONF-0, "calendar months") ----------------

    [Theory]
    [InlineData(2025, 6, 1, 2027, 6, 30)]   // mid-month completion runs to the end of the 24th month
    [InlineData(2025, 1, 31, 2027, 1, 31)]  // already month-end stays month-end
    [InlineData(2024, 2, 29, 2026, 2, 28)]  // leap-day completion clamps, then rounds to the (common) Feb end
    public void Round_to_month_end_extends_a_period_due_date_to_the_last_day_of_its_month(
        int iy, int im, int id, int ey, int em, int ed)
    {
        var cadence = new Cadence
        {
            Kind = CadenceKind.Renewal,
            Anchor = CadenceAnchor.IssueDate,
            PeriodMonths = 24,
            RoundToMonthEnd = true,
        };

        CadenceCalculator.ComputeNextDueDate(cadence, null, new DateOnly(iy, im, id), Eval)
            .Should().Be(new DateOnly(ey, em, ed));
    }

    [Fact]
    public void Without_rounding_the_period_due_date_stays_the_same_day_anniversary()
    {
        var cadence = new Cadence { Kind = CadenceKind.Renewal, Anchor = CadenceAnchor.IssueDate, PeriodMonths = 24 };

        CadenceCalculator.ComputeNextDueDate(cadence, null, new DateOnly(2025, 6, 1), Eval)
            .Should().Be(new DateOnly(2027, 6, 1));
    }
}
