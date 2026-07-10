using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The one-click sample-certificate demo (#238): POST /api/sample seeds a sample vendor + COI run
/// through the real pipeline; DELETE /api/sample clears it. Covers the ticket's test ACs — seeding
/// idempotent + tenant-scoped, removal clean (no blob/audit orphans), and the sample's fields pass
/// the Caterer checklist — plus the friendly storage-outage path and the template-clone editability.
/// </summary>
[Collection("integration")]
public sealed class SampleEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // ---- seeding ----

    [Fact]
    public async Task Seed_creates_a_sample_vendor_with_caterer_and_a_pending_sample_document()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await SeedSampleAsync(auth.Client);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var docId = DocumentId(await resp.Content.ReadFromJsonAsync<JsonElement>());

        await using var db = CreateSystemDb();
        var doc = await db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == docId);
        doc.IsSample.Should().BeTrue();
        doc.OrganizationId.Should().Be(auth.OrgId);
        doc.DocumentType.Should().Be("coi");
        doc.UploadedBy.Should().Be("sample");
        // Lands Pending so the REAL extraction worker processes it like any upload (ADR 0028).
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending);
        doc.BlobStoragePath.Should().NotBeNullOrWhiteSpace();

        var vendor = await db.Vendors.IgnoreQueryFilters().FirstAsync(v => v.Id == doc.VendorId);
        vendor.IsSample.Should().BeTrue();
        var caterer = await db.ComplianceTemplates
            .FirstAsync(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName);
        vendor.ComplianceTemplateId.Should().Be(caterer.Id);

        // The generated COI is really in (fake) blob storage.
        var blobs = Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
        (await blobs.DownloadAsync(doc.BlobStoragePath!, default)).Should().NotBeNull();

        // The seed is audited as its own semantic event.
        (await db.AuditLogs.CountAsync(a => a.OrganizationId == auth.OrgId && a.Action == "sample.seeded"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Seed_is_idempotent_returning_the_existing_sample_on_a_repeat_click()
    {
        var auth = await RegisterAndLoginAsync();

        var first = await SeedSampleAsync(auth.Client);
        var second = await SeedSampleAsync(auth.Client);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK); // existing sample, not re-seeded
        DocumentId(await second.Content.ReadFromJsonAsync<JsonElement>())
            .Should().Be(DocumentId(await first.Content.ReadFromJsonAsync<JsonElement>()));

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.OrganizationId == auth.OrgId && d.IsSample && d.DeletedAt == null)).Should().Be(1);
        (await db.Vendors.IgnoreQueryFilters()
            .CountAsync(v => v.OrganizationId == auth.OrgId && v.IsSample && v.DeletedAt == null)).Should().Be(1);
    }

    [Fact]
    public async Task Seed_replays_under_the_same_idempotency_key_without_double_seeding()
    {
        var auth = await RegisterAndLoginAsync();
        var key = Guid.NewGuid().ToString("N");

        await SeedSampleAsync(auth.Client, key);
        await SeedSampleAsync(auth.Client, key);

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.OrganizationId == auth.OrgId && d.IsSample)).Should().Be(1);
    }

    [Fact]
    public async Task Seed_returns_a_friendly_503_and_persists_nothing_when_storage_is_unavailable()
    {
        var auth = await RegisterAndLoginAsync();
        var blobs = (FakeBlobStorageService)Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
        blobs.ThrowUnavailableOnUpload = true;
        try
        {
            var resp = await SeedSampleAsync(auth.Client);

            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("error").GetProperty("code").GetString().Should().Be("storage.unavailable");

            // The upload threw before any SaveChanges, so no sample vendor/document leaked.
            await using var db = CreateSystemDb();
            (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == auth.OrgId && d.IsSample))
                .Should().Be(0);
            (await db.Vendors.IgnoreQueryFilters().CountAsync(v => v.OrganizationId == auth.OrgId && v.IsSample))
                .Should().Be(0);
        }
        finally
        {
            blobs.Reset();
        }
    }

    [Fact]
    public async Task Seed_is_tenant_scoped_one_orgs_sample_is_invisible_to_another()
    {
        var orgA = await RegisterAndLoginAsync();
        var orgB = await RegisterAndLoginAsync();

        await SeedSampleAsync(orgA.Client);

        var bDocs = await orgB.Client.GetFromJsonAsync<JsonElement>("/api/documents/");
        bDocs.GetProperty("data").GetProperty("total").GetInt32().Should().Be(0);
        var bStats = await orgB.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats");
        bStats.GetProperty("data").GetProperty("hasSampleData").GetBoolean().Should().BeFalse();
    }

    // ---- clearing ----

    [Fact]
    public async Task Clear_removes_the_sample_document_and_vendor_deletes_the_blob_and_audits_the_deletes()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = DocumentId(await (await SeedSampleAsync(auth.Client)).Content.ReadFromJsonAsync<JsonElement>());

        string blobPath;
        await using (var setup = CreateSystemDb())
            blobPath = (await setup.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == docId)).BlobStoragePath!;

        var blobs = Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
        (await blobs.DownloadAsync(blobPath, default)).Should().NotBeNull("the sample blob exists before clearing");

        var clear = await auth.Client.DeleteAsync("/api/sample");
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        // No orphaned blob.
        (await blobs.DownloadAsync(blobPath, default)).Should().BeNull();

        await using var db = CreateSystemDb();
        var doc = await db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == docId);
        doc.DeletedAt.Should().NotBeNull();
        (await db.Vendors.IgnoreQueryFilters().FirstAsync(v => v.Id == doc.VendorId)).DeletedAt.Should().NotBeNull();
        (await db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.OrganizationId == auth.OrgId && d.IsSample && d.DeletedAt == null)).Should().Be(0);

        // The soft-deletes left a clean audit trail (no audit orphans) — the interceptor emitted them.
        (await db.AuditLogs.CountAsync(a => a.OrganizationId == auth.OrgId && a.Action == "document.deleted"))
            .Should().BeGreaterThan(0);
        (await db.AuditLogs.CountAsync(a => a.OrganizationId == auth.OrgId && a.Action == "vendor.deleted"))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Clear_with_no_sample_data_is_a_no_op()
    {
        var auth = await RegisterAndLoginAsync();

        var clear = await auth.Client.DeleteAsync("/api/sample");

        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await clear.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("clearedDocuments").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Clear_only_affects_the_callers_own_sample_data()
    {
        var orgA = await RegisterAndLoginAsync();
        var orgB = await RegisterAndLoginAsync();
        await SeedSampleAsync(orgA.Client);
        await SeedSampleAsync(orgB.Client);

        (await orgB.Client.DeleteAsync("/api/sample")).EnsureSuccessStatusCode();

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.OrganizationId == orgA.OrgId && d.IsSample && d.DeletedAt == null)).Should().Be(1);
        (await db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.OrganizationId == orgB.OrgId && d.IsSample && d.DeletedAt == null)).Should().Be(0);
    }

    // ---- dashboard flags + plan limit ----

    [Fact]
    public async Task Dashboard_stats_reflects_the_sample_lifecycle()
    {
        var auth = await RegisterAndLoginAsync();

        (await Stats(auth.Client)).GetProperty("hasSampleData").GetBoolean().Should().BeFalse();

        var docId = DocumentId(await (await SeedSampleAsync(auth.Client)).Content.ReadFromJsonAsync<JsonElement>());
        var seeded = await Stats(auth.Client);
        seeded.GetProperty("hasSampleData").GetBoolean().Should().BeTrue();
        seeded.GetProperty("sampleDocumentId").GetGuid().Should().Be(docId);

        (await auth.Client.DeleteAsync("/api/sample")).EnsureSuccessStatusCode();
        (await Stats(auth.Client)).GetProperty("hasSampleData").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Sample_document_does_not_count_against_the_plan_document_limit()
    {
        var auth = await RegisterAndLoginAsync();
        await SetPortalEntitlementAsync(auth.OrgId, on: false, documentLimit: 1);

        await SeedSampleAsync(auth.Client); // the sample is excluded from the cap

        // With a limit of 1 and the sample excluded, the first REAL upload still fits.
        var first = await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "real.pdf", "application/pdf"));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // The second real upload is the one that trips the cap — proving the sample never consumed a slot.
        var second = await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "real2.pdf", "application/pdf"));
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- compliance contract: the generated sample passes Caterer ----

    [Fact]
    public async Task Sample_certificate_fields_pass_the_seeded_Caterer_checklist()
    {
        // Pins ADR 0028's contract independent of the LLM: a document carrying the four fields the
        // generated sample COI is built to yield — GL ≥ $1M, a future expiration, workers-comp present,
        // and liquor-liability ≥ $1M (#400) — evaluated against the REAL seeded Caterer template,
        // grades to Compliant.
        var orgId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var setup = CreateSystemDb())
        {
            var catererId = (await setup.ComplianceTemplates
                .FirstAsync(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName)).Id;

            setup.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
            setup.Vendors.Add(new Vendor
            {
                Id = vendorId,
                OrganizationId = orgId,
                Name = "Brightside Catering Co. (Sample)",
                ComplianceTemplateId = catererId,
                IsSample = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            setup.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = orgId,
                VendorId = vendorId,
                DocumentType = "coi",
                OriginalFileName = "Sample Certificate of Insurance.pdf",
                GeneralLiabilityLimit = 2_000_000m,
                ExpirationDate = now.AddYears(1),
                ExtractionFields = JsonSerializer.SerializeToDocument(
                    new Dictionary<string, object>
                    {
                        ["workers_comp_limit"] = "1000000",
                        // #400: the Caterer checklist now also grades liquor liability (bar / alcohol
                        // service). The generated sample COI emits a $1M liquor line, so the sample
                        // vendor carries it here too — otherwise the seeded liquor min_value rule fails
                        // and the demo grades NonCompliant.
                        ["liquor_liability_limit"] = "1000000",
                    }),
                IsSample = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await setup.SaveChangesAsync();
        }

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        await using var appDb = CreateAppDb(user);
        await using var sysDb = CreateSystemDb();
        var status = await new ComplianceCheckService(
                appDb, sysDb, new FixedTimeProvider(now), NullLogger<ComplianceCheckService>.Instance)
            .EvaluateForSystemAsync(docId, default);

        status.Should().Be(ComplianceStatus.Compliant);
    }

    // ---- starter-template editability (the #238 templates AC) ----

    [Fact]
    public async Task Cloning_the_Caterer_template_yields_an_editable_org_checklist_with_its_rules()
    {
        var auth = await RegisterAndLoginAsync();

        // The frontend "Use this" flow at the API level: read the system Caterer, then POST an
        // org-owned copy + its rules.
        var templates = await auth.Client.GetFromJsonAsync<JsonElement>("/api/compliance/templates");
        var caterer = templates.GetProperty("data").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == ComplianceTemplateSeed.SampleVendorTemplateName);
        var catererId = caterer.GetProperty("id").GetGuid();
        var detail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/compliance/templates/{catererId}");

        var created = await auth.Client.PostAsJsonAsync("/api/compliance/templates",
            new { name = "Caterer", description = "Cloned for my caterers." });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var cloneId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();

        foreach (var r in detail.GetProperty("data").GetProperty("rules").EnumerateArray())
        {
            (await auth.Client.PostAsJsonAsync($"/api/compliance/templates/{cloneId}/rules", new
            {
                documentType = r.GetProperty("documentType").GetString(),
                fieldName = Str(r, "fieldName"),
                @operator = r.GetProperty("operator").GetString(),
                expectedValue = Str(r, "expectedValue"),
                errorMessage = Str(r, "errorMessage"),
                sortOrder = r.GetProperty("sortOrder").GetInt32(),
            })).EnsureSuccessStatusCode();
        }

        await using var db = CreateSystemDb();
        var clone = await db.ComplianceTemplates.IgnoreQueryFilters()
            .Include(t => t.Rules).FirstAsync(t => t.Id == cloneId);
        // Editable + owned by the org (not the shared system row), with the Caterer rule set.
        clone.IsSystemTemplate.Should().BeFalse();
        clone.OrganizationId.Should().Be(auth.OrgId);
        clone.Rules.Select(r => r.FieldName)
            .Should().BeEquivalentTo(
                ["general_liability_limit", "expiration_date", "workers_comp_limit", "liquor_liability_limit"]);

        // Editable: the org can rename/update its clone (a system template would 4xx here).
        (await auth.Client.PutAsJsonAsync($"/api/compliance/templates/{cloneId}",
            new { name = "My Caterers", description = "Tweaked." })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Clear_returns_a_friendly_503_and_leaves_rows_intact_when_the_blob_delete_fails()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = DocumentId(await (await SeedSampleAsync(auth.Client)).Content.ReadFromJsonAsync<JsonElement>());

        var blobs = (FakeBlobStorageService)Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
        blobs.ThrowOnDelete = true;
        try
        {
            var clear = await auth.Client.DeleteAsync("/api/sample");

            clear.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await clear.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error").GetProperty("code").GetString().Should().Be("storage.unavailable");

            // Blob-delete-first contract: a delete outage fails loudly BEFORE any row is touched, so
            // nothing is half-cleared and the sample stays fully live for a clean retry.
            await using var db = CreateSystemDb();
            (await db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == docId)).DeletedAt.Should().BeNull();
            (await db.Vendors.IgnoreQueryFilters()
                .CountAsync(v => v.OrganizationId == auth.OrgId && v.IsSample && v.DeletedAt == null)).Should().Be(1);
        }
        finally
        {
            blobs.Reset();
        }
    }

    [Fact]
    public async Task Re_seeding_after_the_sample_document_is_deleted_reuses_the_same_sample_vendor()
    {
        var auth = await RegisterAndLoginAsync();
        var firstDoc = DocumentId(await (await SeedSampleAsync(auth.Client)).Content.ReadFromJsonAsync<JsonElement>());

        Guid vendorId;
        await using (var setup = CreateSystemDb())
        {
            // Soft-delete ONLY the sample document, leaving the sample vendor behind.
            var doc = await setup.Documents.FirstAsync(d => d.Id == firstDoc);
            vendorId = doc.VendorId!.Value;
            setup.Documents.Remove(doc);
            await setup.SaveChangesAsync();
        }

        var resp = await SeedSampleAsync(auth.Client);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("vendorId").GetGuid()
            .Should().Be(vendorId, "the lingering sample vendor is reused, not duplicated");

        await using var db = CreateSystemDb();
        (await db.Vendors.IgnoreQueryFilters()
            .CountAsync(v => v.OrganizationId == auth.OrgId && v.IsSample && v.DeletedAt == null)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_seed_requests_create_exactly_one_sample()
    {
        var auth = await RegisterAndLoginAsync();

        // The existence check can't dedupe a TRUE race, so the IX_Documents_OrganizationId_SampleUnique
        // index must: the losers catch the 23505 violation and return the winner's sample.
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SeedSampleAsync(auth.Client)));

        foreach (var r in responses)
            r.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);

        var docIds = new HashSet<Guid>();
        foreach (var r in responses)
            docIds.Add(DocumentId(await r.Content.ReadFromJsonAsync<JsonElement>()));
        docIds.Should().HaveCount(1, "every racing request resolves to the same single sample");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.OrganizationId == auth.OrgId && d.IsSample && d.DeletedAt == null)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_same_key_seed_requests_create_exactly_one_sample()
    {
        var auth = await RegisterAndLoginAsync();
        var key = Guid.NewGuid().ToString("N");

        // Same Idempotency-Key on every racer — exercises the NEW #336 key co-commit branch, distinct from
        // the sibling no-key test which only proves the IX_Documents_OrganizationId_SampleUnique backstop.
        // Losers catch the unique violation and replay the winner via the cached key response.
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SeedSampleAsync(auth.Client, key)));

        foreach (var r in responses)
            r.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);

        var docIds = new HashSet<Guid>();
        foreach (var r in responses)
            docIds.Add(DocumentId(await r.Content.ReadFromJsonAsync<JsonElement>()));
        docIds.Should().HaveCount(1, "every racing request resolves to the same single sample");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters()
            .CountAsync(d => d.OrganizationId == auth.OrgId && d.IsSample && d.DeletedAt == null)).Should().Be(1);
        (await db.Vendors.IgnoreQueryFilters()
            .CountAsync(v => v.OrganizationId == auth.OrgId && v.IsSample && v.DeletedAt == null)).Should().Be(1);
    }

    // ---- helpers ----

    private static async Task<HttpResponseMessage> SeedSampleAsync(HttpClient client, string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/sample");
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> Stats(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats")).GetProperty("data");

    private static Guid DocumentId(JsonElement body) =>
        body.GetProperty("data").GetProperty("documentId").GetGuid();

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    // Minimal magic-byte-valid PDF (the FileValidationService only inspects the %PDF header).
    private static byte[] PdfBytes() =>
        Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");

    private static MultipartFormDataContent UploadForm(byte[] bytes, string fileName, string contentType)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        return content;
    }
}
