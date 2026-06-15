using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// End-to-end HTTP tests for the #257 compliance-freshness cluster: the date overlay must make the
/// document list (filter + badge), the document detail, the dashboard counts, and the audit export
/// all agree on a doc's status TODAY — and rule/checklist changes must fan out a re-evaluation.
/// Docs are seeded with a deliberately STALE stored status (Compliant) plus a past/near expiration
/// so a regression that drops the derivation re-surfaces immediately.
/// </summary>
public sealed class ComplianceVerdictFreshnessTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<Guid> SeedDocAsync(
        Guid orgId, ComplianceStatus stored, DateTime? expiration, string docType = "coi", Guid? vendorId = null,
        decimal? glLimit = null)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = $"doc-{docId:N}.pdf",
            BlobStorageUrl = "blob://d",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = docType,
            ComplianceStatus = stored,
            ExpirationDate = expiration,
            GeneralLiabilityLimit = glLimit,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return docId;
    }

    private static string[] StatusesOf(JsonElement listResponse) =>
        listResponse.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("complianceStatus").GetString()!)
            .ToArray();

    // ---- the two-answer bug: list Expired filter + badge must agree with the date ----

    [Fact]
    public async Task Expired_filter_returns_a_stale_compliant_doc_whose_date_passed_and_badges_it_Expired()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, DateTime.UtcNow.Date.AddDays(-2));

        var list = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?status=Expired");

        var items = list.GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
        items.Should().ContainSingle("the date-expired doc must surface under the Expired filter");
        items[0].GetProperty("id").GetString().Should().Be(docId.ToString());
        items[0].GetProperty("complianceStatus").GetString().Should().Be("Expired",
            "the row badge must show the derived status, not the stale stored Compliant");
    }

    [Fact]
    public async Task Detail_overlays_Expired_for_a_stale_compliant_doc()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, DateTime.UtcNow.Date.AddDays(-2));

        var detail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");

        detail.GetProperty("data").GetProperty("complianceStatus").GetString().Should().Be("Expired");
    }

    [Fact]
    public async Task Compliant_filter_excludes_a_doc_that_has_since_expired()
    {
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, DateTime.UtcNow.Date.AddDays(-2));

        var list = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?status=Compliant");

        StatusesOf(list).Should().BeEmpty("a stored-Compliant doc past its date is no longer Compliant");
    }

    // ---- dashboard double-count ----

    [Fact]
    public async Task Dashboard_does_not_count_a_date_expired_doc_as_compliant()
    {
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, DateTime.UtcNow.Date.AddDays(-2));

        var stats = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats")).GetProperty("data");

        stats.GetProperty("compliant").GetInt32().Should().Be(0, "an expired doc must not be counted compliant");
        stats.GetProperty("expired").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Dashboard_counts_a_noncompliant_expiring_soon_doc_only_as_nonCompliant()
    {
        // A failing doc expiring within 30 days stays NonCompliant (the date doesn't soften the
        // verdict). It must be counted ONCE — under nonCompliant — not double-counted as expiringSoon,
        // matching the deriver and the documents-list ExpiringSoon filter.
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.NonCompliant, DateTime.UtcNow.Date.AddDays(10));

        var stats = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats")).GetProperty("data");

        stats.GetProperty("nonCompliant").GetInt32().Should().Be(1);
        stats.GetProperty("expiringSoon").GetInt32().Should().Be(0,
            "a NonCompliant doc expiring soon must not also be counted as ExpiringSoon");
    }

    // ---- expiring-within filter lower bound ----

    [Fact]
    public async Task Expiring_within_filter_excludes_already_expired_docs()
    {
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, DateTime.UtcNow.Date.AddDays(-2));
        var soonId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, DateTime.UtcNow.Date.AddDays(10));

        var list = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?expiresWithin=30");

        var ids = list.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()).ToArray();
        ids.Should().ContainSingle().Which.Should().Be(soonId.ToString(),
            "'expiring within 30 days' is a future window — long-expired docs must not appear");
    }

    // ---- export reflects the derived status ----

    [Fact]
    public async Task Csv_export_certifies_the_derived_status_not_the_stale_cache()
    {
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, DateTime.UtcNow.Date.AddDays(-2));

        var resp = await auth.Client.GetAsync("/api/export/csv");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await resp.Content.ReadAsStringAsync();

        csv.Should().Contain("Expired", "the audit export must derive the date status at generation");
        csv.Should().NotContain("Compliant",
            "the export must not certify the stale stored Compliant on an expired document");
    }

    // ---- #294: date-window boundary agreement (deriver vs SQL sites) ----

    [Fact]
    public async Task A_time_bearing_expiry_on_the_30_day_boundary_reads_ExpiringSoon_everywhere()
    {
        // A non-midnight expiry exactly on the today+30 boundary. The badge (deriver, date-only)
        // reads ExpiringSoon; the list filter and the dashboard counts must AGREE. Before the
        // exclusive-bound fix (#294) the raw `exp <= today+30 (midnight)` dropped this noon expiry
        // out of ExpiringSoon and the `exp > today+30` arm counted it Compliant — two answers.
        var auth = await RegisterAndLoginAsync();
        var onBoundaryNoon = DateTime.UtcNow.Date
            .AddDays(ComplianceStatusDeriver.ExpiringSoonWindowDays)
            .AddHours(12);
        var docId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, onBoundaryNoon);

        // Badge (detail) derives ExpiringSoon.
        var detail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        detail.GetProperty("data").GetProperty("complianceStatus").GetString().Should().Be("ExpiringSoon");

        // List filter agrees: under ExpiringSoon, not under Compliant.
        var soon = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?status=ExpiringSoon");
        soon.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString())
            .Should().Contain(docId.ToString(), "the badge says ExpiringSoon, so the ExpiringSoon filter must return it");
        var compliantList = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?status=Compliant");
        StatusesOf(compliantList).Should().BeEmpty("a doc expiring on the boundary day is no longer Compliant");

        // Dashboard counts agree: expiringSoon, not compliant.
        var stats = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats")).GetProperty("data");
        stats.GetProperty("expiringSoon").GetInt32().Should().Be(1);
        stats.GetProperty("compliant").GetInt32().Should().Be(0,
            "the boundary-day doc must not also be counted Compliant — the #294 two-answers split");
    }

    // ---- re-evaluation fan-out ----

    [Fact]
    public async Task Assigning_a_checklist_reevaluates_the_vendors_documents()
    {
        // Portal-first onboarding: a doc uploaded before any checklist sits at Pending. Assigning a
        // checklist (with a rule it fails) must immediately re-grade it — not leave it "Awaiting
        // review" forever.
        var auth = await RegisterAndLoginAsync();
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "V", CreatedAt = now, UpdatedAt = now });
            db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = auth.OrgId, Name = "T", CreatedAt = now });
            db.ComplianceRules.Add(new ComplianceRule
            {
                Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = "coi",
                FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "2000000", SortOrder = 0
            });
            await db.SaveChangesAsync();
        }
        // GL limit 1M fails the 2M rule.
        var docId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Pending, now.AddYears(1), vendorId: vendorId, glLimit: 1_000_000m);

        var resp = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "V", contactEmail = (string?)null, contactPhone = (string?)null, category = (string?)null,
            complianceTemplateId = templateId
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = CreateSystemDb();
        (await verify.Documents.SingleAsync(d => d.Id == docId)).ComplianceStatus
            .Should().Be(ComplianceStatus.NonCompliant, "assigning the checklist must fan out a re-evaluation");
    }

    [Fact]
    public async Task Adding_a_rule_reevaluates_documents_on_the_template()
    {
        var auth = await RegisterAndLoginAsync();
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = auth.OrgId, Name = "T", CreatedAt = now });
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "V", ComplianceTemplateId = templateId, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        var docId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Pending, now.AddYears(1), vendorId: vendorId, glLimit: 1_000_000m);

        // Add a rule the doc fails (needs 2M, has 1M) — the fan-out must flip it to NonCompliant
        // without anyone calling the manual check endpoint.
        var resp = await auth.Client.PostAsJsonAsync($"/api/compliance/templates/{templateId}/rules", new
        {
            id = (Guid?)null, documentType = "coi", fieldName = "general_liability_limit",
            @operator = "min_value", expectedValue = "2000000", errorMessage = (string?)null, sortOrder = 0
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = CreateSystemDb();
        (await verify.Documents.SingleAsync(d => d.Id == docId)).ComplianceStatus
            .Should().Be(ComplianceStatus.NonCompliant, "adding a rule must fan out a re-evaluation across the template's docs");
    }

    [Fact]
    public async Task Fan_out_regrades_every_document_on_the_template_independently()
    {
        // Two docs on one template: one fails the rule (1M < 2M), one passes (3M >= 2M). The batched
        // fan-out must re-grade BOTH to their own correct verdict — proving it covers every doc on
        // the template and that one doc's evaluation doesn't bleed into the next (each doc's outcome
        // is computed independently before the page's single SaveChanges — see #293).
        var auth = await RegisterAndLoginAsync();
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = auth.OrgId, Name = "T", CreatedAt = now });
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "V", ComplianceTemplateId = templateId, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        var failingId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Pending, now.AddYears(1), vendorId: vendorId, glLimit: 1_000_000m);
        var passingId = await SeedDocAsync(auth.OrgId, ComplianceStatus.Pending, now.AddYears(1), vendorId: vendorId, glLimit: 3_000_000m);

        var resp = await auth.Client.PostAsJsonAsync($"/api/compliance/templates/{templateId}/rules", new
        {
            id = (Guid?)null, documentType = "coi", fieldName = "general_liability_limit",
            @operator = "min_value", expectedValue = "2000000", errorMessage = (string?)null, sortOrder = 0
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = CreateSystemDb();
        (await verify.Documents.SingleAsync(d => d.Id == failingId)).ComplianceStatus
            .Should().Be(ComplianceStatus.NonCompliant);
        (await verify.Documents.SingleAsync(d => d.Id == passingId)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant, "the fan-out must re-grade every doc independently, not stop after the first");
    }
}
