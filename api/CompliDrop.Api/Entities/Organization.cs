namespace CompliDrop.Api.Entities;

public class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? CompanySize { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public ICollection<User> Users { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<Vendor> Vendors { get; set; } = [];
    public ICollection<ComplianceTemplate> ComplianceTemplates { get; set; } = [];
    public ICollection<Reminder> Reminders { get; set; } = [];
    public Subscription? Subscription { get; set; }
}
