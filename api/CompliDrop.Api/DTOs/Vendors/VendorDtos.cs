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
    int ActivePortalLinks);

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
    DateTime UpdatedAt);

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
