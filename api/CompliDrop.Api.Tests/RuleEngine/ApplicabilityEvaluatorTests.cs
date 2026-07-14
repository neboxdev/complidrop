using CompliDrop.Api.RuleEngine;
using FluentAssertions;

namespace CompliDrop.Api.Tests.RuleEngine;

/// <summary>
/// Truth-table (property) tests for the three-valued Kleene applicability evaluator (SCHEMA §4). Because
/// the domain is only {False, Unknown, True}, the "property" tests EXHAUSTIVELY enumerate every operand
/// combination and check the evaluator against a reference implementation of Kleene logic — so any drift
/// in the propagation rules is caught. Also pins the load-bearing-missing-fact precision that drives
/// <c>needs-profile-info</c>.
/// </summary>
public class ApplicabilityEvaluatorTests
{
    private static readonly Kleene[] AllValues = [Kleene.False, Kleene.Unknown, Kleene.True];

    // A profile that fixes one bool fact True and one bool fact False, leaving a third unset (Unknown),
    // so a leaf of any target Kleene value can be constructed deterministically.
    private static readonly EntityProfile Profile = EntityProfile.Builder()
        .CarriesWorkersComp(true)    // -> True leaf
        .ProvidesArmedGuards(false)  // -> False leaf (eq true on a false fact)
        .Build();                    // operatesForklifts unset -> Unknown leaf

