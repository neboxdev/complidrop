namespace CompliDrop.Api.Entities;

public class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? CompanySize { get; set; }
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>
    /// The US state the org operates in, for the regulatory rule engine's jurisdiction selection
    /// (e.g. "US-TX"). Null = unknown — the engine then evaluates federal rules only and surfaces the
    /// state as an outstanding profile question; it NEVER assumes a state (SCHEMA §3).
    /// </summary>
    public string? State { get; set; }

    /// <summary>
    /// The org's own regulatory entity-profile facts (SCHEMA §4 registry), as a flat JSON map of
    /// factName → bool|int|string (e.g. employeeCount, servesOrSellsAlcohol). An absent key is UNKNOWN
    /// (Kleene) — never false. Read via <c>RegulatoryProfileMapper</c>; the org itself evaluates as the
    /// "venue-org" entity type.
    /// </summary>
    public System.Text.Json.JsonDocument? RegulatoryFactsJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<User> Users { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<Vendor> Vendors { get; set; } = [];
    public ICollection<ComplianceTemplate> ComplianceTemplates { get; set; } = [];
    public ICollection<Reminder> Reminders { get; set; } = [];
    public Subscription? Subscription { get; set; }
}
