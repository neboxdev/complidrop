using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// End-to-end mechanics tests for <see cref="RegulatoryObligationEvaluator"/> over the SYNTHETIC fixtures:
/// the interstate/intrastate branch (legal-req #1 — unknown fact ⇒ needs-profile-info, never satisfied),
/// satisfiesFederal suppression (SCHEMA §3), version-boundary selection, jurisdiction coverage, entity-type
/// filtering, and document-presence/expiry status. Fixtures are fake ("test-widget"); assertions are about
/// engine BEHAVIOUR, not real law.
/// </summary>
public class RegulatoryObligationEvaluatorTests
{
    private static readonly DateOnly Eval = new(2026, 8, 1); // inside the license v2 window

    // Production posture: verified-only, so the fixture's 'probable' rule is excluded and doesn't add noise.
    private static RuleSet Rules() => RuleSetLoader.LoadFromDirectory(
        Path.Combine(AppContext.BaseDirectory, "RuleEngineFixtures", "valid"),
        new RuleLoadOptions(VerifiedOnly: true));

    private static EntityProfileBuilder WidgetInTexas() =>
        EntityProfile.Builder().State("US-TX").EntityType("test-widget");

    private static ObligationResult? Find(ObligationReport report, string obligationRef) =>
        report.Obligations.FirstOrDefault(o => o.ObligationRef == obligationRef);

    // ---------------- legal-req #1: interstate/intrastate branch ----------------

    [Fact]
    public void Unknown_interstate_yields_needs_profile_info_never_satisfied_even_with_a_document()
    {
        // operatesInterstate is UNSET. Both the federal and the Texas insurance floors gate on it, so
        // neither can resolve — and a present certificate must NOT launder that into a "satisfied".
        var profile = WidgetInTexas()
            .MaxPassengerSeatingCapacity(20)
            .ServesOrSellsAlcohol(false)
            .Build();
        var docs = new[]
        {
            new DocumentLike("doc-1", "coi", "test-fed-insurance", ExpirationDate: new DateOnly(2027, 1, 1)),
        };

        var report = RegulatoryObligationEvaluator.Evaluate(profile, docs, Eval, Rules());

        var fed = Find(report, "OBL-TEST-FED-INSURANCE")!;
        var tx = Find(report, "OBL-TEST-TX-INSURANCE")!;

        fed.Status.Should().Be(ObligationStatus.NeedsProfileInfo);
        fed.MissingFacts.Should().Contain(FactNames.OperatesInterstate);
        tx.Status.Should().Be(ObligationStatus.NeedsProfileInfo);

        report.Obligations.Should().NotContain(o => o.Status == ObligationStatus.Satisfied,
            "no obligation may read satisfied while the interstate branch is unresolved");
        report.OutstandingProfileFacts.Should().Contain(FactNames.OperatesInterstate);
    }

    [Fact]
    public void Interstate_true_selects_the_federal_floor_and_marks_the_texas_floor_not_applicable()
    {
        var profile = WidgetInTexas()
            .OperatesInterstate(true)
            .MaxPassengerSeatingCapacity(20)
            .ServesOrSellsAlcohol(false)
            .Build();
        var docs = new[]
        {
            new DocumentLike("doc-fed", "coi", "test-fed-insurance", ExpirationDate: new DateOnly(2027, 6, 1)),
        };

        var report = RegulatoryObligationEvaluator.Evaluate(profile, docs, Eval, Rules());

        Find(report, "OBL-TEST-FED-INSURANCE")!.Status.Should().Be(ObligationStatus.Satisfied);
        Find(report, "OBL-TEST-FED-INSURANCE")!.MatchedDocumentId.Should().Be("doc-fed");
        Find(report, "OBL-TEST-TX-INSURANCE")!.Status.Should().Be(ObligationStatus.NotApplicable);
    }

