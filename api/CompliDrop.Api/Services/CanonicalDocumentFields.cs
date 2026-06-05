using System.Globalization;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

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
    /// Maps one field <paramref name="fieldName"/>/<paramref name="value"/> onto the document's
    /// typed column when the name is one of the canonical three; a no-op for any other name.
    ///
    /// A value that fails to parse <b>clears the column to null</b> rather than leaving the prior
    /// value, so the typed column can never silently contradict the field the user sees: when the
    /// column is null, <see cref="ComplianceCheckService.LookupValue"/> falls back to the raw string
    /// in <see cref="Document.ExtractionFields"/>. This matters for manual corrections; for the
    /// extraction pipeline a <i>first</i> extraction starts from null columns, so clear-on-failure
    /// and the previous leave-unchanged behavior coincide. On <i>re-extraction</i> the columns may
    /// already hold the prior read's values (<c>Reextract</c> resets only status/processing fields),
    /// so clear-on-failure intentionally overwrites a now-unparseable prior value rather than leaving
    /// it stale — the desired last-write-wins behavior per ADR 0017, not a regression.
    ///
    /// Dates parse as UTC (<see cref="DateTimeStyles.AssumeUniversal"/> +
    /// <see cref="DateTimeStyles.AdjustToUniversal"/> ⇒ <see cref="DateTimeKind.Utc"/>) so the value
    /// is safe to persist to the <c>timestamptz</c> column without a separate
    /// <see cref="DateTime.SpecifyKind"/> step. Amounts parse with
    /// <see cref="NumberStyles.Any"/> + <see cref="CultureInfo.InvariantCulture"/> (bare digits and
    /// grouped thousands like <c>1,500,000</c>).
    /// </summary>
    public static void ApplyToTypedColumn(Document doc, string fieldName, string? value)
    {
        if (string.Equals(fieldName, EffectiveDate, StringComparison.OrdinalIgnoreCase))
            doc.EffectiveDate = ParseUtcDate(value);
        else if (string.Equals(fieldName, ExpirationDate, StringComparison.OrdinalIgnoreCase))
            doc.ExpirationDate = ParseUtcDate(value);
        else if (string.Equals(fieldName, GeneralLiabilityLimit, StringComparison.OrdinalIgnoreCase))
            doc.GeneralLiabilityLimit = ParseDecimal(value);
    }

    private static DateTime? ParseUtcDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
