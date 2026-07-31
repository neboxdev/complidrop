using CompliDrop.Api.DTOs.Compliance;

namespace CompliDrop.Api.DTOs.Documents;

public record DocumentListItem(
    Guid Id,
    string OriginalFileName,
    string DocumentType,
    string? VendorName,
    Guid? VendorId,
    string ExtractionStatus,
    double? ExtractionConfidence,
    string ComplianceStatus,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    int? DaysUntilExpiry,
    // True for the sample-certificate demo document (#238) so the list can badge it "Sample".
    bool IsSample,
    DateTime CreatedAt);

public record DocumentDetail(
    Guid Id,
    string OriginalFileName,
    string DocumentType,
    string? DocumentSubType,
    string? VendorName,
    // The vendor's saved contact email, surfaced so the detail page can offer a
    // one-click "Email {vendor} to fix this" mailto pre-filled with the failed
    // requirements. Null when the vendor is unassigned or has no email. (#193)
    string? VendorContactEmail,
    Guid? VendorId,
    // Whether the assigned vendor has a requirements checklist at all (#443 / ADR 0048 §4).
    // The detail page's "Not checked yet" card NAMES the reason a document reads Pending, and
    // since #443 zero ComplianceCheck rows has FOUR causes, not two: no vendor, a vendor with no
    // checklist, an EMPTY checklist, or a checklist whose rules all govern OTHER document types
    // (reachable today — ComputeOutcome's applicable-rules filter compares DocumentType
    // case-SENSITIVELY). The page cannot tell those apart from complianceChecks.length, and
    // guessing "no checklist" printed a FALSE claim plus a dead-end CTA over a vendor that plainly
    // has one, while the vendor rollup simultaneously read ActionNeeded against that same
    // checklist. The backend already knows, so it says. False when no vendor is assigned (or the
    // vendor was soft-deleted).
    bool VendorHasChecklist,
    // How many requirements that checklist actually holds (#443 review S3). VendorHasChecklist
    // alone is too COARSE for the taxonomy the card names: a vendor assigned an EMPTY checklist
    // has one, so it fell into the "nothing on it applies to this type" arm, whose copy asserts the
    // checklist HAS requirements that govern other types — a claim about the customer's own data
    // that is simply false. The two facts together separate all four causes; neither alone does
    // ("count > 0" cannot tell an empty checklist from no checklist). 0 when no vendor, no
    // checklist, an empty checklist, or a checklist whose template was soft-deleted (which reads
    // as an empty one — the honest remedy is the same: put requirements on this vendor).
    int VendorChecklistRuleCount,
    string ExtractionStatus,
    double? ExtractionConfidence,
    string ComplianceStatus,
    DateTime? EffectiveDate,
    DateTime? ExpirationDate,
    int? DaysUntilExpiry,
    bool IsManuallyVerified,
    string? UploadedBy,
    // True for the sample-certificate demo document (#238) so the detail page can show the
    // "Sample" banner + one-click "Clear sample data".
    bool IsSample,
    // No BlobStorageUrl: the raw Azure URI is private (PublicAccessType.None) and clicking it
    // always 409'd — clients view the file through GET /api/documents/{id}/file instead (#254).
    decimal? GeneralLiabilityLimit,
    DocumentFieldDto[] Fields,
    // The per-requirement evaluation rows (passed + failed) so the detail page
    // can explain non-compliance in plain English. Empty when the document has
    // no requirement set or hasn't been evaluated yet. (#193)
    ComplianceCheckDto[] ComplianceChecks,
    // The canonical field names (effective_date / expiration_date /
    // general_liability_limit) whose stored value is non-blank but unreadable —
    // the #383 / ADR 0040 state. Empty for a healthy document.
    //
    // The detail page NEEDS this to explain a ManualRequired document it cannot
    // otherwise account for. Before #383 the only cause of that status was low
    // average confidence, so the review card told the user to fix "the ones
    // outlined in amber" and the outline came from fieldBorderClass(confidence).
    // An unreadable value is typically read with HIGH confidence (and a manual
    // edit pins the field's confidence to 1.0), so nothing gets outlined and the
    // card points at a marker that isn't there — a dead end, since the backend
    // correctly refuses to clear the flag while the value stays unreadable.
    //
    // Sourced from the SAME DocumentFieldReadability walk over
    // CanonicalDocumentFields.All that raises the flag, never re-derived on the
    // client: a second copy of "can this parse?" in TypeScript would drift from
    // the .NET parse it is supposed to mirror, and drift here means the UI
    // marks the wrong field or none at all. (#383 review round 2)
    string[] UnreadableFields,
    object? ExtractionFields,
    string? ExtractionPromptVersion,
    string? ProcessingError,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record DocumentFieldDto(
    Guid Id,
    string FieldName,
    string? FieldValue,
    string? FieldType,
    double Confidence,
    bool IsManuallyEdited,
    string? OriginalValue);

public record FieldUpdateRequest(string FieldName, string? FieldValue);

public record FieldsUpdateRequest(FieldUpdateRequest[] Fields);

/// <summary>
/// Partial update for a document's vendor assignment and/or declared type.
/// Both fields are optional (PATCH semantics):
///   - <see cref="VendorId"/> non-null  → (re)assign the document to that vendor.
///   - <see cref="DocumentType"/> non-empty → set the declared type.
/// A null/absent field is left unchanged. Unassigning a vendor (setting it back
/// to null) is intentionally not supported here — the whole point of #186 is to
/// keep every document associated with a vendor, never to re-orphan it.
/// </summary>
public record DocumentPatchRequest(Guid? VendorId, string? DocumentType);
