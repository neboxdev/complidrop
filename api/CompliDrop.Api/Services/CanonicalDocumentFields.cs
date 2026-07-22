using System.Globalization;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// What <see cref="CanonicalDocumentFields.ApplyToTypedColumn"/> did with one field. The
/// <see cref="Unreadable"/> case is the whole point of this type (#383): a canonical field whose raw
/// value is NON-BLANK but unparseable clears its typed column to null, which every downstream date /
/// amount check then reads as "the document has no such value" — the OPPOSITE compliance meaning.
/// Returning the outcome lets each writer flag the document for a human instead of silently
/// swallowing a value it could not read.
/// </summary>
internal enum TypedColumnResult
{
    /// <summary>Not one of the canonical three — no typed column was touched.</summary>
    NotCanonical,
    /// <summary>Canonical field with an absent/blank value: the column is legitimately null.</summary>
    Blank,
    /// <summary>Canonical field whose value parsed into the column.</summary>
    Parsed,
    /// <summary>
    /// Canonical field with a NON-BLANK value the parser could not read; the column was cleared to
    /// null. The caller MUST surface this (see <c>ExtractionWorker.PersistSuccess</c> and
    /// <c>DocumentEndpoints.UpdateFields</c>, which both degrade the document to
    /// <see cref="ExtractionStatus.ManualRequired"/>).
    /// </summary>
    Unreadable,
}

/// <summary>
/// The three extracted fields that <see cref="ComplianceCheckService.LookupValue"/> reads from
/// typed <see cref="Document"/> columns (<see cref="Document.EffectiveDate"/>,
/// <see cref="Document.ExpirationDate"/>, <see cref="Document.GeneralLiabilityLimit"/>) rather than
/// from the <see cref="Document.ExtractionFields"/> JSON. Both write paths funnel through here so a
/// field value parses to its column identically whether it arrives from the extraction pipeline
/// (<c>ExtractionWorker.PersistSuccess</c>) or from a manual correction
/// (<c>DocumentEndpoints.UpdateFields</c>). See
/// <see href="../../../docs/adr/0017-manual-field-edits-sync-compliance-inputs.md">ADR 0017</see>
/// for why a manual edit must reach these columns and for the re-extraction-vs-manual-edit
/// precedence. Exposed as <c>internal</c> for direct unit testing via
/// <c>InternalsVisibleTo CompliDrop.Api.Tests</c>.
/// </summary>
internal static class CanonicalDocumentFields
{
    public const string EffectiveDate = "effective_date";
    public const string ExpirationDate = "expiration_date";
    public const string GeneralLiabilityLimit = "general_liability_limit";

    /// <summary>
    /// The three canonical field names, so a caller that must inspect ALL of them
    /// (<see cref="ComplianceCheckService.HasUnreadableCanonicalValue"/>) enumerates one list rather
    /// than re-spelling the trio — a fourth copy would be the thing that drifts.
    /// </summary>
    public static readonly string[] All = [EffectiveDate, ExpirationDate, GeneralLiabilityLimit];

    /// <summary>
    /// Maps one field <paramref name="fieldName"/>/<paramref name="value"/> onto the document's
    /// typed column when the name is one of the canonical three; a no-op for any other name.
    /// Returns what happened so the caller can react to the <see cref="TypedColumnResult.Unreadable"/>
    /// case (#383).
    ///
    /// A value that fails to parse <b>clears the column to null</b> rather than leaving the prior
    /// value, so the typed column can never silently contradict the field the user sees. This matters
    /// for manual corrections; for the extraction pipeline a <i>first</i> extraction starts from null
    /// columns, so clear-on-failure and the previous leave-unchanged behavior coincide. On
    /// <i>re-extraction</i> the columns may already hold the prior read's values (<c>Reextract</c>
    /// resets only status/processing fields), so clear-on-failure intentionally overwrites a
    /// now-unparseable prior value rather than leaving it stale — the desired last-write-wins behavior
    /// per ADR 0017, not a regression.
    ///
    /// <b>But a cleared column is NOT the same fact as an absent one</b> (#383, ADR 0040): "no
    /// expiration date on this certificate" and "an expiration date we could not read" have opposite
    /// compliance meanings, and the null column erases the difference. That is why this returns
    /// <see cref="TypedColumnResult.Unreadable"/> for a non-blank value that failed to parse, and why
    /// <see cref="ComplianceCheckService"/> refuses to satisfy a rule from such a value.
    ///
    /// Dates parse as UTC (<see cref="DateTimeStyles.AssumeUniversal"/> +
    /// <see cref="DateTimeStyles.AdjustToUniversal"/> ⇒ <see cref="DateTimeKind.Utc"/>) so the value
    /// is safe to persist to the <c>timestamptz</c> column without a separate
    /// <see cref="DateTime.SpecifyKind"/> step. Amounts parse with
    /// <see cref="NumberStyles.Any"/> + <see cref="CultureInfo.InvariantCulture"/> after currency
    /// symbols are stripped (bare digits, grouped thousands like <c>1,500,000</c>, and
    /// <c>$1,000,000</c> — see <see cref="TryParseAmount"/>).
    /// </summary>
    public static TypedColumnResult ApplyToTypedColumn(Document doc, string fieldName, string? value)
    {
        if (string.Equals(fieldName, EffectiveDate, StringComparison.OrdinalIgnoreCase))
        {
            doc.EffectiveDate = ParseUtcDate(value);
            return Classify(value, doc.EffectiveDate is not null);
        }
        if (string.Equals(fieldName, ExpirationDate, StringComparison.OrdinalIgnoreCase))
        {
            doc.ExpirationDate = ParseUtcDate(value);
            return Classify(value, doc.ExpirationDate is not null);
        }
        if (string.Equals(fieldName, GeneralLiabilityLimit, StringComparison.OrdinalIgnoreCase))
        {
            doc.GeneralLiabilityLimit = ParseAmount(value);
            return Classify(value, doc.GeneralLiabilityLimit is not null);
        }
        return TypedColumnResult.NotCanonical;
    }