    [Fact]
    public void Interstate_false_selects_the_texas_floor_and_marks_the_federal_floor_not_applicable()
    {
        var profile = WidgetInTexas()
            .OperatesInterstate(false)
            .MaxPassengerSeatingCapacity(20)
            .ServesOrSellsAlcohol(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        Find(report, "OBL-TEST-TX-INSURANCE")!.Status.Should().Be(ObligationStatus.Missing);
        Find(report, "OBL-TEST-FED-INSURANCE")!.Status.Should().Be(ObligationStatus.NotApplicable);
    }

    // ---------------- satisfiesFederal suppression (SCHEMA §3) ----------------

    [Fact]
    public void An_applicable_state_credential_suppresses_the_federal_obligation_it_implements()
    {
        // interstate + capacity>=16 makes BOTH the federal and Texas operator-cert rules applicable; the
        // Texas rule's satisfiesFederal must remove the federal one from the output (no double-count).
        var profile = WidgetInTexas()
            .OperatesInterstate(true)
            .MaxPassengerSeatingCapacity(20)
            .ServesOrSellsAlcohol(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        Find(report, "OBL-TEST-FED-CERT").Should().BeNull("the federal cert is implemented by the applicable state cert");
        Find(report, "OBL-TEST-TX-CERT").Should().NotBeNull();
    }

    [Fact]
    public void Suppression_requires_an_applicable_state_rule_not_merely_a_present_one()
    {
        // Capacity 10 (< 16) makes the state cert rule NOT applicable, so it cannot suppress the federal
        // floor — the federal rule still surfaces (here as NotApplicable, since it also needs capacity>=16),
        // proving suppression is gated on Kleene-True, never on the rule's mere presence.
        var profile = WidgetInTexas()
            .OperatesInterstate(true)
            .MaxPassengerSeatingCapacity(10)
            .ServesOrSellsAlcohol(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        var fed = Find(report, "OBL-TEST-FED-CERT");
        fed.Should().NotBeNull("an inapplicable state rule must not suppress the federal obligation");
        fed!.Status.Should().Be(ObligationStatus.NotApplicable);
    }

    // ---------------- version-boundary selection ----------------

    [Theory]
    [InlineData("2026-06-30", "Synthetic Texas widget license (v1)")]
    [InlineData("2026-07-01", "Synthetic Texas widget license (v2)")]
    public void The_version_effective_at_the_evaluation_date_is_selected(string evalDate, string expectedName)
    {
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], DateOnly.Parse(evalDate), Rules());

        Find(report, "OBL-TEST-TX-LICENSE")!.Name.Should().Be(expectedName);
    }

    [Fact]
    public void A_date_before_any_version_takes_effect_omits_the_rule()
    {
        // The license rule's earliest version starts 2026-01-01; evaluate in 2025 and it isn't in effect.
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], new DateOnly(2025, 1, 1), Rules());

