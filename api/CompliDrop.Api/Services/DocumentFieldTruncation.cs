using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// Judges whether a <see cref="DocumentField"/> row shows a CLIPPED copy of the value the document
/// actually holds (#444). The sibling of <see cref="DocumentFieldReadability"/>: both answer a
/// cross-layer question about document STATE from the two copies the system already keeps, and both
/// live as a dedicated static class in <c>Services/</c> — the <see cref="DocumentSupersession"/> /
/// <see cref="PlanDocumentScope"/> / <see cref="DocumentGrading"/> shape.
/// <para/>
/// <b>The gap this closes.</b> <c>ExtractionWorker.Clamp</c> truncates <see cref="DocumentField.FieldValue"/>
/// at <see cref="InputLengths.DocumentFieldValue"/> with NO marker (ADR 0045 §4 — truncate-not-drop is
/// deliberate and unchanged here). The document detail page binds its editable input to that clamped
/// value, so a clipped <c>description_of_operations</c> is shown as if it were the whole extracted
/// value — and <c>DocumentEndpoints.UpdateFields</c> writes the submitted text back into
/// <c>Document.ExtractionFields</c>, the CANONICAL verdict input. Saving that field therefore narrows
/// the canonical record to the clipped copy, and <c>description_of_operations</c> IS a verdict input
/// (the additional-insured <c>contains</c> fallback in <c>ComplianceCheckService.EvaluateRule</c>, whose
/// match routinely lands on an ACORD 101 continuation past character 2000). The narrowing is
/// fail-CLOSED — removing text can only turn a <c>contains</c> pass into a fail — so this is a
/// disclosure defect, not a safety hole, and #444 changes DISCLOSURE only.
/// <para/>
/// <b>Derived at read time, never persisted.</b> The system already keeps both copies —
/// <c>Document.ExtractionFields</c> (jsonb, no width, written from the FULL value) and the clamped
/// <c>DocumentField.FieldValue</c> — so the flag is a comparison, not a column: no migration, and it
/// SELF-CLEARS correctly. Once a user saves the field, <c>UpdateFields</c> writes the submitted text
/// into BOTH (and REJECTS an over-length one per ADR 0046, so what it writes always fits), the two
/// agree, and the flag goes false because the record now genuinely holds what is shown.
/// <para/>
/// <b>Reproduces the clamp rather than guessing at it.</b> The predicate asks whether the stored row is
/// EXACTLY <see cref="ColumnClamp.To"/> of the full value — the same helper
/// <c>ExtractionWorker.Clamp</c> delegates to, so the surrogate back-off (a cut at
/// <c>maxLength - 1</c>) is honoured for free and a widened column moves both sides at once. A mere
/// "they differ" test would fire on any legitimate divergence; a length test alone would miss that the
/// back-off makes a clipped value 1999 characters long. The two shapes that legitimately differ WITHOUT
/// being a clip both read false: a JSON <c>null</c> (an ABSENCE on both sides —
/// <see cref="DocumentFieldReadability.RawFieldValue"/> maps it to <c>null</c>, ADR 0040), and the
/// second row of a duplicate field name (the worker writes one row per extracted field but only the
/// LAST value into the JSON mirror, so an earlier row matches the clamp only when its first
/// <see cref="InputLengths.DocumentFieldValue"/> characters are identical — in which case the row IS
/// clipped and the flag is right anyway).
/// </summary>
internal static class DocumentFieldTruncation
{
    /// <summary>
    /// True when <paramref name="field"/> displays only the first
    /// <see cref="InputLengths.DocumentFieldValue"/> characters of a longer value the document still
    /// holds in <see cref="Document.ExtractionFields"/>. False for every healthy field, and false once
    /// a manual save has made the two copies agree.
    /// </summary>
    internal static bool ValueWasClipped(Document doc, DocumentField field)
    {
        if (field.FieldValue is null) return false;

        // The JSON mirror is keyed by the field's FULL name while DocumentField.FieldName is itself
        // clamped to InputLengths.DocumentFieldName — so a >200-character name (off-spec provider noise)
        // misses the lookup and reads false. That is the safe direction: no hint, exactly today's
        // behaviour, rather than a hint pointing at a value we cannot line up.
        var full = DocumentFieldReadability.RawFieldValue(doc, field.FieldName);
        if (full is null || full.Length <= InputLengths.DocumentFieldValue) return false;

        return string.Equals(
            field.FieldValue,
            ColumnClamp.To(full, InputLengths.DocumentFieldValue),
            StringComparison.Ordinal);
    }
}
