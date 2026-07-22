using System.Globalization;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the BATCHED compliance re-evaluation fan-out (#293): a rule/template
/// mutation must re-grade every affected document — across an arbitrarily large vendor base — while
/// paging the work so it is no longer one-document-at-a-time on the request thread. Asserts the
/// outcome (every doc gets its own correct verdict, checks are replaced not appended, and the tenant
/// filter scopes the fan-out) rather than the round-trip count. Runs on the Testcontainers harness
/// with a fixed clock so the date overlay never interferes with the rule-driven verdict.
/// </summary>
public sealed class ComplianceFanoutTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Far-future so no document is expired or expiring-soon — the verdict is purely rule-driven.
    private static readonly DateTime FarFuture = new(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private const decimal RequiredGl = 2_000_000m;

    private async Task SeedOrgWithTemplateAsync(Guid orgId, Guid templateId)
    {
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
        db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = orgId, Name = "T", CreatedAt = now });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(),
            ComplianceTemplateId = templateId,
            DocumentType = "coi",
            FieldName = "general_liability_limit",
            Operator = "min_value",
            ExpectedValue = RequiredGl.ToString(CultureInfo.InvariantCulture),
            SortOrder = 0
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedVendorAsync(Guid orgId, Guid? templateId)
    {
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = "V",
            ComplianceTemplateId = templateId, CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return vendorId;
    }

    private async Task<Guid> SeedDocAsync(Guid orgId, Guid vendorId, decimal glLimit, ComplianceStatus stored)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = orgId, VendorId = vendorId,
            OriginalFileName = "d.pdf", BlobStorageUrl = "blob://d", FileSizeBytes = 1, ContentType = "application/pdf",
            DocumentType = "coi", GeneralLiabilityLimit = glLimit, ComplianceStatus = stored,
            ExpirationDate = FarFuture, CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return docId;
    }

    private async Task RunTemplateFanoutAsync(Guid orgId, Guid templateId, int pageSize = ComplianceCheckService.DefaultReevaluationPageSize)
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        await using var appDb = CreateAppDb(user);
        await using var sysDb = CreateSystemDb();
        var svc = new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow), NullLogger<ComplianceCheckService>.Instance, pageSize);
        await svc.ReevaluateForTemplateAsync(templateId, default);
    }

    private async Task RunVendorsFanoutAsync(Guid orgId, IReadOnlyList<Guid> vendorIds, int pageSize = ComplianceCheckService.DefaultReevaluationPageSize)
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        await using var appDb = CreateAppDb(user);
        await using var sysDb = CreateSystemDb();
        var svc = new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow), NullLogger<ComplianceCheckService>.Instance, pageSize);
        await svc.ReevaluateForVendorsAsync(vendorIds, default);
    }

    [Fact]
    public async Task ReevaluateForTemplate_regrades_every_vendors_documents_to_its_own_verdict()
    {
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor1 = await SeedVendorAsync(orgId, templateId);
        var vendor2 = await SeedVendorAsync(orgId, templateId);
        // Seed a deliberately wrong stored status so a missed re-grade is visible.
        var failing = await SeedDocAsync(orgId, vendor1, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);
        var passing = await SeedDocAsync(orgId, vendor2, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        await RunTemplateFanoutAsync(orgId, templateId);

        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.Id == failing)).ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
        (await db.Documents.SingleAsync(d => d.Id == passing)).ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
        (await db.ComplianceChecks.CountAsync(c => c.DocumentId == failing)).Should().Be(1);
        (await db.ComplianceChecks.CountAsync(c => c.DocumentId == passing)).Should().Be(1);
    }

    [Fact]
    public async Task ReevaluateForTemplate_pages_through_more_documents_than_one_page()
    {
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);

        // Five docs, all failing the 2M rule, seeded stale-Compliant. Page size 2 ⇒ three pages: a
        // bug that processed only the first page would leave docs 3-5 stuck at Compliant.
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await SeedDocAsync(orgId, vendor, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant));

        await RunTemplateFanoutAsync(orgId, templateId, pageSize: 2);

        await using var db = CreateSystemDb();
        foreach (var id in ids)
            (await db.Documents.SingleAsync(d => d.Id == id)).ComplianceStatus
                .Should().Be(ComplianceStatus.NonCompliant, "every page must be processed, not just the first");
    }

    [Fact]
    public async Task ReevaluateForTemplate_run_twice_replaces_checks_rather_than_appending()
    {
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        var doc = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        await RunTemplateFanoutAsync(orgId, templateId);
        await RunTemplateFanoutAsync(orgId, templateId);

        await using var db = CreateSystemDb();
        (await db.ComplianceChecks.CountAsync(c => c.DocumentId == doc)).Should().Be(1,
            "the batched delete must replace the prior check rows, not append a second set");
    }

    [Fact]
    public async Task ReevaluateForVendors_regrades_only_listed_vendors_and_respects_the_tenant_filter()
    {
        var orgA = Guid.NewGuid();
        var templateA = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgA, templateA);
        var v1 = await SeedVendorAsync(orgA, templateA);
        var v2 = await SeedVendorAsync(orgA, templateA);
        var v3 = await SeedVendorAsync(orgA, templateA);
        var d1 = await SeedDocAsync(orgA, v1, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);
        var d2 = await SeedDocAsync(orgA, v2, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);
        var d3 = await SeedDocAsync(orgA, v3, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);

        // A foreign org's vendor + doc. Handing org A's fan-out this vendor id must change nothing —
        // the AppDbContext tenant filter keeps org B's document invisible.
        var orgB = Guid.NewGuid();
        var templateB = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgB, templateB);
        var vB = await SeedVendorAsync(orgB, templateB);
        var dB = await SeedDocAsync(orgB, vB, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);

        await RunVendorsFanoutAsync(orgA, new[] { v1, v2, vB });

        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.Id == d1)).ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
        (await db.Documents.SingleAsync(d => d.Id == d2)).ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
        (await db.Documents.SingleAsync(d => d.Id == d3)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant, "v3 was not in the re-evaluation list");
        (await db.Documents.SingleAsync(d => d.Id == dB)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant, "a foreign org's document must be invisible to org A's tenant-filtered fan-out");
    }

    [Fact]
    public async Task ReevaluateForVendors_drops_to_Pending_and_sheds_checks_when_the_assignment_is_cleared()
    {
        // Mirrors the template-delete path: a vendor whose checklist is removed must have its
        // documents drop to "no requirements apply" (Pending) and shed the now-orphaned check rows.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        var doc = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        await RunVendorsFanoutAsync(orgId, new[] { vendor });
        await using (var db = CreateSystemDb())
        {
            (await db.Documents.SingleAsync(d => d.Id == doc)).ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
            (await db.ComplianceChecks.CountAsync(c => c.DocumentId == doc)).Should().Be(1);
        }

        await using (var db = CreateSystemDb())
            await db.Vendors.Where(v => v.Id == vendor)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ComplianceTemplateId, (Guid?)null));

        await RunVendorsFanoutAsync(orgId, new[] { vendor });

        await using (var verify = CreateSystemDb())
        {
            (await verify.Documents.SingleAsync(d => d.Id == doc)).ComplianceStatus
                .Should().Be(ComplianceStatus.Pending, "with no checklist the document has no governing rules");
            (await verify.ComplianceChecks.CountAsync(c => c.DocumentId == doc)).Should().Be(0,
                "the now-orphaned check rows must be shed");
        }
    }

    [Fact]
    public async Task ReevaluateForVendors_selects_a_soft_deleted_vendors_documents_via_the_VendorId_FK()
    {
        // #422: the vendor-delete fan-out re-grades documents whose vendor is ALREADY soft-deleted
        // by the time it runs. That only works because the membership predicate keys on the VendorId
        // FK — the d.Vendor nav carries the Vendor soft-delete query filter, so a predicate that
        // joined through the nav would select NOTHING here and silently leave the vacuous Compliant
        // in place (this test's regression mode). Each selected document then loads Vendor == null
        // (the page query's Include DOES honor the filter) and takes the no-governing-rules branch:
        // Pending, checks shed. Runs on a fresh context, so this pins the raw predicate — the
        // endpoint-level VendorEndpointsTests pin covers the shared-request-context path.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        var doc = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        // A genuine pre-delete verdict: Compliant with one check row against the vendor's checklist.
        await RunVendorsFanoutAsync(orgId, new[] { vendor });
        await using (var db = CreateSystemDb())
        {
            (await db.Documents.SingleAsync(d => d.Id == doc)).ComplianceStatus
                .Should().Be(ComplianceStatus.Compliant, "arrange: the doc must hold a real pre-delete verdict");
            (await db.ComplianceChecks.CountAsync(c => c.DocumentId == doc)).Should().Be(1);
        }

        // Soft-delete the vendor — the row state DeleteVendor commits before its fan-out runs.
        await using (var db = CreateSystemDb())
            await db.Vendors.Where(v => v.Id == vendor)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.DeletedAt, (DateTime?)DateTime.UtcNow));

        await RunVendorsFanoutAsync(orgId, new[] { vendor });

        await using (var verify = CreateSystemDb())
        {
            (await verify.Documents.SingleAsync(d => d.Id == doc)).ComplianceStatus
                .Should().Be(ComplianceStatus.Pending,
                    "the FK-keyed predicate must still select the soft-deleted vendor's documents — a nav-joined one would select nothing and strand the vacuous Compliant");
            (await verify.ComplianceChecks.CountAsync(c => c.DocumentId == doc)).Should().Be(0,
                "the checks graded against the dead vendor's checklist must be shed");
        }
    }

    [Fact]
    public async Task ReevaluateForVendors_with_an_empty_list_is_a_no_op()
    {
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        var doc = await SeedDocAsync(orgId, vendor, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);

        await RunVendorsFanoutAsync(orgId, Array.Empty<Guid>());

        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.Id == doc)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant, "an empty vendor list must touch nothing");
    }

    [Fact]
    public async Task ReevaluateForTemplate_keeps_check_rows_on_a_document_that_has_since_expired()
    {
        // The Expired branch returns ClearExistingChecks:false — a doc that crosses its expiration
        // keeps the check rows from its last rule evaluation (only the date changed). Every OTHER
        // terminal branch clears checks, so this is the branch a batch-path slip would most likely
        // get wrong; pin it through the fan-out's ApplyEvaluationsAsync.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        var doc = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        // First pass: passes the 2M rule on a far-future date ⇒ Compliant + one check row.
        await RunTemplateFanoutAsync(orgId, templateId);
        await using (var db = CreateSystemDb())
        {
            (await db.Documents.SingleAsync(d => d.Id == doc)).ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
            (await db.ComplianceChecks.CountAsync(c => c.DocumentId == doc)).Should().Be(1);
        }

        // The certificate lapses (expiration moves before the fixed clock's date).
        await using (var db = CreateSystemDb())
            await db.Documents.Where(d => d.Id == doc)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ExpirationDate, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        await RunTemplateFanoutAsync(orgId, templateId);

        await using (var verify = CreateSystemDb())
        {
            (await verify.Documents.SingleAsync(d => d.Id == doc)).ComplianceStatus
                .Should().Be(ComplianceStatus.Expired);
            (await verify.ComplianceChecks.CountAsync(c => c.DocumentId == doc)).Should().Be(1,
                "the Expired branch must NOT clear existing check rows");
        }
    }

    [Fact]
    public async Task ReevaluateForTemplate_skips_a_failed_page_and_still_commits_the_others()
    {
        // Proves the per-page best-effort guarantee: when one page's SaveChanges throws, the fan-out
        // logs + skips it (those docs keep their prior verdict) and KEEPS GOING — later pages commit.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        // Four docs failing the 2M rule, seeded stale-Compliant; page size 2 ⇒ two pages. The
        // interceptor throws on the FIRST page's SaveChanges only.
        for (var i = 0; i < 4; i++)
            await SeedDocAsync(orgId, vendor, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        await using (var appDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(Fixture.ConnectionString)
                .AddInterceptors(new AuditSaveChangesInterceptor(() => user), new ThrowOnceSaveChangesInterceptor())
                .Options, user))
        await using (var sysDb = CreateSystemDb())
        {
            var svc = new ComplianceCheckService(
                appDb, sysDb, new FixedTimeProvider(FixedNow), NullLogger<ComplianceCheckService>.Instance, reevaluationPageSize: 2);

            // Must NOT throw despite a page failing mid-run.
            await svc.ReevaluateForTemplateAsync(templateId, default);
        }

        await using var verify = CreateSystemDb();
        var statuses = await verify.Documents.Where(d => d.OrganizationId == orgId)
            .Select(d => d.ComplianceStatus).ToListAsync();
        statuses.Count(s => s == ComplianceStatus.NonCompliant).Should().Be(2, "the surviving page must still be re-graded");
        statuses.Count(s => s == ComplianceStatus.Compliant).Should().Be(2, "the failed page's documents keep their prior verdict");
    }
}