        Find(report, "OBL-TEST-TX-LICENSE").Should().BeNull();
    }

    // ---------------- jurisdiction coverage (SCHEMA §3) ----------------

    [Fact]
    public void A_non_covered_state_reports_not_covered_never_an_empty_compliant()
    {
        var profile = EntityProfile.Builder().State("US-CA").EntityType("test-widget").Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        report.Coverage.Should().Be(JurisdictionCoverage.NotCovered);
        report.CoverageMessage.Should().Contain("US-TX", "the message derives the covered set from the loaded rules, never a hardcoded state (UNVER-7)");
        report.Obligations.Should().BeEmpty();
        report.Completeness.Text.Should().NotBeNullOrWhiteSpace("the non-exhaustiveness notice is present even when not covered");
    }

    [Fact]
    public void A_set_but_unmodeled_entity_type_reports_not_covered_never_an_empty_all_clear()
    {
        // C-1/A-3: a KNOWN-but-unmodeled entity type must not yield a bare empty all-clear (the old
        // "filters out everything ⇒ Covered with 0 obligations" completeness illusion). It reports
        // NotCovered ("no rule set is modeled"). The synthetic fixtures model only "test-widget".
        var profile = EntityProfile.Builder().State("US-TX").EntityType("test-unrelated").Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        report.Coverage.Should().Be(JurisdictionCoverage.NotCovered);
        report.CoverageMessage.Should().Contain("test-unrelated");
        report.Obligations.Should().BeEmpty();
        report.Completeness.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_null_entity_type_makes_type_scoped_rules_needs_profile_info_never_missing_or_satisfied()
    {
        // C-1/A-3 (other direction): with entityType UNKNOWN the engine must not bypass the type filter into
        // a Kleene-True and emit a wrong Missing/Satisfied (e.g. an {all:[]} rule). Every type-scoped rule
        // reads needs-profile-info(entityType); entityType is surfaced in OutstandingProfileFacts.
        var profile = EntityProfile.Builder().State("US-TX").Build(); // no EntityType

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        report.Coverage.Should().Be(JurisdictionCoverage.Covered);
        report.Obligations.Should().NotBeEmpty();
        report.Obligations.Should().OnlyContain(o => o.Status == ObligationStatus.NeedsProfileInfo,
            "every synthetic rule is type-scoped, so all read needs-profile-info when the type is unknown");
        report.Obligations.Should().OnlyContain(o => o.MissingFacts.Contains(FactNames.EntityType));
        report.Obligations.Should().NotContain(o =>
            o.Status == ObligationStatus.Missing || o.Status == ObligationStatus.Satisfied);
        report.OutstandingProfileFacts.Should().Contain(FactNames.EntityType);
    }

    // ---------------- document presence / expiry status ----------------

    [Theory]
    [InlineData("2027-08-01", ObligationStatus.Satisfied)] // far future
    [InlineData("2026-08-31", ObligationStatus.Expiring)]  // exactly 30 days out
    [InlineData("2026-08-11", ObligationStatus.Expiring)]  // 10 days out
    [InlineData("2026-08-01", ObligationStatus.Expiring)]  // expires TODAY (== evaluation date): strict < so not yet Expired (T-5)
    [InlineData("2026-09-01", ObligationStatus.Satisfied)] // 31 days out — beyond the window
    [InlineData("2026-07-31", ObligationStatus.Expired)]   // yesterday
    public void License_status_follows_the_matched_documents_expiry(string expiry, ObligationStatus expected)
    {
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();
        var docs = new[]
        {
            new DocumentLike("lic-1", "license", "test-tx-widget-license", ExpirationDate: DateOnly.Parse(expiry)),
        };

        var report = RegulatoryObligationEvaluator.Evaluate(profile, docs, Eval, Rules());

        var license = Find(report, "OBL-TEST-TX-LICENSE")!;
        license.Status.Should().Be(expected);
        license.MatchedDocumentId.Should().Be("lic-1");
    }

    [Fact]
    public void An_applicable_obligation_with_no_matching_document_is_missing()
    {
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        Find(report, "OBL-TEST-TX-LICENSE")!.Status.Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void A_document_with_a_null_subtype_does_not_satisfy_a_subtyped_obligation()
    {
        // RD-c: an unmapped/legacy document (no subtype) never auto-satisfies a specific obligation.
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();
        var docs = new[]
        {
            new DocumentLike("lic-legacy", "license", DocumentSubType: null, ExpirationDate: new DateOnly(2028, 1, 1)),
        };

        var report = RegulatoryObligationEvaluator.Evaluate(profile, docs, Eval, Rules());

        Find(report, "OBL-TEST-TX-LICENSE")!.Status.Should().Be(ObligationStatus.Missing);
    }

    [Fact]
    public void A_matched_renewal_document_with_no_readable_expiry_is_needs_document_info_never_satisfied()
    {
        // A-1: the widget license is a renewal anchored on documentExpiration. A matched document with NO
        // printed expiry and no issue date yields no determinable due date — it must read NeedsDocumentInfo
        // (we hold the doc but can't confirm currency), NEVER a false Satisfied.
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();
        var docs = new[] { new DocumentLike("lic-noexp", "license", "test-tx-widget-license") }; // no expiry, no issue

        var report = RegulatoryObligationEvaluator.Evaluate(profile, docs, Eval, Rules());

        var license = Find(report, "OBL-TEST-TX-LICENSE")!;
        license.Status.Should().Be(ObligationStatus.NeedsDocumentInfo);
        license.MatchedDocumentId.Should().Be("lic-noexp");
    }

    // ---------------- satisfiesFederal suppression does NOT fire on an UNKNOWN state rule (T-1) ----------------

    [Fact]
    public void An_unknown_state_rule_does_not_suppress_the_federal_obligation_it_would_implement()
    {
        // T-1: interstate=true but capacity UNSET makes the state operator-cert rule's applicability Unknown.
        // Suppression fires only on a Kleene-TRUE state match, so the federal cert must STILL be emitted
        // (here as NeedsProfileInfo), never silently suppressed by an unresolved state rule. This kills the
        // `== Kleene.True` → `!= Kleene.False` mutant that the capacity-10 (Kleene-False) case leaves alive.
        var profile = WidgetInTexas()
            .OperatesInterstate(true)
            .ServesOrSellsAlcohol(false)
            .Build(); // maxPassengerSeatingCapacity UNSET ⇒ both cert rules Unknown

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        var fed = Find(report, "OBL-TEST-FED-CERT");
        fed.Should().NotBeNull("an UNKNOWN state rule must not suppress the federal obligation");
        fed!.Status.Should().Be(ObligationStatus.NeedsProfileInfo);
        Find(report, "OBL-TEST-TX-CERT")!.Status.Should().Be(ObligationStatus.NeedsProfileInfo);
    }

    // ---------------- unset state ⇒ federal-only, never assume Texas (T-3, synthetic) ----------------

    [Fact]
    public void An_unset_state_evaluates_federal_rules_only_and_surfaces_state_as_outstanding()
    {
        // T-3: no .State() ⇒ Covered (an UNKNOWN state is not a NOT-covered state), only federal rules are
        // candidates (no state law applied from ignorance), and `state` is surfaced as outstanding.
        var profile = EntityProfile.Builder().EntityType("test-widget")
            .OperatesInterstate(true)
            .MaxPassengerSeatingCapacity(20)
            .ServesOrSellsAlcohol(false)
            .Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        report.Coverage.Should().Be(JurisdictionCoverage.Covered);
        report.Obligations.Should().NotBeEmpty();
        report.Obligations.Should().OnlyContain(o => o.RuleId.StartsWith("test-fed-"),
            "with an unknown state only federal rules apply — never Texas by default");
        report.OutstandingProfileFacts.Should().Contain(FactNames.State);
    }

    [Fact]
    public void A_missing_fixed_annual_filing_still_surfaces_its_next_due_date()
    {
        // The alcohol permit is a fixed-annual (May 15) filing; even with no document on record the engine
        // surfaces the next due date so a reminder can be scheduled.
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(true).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        var alcohol = Find(report, "OBL-TEST-TX-ALCOHOL")!;
        alcohol.Status.Should().Be(ObligationStatus.Missing);
        alcohol.NextDueDate.Should().Be(new DateOnly(2027, 5, 15), "May 15 has passed for 2026 by the Aug 1 evaluation date");
    }

    // ---------------- structural guarantees (legal-req #2/#3) ----------------

    [Fact]
    public void Every_report_carries_the_completeness_notice_and_verbatim_rule_framing()
    {
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        report.Completeness.Should().NotBeNull();
        report.Completeness.Text.Should().Contain("not a complete list");

        // The user-facing framing is copied verbatim from the rule version — the engine synthesises no verdict.
        var license = Find(report, "OBL-TEST-TX-LICENSE")!;
        license.Rationale.Should().StartWith("Synthetic test rule");
        license.UserAction.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Obligations_are_ordered_actionable_first_then_by_ref()
    {
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        // Actionable statuses sort before the inert ones (Satisfied/NotApplicable), so a same-obligationRef
        // NotApplicable sibling can never shadow an actionable result (A-4). Within each tier: by obligationRef.
        var ranks = report.Obligations.Select(o => IsActionable(o.Status) ? 0 : 1).ToList();
        ranks.Should().BeInAscendingOrder("actionable obligations come first");

        var actionableRefs = report.Obligations.Where(o => IsActionable(o.Status)).Select(o => o.ObligationRef).ToList();
        actionableRefs.Should().Equal("OBL-TEST-TX-INSURANCE", "OBL-TEST-TX-LICENSE");

        report.Obligations.Where(o => !IsActionable(o.Status)).Select(o => o.ObligationRef)
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public void Each_result_carries_its_unique_rule_id()
    {
        // A-4: RuleId is one-to-one with the emitted obligation (unlike the possibly-shared obligationRef).
        var profile = WidgetInTexas().OperatesInterstate(false).ServesOrSellsAlcohol(false).Build();

        var report = RegulatoryObligationEvaluator.Evaluate(profile, [], Eval, Rules());

        report.Obligations.Should().OnlyContain(o => !string.IsNullOrEmpty(o.RuleId));
        report.Obligations.Select(o => o.RuleId).Should().OnlyHaveUniqueItems(
            "RuleId is one-to-one with the emitted obligation (UNVER-27: the audit guide maps the uniqueness guarantee here)");
        Find(report, "OBL-TEST-TX-LICENSE")!.RuleId.Should().Be("test-tx-widget-license");
    }

    private static bool IsActionable(ObligationStatus status) =>
        status is ObligationStatus.Missing or ObligationStatus.Expiring or ObligationStatus.Expired
               or ObligationStatus.NeedsDocumentInfo or ObligationStatus.NeedsProfileInfo;
}
