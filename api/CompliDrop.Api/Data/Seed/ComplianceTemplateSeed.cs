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
    /// Name of the system venue-type checklist the sample-certificate demo (#238) assigns to its
    /// sample vendor. The generated sample COI is built to PASS exactly this checklist's rules
    /// (GL ≥ $1M each-occurrence, expiration date present, workers-comp coverage present, and
    /// liquor liability ≥ $1M — the Caterer checklist now covers bar / alcohol service, #400), so
    /// the demo lands a fresh org on a real "Compliant" verdict. Kept here as the single source so
    /// the seed below and the sample-seed endpoint can never drift on the name.
    /// </summary>
    internal const string SampleVendorTemplateName = "Caterer";

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

        // Load already-seeded system templates WITH their rules so a re-run can additively
        // reconcile MISSING rules onto them — not just insert whole new templates. Seeding was
        // historically insert-only (skip a template whose Name already exists), so a rule added
        // to a template definition here would be INERT in prod: the system Caterer / Security
        // Service rows already exist, so they would never gain the new rule (#400). The reconcile
        // is ADDITIVE ONLY — it never edits or deletes an existing rule (system rows are shared
        // across every org, and a tenant's evaluation may rely on the exact stored wording).
        var existingTemplates = await db.ComplianceTemplates.IgnoreQueryFilters()
            .Where(t => t.IsSystemTemplate)
            .Include(t => t.Rules)
            .ToListAsync(ct);
        var byName = existingTemplates.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var tpl in Templates)
        {
            if (byName.TryGetValue(tpl.Name, out var live))
            {
                // Back-fill any seed rule the live template lacks, keyed on the
                // (DocumentType, FieldName, Operator) natural key the rules endpoints also dedupe
                // on. Each seed template's rules are distinct on that key, so a re-run adds at most
                // the genuinely-new rules and is a no-op once they are present (idempotent) — the
                // back-fill self-limits to the ONE first boot after a rule is added to the seed.
                // No cross-instance guard: the deploy model boots a single instance through this
                // seed (the old instance already seeded at its own boot and does not re-run), and a
                // rule's natural key is unique per template, so a repeat run never rewrites — it only
                // skips. (A concurrent double-boot is the sole unguarded window; add a unique index
                // on the rule natural key if that ever needs closing.)
                foreach (var rule in tpl.Rules)
                {
                    var present = live.Rules.Any(r => RuleMatches(r, rule));
                    if (!present)
                        db.ComplianceRules.Add(NewRule(live.Id, rule));
                }
                continue;
            }

            // New template: insert it and all of its rules.
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
                db.ComplianceRules.Add(NewRule(templateId, rule));
        }
        await db.SaveChangesAsync(ct);
    }

    // The (DocumentType, FieldName, Operator) natural key — mirrors the dedupe key the compliance
    // rules endpoints enforce (ComplianceEndpoints.UpsertRule). Case-insensitive and null-safe.
    private static bool RuleMatches(ComplianceRule live, RuleSeed seed) =>
        string.Equals(live.DocumentType, seed.DocumentType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(live.FieldName, seed.FieldName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(live.Operator, seed.Operator, StringComparison.OrdinalIgnoreCase);

    private static ComplianceRule NewRule(Guid templateId, RuleSeed rule) => new()
    {
        Id = Guid.NewGuid(),
        ComplianceTemplateId = templateId,
        DocumentType = rule.DocumentType,
        FieldName = rule.FieldName,
        Operator = rule.Operator,
        ExpectedValue = rule.ExpectedValue,
        ErrorMessage = rule.ErrorMessage,
        SortOrder = rule.SortOrder
    };

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
        new(SampleVendorTemplateName,
            "Typical insurance for a food & beverage caterer, including bar / alcohol service.",
            [
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000.", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2),
                new("coi", "workers_comp_limit", "required", null, "Workers comp coverage is required.", 3),
                // Bar / alcohol service is the classic private-event (dram-shop / social-host)
                // exposure that general liability excludes — a bar-service caterer graded "Covered"
                // with no liquor liability is exactly the gap #400 was filed for. A food-only
                // caterer failing this is safe friction: the owner removes it from their own clone.
                new("coi", "liquor_liability_limit", "min_value", "1000000", "Liquor liability must be at least $1,000,000.", 4)
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
            "Licensing plus general-liability insurance for event security and guard services.",
            [
                new("license", "license_number", "required", null, "License number is required.", 1),
                new("license", "expiration_date", "required", null, "License expiration date is required.", 2),
                new("certification", "expiration_date", "required", null, "Certification expiration date is required.", 3),
                // Guard / security vendors carry assault-and-battery exposure; a licence alone
                // insures nothing. Require general liability like every other insured category
                // — a Security Service checklist that asks for NO insurance was the #400 gap.
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000.", 4)
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
