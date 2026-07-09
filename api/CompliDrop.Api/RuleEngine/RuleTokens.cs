using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompliDrop.Api.RuleEngine;

/// <summary>
/// The exact string tokens the JSON rule files use for the closed enum sets (SCHEMA §2/§4/§5), and the
/// parse/format helpers that keep the on-disk vocabulary in ONE place. Hyphenated / camelCase tokens
/// (e.g. "worker-certification", "fixed-annual", "documentExpiration") don't map to enum member names,
/// so the mapping is explicit rather than relying on <see cref="JsonStringEnumConverter"/>. Case-insensitive.
/// </summary>
public static class RuleTokens
{
    private static readonly Dictionary<string, ConditionOp> Ops = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = ConditionOp.Eq,
        ["neq"] = ConditionOp.Neq,
        ["gte"] = ConditionOp.Gte,
        ["lte"] = ConditionOp.Lte,
        ["in"] = ConditionOp.In,
    };

    private static readonly Dictionary<string, CadenceKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["renewal"] = CadenceKind.Renewal,
        ["fixed-annual"] = CadenceKind.FixedAnnual,
        ["one-time"] = CadenceKind.OneTime,
        ["conditional-filing"] = CadenceKind.ConditionalFiling,
    };

    private static readonly Dictionary<string, CadenceAnchor> Anchors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["documentExpiration"] = CadenceAnchor.DocumentExpiration,
        ["issueDate"] = CadenceAnchor.IssueDate,
        ["calendarDate"] = CadenceAnchor.CalendarDate,
        ["fixedDate"] = CadenceAnchor.FixedDate,
    };

    private static readonly Dictionary<string, RuleConfidence> Confidences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["verified"] = RuleConfidence.Verified,
        ["probable"] = RuleConfidence.Probable,
        ["uncertain"] = RuleConfidence.Uncertain,
    };

    private static readonly Dictionary<string, InsuranceFloorKind> FloorKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["combined-single-limit"] = InsuranceFloorKind.CombinedSingleLimit,
        ["split-limits"] = InsuranceFloorKind.SplitLimits,
    };

    private static readonly Dictionary<string, InsuranceCoverageLine> CoverageLines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general-liability"] = InsuranceCoverageLine.GeneralLiability,
        ["auto-liability"] = InsuranceCoverageLine.AutoLiability,
    };

    public static bool TryParseOp(string? token, out ConditionOp value) => TryParse(Ops, token, out value);
    public static bool TryParseKind(string? token, out CadenceKind value) => TryParse(Kinds, token, out value);
    public static bool TryParseAnchor(string? token, out CadenceAnchor value) => TryParse(Anchors, token, out value);
    public static bool TryParseConfidence(string? token, out RuleConfidence value) => TryParse(Confidences, token, out value);
    public static bool TryParseFloorKind(string? token, out InsuranceFloorKind value) => TryParse(FloorKinds, token, out value);
    public static bool TryParseCoverageLine(string? token, out InsuranceCoverageLine value) => TryParse(CoverageLines, token, out value);

    public static string ToToken(ConditionOp op) => op switch
    {
        ConditionOp.Eq => "eq",
        ConditionOp.Neq => "neq",
        ConditionOp.Gte => "gte",
        ConditionOp.Lte => "lte",
        ConditionOp.In => "in",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    public static string ToToken(CadenceKind kind) => kind switch
    {
        CadenceKind.Renewal => "renewal",
        CadenceKind.FixedAnnual => "fixed-annual",
        CadenceKind.OneTime => "one-time",
        CadenceKind.ConditionalFiling => "conditional-filing",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string ToToken(CadenceAnchor anchor) => anchor switch
    {
        CadenceAnchor.DocumentExpiration => "documentExpiration",
        CadenceAnchor.IssueDate => "issueDate",
        CadenceAnchor.CalendarDate => "calendarDate",
        CadenceAnchor.FixedDate => "fixedDate",
        _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
    };

    public static string ToToken(RuleConfidence confidence) => confidence switch
    {
        RuleConfidence.Verified => "verified",
        RuleConfidence.Probable => "probable",
        RuleConfidence.Uncertain => "uncertain",
        _ => throw new ArgumentOutOfRangeException(nameof(confidence)),
    };

    public static string ToToken(InsuranceFloorKind kind) => kind switch
    {
        InsuranceFloorKind.CombinedSingleLimit => "combined-single-limit",
        InsuranceFloorKind.SplitLimits => "split-limits",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string ToToken(InsuranceCoverageLine line) => line switch
    {
        InsuranceCoverageLine.GeneralLiability => "general-liability",
        InsuranceCoverageLine.AutoLiability => "auto-liability",
        _ => throw new ArgumentOutOfRangeException(nameof(line)),
    };

    private static bool TryParse<T>(Dictionary<string, T> map, string? token, out T value)
    {
        if (token is not null && map.TryGetValue(token.Trim(), out var v))
        {
            value = v;
            return true;
        }
        value = default!;
        return false;
    }
}

internal sealed class CadenceKindJsonConverter : JsonConverter<CadenceKind>
{
    public override CadenceKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!RuleTokens.TryParseKind(s, out var v))
            throw new JsonException($"Unknown cadence kind '{s}'. Expected one of: renewal, fixed-annual, one-time, conditional-filing.");
        return v;
    }

    public override void Write(Utf8JsonWriter writer, CadenceKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RuleTokens.ToToken(value));
}

internal sealed class CadenceAnchorJsonConverter : JsonConverter<CadenceAnchor>
{
    public override CadenceAnchor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!RuleTokens.TryParseAnchor(s, out var v))
            throw new JsonException($"Unknown cadence anchor '{s}'. Expected one of: documentExpiration, issueDate, calendarDate, fixedDate.");
        return v;
    }

    public override void Write(Utf8JsonWriter writer, CadenceAnchor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RuleTokens.ToToken(value));
}

internal sealed class RuleConfidenceJsonConverter : JsonConverter<RuleConfidence>
{
    public override RuleConfidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!RuleTokens.TryParseConfidence(s, out var v))
            throw new JsonException($"Unknown confidence '{s}'. Expected one of: verified, probable, uncertain.");
        return v;
    }

    public override void Write(Utf8JsonWriter writer, RuleConfidence value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RuleTokens.ToToken(value));
}

internal sealed class InsuranceFloorKindJsonConverter : JsonConverter<InsuranceFloorKind>
{
    public override InsuranceFloorKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!RuleTokens.TryParseFloorKind(s, out var v))
            throw new JsonException($"Unknown insurance floor kind '{s}'. Expected one of: combined-single-limit, split-limits.");
        return v;
    }

    public override void Write(Utf8JsonWriter writer, InsuranceFloorKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RuleTokens.ToToken(value));
}

internal sealed class InsuranceCoverageLineJsonConverter : JsonConverter<InsuranceCoverageLine>
{
    public override InsuranceCoverageLine Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (!RuleTokens.TryParseCoverageLine(s, out var v))
            throw new JsonException($"Unknown insurance coverage line '{s}'. Expected one of: general-liability, auto-liability.");
        return v;
    }

    public override void Write(Utf8JsonWriter writer, InsuranceCoverageLine value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RuleTokens.ToToken(value));
}
