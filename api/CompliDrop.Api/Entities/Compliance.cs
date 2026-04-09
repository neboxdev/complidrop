namespace CompliDrop.Api.Entities;

public class ComplianceTemplate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemTemplate { get; set; } = false;
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Organization Organization { get; set; } = null!;
    public ICollection<ComplianceRule> Rules { get; set; } = [];
    public ICollection<Vendor> Vendors { get; set; } = [];
}

public class ComplianceRule
{
    public Guid Id { get; set; }
    public Guid ComplianceTemplateId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string? FieldName { get; set; }
    public string Operator { get; set; } = string.Empty;
    public string? ExpectedValue { get; set; }
    public string? ErrorMessage { get; set; }
    public int SortOrder { get; set; }

    // Navigation
    public ComplianceTemplate ComplianceTemplate { get; set; } = null!;
}

public class ComplianceCheck
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ComplianceRuleId { get; set; }
    public bool IsPassed { get; set; }
    public string? ActualValue { get; set; }
    public string? Notes { get; set; }
    public DateTime CheckedAt { get; set; }

    // Navigation
    public Document Document { get; set; } = null!;
    public ComplianceRule ComplianceRule { get; set; } = null!;
}
