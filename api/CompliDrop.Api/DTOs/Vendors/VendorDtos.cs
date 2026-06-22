namespace CompliDrop.Api.DTOs.Vendors;

public record VendorSummary(
    Guid Id,
    string Name,
    string? ContactEmail,
    string? ContactPhone,
    string? Category,
    Guid? ComplianceTemplateId,
    string? ComplianceTemplateName,
    int DocumentCount,
    int ActivePortalLinks,
    // True for the demo's sample vendor (#238) so the vendors list can badge it "Sample".
    bool IsSample,
    // Per-vendor coverage rollup so the list can answer "who is NOT ok?" (#319 FP-074).
    VendorCoverage Coverage,
    // null when deliverable, else "bounced" / "complained" — so the list can badge a vendor whose
    // ContactEmail the reminder engine has stopped sending to (#340).
    string? ContactEmailStatus);

/// <summary>
/// Whether a vendor's documents currently satisfy its assigned checklist, rolled up across the
/// distinct document types its rules require (#319 FP-074). Computed server-side in the single
/// ListVendors projection — never per-vendor round trips. <see cref="Status"/> is one of:
/// <c>NoRequirements</c> (no checklist / no rules), <c>Missing</c> (a required document type has no
/// document — <see cref="MissingTypes"/> names them), <c>ActionNeeded</c> (a required type's latest
/// doc is effectively Expired / NonCompliant / not-yet-graded), or <c>Covered</c> (every required
/// type's latest doc is effectively Compliant or ExpiringSoon).
/// </summary>
public record VendorCoverage(string Status, string[] MissingTypes);

public record VendorDetail(
    Guid Id,
    string Name,
    string? ContactEmail,
    string? ContactPhone,
    string? Category,
    Guid? ComplianceTemplateId,
    string? ComplianceTemplateName,
    PortalLinkDto[] PortalLinks,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // Same per-vendor coverage rollup as the list (#319 FP-074), so the detail page can show the
    // verdict badge from one server-side source of truth (shared ComputeCoverage).
    VendorCoverage Coverage,
    // null when deliverable, else "bounced" / "complained" — the reminder engine has stopped sending to
    // this ContactEmail (#340), so the detail page surfaces it ("reminders paused").
    string? ContactEmailStatus);

public record PortalLinkDto(
    Guid Id,
    string Token,
    string FullUrl,
    bool IsActive,
    int UploadCount,
    int MaxUploads,
    DateTime? ExpiresAt,
    DateTime CreatedAt);

public record VendorUpsertRequest(
    string Name,
    string? ContactEmail,
    string? ContactPhone,
    string? Category,
    Guid? ComplianceTemplateId);
