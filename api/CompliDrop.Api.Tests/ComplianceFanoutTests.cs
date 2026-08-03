using System.Globalization;
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
        // Through the harness helper with the fault added on top, NOT a hand-built copy of the production
        // interceptor list: this one used to omit ComplianceCheckDeleteConcurrencyInterceptor entirely, so a
        // fan-out driven through it ran with pre-#468 save semantics (ADR 0030 Amendment 4).
        await using (var appDb = CreateAppDb(user, new ThrowOnceSaveChangesInterceptor()))
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

    /// <summary>
    /// The command text that appears ONLY in <c>LoadPageAsync</c>'s root query, and therefore the way this
    /// suite counts page loads. The fan-out's id snapshot projects <c>d."Id"</c> alone and the split query
    /// for <c>template.Rules</c> projects rule columns, so neither carries the jsonb column an unprojected
    /// <c>Document</c> materialization does. (That it IS shipped, twice per page and read by nothing, is
    /// <see href="https://github.com/neboxdev/complidrop/issues/423">#423</see>.)
    /// </summary>
    private const string PageLoadMarker = "\"ExtractionRawJson\"";

    [Fact]
    public async Task A_failed_verification_pass_counts_the_committed_page_as_failed()
    {
        // #470 / ADR 0030 Amendment 5: the page commits, then VerifyPageAsync re-reads it. If that
        // confirmation throws, the page counts as FAILED even though its own SaveChanges committed —
        // because RegradeResult.AllSucceeded is what gates the seed's re-grade watermark (#416, ADR 0036
        // Amendment 2), and a page whose verdicts are committed but UNVERIFIED is exactly a page the next
        // boot must re-fire. Swallowed silently, the watermark would advance over it and nothing would ever
        // re-grade those documents — the stale-verdict-survives-forever hole the watermark exists to close.
        //
        // Driven through ReevaluateForTemplateForSystemAsync, the ONE entry point that returns a
        // RegradeResult and the one caller whose consumer reads it. This is also the only place any test
        // observes a RegradeResult produced by the REAL service rather than by a fake reevaluator.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        // Deliberately wrong stored verdicts, so "the page committed" is observable rather than assumed.
        var failing = await SeedDocAsync(orgId, vendor, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);
        var passing = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        // Fail the SECOND page load of the run — i.e. the verification's, not the grade's. The fault is
        // raised from ReaderExecutingAsync, before the command reaches Postgres, so the connection stays
        // usable and nothing is left half-executed: a transient read failure, which is the realistic shape.
        var fault = new SystemCommandFaultInterceptor();
        var pageLoads = 0;
        fault.ShouldFault = sql =>
            sql.Contains(PageLoadMarker, StringComparison.Ordinal) && ++pageLoads == 2;

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        var logger = new ListLogger<ComplianceCheckService>();
        RegradeResult result;
        await using (var appDb = CreateAppDb(user))
        await using (var sysDb = CreateSystemDb(user, fault))
        {
            var svc = new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow), logger);

            // Must NOT throw: the fan-out is best-effort per page, and the triggering seed convergence has
            // already committed.
            result = await svc.ReevaluateForTemplateForSystemAsync(templateId, default);
        }

        fault.FaultCount.Should().Be(1,
            "anti-no-op: without a fault actually raised, every assertion below would pass on a clean run");

        result.Targeted.Should().Be(2);
        result.Regraded.Should().Be(2,
            "the page's own SaveChanges committed — those documents WERE re-graded; what failed is the "
            + "confirmation. (Had the fault landed on the GRADE's page load instead, this would be 0.)");
        result.FailedPages.Should().Be(1,
            "a committed-but-unconfirmed page is one the next boot must re-fire (#470, ADR 0030 Amendment 5)");
        result.AllSucceeded.Should().BeFalse(
            "AllSucceeded gates ComplianceTemplateSeed's RegradedThroughRevision advance; advancing it over "
            + "an unverified page would leave a stale verdict with nothing left to heal it (#416)");
        result.Regraded.Should().Be(result.Targeted,
            "and this tuple — everything re-graded, yet not all succeeded — is REACHABLE, which is what the "
            + "RegradeResult contract had to be amended to say (it previously described one arm only)");

        // The fault lands on the verification's FIRST page load, so NOTHING was confirmed and the whole page
        // is what the line must name. Naming the ids at all is the point (a bare count is not actionable),
        // and naming the RIGHT ids is why the unconfirmed set is tracked rather than the page reprinted: had
        // pass 1 confirmed one of these two before the throw, only the other belongs on this line.
        logger.Entries.Should().Contain(
            e => e.Message.Contains("verification pass failed for a page", StringComparison.Ordinal)
                && e.Message.Contains(failing.ToString(), StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains(passing.ToString(), StringComparison.OrdinalIgnoreCase),
            "the failure is logged as well as counted, and it NAMES the documents left unconfirmed — ids "
            + "only, never an ActualValue or a Notes string, which are document content");

        await using var verify = CreateSystemDb();
        (await verify.Documents.SingleAsync(d => d.Id == failing)).ComplianceStatus
            .Should().Be(ComplianceStatus.NonCompliant, "the page's verdicts are committed, not rolled back");
        (await verify.Documents.SingleAsync(d => d.Id == passing)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public async Task A_failed_verification_pass_names_at_most_the_bounded_id_sample()
    {
        // The other half of the naming contract: a page is up to DefaultReevaluationPageSize documents and a
        // log line is not a dump, so the line promises "up to {Max} shown" and must keep that promise. The
        // COUNT beside the sample is always the complete one — that pair is what makes the line both bounded
        // and honest, and neither half was asserted anywhere.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);

        const int docCount = ComplianceCheckService.LoggedIdSampleSize + 3;
        for (var i = 0; i < docCount; i++)
            await SeedDocAsync(orgId, vendor, glLimit: 1_000_000m, stored: ComplianceStatus.Compliant);

        // One page (default size 200 > docCount), so page load 1 is the grade's and load 2 the verification's.
        var fault = new SystemCommandFaultInterceptor();
        var pageLoads = 0;
        fault.ShouldFault = sql =>
            sql.Contains(PageLoadMarker, StringComparison.Ordinal) && ++pageLoads == 2;

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        var logger = new ListLogger<ComplianceCheckService>();
        RegradeResult result;
        await using (var appDb = CreateAppDb(user))
        await using (var sysDb = CreateSystemDb(user, fault))
        {
            var svc = new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow), logger);
            result = await svc.ReevaluateForTemplateForSystemAsync(templateId, default);
        }

        fault.FaultCount.Should().Be(1, "anti-no-op: the assertions below need a real verification failure");
        result.FailedPages.Should().Be(1);

        var line = logger.Entries.Should().ContainSingle(
            e => e.Message.Contains("verification pass failed for a page", StringComparison.Ordinal)).Subject;

        CountGuids(line.Message).Should().Be(ComplianceCheckService.LoggedIdSampleSize,
            $"the sample is capped at {ComplianceCheckService.LoggedIdSampleSize}, not the whole page — a "
            + "line that dumped every id of a 200-document page is the thing the bound exists to prevent");
        line.Message.Should().Contain(docCount.ToString(CultureInfo.InvariantCulture),
            "…while the COUNT printed beside the sample is the COMPLETE one, or the reader would think the "
            + "sample was the whole set");
    }

    /// <summary>How many distinct GUIDs a rendered log line names — the id sample's observable size.</summary>
    private static int CountGuids(string message) =>
        System.Text.RegularExpressions.Regex.Matches(
            message, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Count;

    [Fact]
    public async Task A_failed_verification_pass_names_only_the_documents_still_unconfirmed()
    {
        // The naming contract's sharp edge, and the ONLY shape that discriminates it: the failure must name
        // the documents left UNCONFIRMED, not the whole page. Faulting pass 1 (the two tests above) cannot
        // see the difference — nothing has been confirmed yet, so the unconfirmed set IS the page. Here pass
        // 1 confirms the bystander and narrows to the one mover, and only THEN does the read fail. Naming
        // the page instead would bury the one document that needs attention among documents already proven
        // fine — and on a real 200-document page the bounded sample would show it with ~10% probability,
        // which defeats the reason ids are logged at all.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        var mover = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);
        var bystander = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        // One competing edit, inside the PAGE's save only, so exactly one document moves.
        var hook = new ConcurrentSystemWriteInterceptor();
        var competingWrites = 0;
        hook.OnSavingChanges = async () =>
        {
            if (competingWrites++ > 0) return;
            await using var edit = CreateSystemDb();
            await edit.Documents.Where(d => d.Id == mover)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.GeneralLiabilityLimit, (decimal?)1_000_000m));
        };

        // Page loads: 1 = the grade, 2 = verification pass 1 (confirms the bystander, corrects the mover),
        // 3 = verification pass 2 — which is the one that fails.
        var fault = new SystemCommandFaultInterceptor();
        var pageLoads = 0;
        fault.ShouldFault = sql =>
            sql.Contains(PageLoadMarker, StringComparison.Ordinal) && ++pageLoads == 3;

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        var logger = new ListLogger<ComplianceCheckService>();
        RegradeResult result;
        await using (var appDb = CreateAppDb(user))
        await using (var sysDb = CreateSystemDb(user, hook, fault))
        {
            var svc = new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow), logger);
            result = await svc.ReevaluateForTemplateForSystemAsync(templateId, default);
        }

        fault.FaultCount.Should().Be(1, "anti-no-op: the verification really did fail…");
        logger.Entries.Should().Contain(
            e => e.Message.Contains("whose inputs changed while the page was being graded", StringComparison.Ordinal),
            "…and pass 1 really did find a mover first, which is what makes the unconfirmed set a STRICT "
            + "subset of the page");
        result.FailedPages.Should().Be(1);

        var line = logger.Entries.Should().ContainSingle(
            e => e.Message.Contains("verification pass failed for a page", StringComparison.Ordinal)).Subject;

        line.Message.Should().Contain(mover.ToString(),
            "the document whose correction nobody confirmed is the one worth naming");
        line.Message.Should().NotContain(bystander.ToString(),
            "pass 1 read the bystander and found the row saying exactly what this fan-out asserted — it is "
            + "CONFIRMED, and reporting it as unconfirmed is the noise that hides the mover");
    }

    [Fact]
    public async Task A_page_left_unconfirmed_by_a_spent_bound_counts_as_a_failed_page()
    {
        // The give-up arm, from the real service and through the ONE entry point that returns a
        // RegradeResult. Verification can end two ways without confirming — it THREW (the test above) or it
        // SPENT ITS BOUND on a document whose inputs kept moving — and both leave the identical state:
        // verdicts committed, nobody re-read them. Only the first used to reach failedPages, so a give-up
        // answered AllSucceeded == true, ComplianceTemplateSeed advanced RegradedThroughRevision over it
        // (Data/Seed/ComplianceTemplateSeed.cs — the advance is gated on exactly that flag) and the next boot
        // skipped the template. Nothing else re-grades it: the nightly sweep does date transitions only.
        //
        // The other half of that chain — AllSucceeded == false ⇒ the watermark is HELD BACK and the next boot
        // re-fires — is pinned by
        // ComplianceTemplateSeedTests.Interrupted_regrade_leaves_watermark_behind_and_the_next_boot_heals_the_stale_verdict,
        // which drives the real seed with a RegradeResult whose only relevant property is that same flag.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);
        // Seeded Pending so "the page committed a verdict" is observable rather than assumed.
        var docId = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);

        // Every save this fan-out makes — the page's and both corrections' — is beaten by a competing edit
        // that ALTERNATES the limit across the 2M floor, so each following fresh read disagrees with what was
        // just applied and the verification can never confirm. The edit runs on its own connection and lands
        // between the interceptor and EF's UPDATE, i.e. inside the window #470 is about.
        var hook = new ConcurrentSystemWriteInterceptor();
        var competingWrites = 0;
        hook.OnSavingChanges = async () =>
        {
            var next = competingWrites++ % 2 == 0 ? 1_000_000m : 3_000_000m;
            await using var edit = CreateSystemDb();
            await edit.Documents.Where(d => d.Id == docId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.GeneralLiabilityLimit, (decimal?)next));
        };

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        var logger = new ListLogger<ComplianceCheckService>();
        RegradeResult result;
        await using (var appDb = CreateAppDb(user))
        await using (var sysDb = CreateSystemDb(user, hook))
        {
            var svc = new ComplianceCheckService(appDb, sysDb, new FixedTimeProvider(FixedNow), logger);
            result = await svc.ReevaluateForTemplateForSystemAsync(templateId, default);
        }

        competingWrites.Should().Be(1 + ComplianceCheckService.MaxVerificationPasses,
            "anti-no-op AND the bound: the page's save plus one per verification pass, each beaten — without "
            + "an edit landing inside every one of them nothing would be left unconfirmed and every "
            + "assertion below would pass on a run that reproduced nothing");

        logger.Entries.Should().Contain(
            e => e.Message.Contains("Gave up confirming", StringComparison.Ordinal)
                && e.Message.Contains(docId.ToString(), StringComparison.OrdinalIgnoreCase),
            "the give-up is disclosed and NAMES the document — an unconfirmed verdict has to be findable");

        result.Targeted.Should().Be(1);
        result.Regraded.Should().Be(1,
            "the document WAS re-graded — what is missing is the confirmation, exactly as on the thrown arm");
        result.FailedPages.Should().Be(1,
            "a page whose verdicts are committed but unconfirmed is one the next boot must re-fire, however "
            + "the verification ran out — by exception or by spending its bound (#470, ADR 0030 Amendment 5)");
        result.AllSucceeded.Should().BeFalse(
            "which is the whole point: AllSucceeded is the durability signal ComplianceTemplateSeed gates "
            + "RegradedThroughRevision on. True here would advance the watermark over a document holding a "
            + "verdict graded from inputs that moved under it, with nothing left to heal it (#416)");

        await using var verify = CreateSystemDb();
        (await verify.Documents.SingleAsync(d => d.Id == docId)).ComplianceStatus
            .Should().NotBe(ComplianceStatus.Pending,
                "the page's verdict is committed and the give-up does NOT degrade it — ADR 0030 Amendment 3 "
                + "Option I, refuted for a caller that owns no inputs");
    }

    /// <summary>
    /// A clock that answers <paramref name="first"/> once and <c>first + advance</c> on every later read, so
    /// a code path that RE-READS the clock is distinguishable from one that reuses a held reading. A
    /// <see cref="FixedTimeProvider"/> cannot tell the two apart — which is why swapping the fan-out's held
    /// <c>nowUtc</c> for a fresh <c>GetUtcNow()</c> inside the verification left the whole suite green.
    /// </summary>
    private sealed class AdvancingTimeProvider(DateTimeOffset first, TimeSpan advance) : TimeProvider
    {
        private int _reads;

        public int Reads => _reads;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _reads) == 1 ? first : first + advance;
    }

    [Fact]
    public async Task The_verification_grades_against_the_fan_outs_OWN_clock_reading()
    {
        // ADR 0030 Amendment 5: nowUtc is read ONCE and reused by every verification pass, deliberately. A
        // fresh reading would turn an expiry boundary crossed mid-fan-out into a "mover" on documents nobody
        // touched — real writes, an UpdatedAt bump and an audit row per document, done for a non-reason —
        // and would break the property the whole detection rests on: that a difference means an INPUT moved
        // and nothing else.
        var orgId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await SeedOrgWithTemplateAsync(orgId, templateId);
        var vendor = await SeedVendorAsync(orgId, templateId);

        // Expires LATE on the fan-out's own day: not expired at the held reading (and inside the 30-day
        // window, so the rule-passing verdict is ExpiringSoon), expired one day later. Nothing about the
        // document changes — only which clock the verification asks.
        var expiresToday = new DateTime(2026, 6, 1, 23, 0, 0, DateTimeKind.Utc);
        var docId = await SeedDocAsync(orgId, vendor, glLimit: 3_000_000m, stored: ComplianceStatus.Pending);
        await using (var db = CreateSystemDb())
            await db.Documents.Where(d => d.Id == docId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ExpirationDate, expiresToday));

        var clock = new AdvancingTimeProvider(FixedNow, TimeSpan.FromDays(1));
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        var logger = new ListLogger<ComplianceCheckService>();
        await using (var appDb = CreateAppDb(user))
        await using (var sysDb = CreateSystemDb(user))
        {
            var svc = new ComplianceCheckService(appDb, sysDb, clock, logger);
            await svc.ReevaluateForTemplateAsync(templateId, default);
        }

        clock.Reads.Should().Be(1,
            "the fan-out reads its clock ONCE and hands that reading to every pass; a second read is the "
            + "regression this test exists for");

        await using var verify = CreateSystemDb();
        (await verify.Documents.SingleAsync(d => d.Id == docId)).ComplianceStatus
            .Should().Be(ComplianceStatus.ExpiringSoon,
                "anti-no-op: the fan-out really did grade this document at the HELD instant. Graded a day "
                + "later it would read Expired, which is exactly the disagreement a fresh clock would invent");

        logger.Entries.Should().NotContain(
            e => e.Message.Contains("whose inputs changed while the page was being graded", StringComparison.Ordinal),
            "no INPUT moved, so nothing is a mover — a verification on a fresh clock would call this "
            + "untouched document one and re-grade it");

        (await verify.AuditLogs.CountAsync(a =>
            a.EntityType == nameof(Document) && a.EntityId == docId && a.Action == "document.updated"))
            .Should().Be(1, "…and would write it a second time, for a boundary the document did not cross");
    }
}
