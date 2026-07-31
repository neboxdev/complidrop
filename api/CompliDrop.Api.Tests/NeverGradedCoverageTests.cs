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
/// End-to-end HTTP tests for #443 / ADR 0048: a document nothing ever graded — zero
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

    /// <summary>
    /// The BADGE an UNFILTERED documents-list row renders for one document — a different code path from
    /// the <c>?status=</c> SQL arms above (a C# projection over a per-row check COUNT), and the one the
    /// #294 count-vs-badge split lives in. Read from the unfiltered list on purpose: a badge asserted only
    /// inside <c>?status=Pending</c> would agree with the filter by construction.
    /// </summary>
    private static async Task<string> ListBadgeAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/documents/"))
            .GetProperty("data").GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetGuid() == id)
            .GetProperty("complianceStatus").GetString()!;

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
        //
        // The org holds one GRADED and one ungraded doc on purpose. The list badge is a C# projection over
        // a per-row check count — independent code from the SQL ?status= arms — so a projection that read
        // an org-wide "does anything here have checks" scalar, or one that lost the grading input
        // entirely, would still pass against a single-document org.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, fileName: "ungraded.pdf");
        var graded = await SeedDocAsync(
            auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, graded: true, fileName: "graded.pdf");

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Pending");
        (await ListBadgeAsync(auth.Client, id)).Should().Be("Pending",
            "the list ROW must render the same verdict the filter selects on — a badge saying 'Expiring "
            + "soon' inside the Pending list is the #294 split this ticket names as the failure mode");
        (await ListBadgeAsync(auth.Client, graded)).Should().Be("ExpiringSoon",
            "the graded row is untouched — the demotion is per-document, not per-org");
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(id).And.NotContain(graded);
        (await ListIdsAsync(auth.Client, "ExpiringSoon")).Should().NotContain(id).And.Contain(graded);
        (await ListIdsAsync(auth.Client, "Compliant")).Should().NotContain(id);

        var stats = await StatsAsync(auth.Client);
        stats.GetProperty("expiringSoon").GetInt32().Should().Be(1, "only the graded doc was measured");
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
        //
        // A second, GRADED + Compliant document is seeded so the compliance rate can discriminate. With
        // only the expired doc, `compliant` is 0 and the endpoint returns 0 whether the denominator is 1
        // or 0 — the "expired never-graded docs stay in the denominator" claim would be unfalsifiable.
        // Two docs make it arithmetic: expired one COUNTED => 1/2 => 50; excluded => 1/1 => 100.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, Today.AddDays(-1), fileName: "lapsed.pdf");
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, graded: true, fileName: "graded.pdf");

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Expired");
        (await ListIdsAsync(auth.Client, "Expired")).Should().Contain(id);
        (await ListIdsAsync(auth.Client, "Pending")).Should().NotContain(id);
        var stats = await StatsAsync(auth.Client);
        stats.GetProperty("expired").GetInt32().Should().Be(1);
        // …and it stays IN the compliance-rate denominator: an expiry is a verdict. This is why BOTH
        // demotion clauses in that denominator carry the not-yet-expired guard (parity with ADR 0041).
        stats.GetProperty("complianceRate").GetDouble().Should().Be(50,
            "1 compliant of 2 documents that have a verdict — the never-graded one is EXPIRED, and a "
            + "lapsed date is a real verdict, so dropping it from the denominator would flatter the rate");
    }

    [Fact]
    public async Task A_never_graded_doc_with_NO_expiry_date_reads_Pending_on_the_badge_and_the_list()
    {
        // The null-expiry path. The demotion sits AFTER the deriver's expiry if/else precisely so it also
        // catches a document with no expiration date (the #362 review's S2 lesson), and the ?status=Pending
        // SQL arm carries its own `d.ExpirationDate == null` branch to match. Only the pure deriver test
        // covered this axis; #362 set the precedent of pinning it over HTTP as well.
        var auth = await RegisterAndLoginAsync();
        var graded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, null, graded: true, fileName: "graded.pdf");
        var ungraded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, null, fileName: "ungraded.pdf");

        (await DetailStatusAsync(auth.Client, ungraded)).Should().Be("Pending");
        (await ListBadgeAsync(auth.Client, ungraded)).Should().Be("Pending");
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(ungraded,
            "a never-graded doc with no expiry date has nothing to be Expired by, so the Pending arm's "
            + "null-expiry branch is the ONLY thing that puts it in the list its badge says it belongs to");
        (await ListIdsAsync(auth.Client, "Compliant")).Should().NotContain(ungraded).And.Contain(graded);
        (await StatsAsync(auth.Client)).GetProperty("compliant").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_dashboard_awaitingReview_count_equals_its_deep_linked_Pending_list()
    {
        // #443 review B5: a demoted document must be REACHABLE from the dashboard, not merely absent from
        // the affirmative tiles. The awaitingReview stat is what makes it so, and — like every other
        // count/list pair on this screen — it must equal the list it deep-links to (#294).
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.ExpiringSoon, InTenDays, fileName: "ungraded.pdf");
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, graded: true, fileName: "graded.pdf");
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Pending, FarFuture, graded: true, fileName: "genuinely-pending.pdf");

        var listed = await ListIdsAsync(auth.Client, "Pending");
        (await StatsAsync(auth.Client)).GetProperty("awaitingReview").GetInt32()
            .Should().Be(listed.Length, "the tile and the list it links to are the same population");
        listed.Length.Should().Be(2, "the never-graded doc joins the genuinely-Pending one; the graded "
            + "Compliant doc does not");
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
    public async Task MarkGradedAsync_picks_the_backing_rule_deterministically()
    {
        // The fixture seam itself. MarkGradedAsync reuses a rule already in the org, and Postgres
        // guarantees NO row order without an ORDER BY — so an unordered FirstOrDefaultAsync leaves WHICH
        // rule backs the seeded check row up to the planner. Invisible while a test only counts check
        // rows, but not once one asserts on the requirement behind one (the detail page's "What we
        // checked" panel renders the rule's field, operator and message). Lowest SortOrder, then Id.
        var auth = await RegisterAndLoginAsync();
        var templateId = Guid.NewGuid();
        var firstRuleId = Guid.NewGuid();
        await using (var seed = CreateSystemDb())
        {
            seed.ComplianceTemplates.Add(new ComplianceTemplate
            {
                Id = templateId, OrganizationId = auth.OrgId, Name = "T", CreatedAt = DateTime.UtcNow
            });
            // Inserted OUT of SortOrder on purpose: insertion order must not be what decides.
            seed.ComplianceRules.Add(new ComplianceRule
            {
                Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = "coi",
                FieldName = "expiration_date", Operator = "required", SortOrder = 2
            });
            seed.ComplianceRules.Add(new ComplianceRule
            {
                Id = firstRuleId, ComplianceTemplateId = templateId, DocumentType = "coi",
                FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "1000000",
                SortOrder = 0
            });
            await seed.SaveChangesAsync();
        }
        var docId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, graded: true);

        await using var db = CreateSystemDb();
        (await db.ComplianceChecks.Where(c => c.DocumentId == docId).Select(c => c.ComplianceRuleId).SingleAsync())
            .Should().Be(firstRuleId, "the lowest SortOrder wins, not whichever row the planner returned");
    }

    [Fact]
    public async Task The_SQL_grading_predicate_agrees_with_the_in_memory_one_and_with_the_check_rows()
    {
        // Exactly TWO forms of "was this graded" ship: the SQL read arms spell it inline as
        // `d.ComplianceChecks.Any()`, and the in-memory read sites ask DocumentGrading.IsGraded of a
        // projected count. Both traverse the SAME ComplianceChecks navigation, so comparing them only to
        // each other pins `Any() == Count > 0` and nothing else — a filter appearing on the navigation
        // would move them identically and this test would stay green. So the third leg is GROUND TRUTH
        // read straight from the ComplianceChecks table by DocumentId, which the navigation's filters
        // cannot reach: if a query filter or a soft-delete column ever lands on ComplianceCheck, the
        // shipping forms diverge from the rows themselves and it fails HERE rather than as a silent
        // coverage overclaim.
        var auth = await RegisterAndLoginAsync();
        var graded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, graded: true, fileName: "graded.pdf");
        var ungraded = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, FarFuture, fileName: "ungraded.pdf");

        await using var db = CreateSystemDb();
        var orgDocs = db.Documents.Where(d => d.OrganizationId == auth.OrgId);

        // (1) the SQL spelling every read arm uses, inline in a composite predicate.
        var viaSql = await orgDocs.Where(d => d.ComplianceChecks.Any()).Select(d => d.Id).ToListAsync();
        // (2) the in-memory spelling every projection/deriver call site uses.
        var viaCount = (await orgDocs.Select(d => new { d.Id, Count = d.ComplianceChecks.Count }).ToListAsync())
            .Where(x => DocumentGrading.IsGraded(x.Count))
            .Select(x => x.Id)
            .ToList();
        // (3) ground truth: the check ROWS, keyed by DocumentId, never through the navigation.
        var viaRows = new List<Guid>();
        foreach (var id in await orgDocs.Select(d => d.Id).ToListAsync())
            if (await db.ComplianceChecks.CountAsync(c => c.DocumentId == id) > 0)
                viaRows.Add(id);

        viaRows.Should().BeEquivalentTo([graded], "one seeded doc carries a check row and one carries none");
        viaSql.Should().BeEquivalentTo(viaRows);
        viaCount.Should().BeEquivalentTo(viaRows);
        viaSql.Should().NotContain(ungraded);
    }
}
