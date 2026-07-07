using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// A node in the applicability condition tree (SCHEMA §4). A closed language over the frozen entity-fact
/// registry: the combinators <c>all</c> / <c>any</c> / <c>not</c> and a leaf <c>{fact, op, value}</c>.
/// Evaluated with three-valued (Kleene) logic by <see cref="ApplicabilityEvaluator"/>. The JSON shape is
/// polymorphic — exactly one of <c>all</c>, <c>any</c>, <c>not</c>, or a leaf per object — so it carries a
/// custom converter. Use the <see cref="Condition"/> factory to build trees in code.
/// </summary>
[JsonConverter(typeof(ApplicabilityJsonConverter))]
public abstract record Applicability;

/// <summary>Conjunction. Kleene: any-False ⇒ False; else any-Unknown ⇒ Unknown; else True. Empty ⇒ True.</summary>
public sealed record AllCondition(IReadOnlyList<Applicability> Conditions) : Applicability;

/// <summary>Disjunction. Kleene: any-True ⇒ True; else any-Unknown ⇒ Unknown; else False. Empty ⇒ False.</summary>
public sealed record AnyCondition(IReadOnlyList<Applicability> Conditions) : Applicability;

/// <summary>Negation. Kleene: swaps True/False; Unknown stays Unknown.</summary>
public sealed record NotCondition(Applicability Inner) : Applicability;

/// <summary>
/// A leaf test <c>{fact, op, value}</c> (SCHEMA §4). <see cref="Value"/> is the raw JSON literal (scalar
/// for eq/neq/gte/lte, array for in) — kept as a <see cref="JsonElement"/> so comparison is type-aware.
/// An UNSET fact makes this leaf evaluate to Unknown, never False (Kleene).
/// </summary>
public sealed record LeafCondition(string Fact, ConditionOp Op, JsonElement Value) : Applicability;

/// <summary>
/// Ergonomic constructors for building applicability trees in code (tests, and any caller that isn't
/// deserializing JSON). Handles wrapping primitive values into <see cref="JsonElement"/>.
/// </summary>
public static class Condition
{
    public static AllCondition All(params Applicability[] conditions) => new(conditions);
    public static AnyCondition Any(params Applicability[] conditions) => new(conditions);
    public static NotCondition Not(Applicability inner) => new(inner);

    public static LeafCondition Leaf(string fact, ConditionOp op, bool value) => new(fact, op, Element(value));
    public static LeafCondition Leaf(string fact, ConditionOp op, long value) => new(fact, op, Element(value));
    public static LeafCondition Leaf(string fact, ConditionOp op, int value) => new(fact, op, Element((long)value));
    public static LeafCondition Leaf(string fact, ConditionOp op, string value) => new(fact, op, Element(value));

    /// <summary>An <c>in</c> leaf over a set of string values.</summary>
    public static LeafCondition In(string fact, params string[] values) => new(fact, ConditionOp.In, Element(values));

    /// <summary>An <c>in</c> leaf over a set of integer values.</summary>
    public static LeafCondition In(string fact, params long[] values) => new(fact, ConditionOp.In, Element(values));

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
}

/// <summary>
/// Reads/writes the polymorphic applicability JSON. Enforces the structural invariant at parse time:
/// each object is EXACTLY one of <c>all</c> (array), <c>any</c> (array), <c>not</c> (object), or a leaf
/// (<c>fact</c> + <c>op</c> + <c>value</c>). Anything else — two combinators, a leaf missing its op, an
/// unknown op — is a <see cref="JsonException"/>, which the loader surfaces as a schema violation.
/// </summary>
internal sealed class ApplicabilityJsonConverter : JsonConverter<Applicability>
{
    public override Applicability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return Build(doc.RootElement, options);
    }

    private static Applicability Build(JsonElement el, JsonSerializerOptions options)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new JsonException($"An applicability node must be a JSON object, got {el.ValueKind}.");

        var hasAll = el.TryGetProperty("all", out var allEl);
        var hasAny = el.TryGetProperty("any", out var anyEl);
        var hasNot = el.TryGetProperty("not", out var notEl);
        var hasFact = el.TryGetProperty("fact", out var factEl);

        var forms = (hasAll ? 1 : 0) + (hasAny ? 1 : 0) + (hasNot ? 1 : 0) + (hasFact ? 1 : 0);
        if (forms != 1)
            throw new JsonException(
                "An applicability node must have EXACTLY one of 'all', 'any', 'not', or a leaf 'fact'. " +
                $"Found {forms} of them in: {el.GetRawText()}");

        if (hasAll) return new AllCondition(BuildChildren(allEl, "all", options));
        if (hasAny) return new AnyCondition(BuildChildren(anyEl, "any", options));
        if (hasNot)
        {
            if (notEl.ValueKind != JsonValueKind.Object)
                throw new JsonException($"'not' must be a single applicability object, got {notEl.ValueKind}.");
            return new NotCondition(Build(notEl, options));
        }

        // Leaf.
        var fact = factEl.GetString()
            ?? throw new JsonException("A leaf 'fact' must be a non-null string.");
        if (!el.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
            throw new JsonException($"Leaf for fact '{fact}' is missing a string 'op'.");
        if (!RuleTokens.TryParseOp(opEl.GetString(), out var op))
            throw new JsonException($"Leaf for fact '{fact}' has unknown op '{opEl.GetString()}'. Expected eq|neq|gte|lte|in.");
        if (!el.TryGetProperty("value", out var valueEl))
            throw new JsonException($"Leaf for fact '{fact}' is missing 'value'.");

        return new LeafCondition(fact, op, valueEl.Clone());
    }

    private static List<Applicability> BuildChildren(JsonElement arrayEl, string combinator, JsonSerializerOptions options)
    {
        if (arrayEl.ValueKind != JsonValueKind.Array)
            throw new JsonException($"'{combinator}' must be an array of applicability nodes, got {arrayEl.ValueKind}.");
        var children = new List<Applicability>();
        foreach (var child in arrayEl.EnumerateArray())
            children.Add(Build(child, options));
        return children;
    }

    public override void Write(Utf8JsonWriter writer, Applicability value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case AllCondition all:
                writer.WriteStartObject();
                writer.WritePropertyName("all");
                WriteArray(writer, all.Conditions, options);
                writer.WriteEndObject();
                break;
            case AnyCondition any:
                writer.WriteStartObject();
                writer.WritePropertyName("any");
                WriteArray(writer, any.Conditions, options);
                writer.WriteEndObject();
                break;
            case NotCondition not:
                writer.WriteStartObject();
                writer.WritePropertyName("not");
                Write(writer, not.Inner, options);
                writer.WriteEndObject();
                break;
            case LeafCondition leaf:
                writer.WriteStartObject();
                writer.WriteString("fact", leaf.Fact);
                writer.WriteString("op", RuleTokens.ToToken(leaf.Op));
                writer.WritePropertyName("value");
                leaf.Value.WriteTo(writer);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unknown applicability node type {value.GetType().Name}.");
        }
    }

    private void WriteArray(Utf8JsonWriter writer, IReadOnlyList<Applicability> conditions, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var c in conditions) Write(writer, c, options);
        writer.WriteEndArray();
    }
}
