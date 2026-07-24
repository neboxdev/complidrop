using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// The extracted fields whose value BACKS a compliance verdict — a coverage limit, an effective/
/// expiration date, or the additional-insured party. These are the fields a single mis-read silently
/// flips a verdict on, so <see cref="BackgroundServices.ExtractionWorker"/> routes a document to
/// <see cref="ExtractionStatus.ManualRequired"/> when ANY of them comes back below the confidence gate —
/// even if the FIELD AVERAGE clears it (#401 / ADR 0042). Averaging a lone 0.3-confidence
/// <c>expiration_date</c> among a dozen 0.95 incidental fields hides exactly the value that decides the
/// verdict, and for a compliance product a verdict the machine itself distrusts must not silently roll up
/// to "Covered".
///
/// The names are DERIVED from the codebase so the set matches what extraction actually emits
/// (<see cref="Extraction.ExtractionPrompts"/> COI field list) and what the rule catalog grades
/// (<c>frontend/src/lib/requirements.ts</c> fieldNames / <see cref="ComplianceCheckService.LookupValue"/>):
/// the two typed date columns + the general-liability typed column reuse the
/// <see cref="CanonicalDocumentFields"/> constants so the spelling lives in ONE place; the remaining
/// coverage-limit + flag fields are the <c>min_value</c> / <c>required</c> / <c>contains</c> rule fields.
///
/// Scope, deliberately: universal DATES + INSURANCE COVERAGE — the fields the ticket names ("a limit, an
/// expiration, the additional-insured party"). The license/certification IDENTITY fields
/// (<c>license_number</c>, <c>license_type</c>, <c>certification_number</c>/<c>_name</c>) are OUT: their
/// date requirement is already covered by <c>expiration_date</c>, and gating every identity field would
/// drag much of the license/permit corpus into manual review for little verdict-safety gain.
/// <c>general_liability_limit</c> already IS the each-occurrence reading (the prompt reads that ACORD 25
/// cell into it), so there is no separate occurrence/aggregate field name to gate.
/// </summary>
internal static class VerdictBearingFields
{
    /// <summary>
    /// The verdict-bearing field names, in ONE collection (never scattered literals). Matched
    /// case-insensitively — extraction emits lower snake_case, but the whole compliance path compares
    /// field names case-insensitively (see <see cref="CanonicalDocumentFields.IsCanonical"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CanonicalDocumentFields.EffectiveDate,          // effective_date  — future-effective overlay (ADR 0041)
        CanonicalDocumentFields.ExpirationDate,         // expiration_date — Expired/ExpiringSoon + the `required` date rule
        CanonicalDocumentFields.GeneralLiabilityLimit,  // general_liability_limit — min_value
        "auto_liability_limit",                         // min_value
        "professional_liability_limit",                 // min_value (E&O)
        "umbrella_limit",                               // min_value
        "liquor_liability_limit",                       // min_value
        "workers_comp_limit",                           // required
        "additional_insured",                           // contains
    };

    /// <summary>True when <paramref name="fieldName"/> is one whose value backs a compliance verdict.</summary>
    public static bool Contains(string? fieldName) => fieldName is not null && All.Contains(fieldName);
}
