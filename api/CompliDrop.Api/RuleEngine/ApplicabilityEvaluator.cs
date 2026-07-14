using System.Text.Json;

namespace CompliDrop.Api.RuleEngine;

/// <summary>Three-valued (Kleene) logic value. Order is deliberate: False &lt; Unknown &lt; True.</summary>
public enum Kleene
{
    False,
    Unknown,
    True
}

/// <summary>
/// The result of evaluating an applicability tree: the Kleene verdict plus, when it is
/// <see cref="Kleene.Unknown"/>, the set of unset facts that are LOAD-BEARING for that verdict — i.e. the
/// questions whose answers could still change it. A decided branch (an <c>all</c> with a False child, an
/// <c>any</c> with a True child) contributes NO missing facts, because no further answer matters there.
/// </summary>
public readonly record struct ApplicabilityResult(Kleene Value, IReadOnlySet<string> MissingFacts);

/// <summary>
/// Evaluates an <see cref="Applicability"/> tree against an <see cref="EntityProfile"/> with three-valued
/// Kleene logic (SCHEMA §4). An unset fact makes its leaf Unknown — never False — so the engine can emit
/// <c>needs-profile-info</c> rather than silently dropping an obligation or asserting compliance from
/// ignorance. Pure: no clock, no I/O.
///
/// Propagation:
///   all: any child False ⇒ False; else any child Unknown ⇒ Unknown; else True. (empty ⇒ True)
///   any: any child True  ⇒ True;  else any child Unknown ⇒ Unknown; else False. (empty ⇒ False)
///   not: swaps True/False; Unknown stays Unknown.
/// </summary>
public static class ApplicabilityEvaluator
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    /// <summary>Full evaluation: Kleene verdict + the load-bearing missing facts when Unknown.</summary>
    public static ApplicabilityResult Evaluate(Applicability node, EntityProfile profile) => node switch
    {
        LeafCondition leaf => EvaluateLeaf(leaf, profile),
        NotCondition not => Negate(Evaluate(not.Inner, profile)),
        AllCondition all => EvaluateAll(all.Conditions, profile),
        AnyCondition any => EvaluateAny(any.Conditions, profile),
        _ => throw new ArgumentOutOfRangeException(nameof(node), $"Unknown applicability node {node.GetType().Name}."),
    };

    /// <summary>Convenience: just the Kleene verdict.</summary>
    public static Kleene EvaluateValue(Applicability node, EntityProfile profile) => Evaluate(node, profile).Value;

    private static ApplicabilityResult EvaluateLeaf(LeafCondition leaf, EntityProfile profile)
    {
        if (!profile.TryGet(leaf.Fact, out var value))
            return new ApplicabilityResult(Kleene.Unknown, new HashSet<string> { leaf.Fact });

        var passed = LeafComparer.Compare(value, leaf.Op, leaf.Value);
        return new ApplicabilityResult(passed ? Kleene.True : Kleene.False, None);
    }

    private static ApplicabilityResult EvaluateAll(IReadOnlyList<Applicability> children, EntityProfile profile)
    {
        HashSet<string>? missing = null;
        var anyUnknown = false;
        foreach (var child in children)
        {
            var r = Evaluate(child, profile);
            if (r.Value == Kleene.False)
                return new ApplicabilityResult(Kleene.False, None); // decided: no answer can rescue it
            if (r.Value == Kleene.Unknown)
            {
                anyUnknown = true;
                (missing ??= new HashSet<string>()).UnionWith(r.MissingFacts);
            }
        }
        return anyUnknown
            ? new ApplicabilityResult(Kleene.Unknown, missing!)
            : new ApplicabilityResult(Kleene.True, None);
    }

    private static ApplicabilityResult EvaluateAny(IReadOnlyList<Applicability> children, EntityProfile profile)
    {
        HashSet<string>? missing = null;
        var anyUnknown = false;
        foreach (var child in children)
        {
            var r = Evaluate(child, profile);
            if (r.Value == Kleene.True)
                return new ApplicabilityResult(Kleene.True, None); // decided
            if (r.Value == Kleene.Unknown)
            {
                anyUnknown = true;
                (missing ??= new HashSet<string>()).UnionWith(r.MissingFacts);
            }
        }
        return anyUnknown
            ? new ApplicabilityResult(Kleene.Unknown, missing!)
            : new ApplicabilityResult(Kleene.False, None);
    }

    private static ApplicabilityResult Negate(ApplicabilityResult inner) => inner.Value switch
    {
        Kleene.True => new ApplicabilityResult(Kleene.False, None),
        Kleene.False => new ApplicabilityResult(Kleene.True, None),
        _ => new ApplicabilityResult(Kleene.Unknown, inner.MissingFacts), // Unknown stays; its missing facts still gate it
    };
}

/// <summary>
/// Type-aware comparison of a set <see cref="FactValue"/> against a leaf's operator and JSON literal.
/// The loader validates operator/type compatibility up front, so at evaluation time this can assume the
/// value shapes line up; anything genuinely mismatched (e.g. a hand-built tree) fails closed to <c>false</c>
/// rather than throwing — including <c>neq</c>, which only negates a COMPARABLE pair (a naive
/// <c>!ScalarEquals</c> would fail OPEN to true on a type mismatch). String equality is
/// ordinal-case-insensitive, matching the rest of the codebase.
/// </summary>
internal static class LeafComparer
{
    public static bool Compare(FactValue fact, ConditionOp op, JsonElement value) => op switch
    {
        ConditionOp.Eq => ScalarEquals(fact, value),
        ConditionOp.Neq => ScalarComparable(fact, value) && !ScalarEquals(fact, value),
        ConditionOp.Gte => TryNumeric(fact, value, out var f, out var v) && f >= v,
        ConditionOp.Lte => TryNumeric(fact, value, out var f, out var v) && f <= v,
        ConditionOp.In => InArray(fact, value),
        _ => false,
    };

    private static bool ScalarComparable(FactValue fact, JsonElement value) => fact.Kind switch
    {
        FactKind.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        FactKind.Int => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        FactKind.String => value.ValueKind == JsonValueKind.String,
        _ => false,
    };

    private static bool ScalarEquals(FactValue fact, JsonElement value) => fact.Kind switch
    {
        FactKind.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False && fact.AsBool == value.GetBoolean(),
        FactKind.Int => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && fact.AsInt == n,
        FactKind.String => value.ValueKind == JsonValueKind.String
            && string.Equals(fact.AsString, value.GetString(), StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool TryNumeric(FactValue fact, JsonElement value, out long factValue, out long literal)
    {
        factValue = 0;
        literal = 0;
        if (fact.Kind != FactKind.Int || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out literal))
            return false;
        factValue = fact.AsInt;
        return true;
    }

    private static bool InArray(FactValue fact, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var element in value.EnumerateArray())
            if (ScalarEquals(fact, element))
                return true;
        return false;
    }
}
