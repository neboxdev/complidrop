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

    /// <summary>
    /// The vendor's regulatory entity type (SCHEMA §4 <c>entityType</c> value space: caterer,
    /// event-rental, security-service, transportation, photographer-videographer). Null = unknown —
    /// the rule engine emits needs-profile-info for type-scoped rules, never a wrong verdict. Distinct
    /// from <see cref="Category"/> (a free-text display label predating the rule engine).
    /// </summary>
    public string? EntityType { get; set; }

    /// <summary>
    /// The vendor's regulatory entity-profile facts (SCHEMA §4 registry), as a flat JSON map of
    /// factName → bool|int|string. An absent key is UNKNOWN (Kleene) — never false. Read via
    /// <c>RegulatoryProfileMapper</c>.
    /// </summary>
    public System.Text.Json.JsonDocument? RegulatoryFactsJson { get; set; }

    /// <summary>
    /// True only for the sample vendor created by the one-click demo (#238). The demo seeds this
    /// vendor, assigns it the "Caterer" system checklist, and attaches the generated sample COI so a
    /// fresh org reaches a real verdict with no file on hand. "Clear sample data" removes it.
    /// </summary>
    public bool IsSample { get; set; } = false;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

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
    public int MaxUploads { get; set; } = 20;

    public Vendor Vendor { get; set; } = null!;
}
