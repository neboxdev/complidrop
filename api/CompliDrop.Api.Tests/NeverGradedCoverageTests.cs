using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// End-to-end HTTP tests for #443 / ADR 0047: a document nothing ever graded — zero
/// <see cref="ComplianceCheck"/> rows — must not roll up to "Covered" on the vendor page, must not print an
/// affirmative verdict in the auditor-facing export, and must not read ExpiringSoon anywhere. It reads
/// Pending on EVERY surface (detail badge, list filter + badge, dashboard counts, vendor rollup, all three
/// export artifacts) so the vendor rollup and the document-level surfaces can never tell an auditor two
/// different stories. Expired still wins outright, a hard fail is never masked, and the STORED verdict is
/// untouched so the doc self-heals the moment something grades it.
/// </summary>
public sealed class NeverGradedCoverageTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime InTenDays => Today.AddDays(10);
    private static DateTime FarFuture => Today.AddDays(300);

    /// <param name="graded">false seeds the #443 never-graded state (no check rows) — the default here,
    /// since that IS the subject; true backs the stored verdict with a real check row.</param>
    private async Task<Guid> SeedDocAsync(
        Guid orgId, ComplianceStatus stored, DateTime? expiration, Guid? vendorId = null,
        string docType = "coi", bool graded = false, string fileName = "cert.pdf")
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = orgId,
                VendorId = vendorId,
                OriginalFileName = fileName,
                BlobStorageUrl = "blob://d",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = docType,
                ComplianceStatus = stored,
                ExpirationDate = expiration,
                ExtractionStatus = ExtractionStatus.Completed,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        if (graded) await MarkGradedAsync(orgId, docId);
        return docId;
    }

    /// <summary>A vendor on a checklist requiring a lower-case "coi", the shape the ticket describes.</summary>
    private async Task<Guid> SeedVendorRequiringCoiAsync(Guid orgId)
    {
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = orgId, Name = "T", CreatedAt = now });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "1000000", SortOrder = 0
        });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = "V", ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return vendorId;
    }

    private static async Task<string> DetailStatusAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/documents/{id}"))
            .GetProperty("data").GetProperty("complianceStatus").GetString()!;

    private static async Task<Guid[]> ListIdsAsync(HttpClient client, string status) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/documents/?status={status}"))
            .GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private static async Task<JsonElement> StatsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats")).GetProperty("data");

    private static async Task<JsonElement> CoverageAsync(HttpClient client, Guid vendorId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/vendors/{vendorId}"))
            .GetProperty("data").GetProperty("coverage");

    private static async Task<JsonElement> ListCoverageAsync(HttpClient client, Guid vendorId) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/vendors/"))
            .GetProperty("data").EnumerateArray()
            .First(v => v.GetProperty("id").GetGuid() == vendorId)
            .GetProperty("coverage");

    // ---- the vendor rollup: the ADR 0042 precedent, applied to the grading axis ----

    [Fact]
    public async Task A_vendor_whose_only_cert_for_a_required_type_was_never_graded_reads_ActionNeeded()
    {
        // THE bug. The vendor's only COI is a case-variant "COI" against a "coi" rule — it matched zero
        // rules, so nothing was ever measured against it, yet the rollup counted it as an in-force cert of
        // the required type and read Covered. It must read ActionNeeded, exactly as an expired-only or a
        // distrusted-extraction-only type does (ADR 0042).
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorRequiringCoiAsync(auth.OrgId);
        await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, vendorId, docType: "COI");

        (await CoverageAsync(auth.Client, vendorId)).GetProperty("status").GetString()
            .Should().Be("ActionNeeded", "no requirement was ever checked against this vendor's only COI");
        // The list rollup runs a SEPARATE projection from the detail one — both must agree, or the vendor
        // list and the vendor page disagree about the same vendor.
        (await ListCoverageAsync(auth.Client, vendorId)).GetProperty("status").GetString()
            .Should().Be("ActionNeeded");
    }

    [Fact]
    public async Task A_graded_in_force_cert_still_reads_Covered()
    {
        // The control that keeps the fix from being a blanket demotion: the same vendor, the same dates,
        // the same stored verdict — but the document was actually measured against the requirement.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorRequiringCoiAsync(auth.OrgId);
        await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, vendorId, graded: true);

        var coverage = await CoverageAsync(auth.Client, vendorId);
        coverage.GetProperty("status").GetString().Should().Be("Covered");
        coverage.GetProperty("coveredThrough").GetDateTime().Date.Should().Be(InTenDays.Date);
    }

    [Fact]
    public async Task A_never_graded_cert_does_not_extend_the_covered_through_horizon()
    {
        // #399's horizon is computed from the IN-FORCE set, so a never-graded cert must not appear in it.
        // A vendor genuinely covered to +10 days who also holds an ungraded cert running to +300 must not
        // be advertised as covered through +300 — that is the same overclaim one level down.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorRequiringCoiAsync(auth.OrgId);
        await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, vendorId, graded: true, fileName: "graded.pdf");
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, vendorId, docType: "COI", fileName: "ungraded.pdf");

        var coverage = await CoverageAsync(auth.Client, vendorId);
        coverage.GetProperty("status").GetString().Should().Be("Covered", "the graded cert is genuinely in force");
        coverage.GetProperty("coveredThrough").GetDateTime().Date
            .Should().Be(InTenDays.Date, "the ungraded cert's far-future expiry is not coverage");
    }

    // ---- the document-level surfaces: one story, not two ----

    [Fact]
    public async Task A_never_graded_doc_reads_Pending_on_the_badge_the_list_and_the_dashboard()
    {
        // The #294-class guard. The vendor rollup and the document-level surfaces must agree, because the
        // reason ADR 0042 left the document surfaces alone for a distrusted extraction does NOT transfer
        // here: the list carries a separate ManualRequired *extraction* badge beside the compliance badge,
        // but there is no badge anywhere for "nothing graded this" — the detail page's "What we checked"
        // panel is simply EMPTY under the verdict.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays);

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Pending");
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(id);
        (await ListIdsAsync(auth.Client, "ExpiringSoon")).Should().NotContain(id);
        (await ListIdsAsync(auth.Client, "Compliant")).Should().NotContain(id);

        var stats = await StatsAsync(auth.Client);
        stats.GetProperty("expiringSoon").GetInt32().Should().Be(0, "nothing was measured against it");
        stats.GetProperty("compliant").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Dashboard_expiringSoon_count_equals_the_deep_linked_list_with_a_never_graded_doc()
    {
        // The cross-surface pin (#294): the count and the list it deep-links to are separate SQL, so an arm
        // that mirrors the demotion and one that doesn't produce "1 expiring soon" over an empty list. Two
        // docs, same dates, only the grading differs.
        var auth = await RegisterAndLoginAsync();
        var graded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, InTenDays, graded: true, fileName: "graded.pdf");
        var ungraded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, InTenDays, fileName: "ungraded.pdf");

        var listed = await ListIdsAsync(auth.Client, "ExpiringSoon");
        (await StatsAsync(auth.Client)).GetProperty("expiringSoon").GetInt32()
            .Should().Be(listed.Length, "the dashboard count and its deep-linked list are the same population");
        listed.Should().BeEquivalentTo([graded]);
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(ungraded);
    }

    [Fact]
    public async Task Dashboard_compliant_count_equals_the_deep_linked_list_with_a_never_graded_doc()
    {
        // Same pin on the Compliant arm. A stored-Compliant-with-no-checks row is only transiently
        // reachable (a rule delete hard-deletes its checks and re-grades after the commit), but both
        // surfaces must fail closed on it identically or the count and the list disagree.
        var auth = await RegisterAndLoginAsync();
        var graded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, graded: true, fileName: "graded.pdf");
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, fileName: "ungraded.pdf");

        var listed = await ListIdsAsync(auth.Client, "Compliant");
        (await StatsAsync(auth.Client)).GetProperty("compliant").GetInt32().Should().Be(listed.Length);
        listed.Should().BeEquivalentTo([graded]);
    }

    [Fact]
    public async Task The_compliance_rate_excludes_a_never_graded_doc_from_the_denominator()
    {
        // "Documents that have a verdict" must not count one nothing produced. A never-graded doc stored
        // ExpiringSoon used to sit in the denominator without ever being able to reach the numerator, so it
        // silently dragged the org's compliance rate down for a document the product never graded.
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, graded: true, fileName: "graded.pdf");
        await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, fileName: "ungraded.pdf");

        (await StatsAsync(auth.Client)).GetProperty("complianceRate").GetDouble()
            .Should().Be(100, "1 compliant of 1 graded document — the ungraded one is in neither");
    }

    // ---- edges: what the demotion must NOT touch ----

    [Fact]
    public async Task A_never_graded_EXPIRED_doc_still_reads_Expired_everywhere()
    {
        // Expired is top precedence and is never softened: a lapsed date is a real fact and a present
        // liability, whether or not anything graded the document. Demoting it to Pending would HIDE a gap —
        // the exact failure this ticket exists to prevent, inverted.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, Today.AddDays(-1));

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Expired");
        (await ListIdsAsync(auth.Client, "Expired")).Should().Contain(id);
        (await ListIdsAsync(auth.Client, "Pending")).Should().NotContain(id);
        (await StatsAsync(auth.Client)).GetProperty("expired").GetInt32().Should().Be(1);
        // …and it stays IN the compliance-rate denominator: an expiry is a verdict.
        (await StatsAsync(auth.Client)).GetProperty("complianceRate").GetDouble().Should().Be(0);
    }

    [Fact]
    public async Task A_never_graded_doc_outside_the_expiry_window_is_unchanged_still_Pending()
    {
        // The no-op half. This case already read Pending before #443 (ComputeOutcome's zero-applicable-rules
        // branch stores Pending outside the window); the fix must not move it, only make the in-window case
        // agree with it.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.Pending, FarFuture);

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Pending");
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(id);
        (await StatsAsync(auth.Client)).GetProperty("expiringSoon").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_still_extracting_upload_is_unaffected()
    {
        // A fresh upload has no checks either, and must keep behaving exactly as before: Pending compliance,
        // counted under pendingExtraction, out of the rate denominator. The demotion adds nothing here — it
        // is the in-window ExpiringSoon promotion it removes.
        var auth = await RegisterAndLoginAsync();
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.Documents.Add(new Document
            {
                Id = id, OrganizationId = auth.OrgId, OriginalFileName = "fresh.pdf",
                BlobStorageUrl = "blob://f", FileSizeBytes = 1, ContentType = "application/pdf",
                DocumentType = "coi", ComplianceStatus = ComplianceStatus.Pending,
                ExtractionStatus = ExtractionStatus.Pending, CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Pending");
        var stats = await StatsAsync(auth.Client);
        stats.GetProperty("pendingExtraction").GetInt32().Should().Be(1);
        stats.GetProperty("complianceRate").GetDouble().Should().Be(0, "nothing is graded yet");
    }

    // ---- the read-only-overlay contract ----

    [Fact]
    public async Task The_stored_ComplianceStatus_is_never_rewritten_by_a_read()
    {
        // ADR 0041's rule, on the grading axis. Reading every surface must leave the column alone: writing
        // Pending here would strand the document (nothing re-runs rule evaluation on a grading change) AND
        // collide with the extraction worker, which CLAIMS documents on ComplianceStatus-adjacent Pending
        // semantics. The demotion has to stay a read overlay so it self-heals.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorRequiringCoiAsync(auth.OrgId);
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, vendorId, docType: "COI");

        await DetailStatusAsync(auth.Client, id);
        await ListIdsAsync(auth.Client, "Pending");
        await StatsAsync(auth.Client);
        await CoverageAsync(auth.Client, vendorId);
        (await auth.Client.GetAsync("/api/export/csv")).EnsureSuccessStatusCode();

        await using var db = CreateSystemDb();
        (await db.Documents.Where(d => d.Id == id).Select(d => d.ComplianceStatus).SingleAsync())
            .Should().Be(ComplianceStatus.ExpiringSoon, "the stored verdict stays real; only the read demotes");
    }

    [Fact]
    public async Task The_verdict_self_heals_the_moment_a_requirement_is_checked()
    {
        // The payoff of keeping it read-only: grading the SAME document (here by seeding the check row a
        // re-evaluation would write) restores its real verdict on every surface, with no write to
        // ComplianceStatus and no re-evaluation of the read path.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorRequiringCoiAsync(auth.OrgId);
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, vendorId, docType: "COI");

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Pending");
        (await CoverageAsync(auth.Client, vendorId)).GetProperty("status").GetString().Should().Be("ActionNeeded");

        await MarkGradedAsync(auth.OrgId, id);

        (await DetailStatusAsync(auth.Client, id)).Should().Be("ExpiringSoon");
        (await CoverageAsync(auth.Client, vendorId)).GetProperty("status").GetString().Should().Be("Covered");
    }

    [Fact]
    public async Task The_EF_graded_predicate_agrees_with_the_in_memory_one()
    {
        // DocumentGrading has two shapes — IsGraded(count) in memory and the Graded expression in SQL — and
        // the SQL read arms spell the same fact inline as d.ComplianceChecks.Any(). Pin all three against
        // one seeded population so a divergence (a soft-delete filter appearing on ComplianceCheck, say)
        // fails here rather than as a silent coverage overclaim.
        var auth = await RegisterAndLoginAsync();
        var graded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, graded: true, fileName: "graded.pdf");
        var ungraded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, fileName: "ungraded.pdf");

        await using var db = CreateSystemDb();
        var viaExpression = await db.Documents
            .Where(d => d.OrganizationId == auth.OrgId)
            .Where(DocumentGrading.Graded)
            .Select(d => d.Id)
            .ToListAsync();
        var viaCount = (await db.Documents
                .Where(d => d.OrganizationId == auth.OrgId)
                .Select(d => new { d.Id, Count = d.ComplianceChecks.Count })
                .ToListAsync())
            .Where(x => DocumentGrading.IsGraded(x.Count))
            .Select(x => x.Id)
            .ToList();

        viaExpression.Should().BeEquivalentTo([graded]);
        viaCount.Should().BeEquivalentTo(viaExpression);
        viaExpression.Should().NotContain(ungraded);
    }
}
