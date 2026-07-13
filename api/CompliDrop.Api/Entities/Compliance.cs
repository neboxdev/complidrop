namespace CompliDrop.Api.Entities;

public class ComplianceTemplate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemTemplate { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Seed re-grade durability watermark (#416, ADR 0036 Amendment 2). Bookkeeping only — NOT rule
    // content. RulesRevision is bumped by ComplianceTemplateSeed.EnsureAsync whenever convergence changes
    // this SYSTEM template's rule set (any rule add / delete / value / message / sort change). After a
    // successful cross-org re-grade of the template's documents, RegradedThroughRevision is advanced to
    // match. The seed re-grades every system template whose RulesRevision has outrun its
    // RegradedThroughRevision, so an interrupted boot (SIGTERM / startup timeout / a caught-and-skipped
    // re-grade page) re-fires on the NEXT boot until the documents actually catch up — the convergence
    // commits the corrected rules and the re-grade separately, and this watermark is what keeps a stale
    // persisted verdict from surviving forever. Tenant clones never converge, so their watermark stays 0/0.
    public int RulesRevision { get; set; }
    public int RegradedThroughRevision { get; set; }

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

    public Document Document { get; set; } = null!;
    public ComplianceRule ComplianceRule { get; set; } = null!;
}
