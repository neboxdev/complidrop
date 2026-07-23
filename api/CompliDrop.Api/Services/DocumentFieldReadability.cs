using System.Globalization;
using System.Text.Json;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// Reads a <see cref="Document"/>'s field values, and judges whether a canonical one is READABLE
/// (#383, ADR 0040). "Does this document carry a value we could not read?" is a cross-layer question
/// about document STATE, not about one rule evaluation, so it lives in its own static class in the
/// <c>Services/</c> namespace rather than hanging off the DI-registered
/// <see cref="ComplianceCheckService"/> — same shape as <see cref="DocumentSupersession"/> and
/// <see cref="PlanDocumentScope"/>, and it keeps <c>DocumentEndpoints</c> from compiling against the
/// concrete compliance implementation instead of <see cref="IComplianceCheckService"/> just to ask it
/// (#383 review round 2, S4).
///
/// Callers:
/// <list type="bullet">
///   <item><c>ComplianceCheckService.EvaluateRule</c> — the fail-closed guard ahead of the operator switch.</item>
///   <item><c>ComplianceCheckService.LookupValue</c> — the narrowed raw-string fallback.</item>
///   <item><c>DocumentEndpoints.ResolveManualReview</c> — re-raises <see cref="ExtractionStatus.ManualRequired"/>.</item>
///   <item><c>DocumentEndpoints.GetDocument</c> — <c>DocumentDetail.UnreadableFields</c>, so the detail page
///         can name the offending field instead of pointing at a confidence outline that isn't there.</item>
/// </list>
/// </summary>
internal static class DocumentFieldReadability
{
    /// <summary>The canonical field's typed column rendered as the string a rule compares, or null when unset.</summary>
    internal static string? TypedColumnValue(Document doc, string fieldName)
    {
        if (string.Equals(fieldName, CanonicalDocumentFields.ExpirationDate, StringComparison.OrdinalIgnoreCase))
            return doc.ExpirationDate?.ToString("yyyy-MM-dd");
        if (string.Equals(fieldName, CanonicalDocumentFields.EffectiveDate, StringComparison.OrdinalIgnoreCase))
            return doc.EffectiveDate?.ToString("yyyy-MM-dd");
        if (string.Equals(fieldName, CanonicalDocumentFields.GeneralLiabilityLimit, StringComparison.OrdinalIgnoreCase))
            return doc.GeneralLiabilityLimit?.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>The field's value straight out of the <see cref="Document.ExtractionFields"/> JSON, unparsed.</summary>
    internal static string? RawFieldValue(Document doc, string fieldName)
    {
        if (doc.ExtractionFields?.RootElement.ValueKind == JsonValueKind.Object
            && doc.ExtractionFields.RootElement.TryGetProperty(fieldName, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                // A JSON null is an ABSENCE, and must read as one on BOTH sides (#383 review).
                // FieldUpdateRequest.FieldValue is string?, so PUT /fields with `fieldValue: null`
                // stores a JSON null in ExtractionFields — and the generic GetRawText() arm below
                // returned the literal 4-character string "null" for it. That made the reader
                // disagree with the writer about the very same edit: ApplyToTypedColumn classified
                // it Blank (honestly absent), while IsUnreadable saw non-blank text it could not
                // parse and reported the value UNREADABLE — stamping a check row with the
                // "we couldn't read this" note and ActualValue "null", which the detail page then
                // showed the user verbatim. Sticky, too: the JSON null persists, so every later
                // evaluation re-read "null". ADR 0040 is explicit that Blank stays Blank. Mapping it
                // here also closes the pre-existing fail-open on NON-canonical fields, where
                // `required` was satisfied by that same 4-character string.
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText()
            };
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="fieldName"/> is a canonical field whose typed column is null while
    /// the document DOES carry a raw value for it that cannot be parsed — the #383 state where a
    /// null column means "unreadable", not "absent". Yields the raw text so the check row can show the
    /// user exactly what is on the document. <see cref="CanonicalDocumentFields.ApplyToTypedColumn"/>
    /// is the only writer of the three columns, so a null column with an unparseable raw value is
    /// precisely the value it refused.
    /// </summary>
    internal static bool TryGetUnreadableValue(Document doc, string? fieldName, out string? raw)
    {
        raw = null;
        if (string.IsNullOrWhiteSpace(fieldName) || !CanonicalDocumentFields.IsCanonical(fieldName)) return false;
        if (TypedColumnValue(doc, fieldName) is not null) return false;
        raw = RawFieldValue(doc, fieldName);
        return CanonicalDocumentFields.IsUnreadable(fieldName, raw);
    }

    /// <summary>
    /// Every canonical field the document CURRENTLY carries an unreadable value for, in
    /// <see cref="CanonicalDocumentFields.All"/> order. Empty for a healthy document.
    ///
    /// Asked of the document's own state rather than of one request's field list on purpose
    /// (#383 review): <c>DocumentEndpoints.ResolveManualReview</c> must be able to tell whether the
    /// review it is about to clear is actually resolved, and a request that never mentions
    /// <c>expiration_date</c> — including the deliberately-empty save the detail page offers while
    /// the review card is up — says nothing about whether the stored expiration is readable.
    ///
    /// Also the payload the detail page needs: <see cref="ExtractionStatus.ManualRequired"/> raised
    /// for THIS reason has no low-confidence field to outline (an unreadable value is typically read
    /// with high confidence, and a manual edit pins its confidence to 1.0), so the UI must be told
    /// WHICH field to point at or the user is told to fix something nothing marks.
    /// </summary>
    internal static string[] UnreadableCanonicalFields(Document doc)
    {
        List<string>? found = null;
        foreach (var field in CanonicalDocumentFields.All)
            if (TryGetUnreadableValue(doc, field, out _))
                (found ??= new List<string>(CanonicalDocumentFields.All.Length)).Add(field);
        return found?.ToArray() ?? [];
    }

    /// <summary>
    /// True when the document CURRENTLY carries an unreadable value on any of the canonical three —
    /// i.e. it is in the #383 state whatever request or extraction put it there. Defined in terms of
    /// <see cref="UnreadableCanonicalFields"/> rather than as a second short-circuiting walk, so the
    /// predicate that gates the review flag and the list the UI renders can never disagree.
    /// </summary>
    internal static bool HasUnreadableCanonicalValue(Document doc) =>
        UnreadableCanonicalFields(doc).Length > 0;
}
