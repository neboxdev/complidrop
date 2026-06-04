using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>Integration tests for the document upload pipeline (magic bytes, plan limit, idempotency, soft delete, tenant scoping).</summary>
public sealed class DocumentEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{

    private static async Task<Guid> UploadedId(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();

    [Fact]
    public async Task Upload_valid_pdf_returns_201_and_appears_in_the_list()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var list = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/");
        list.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Upload_with_bytes_not_matching_a_supported_type_is_rejected()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync("/api/documents/upload", UploadForm(TextBytes(), "evil.pdf", "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_is_refused_once_the_free_plan_limit_is_reached()
    {
        var auth = await RegisterAndLoginAsync();

        // Free tier allows 5 documents; seed 5 so the next upload is the 6th.
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 5; i++)
                db.Documents.Add(new Document
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = auth.OrgId,
                    OriginalFileName = $"d{i}.pdf",
                    BlobStorageUrl = "memory://x",
                    FileSizeBytes = 1,
                    ContentType = "application/pdf",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "sixth.pdf", "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Same_idempotency_key_replays_without_creating_a_duplicate()
    {
        var auth = await RegisterAndLoginAsync();
        var key = Guid.NewGuid().ToString("N");

        (await PostWithIdempotency(auth.Client, key)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await PostWithIdempotency(auth.Client, key)).StatusCode.Should().Be(HttpStatusCode.Created);

        await using var db = CreateSystemDb();
        (await db.Documents.CountAsync(d => d.OrganizationId == auth.OrgId)).Should().Be(1);
    }

    private async Task<HttpResponseMessage> PostWithIdempotency(HttpClient client, string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/documents/upload")
        {
            Content = UploadForm(PdfBytes(), "c.pdf", "application/pdf")
        };
        req.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Soft_deleted_document_is_404_and_absent_from_the_list()
    {
        var auth = await RegisterAndLoginAsync();
        var id = await UploadedId(await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "c.pdf", "application/pdf")));

        (await auth.Client.DeleteAsync($"/api/documents/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await auth.Client.GetAsync($"/api/documents/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var list = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/");
        list.GetProperty("data").GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_document_is_not_visible_to_another_org()
    {
        var owner = await RegisterAndLoginAsync();
        var id = await UploadedId(await owner.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "c.pdf", "application/pdf")));

        var other = await RegisterAndLoginAsync(); // a different organization

        (await other.Client.GetAsync($"/api/documents/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- PATCH /api/documents/{id} : assign vendor / change type (#186) ----

    /// <summary>
    /// Seeds a vendor carrying a one-rule "expiration_date required" COI template
    /// plus an orphaned, fully-extracted COI document with a far-future expiry.
    /// Returns the new document and vendor ids.
    /// </summary>
    private async Task<(Guid DocId, Guid VendorId)> SeedOrphanDocAndVendorWithTemplate(Guid orgId)
    {
        await using var db = CreateSystemDb();
        var now = DateTime.UtcNow;

        var template = new ComplianceTemplate
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = "Venue COI",
            CreatedAt = now
        };
        db.ComplianceTemplates.Add(template);
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(),
            ComplianceTemplateId = template.Id,
            DocumentType = "coi",
            FieldName = "expiration_date",
            Operator = "required",
            SortOrder = 1
        });
        var vendor = new Vendor
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = "Acme Catering",
            ComplianceTemplateId = template.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Vendors.Add(vendor);
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = null,
            OriginalFileName = "coi.pdf",
            BlobStorageUrl = "memory://x",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed,
            ComplianceStatus = ComplianceStatus.Pending,
            ExpirationDate = now.AddDays(365),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return (doc.Id, vendor.Id);
    }

    [Fact]
    public async Task Assigning_a_vendor_with_a_requirement_set_produces_a_compliance_verdict()
    {
        var auth = await RegisterAndLoginAsync();
        var (docId, vendorId) = await SeedOrphanDocAndVendorWithTemplate(auth.OrgId);

        var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var updated = await db.Documents.FirstAsync(d => d.Id == docId);
        updated.VendorId.Should().Be(vendorId);
        // The forever-"Pending" verdict flips to a real answer because the
        // newly-assigned vendor's template has a rule the COI satisfies.
        updated.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
        // A vendor-only PATCH must NOT clobber the document type (partial update).
        updated.DocumentType.Should().Be("coi");
    }

    [Fact]
    public async Task Changing_the_document_type_persists_the_new_type()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = await UploadedId(await auth.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "doc.pdf", "application/pdf")));

        var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = "permit" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var updated = await db.Documents.FirstAsync(d => d.Id == docId);
        updated.DocumentType.Should().Be("permit");
        // A type-only PATCH must NOT touch the vendor assignment (partial update).
        updated.VendorId.Should().BeNull();
    }

    [Fact]
    public async Task An_unrecognized_document_type_is_rejected()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = await UploadedId(await auth.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "doc.pdf", "application/pdf")));

        var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = "banana" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Assigning_a_vendor_that_does_not_exist_is_rejected()
    {
        var auth = await RegisterAndLoginAsync();
        var docId = await UploadedId(await auth.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "doc.pdf", "application/pdf")));

        var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = Guid.NewGuid() });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_vendor_from_another_org_cannot_be_assigned()
    {
        var owner = await RegisterAndLoginAsync();
        var docId = await UploadedId(await owner.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "doc.pdf", "application/pdf")));

        // A vendor that genuinely exists, but in a DIFFERENT org — the tenant
        // filter must make it invisible to the owner's PATCH.
        var other = await RegisterAndLoginAsync();
        Guid otherVendorId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = other.OrgId,
                Name = "Other Org Vendor",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Vendors.Add(vendor);
            await db.SaveChangesAsync();
            otherVendorId = vendor.Id;
        }

        var resp = await owner.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = otherVendorId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var verify = CreateSystemDb();
        (await verify.Documents.FirstAsync(d => d.Id == docId)).VendorId.Should().BeNull();
    }

    [Fact]
    public async Task Patching_a_nonexistent_document_is_404()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{Guid.NewGuid()}", new { documentType = "coi" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_document_cannot_be_patched_by_another_org()
    {
        var owner = await RegisterAndLoginAsync();
        var docId = await UploadedId(await owner.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "doc.pdf", "application/pdf")));

        var other = await RegisterAndLoginAsync();

        var resp = await other.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = "permit" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Assigning_a_vendor_with_no_template_leaves_the_document_pending()
    {
        var auth = await RegisterAndLoginAsync();
        Guid docId, vendorId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Name = "No-Template Vendor",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Vendors.Add(vendor);
            var doc = new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Pending,
                ExpirationDate = now.AddDays(365),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            docId = doc.Id;
            vendorId = vendor.Id;
        }

        var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        var updated = await verify.Documents.FirstAsync(d => d.Id == docId);
        updated.VendorId.Should().Be(vendorId);
        // No requirement set → no verdict to give; the document stays Pending
        // rather than being wrongly marked Compliant/NonCompliant.
        updated.ComplianceStatus.Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public async Task A_no_op_patch_returns_ok_without_changes()
    {
        var auth = await RegisterAndLoginAsync();
        // A freshly-uploaded document defaults to type "other"; re-asserting it
        // is a no-op that must skip the save / re-eval / audit work.
        var docId = await UploadedId(await auth.Client.PostAsync(
            "/api/documents/upload", UploadForm(PdfBytes(), "doc.pdf", "application/pdf")));

        var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = "other" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("message").GetString().Should().Be("No changes.");
    }

    [Fact]
    public async Task The_assignment_persists_even_when_compliance_re_eval_throws()
    {
        // Best-effort guarantee: a failing inline compliance recompute must not
        // fail the vendor assignment the user just made. Swap in a throwing
        // IComplianceCheckService for this one host.
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IComplianceCheckService>();
                services.AddScoped<IComplianceCheckService, ThrowingComplianceCheckService>();
            }));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        // Register through the derived host (shares the same test database).
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "Password1234",
            fullName = "Test User",
            companyName = "Test Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        reg.EnsureSuccessStatusCode();
        var orgId = (await reg.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("organizationId").GetGuid();

        Guid docId, vendorId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Name = "Acme",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Vendors.Add(vendor);
            var doc = new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            docId = doc.Id;
            vendorId = vendor.Id;
        }

        var resp = await client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        (await verify.Documents.FirstAsync(d => d.Id == docId)).VendorId.Should().Be(vendorId);
    }

    [Fact]
    public async Task Upload_persists_a_supplied_vendor_and_document_type()
    {
        var auth = await RegisterAndLoginAsync();
        Guid vendorId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Name = "Acme",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Vendors.Add(vendor);
            await db.SaveChangesAsync();
            vendorId = vendor.Id;
        }

        var resp = await auth.Client.PostAsync("/api/documents/upload",
            UploadForm(PdfBytes(), "coi.pdf", "application/pdf", new Dictionary<string, string>
            {
                ["vendorId"] = vendorId.ToString(),
                ["documentType"] = "permit",
            }));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await UploadedId(resp);
        await using var verify = CreateSystemDb();
        var doc = await verify.Documents.FirstAsync(d => d.Id == id);
        doc.VendorId.Should().Be(vendorId);
        doc.DocumentType.Should().Be("permit");
    }

    [Fact]
    public async Task List_paginates_with_page_and_pageSize()
    {
        var auth = await RegisterAndLoginAsync();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 3; i++)
                db.Documents.Add(new Document
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = auth.OrgId,
                    OriginalFileName = $"d{i}.pdf",
                    BlobStorageUrl = "memory://x",
                    FileSizeBytes = 1,
                    ContentType = "application/pdf",
                    CreatedAt = now.AddSeconds(i),
                    UpdatedAt = now
                });
            await db.SaveChangesAsync();
        }

        var p1 = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?page=1&pageSize=2");
        p1.GetProperty("data").GetProperty("total").GetInt32().Should().Be(3);
        p1.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(2);
        p1.GetProperty("data").GetProperty("page").GetInt32().Should().Be(1);

        var p2 = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?page=2&pageSize=2");
        p2.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task List_search_matches_file_name_and_vendor_name_case_insensitively()
    {
        var auth = await RegisterAndLoginAsync();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Name = "Northside Tents",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Vendors.Add(vendor);
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                OriginalFileName = "acme-coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                VendorId = vendor.Id,
                OriginalFileName = "permit-2026.pdf",
                BlobStorageUrl = "memory://y",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "permit",
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        // Match on file name.
        var byFile = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?search=ACME");
        byFile.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
        byFile.GetProperty("data").GetProperty("items")[0]
            .GetProperty("originalFileName").GetString().Should().Be("acme-coi.pdf");

        // Match on the assigned vendor's name (case-insensitive).
        var byVendor = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?search=northside");
        byVendor.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
        byVendor.GetProperty("data").GetProperty("items")[0]
            .GetProperty("originalFileName").GetString().Should().Be("permit-2026.pdf");

        // A term that matches neither returns nothing.
        var none = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/?search=zzz-nomatch");
        none.GetProperty("data").GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Upload_with_a_cross_org_vendor_is_rejected()
    {
        var owner = await RegisterAndLoginAsync();
        var other = await RegisterAndLoginAsync();
        Guid otherVendorId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = other.OrgId,
                Name = "Other Org Vendor",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Vendors.Add(vendor);
            await db.SaveChangesAsync();
            otherVendorId = vendor.Id;
        }

        var resp = await owner.Client.PostAsync("/api/documents/upload",
            UploadForm(PdfBytes(), "coi.pdf", "application/pdf", new Dictionary<string, string>
            {
                ["vendorId"] = otherVendorId.ToString(),
            }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
