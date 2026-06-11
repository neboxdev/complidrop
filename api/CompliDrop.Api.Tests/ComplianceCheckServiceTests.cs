using CompliDrop.Api.Auth;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for <see cref="ComplianceCheckService"/>'s DB-coupled orchestration:
/// expiration, the 30-day expiring-soon window, document-type scoping, status aggregation,
/// ComplianceCheck persistence, and tenant scoping. Runs on the Testcontainers Postgres harness.
/// A fixed clock is injected so the date boundaries are deterministic (no wall-clock flake).
/// </summary>
public sealed class ComplianceCheckServiceTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Seed expirations relative to the fixed instant (noon UTC), NOT midnight, so the date the
    // service compares (exp.Date) cannot be shifted across a day boundary by any timezone skew in
    // the Postgres timestamptz round-trip. The service's "today" derives from the same FixedNow.
    private static DateTime Anchor => FixedNow.UtcDateTime; // 2026-06-01T12:00:00Z

    private readonly Guid _orgId = Guid.NewGuid();

    /// <summary>Seeds org + (optional template/rules) + vendor + document; returns the document id.</summary>
    private async Task<Guid> SeedAsync(
        DateTime? expiration = null,
        string docType = "coi",
        decimal? glLimit = null,
        params (string docType, string field, string op, string? expected)[] rules)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        Guid? vendorTemplateId = null;

        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = _orgId, Name = $"Org-{_orgId:N}", CreatedAt = now, UpdatedAt = now });

        if (rules.Length > 0)
        {
            var templateId = Guid.NewGuid();
            db.ComplianceTemplates.Add(new ComplianceTemplate
            {
                Id = templateId, OrganizationId = _orgId, Name = "T", CreatedAt = now
            });
            var order = 0;
            foreach (var r in rules)
            {
                db.ComplianceRules.Add(new ComplianceRule
                {
                    Id = Guid.NewGuid(),
                    ComplianceTemplateId = templateId,
                    DocumentType = r.docType,
                    FieldName = r.field,
                    Operator = r.op,
                    ExpectedValue = r.expected,
                    SortOrder = order++
                });
            }
            vendorTemplateId = templateId;
        }

        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = _orgId, Name = "V",
            ComplianceTemplateId = vendorTemplateId, CreatedAt = now, UpdatedAt = now
        });

        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = _orgId, VendorId = vendorId,
            OriginalFileName = "d.pdf", BlobStorageUrl = "blob://d",
            FileSizeBytes = 1, ContentType = "application/pdf",
            DocumentType = docType, ExpirationDate = expiration, GeneralLiabilityLimit = glLimit,
            CreatedAt = now, UpdatedAt = now
        });

        await db.SaveChangesAsync();
        return docId;
    }

    private async Task<ComplianceStatus> EvaluateForSystem(Guid documentId)
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgId };
        await using var appDb = CreateAppDb(user);
        await using var sysDb = CreateSystemDb();
        return await new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow))
            .EvaluateForSystemAsync(documentId, default);
    }

    // ---------------- expiration boundaries (deterministic via the fixed clock) ----------------

    [Fact]
    public async Task Expired_document_is_marked_Expired()
    {
        var id = await SeedAsync(expiration: Anchor.AddDays(-1));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public async Task Document_expiring_exactly_today_is_ExpiringSoon_not_Expired()
    {
        // The service uses a strict `<` for Expired, so a doc expiring TODAY is not yet expired.
        var id = await SeedAsync(expiration: Anchor);

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public async Task Document_expiring_within_30_days_is_ExpiringSoon()
    {
        var id = await SeedAsync(expiration: Anchor.AddDays(10));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public async Task Document_expiring_in_exactly_30_days_is_ExpiringSoon()
    {
        var id = await SeedAsync(expiration: Anchor.AddDays(30));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public async Task Document_expiring_in_31_days_is_not_ExpiringSoon()
    {
        var id = await SeedAsync(expiration: Anchor.AddDays(31));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public async Task Document_with_no_template_is_Pending()
    {
        var id = await SeedAsync(expiration: Anchor.AddDays(365));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.Pending);
    }

    // ---------------- rule evaluation + status aggregation ----------------

    [Fact]
    public async Task All_rules_passing_yields_Compliant_and_persists_a_check()
    {
        var id = await SeedAsync(
            expiration: Anchor.AddDays(365),
            docType: "coi", glLimit: 2000000m,
            rules: ("coi", "general_liability_limit", "min_value", "1000000"));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.Compliant);

        await using var db = CreateSystemDb();
        var checks = await db.ComplianceChecks.Where(c => c.DocumentId == id).ToListAsync();
        checks.Should().ContainSingle().Which.IsPassed.Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_rule_yields_NonCompliant()
    {
        var id = await SeedAsync(
            expiration: Anchor.AddDays(365),
            docType: "coi", glLimit: 500000m,
            rules: ("coi", "general_liability_limit", "min_value", "1000000"));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task Rules_for_other_document_types_are_skipped()
    {
        // Document is a 'license'; the only rule targets 'coi' → no applicable rules → vacuously Compliant.
        var id = await SeedAsync(
            expiration: Anchor.AddDays(365),
            docType: "license",
            rules: ("coi", "general_liability_limit", "min_value", "1000000"));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.Compliant);

        await using var db = CreateSystemDb();
        (await db.ComplianceChecks.CountAsync(c => c.DocumentId == id)).Should().Be(0);
    }

    [Fact]
    public async Task Expiring_soon_with_passing_rules_stays_ExpiringSoon()
    {
        var id = await SeedAsync(
            expiration: Anchor.AddDays(10),
            docType: "coi", glLimit: 2000000m,
            rules: ("coi", "general_liability_limit", "min_value", "1000000"));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public async Task Expiring_soon_with_a_failing_rule_is_NonCompliant()
    {
        var id = await SeedAsync(
            expiration: Anchor.AddDays(10),
            docType: "coi", glLimit: 500000m,
            rules: ("coi", "general_liability_limit", "min_value", "1000000"));

        (await EvaluateForSystem(id)).Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task Re_evaluation_replaces_previous_checks()
    {
        var id = await SeedAsync(
            expiration: Anchor.AddDays(365),
            docType: "coi", glLimit: 2000000m,
            rules: ("coi", "general_liability_limit", "min_value", "1000000"));

        await EvaluateForSystem(id);
        await EvaluateForSystem(id); // second run must not duplicate check rows

        await using var db = CreateSystemDb();
        (await db.ComplianceChecks.CountAsync(c => c.DocumentId == id)).Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_is_tenant_scoped()
    {
        var id = await SeedAsync(expiration: Anchor.AddDays(365));

        // A different org's tenant context cannot see the document → not found → Pending.
        var otherOrgUser = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
        await using var appDb = CreateAppDb(otherOrgUser);
        await using var sysDb = CreateSystemDb();

        var status = await new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow))
            .EvaluateAsync(id, default);

        status.Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public async Task Foreign_org_template_is_ignored_by_the_system_evaluation_path()
    {
        // #273 defense-in-depth: the assignment-time guard in VendorEndpoints blocks NEW
        // cross-org template ids, but a Vendor row poisoned BEFORE that guard deployed still
        // carries the foreign FK — and the system path (ExtractionWorker → EvaluateForSystemAsync,
        // SystemDbContext, no tenant filter) would load the foreign template and write its rule
        // names/expected values into this org's visible ComplianceCheck rows. The evaluation
        // must treat such a template as absent and clear any previously-leaked rows.
        var id = await SeedAsync(expiration: Anchor.AddDays(365), docType: "coi", glLimit: 500000m);

        var now = DateTime.UtcNow;
        var foreignOrgId = Guid.NewGuid();
        var foreignTemplateId = Guid.NewGuid();
        var foreignRuleId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.Organizations.Add(new Organization { Id = foreignOrgId, Name = "Foreign Org", CreatedAt = now, UpdatedAt = now });
            db.ComplianceTemplates.Add(new ComplianceTemplate
            {
                Id = foreignTemplateId, OrganizationId = foreignOrgId, Name = "Foreign secret checklist", CreatedAt = now
            });
            db.ComplianceRules.Add(new ComplianceRule
            {
                Id = foreignRuleId,
                ComplianceTemplateId = foreignTemplateId,
                DocumentType = "coi",
                FieldName = "general_liability_limit",
                Operator = "min_value",
                ExpectedValue = "5000000",
                ErrorMessage = "Foreign org's secret threshold",
                SortOrder = 1
            });
            // Persist the foreign org/template/rule BEFORE the ExecuteUpdate below — it
            // executes immediately and the FK needs the template row on disk.
            await db.SaveChangesAsync();

            // Poison the vendor with the foreign template (the pre-guard legacy shape) and seed
            // one already-leaked check row from a hypothetical earlier evaluation.
            var doc = await db.Documents.SingleAsync(d => d.Id == id);
            await db.Vendors.Where(v => v.Id == doc.VendorId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ComplianceTemplateId, foreignTemplateId));
            db.ComplianceChecks.Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(),
                DocumentId = id,
                ComplianceRuleId = foreignRuleId,
                IsPassed = false,
                ActualValue = "500000",
                Notes = "leaked",
                CheckedAt = now
            });
            await db.SaveChangesAsync();
        }

        var status = await EvaluateForSystem(id);

        // Foreign rules never execute (the failing min_value rule would yield NonCompliant);
        // the poisoned row self-heals: no-governing-rules branch → Pending, leaked rows cleared.
        status.Should().Be(ComplianceStatus.Pending);
        await using var verify = CreateSystemDb();
        (await verify.ComplianceChecks.AnyAsync(c => c.DocumentId == id)).Should().BeFalse(
            "previously-leaked foreign-rule check rows must be cleared on re-evaluation");
    }
}
