namespace CompliDrop.Api.Entities;

public class Vendor
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Category { get; set; }
    public Guid? ComplianceTemplateId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organization Organization { get; set; } = null!;
    public ComplianceTemplate? ComplianceTemplate { get; set; }
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<VendorPortalLink> PortalLinks { get; set; } = [];
}

public class VendorPortalLink
{
    public Guid Id { get; set; }
    public Guid VendorId { get; set; }
    public string Token { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int UploadCount { get; set; } = 0;

    // Navigation
    public Vendor Vendor { get; set; } = null!;
}
