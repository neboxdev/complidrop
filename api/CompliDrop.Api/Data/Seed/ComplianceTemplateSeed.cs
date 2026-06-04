using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Data.Seed;

public static class ComplianceTemplateSeed
{
    /// <summary>
    /// Identity of the synthetic organization that owns all <c>IsSystemTemplate=true</c>
    /// templates. Exposed to the test project (<see cref="InternalsVisibleToAttribute"/>) so the
    /// integration-test harness can target it directly in custom wipe SQL — keeps the magic
    /// constant in one place.
    /// </summary>
    internal static readonly Guid SystemOrgId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Number of system templates this seed installs. Exposed to the test project
    /// (<see cref="InternalsVisibleToAttribute"/>) so harness assertions can pin the exact count
    /// without re-declaring a brittle hand-mirror constant — adding a template here forces the
    /// test to be updated (or, if the count is read indirectly, doesn't break it at all).
    /// </summary>
    internal static int TemplateCount => Templates.Length;

    public static async Task EnsureAsync(SystemDbContext db, CancellationToken ct = default)
    {
        var org = await db.Organizations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == SystemOrgId, ct);
        if (org is null)
        {
            org = new Organization
            {
                Id = SystemOrgId,
                Name = "CompliDrop System",
                Industry = "system",
                CompanySize = "n/a",
                TimeZone = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Organizations.Add(org);
            await db.SaveChangesAsync(ct);
        }

        var existing = await db.ComplianceTemplates.IgnoreQueryFilters()
            .Where(t => t.IsSystemTemplate)
            .Select(t => t.Name)
            .ToListAsync(ct);

        foreach (var tpl in Templates)
        {
            if (existing.Contains(tpl.Name)) continue;

            var templateId = Guid.NewGuid();
            db.ComplianceTemplates.Add(new ComplianceTemplate
            {
                Id = templateId,
                OrganizationId = SystemOrgId,
                Name = tpl.Name,
                Description = tpl.Description,
                IsSystemTemplate = true,
                CreatedAt = DateTime.UtcNow
            });
            foreach (var rule in tpl.Rules)
            {
                db.ComplianceRules.Add(new ComplianceRule
                {
                    Id = Guid.NewGuid(),
                    ComplianceTemplateId = templateId,
                    DocumentType = rule.DocumentType,
                    FieldName = rule.FieldName,
                    Operator = rule.Operator,
                    ExpectedValue = rule.ExpectedValue,
                    ErrorMessage = rule.ErrorMessage,
                    SortOrder = rule.SortOrder
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private record RuleSeed(
        string DocumentType,
        string? FieldName,
        string Operator,
        string? ExpectedValue,
        string ErrorMessage,
        int SortOrder);

    private record TemplateSeed(string Name, string Description, RuleSeed[] Rules);

    // Event-venue vendor types (#192) — the suggested checklists a venue manager
    // recognises from her vendor packet. Renamed from the old generic trade names
    // by the RenameSystemTemplatesToVenueTypes migration so existing databases
    // pick up the new names without duplicating. Keep the (name → rules) pairing
    // in lockstep with that migration's renames.
    private static readonly TemplateSeed[] Templates =
    [
        new("Caterer",
            "Typical insurance for a food & beverage caterer.",
            [
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000.", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2),
                new("coi", "workers_comp_limit", "required", null, "Workers comp coverage is required.", 3)
            ]),
        new("Event Rental Company",
            "Coverage for table, tent, and equipment rental vendors.",
            [
                // The "names your venue as additional insured" requirement is intentionally
                // NOT seeded: a venue names ITSELF, so the value is per-tenant — the user adds
                // it (with their own venue name) after cloning. A one-size placeholder reads
                // nonsensically. (#192 review.)
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000.", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2)
            ]),
        new("Security Service",
            "Licensing for event security and guard services.",
            [
                new("license", "license_number", "required", null, "License number is required.", 1),
                new("license", "expiration_date", "required", null, "License expiration date is required.", 2),
                new("certification", "expiration_date", "required", null, "Certification expiration date is required.", 3)
            ]),
        new("Transportation / Shuttle",
            "Auto coverage and a CDL for shuttle and transport vendors.",
            [
                new("coi", "auto_liability_limit", "min_value", "1000000", "Auto liability must be at least $1,000,000.", 1),
                new("license", "license_type", "equals", "CDL", "Driver must hold a CDL.", 2),
                new("license", "expiration_date", "required", null, "License expiration date is required.", 3)
            ]),
        new("Photographer / Videographer",
            "General + professional (E&O) coverage for photo and video vendors.",
            [
                new("coi", "general_liability_limit", "min_value", "500000", "General liability must be at least $500,000.", 1),
                new("coi", "professional_liability_limit", "min_value", "1000000", "Professional liability (E&O) must be at least $1,000,000.", 2),
                new("license", "expiration_date", "required", null, "Professional license expiration date is required.", 3)
            ])
    ];
}