    private static Applicability LeafFor(Kleene target) => target switch
    {
        Kleene.True => Condition.Leaf(FactNames.CarriesWorkersComp, ConditionOp.Eq, true),
        Kleene.False => Condition.Leaf(FactNames.ProvidesArmedGuards, ConditionOp.Eq, true),
        Kleene.Unknown => Condition.Leaf(FactNames.OperatesForklifts, ConditionOp.Eq, true),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    // Reference Kleene semantics (SCHEMA §4), independent of the implementation under test.
    private static Kleene RefAll(IEnumerable<Kleene> xs)
    {
        var list = xs.ToList();
        if (list.Contains(Kleene.False)) return Kleene.False;
        if (list.Contains(Kleene.Unknown)) return Kleene.Unknown;
        return Kleene.True;
    }

    private static Kleene RefAny(IEnumerable<Kleene> xs)
    {
        var list = xs.ToList();
        if (list.Contains(Kleene.True)) return Kleene.True;
        if (list.Contains(Kleene.Unknown)) return Kleene.Unknown;
        return Kleene.False;
    }

    private static Kleene RefNot(Kleene x) => x switch
    {
        Kleene.True => Kleene.False,
        Kleene.False => Kleene.True,
        _ => Kleene.Unknown,
    };

    [Fact]
    public void Leaf_helpers_yield_their_intended_kleene_values()
    {
        ApplicabilityEvaluator.EvaluateValue(LeafFor(Kleene.True), Profile).Should().Be(Kleene.True);
        ApplicabilityEvaluator.EvaluateValue(LeafFor(Kleene.False), Profile).Should().Be(Kleene.False);
        ApplicabilityEvaluator.EvaluateValue(LeafFor(Kleene.Unknown), Profile).Should().Be(Kleene.Unknown);
    }

    [Fact]
    public void Not_matches_the_kleene_truth_table_for_all_values()
    {
        foreach (var v in AllValues)
        {
            var actual = ApplicabilityEvaluator.EvaluateValue(Condition.Not(LeafFor(v)), Profile);
            actual.Should().Be(RefNot(v), $"not({v}) should be {RefNot(v)}");
        }
    }

    [Fact]
    public void Double_negation_is_identity_for_all_values()
    {
        foreach (var v in AllValues)
            ApplicabilityEvaluator.EvaluateValue(Condition.Not(Condition.Not(LeafFor(v))), Profile).Should().Be(v);
    }

    [Fact]
    public void All_matches_the_kleene_truth_table_for_every_operand_pair()
    {
        foreach (var a in AllValues)
            foreach (var b in AllValues)
            {
                var actual = ApplicabilityEvaluator.EvaluateValue(Condition.All(LeafFor(a), LeafFor(b)), Profile);
                actual.Should().Be(RefAll([a, b]), $"all({a},{b})");
            }
    }

    [Fact]
    public void Any_matches_the_kleene_truth_table_for_every_operand_pair()
    {
        foreach (var a in AllValues)
            foreach (var b in AllValues)
            {
                var actual = ApplicabilityEvaluator.EvaluateValue(Condition.Any(LeafFor(a), LeafFor(b)), Profile);
                actual.Should().Be(RefAny([a, b]), $"any({a},{b})");
            }
    }

    [Fact]
    public void All_and_any_match_the_reference_over_every_operand_triple()
    {
        foreach (var a in AllValues)
            foreach (var b in AllValues)
                foreach (var c in AllValues)
                {
                    var operands = new[] { a, b, c };
                    var leaves = operands.Select(LeafFor).ToArray();

                    ApplicabilityEvaluator.EvaluateValue(Condition.All(leaves), Profile)
                        .Should().Be(RefAll(operands), $"all({a},{b},{c})");
                    ApplicabilityEvaluator.EvaluateValue(Condition.Any(leaves), Profile)
                        .Should().Be(RefAny(operands), $"any({a},{b},{c})");
                }
    }

    [Fact]
    public void Empty_all_is_true_and_empty_any_is_false()
    {
        ApplicabilityEvaluator.EvaluateValue(Condition.All(), Profile).Should().Be(Kleene.True);
        ApplicabilityEvaluator.EvaluateValue(Condition.Any(), Profile).Should().Be(Kleene.False);
    }

    // ---------------- missing-fact precision (drives needs-profile-info) ----------------

    [Fact]
    public void A_decided_all_reports_no_missing_facts_even_when_an_unknown_sibling_exists()
    {
        // all(False, Unknown) is decided False — no answer to the unknown can change it, so nothing is asked.
        var result = ApplicabilityEvaluator.Evaluate(
            Condition.All(LeafFor(Kleene.False), LeafFor(Kleene.Unknown)), Profile);

        result.Value.Should().Be(Kleene.False);
        result.MissingFacts.Should().BeEmpty();
    }

    [Fact]
    public void A_decided_any_reports_no_missing_facts_even_when_an_unknown_sibling_exists()
    {
        // any(True, Unknown) is decided True.
        var result = ApplicabilityEvaluator.Evaluate(
            Condition.Any(LeafFor(Kleene.True), LeafFor(Kleene.Unknown)), Profile);

        result.Value.Should().Be(Kleene.True);
        result.MissingFacts.Should().BeEmpty();
    }

    [Fact]
    public void An_undecided_all_reports_only_the_load_bearing_unknown_facts()
    {
        // all(True, Unknown[operatesForklifts]) is Unknown; only the unknown leaf's fact is needed.
        var result = ApplicabilityEvaluator.Evaluate(
            Condition.All(LeafFor(Kleene.True), LeafFor(Kleene.Unknown)), Profile);

        result.Value.Should().Be(Kleene.Unknown);
        result.MissingFacts.Should().BeEquivalentTo([FactNames.OperatesForklifts]);
    }

    [Fact]
    public void An_undecided_any_reports_the_union_of_unknown_facts()
    {
        var result = ApplicabilityEvaluator.Evaluate(
            Condition.Any(
                LeafFor(Kleene.False),
                Condition.Leaf(FactNames.OperatesForklifts, ConditionOp.Eq, true),
                Condition.Leaf(FactNames.OperatesInterstate, ConditionOp.Eq, true)),
            Profile);

        result.Value.Should().Be(Kleene.Unknown);
        result.MissingFacts.Should().BeEquivalentTo([FactNames.OperatesForklifts, FactNames.OperatesInterstate]);
    }

    [Fact]
    public void Not_of_unknown_propagates_the_missing_fact()
    {
        var result = ApplicabilityEvaluator.Evaluate(
            Condition.Not(Condition.Leaf(FactNames.OperatesInterstate, ConditionOp.Eq, true)), Profile);

        result.Value.Should().Be(Kleene.Unknown);
        result.MissingFacts.Should().BeEquivalentTo([FactNames.OperatesInterstate]);
    }

    // ---------------- leaf operators ----------------

    [Fact]
    public void Numeric_gte_and_lte_compare_at_the_boundary()
    {
        var profile = EntityProfile.Builder().MaxPassengerSeatingCapacity(16).Build();

        ApplicabilityEvaluator.EvaluateValue(Condition.Leaf(FactNames.MaxPassengerSeatingCapacity, ConditionOp.Gte, 16), profile).Should().Be(Kleene.True);
        ApplicabilityEvaluator.EvaluateValue(Condition.Leaf(FactNames.MaxPassengerSeatingCapacity, ConditionOp.Gte, 17), profile).Should().Be(Kleene.False);
        ApplicabilityEvaluator.EvaluateValue(Condition.Leaf(FactNames.MaxPassengerSeatingCapacity, ConditionOp.Lte, 16), profile).Should().Be(Kleene.True);
        ApplicabilityEvaluator.EvaluateValue(Condition.Leaf(FactNames.MaxPassengerSeatingCapacity, ConditionOp.Lte, 15), profile).Should().Be(Kleene.False);
    }

    [Fact]
    public void Neq_is_the_negation_of_eq()
    {
        var profile = EntityProfile.Builder().State("US-TX").Build();

        ApplicabilityEvaluator.EvaluateValue(Condition.Leaf(FactNames.State, ConditionOp.Neq, "US-CA"), profile).Should().Be(Kleene.True);
        ApplicabilityEvaluator.EvaluateValue(Condition.Leaf(FactNames.State, ConditionOp.Neq, "us-tx"), profile).Should().Be(Kleene.False);
    }

    [Fact]
    public void In_matches_any_listed_value_case_insensitively_for_strings()
    {
        var profile = EntityProfile.Builder().EntityType("test-widget").Build();

        ApplicabilityEvaluator.EvaluateValue(Condition.In(FactNames.EntityType, "test-gadget", "TEST-WIDGET"), profile).Should().Be(Kleene.True);
        ApplicabilityEvaluator.EvaluateValue(Condition.In(FactNames.EntityType, "test-gadget", "test-gizmo"), profile).Should().Be(Kleene.False);
    }

    [Fact]
    public void An_unset_fact_is_unknown_never_false()
    {
        // The core Kleene guarantee: an unanswered question is Unknown, so the engine asks — it does not
        // read "no" and silently drop the obligation.
        ApplicabilityEvaluator.EvaluateValue(
            Condition.Leaf(FactNames.OperatesInterstate, ConditionOp.Eq, true), EntityProfile.Empty)
            .Should().Be(Kleene.Unknown);
    }
}
