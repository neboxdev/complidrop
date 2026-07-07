using System.Reflection;
using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Tests for the entity-profile fact typing (SCHEMA §4) and the STRUCTURAL enforcement of legal-req #2 on
/// <see cref="ObligationReport"/> — the report type is shaped so a bare "compliant" cannot be rendered.
/// </summary>
public class EntityProfileAndReportTests
{
    // ---------------- EntityProfile is typed over the frozen registry ----------------

    [Fact]
    public void Builder_round_trips_typed_facts()
    {
        var profile = EntityProfile.Builder()
            .State("US-TX")
            .EntityType("test-widget")
            .EmployeeCount(12)
            .OperatesInterstate(true)
            .MaxPassengerSeatingCapacity(20)
            .Build();

        profile.TryGet(FactNames.State, out var state).Should().BeTrue();
        state.AsString.Should().Be("US-TX");
        profile.TryGet(FactNames.EmployeeCount, out var count).Should().BeTrue();
        count.AsInt.Should().Be(12);
        profile.TryGet(FactNames.OperatesInterstate, out var interstate).Should().BeTrue();
        interstate.AsBool.Should().BeTrue();
    }

    [Fact]
    public void An_unset_fact_is_not_present()
    {
        var profile = EntityProfile.Builder().State("US-TX").Build();

        profile.IsSet(FactNames.OperatesInterstate).Should().BeFalse();
        profile.TryGet(FactNames.OperatesInterstate, out _).Should().BeFalse();
    }

    [Fact]
    public void Constructing_a_profile_with_an_unregistered_fact_throws()
    {
        var facts = new Dictionary<string, FactValue> { ["not_a_registered_fact"] = FactValue.Of(true) };

        var act = () => new EntityProfile(facts);
        act.Should().Throw<ArgumentException>().WithMessage("*not a registered fact*");
    }

    [Fact]
    public void Constructing_a_profile_with_a_wrong_kind_value_throws()
    {
        // operatesInterstate is Bool; a string value is rejected so the profile can never disagree with the registry.
        var facts = new Dictionary<string, FactValue> { [FactNames.OperatesInterstate] = FactValue.Of("yes") };

        var act = () => new EntityProfile(facts);
        act.Should().Throw<ArgumentException>().WithMessage("*Bool*String*");
    }

    [Fact]
    public void The_frozen_registry_contains_the_sixteen_v1_facts_with_expected_kinds()
    {
        FactRegistry.All.Should().HaveCount(16);
        FactRegistry.Get(FactNames.State).Kind.Should().Be(FactKind.String);
        FactRegistry.Get(FactNames.EmployeeCount).Kind.Should().Be(FactKind.Int);
        FactRegistry.Get(FactNames.MaxPassengerSeatingCapacity).Kind.Should().Be(FactKind.Int);
        FactRegistry.Get(FactNames.OperatesInterstate).Kind.Should().Be(FactKind.Bool);
        FactRegistry.IsKnown("invented").Should().BeFalse();
    }

    [Fact]
    public void FactValue_equality_is_case_insensitive_for_strings()
    {
        FactValue.Of("US-TX").Should().Be(FactValue.Of("us-tx"));
        FactValue.Of(16).Should().Be(FactValue.Of(16L));
        FactValue.Of(true).Should().NotBe(FactValue.Of(false));
    }

    // ---------------- ObligationReport structurally forbids a bare "compliant" ----------------

    [Fact]
    public void Report_type_exposes_no_overall_compliant_boolean()
    {
        // Legal-req #2, enforced structurally: there is NO overall isCompliant/compliant flag — only
        // per-obligation statuses plus the mandatory notice. A reflection guard so a future edit that
        // adds such a property fails this test loudly.
        var boolProps = typeof(ObligationReport)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool))
            .Select(p => p.Name)
            .ToList();

        boolProps.Should().BeEmpty("the report must not carry any overall boolean verdict");
        typeof(ObligationReport).GetProperty("IsCompliant").Should().BeNull();
        typeof(ObligationReport).GetProperty("Compliant").Should().BeNull();
    }

    [Fact]
    public void A_report_cannot_be_built_with_an_empty_completeness_notice()
    {
        var emptyNotice = new CompletenessNotice { Text = "  " };

        var act = () => ObligationReport.ForObligations([], emptyNotice);
        act.Should().Throw<ArgumentException>().WithMessage("*completeness notice*non-empty*");
    }

    [Fact]
    public void A_fully_satisfied_report_still_carries_the_non_exhaustiveness_notice()
    {
        var satisfied = new ObligationResult
        {
            ObligationRef = "OBL-X",
            Name = "X",
            Status = ObligationStatus.Satisfied,
        };

        var report = ObligationReport.ForObligations([satisfied], CompletenessNotice.Default());

        report.Obligations.Should().OnlyContain(o => o.Status == ObligationStatus.Satisfied);
        report.Completeness.Text.Should().Be(CompletenessNotice.DefaultText);
        report.Completeness.Text.Should().Contain("not a complete list");
    }

    [Fact]
    public void Outstanding_profile_facts_aggregate_missing_facts_across_obligations()
    {
        var needsA = new ObligationResult
        {
            ObligationRef = "OBL-A", Name = "A", Status = ObligationStatus.NeedsProfileInfo,
            MissingFacts = [FactNames.OperatesInterstate],
        };
        var needsB = new ObligationResult
        {
            ObligationRef = "OBL-B", Name = "B", Status = ObligationStatus.NeedsProfileInfo,
            MissingFacts = [FactNames.OperatesInterstate, FactNames.MaxPassengerSeatingCapacity],
        };

        var report = ObligationReport.ForObligations([needsA, needsB], CompletenessNotice.Default());

        report.OutstandingProfileFacts.Should().BeEquivalentTo(
            [FactNames.OperatesInterstate, FactNames.MaxPassengerSeatingCapacity]);
    }
}