    private static TypedColumnResult Classify(string? value, bool parsed) =>
        parsed ? TypedColumnResult.Parsed
        : string.IsNullOrWhiteSpace(value) ? TypedColumnResult.Blank
        : TypedColumnResult.Unreadable;

    /// <summary>True when <paramref name="fieldName"/> is one of the three typed-column fields.</summary>
    public static bool IsCanonical(string? fieldName) =>
        IsDateField(fieldName) || IsAmountField(fieldName);

    private static bool IsDateField(string? fieldName) =>
        string.Equals(fieldName, EffectiveDate, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fieldName, ExpirationDate, StringComparison.OrdinalIgnoreCase);

    private static bool IsAmountField(string? fieldName) =>
        string.Equals(fieldName, GeneralLiabilityLimit, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="value"/> is a NON-BLANK value for a canonical field that
    /// <see cref="ApplyToTypedColumn"/> cannot parse — i.e. exactly the case where the typed column
    /// ends up null while the document really does carry a value (#383). Blank values are NOT
    /// unreadable (they are honestly absent), and a non-canonical field is never unreadable here
    /// (it has no typed column to contradict). Pure and allocation-free so
    /// <see cref="ComplianceCheckService"/> can call it on every rule evaluation.
    /// </summary>
    public static bool IsUnreadable(string? fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (IsDateField(fieldName)) return ParseUtcDate(value) is null;
        if (IsAmountField(fieldName)) return ParseAmount(value) is null;
        return false;
    }

    private static DateTime? ParseUtcDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static decimal? ParseAmount(string? value) =>
        TryParseAmount(value, out var parsed) ? parsed : null;

    /// <summary>
    /// The one money parse for the whole compliance path: strips edge currency symbols and
    /// whitespace, then parses with <see cref="NumberStyles.Any"/> +
    /// <see cref="CultureInfo.InvariantCulture"/>.
    ///
    /// The strip is load-bearing (#383, secondary): <c>NumberStyles.Any</c> includes
    /// <see cref="NumberStyles.AllowCurrencySymbol"/>, but the symbol it allows is the *invariant*
    /// culture's <c>¤</c> — NOT <c>$</c>. So <c>"$1,000,000"</c>, the most natural way for a model or
    /// a user to write a coverage limit, failed to parse and nulled
    /// <see cref="Document.GeneralLiabilityLimit"/>. Used for BOTH sides of the <c>min_value</c>
    /// comparison so a rule whose expected value was typed as <c>"$1,000,000"</c> reads the same as
    /// the document's.
    ///
    /// Edges only, and a linear scan: a symbol in the MIDDLE (<c>"1,000,000 USD 2M"</c>) still fails,
    /// which is the safe direction — it becomes <see cref="TypedColumnResult.Unreadable"/> and a human
    /// looks at it.
    /// </summary>
    public static bool TryParseAmount(string? value, out decimal amount)
    {
        amount = 0m;
        if (value is null) return false;
        return decimal.TryParse(
            TrimCurrency(value.AsSpan()), NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }

    private static ReadOnlySpan<char> TrimCurrency(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsCurrencyOrSpace(value[start])) start++;
        while (end > start && IsCurrencyOrSpace(value[end - 1])) end--;
        return value[start..end];
    }

    private static bool IsCurrencyOrSpace(char c) =>
        char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol;
}
