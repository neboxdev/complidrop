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
    public async Task List_filters_by_status_type_and_expiry()
    {
        var auth = await RegisterAndLoginAsync();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                OriginalFileName = "a-coi.pdf",
                BlobStorageUrl = "memory://a",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ComplianceStatus = ComplianceStatus.Compliant,
                ExpirationDate = now.AddDays(20),
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                OriginalFileName = "b-permit.pdf",
                BlobStorageUrl = "memory://b",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "permit",
                ComplianceStatus = ComplianceStatus.NonCompliant,
                ExpirationDate = now.AddDays(60),
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        async Task<JsonElement> Items(string qs) =>
            (await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/?{qs}")).GetProperty("data");

        // type filter
        var byType = await Items("type=permit");
        byType.GetProperty("total").GetInt32().Should().Be(1);
        byType.GetProperty("items")[0].GetProperty("originalFileName").GetString().Should().Be("b-permit.pdf");

        // status filter
        var byStatus = await Items("status=Compliant");
        byStatus.GetProperty("total").GetInt32().Should().Be(1);
        byStatus.GetProperty("items")[0].GetProperty("originalFileName").GetString().Should().Be("a-coi.pdf");

        // expiresWithin filter: the +20d doc is within 30 days, the +60d one is not.
        var byExpiry = await Items("expiresWithin=30");
        byExpiry.GetProperty("total").GetInt32().Should().Be(1);
        byExpiry.GetProperty("items")[0].GetProperty("originalFileName").GetString().Should().Be("a-coi.pdf");
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

    // ---- #193: document-detail compliance checks, vendor email, ManualRequired resolution ----

    /// <summary>
    /// Seeds a vendor (with the supplied contact email + a one-rule "general
    /// liability ≥ $1,000,000" COI template) and a COI document with a single
    /// FAILED compliance check (actual $500,000). Returns the document + vendor ids.
    /// </summary>
    private async Task<(Guid DocId, Guid VendorId)> SeedDocWithFailedCheck(
        Guid orgId,
        string? vendorEmail,
        ExtractionStatus extractionStatus = ExtractionStatus.Completed)
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
        var rule = new ComplianceRule
        {
            Id = Guid.NewGuid(),
            ComplianceTemplateId = template.Id,
            DocumentType = "coi",
            FieldName = "general_liability_limit",
            Operator = "min_value",
            ExpectedValue = "1000000",
            ErrorMessage = "General liability must be at least $1,000,000",
            SortOrder = 1
        };
        db.ComplianceRules.Add(rule);
        var vendor = new Vendor
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = "Acme Catering",
            ContactEmail = vendorEmail,
            ComplianceTemplateId = template.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Vendors.Add(vendor);
        var doc = new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = vendor.Id,
            OriginalFileName = "coi.pdf",
            BlobStorageUrl = "memory://x",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = extractionStatus,
            ComplianceStatus = ComplianceStatus.NonCompliant,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Documents.Add(doc);
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            ComplianceRuleId = rule.Id,
            IsPassed = false,
            ActualValue = "500000",
            Notes = "Value 500000 below required minimum 1000000.",
            CheckedAt = now
        });
        await db.SaveChangesAsync();
        return (doc.Id, vendor.Id);
    }

    [Fact]
    public async Task Get_document_includes_failed_compliance_checks_and_vendor_contact_email()
    {
        var auth = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(auth.OrgId, "vendor@example.com");

        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var data = body.GetProperty("data");

        // The mailto-CTA needs the vendor's email surfaced on the detail payload.
        data.GetProperty("vendorContactEmail").GetString().Should().Be("vendor@example.com");
        // The per-rule check rows ride along so the page can explain non-compliance.
        var checks = data.GetProperty("complianceChecks");
        checks.GetArrayLength().Should().Be(1);
        var check = checks[0];
        check.GetProperty("isPassed").GetBoolean().Should().BeFalse();
        check.GetProperty("ruleErrorMessage").GetString()
            .Should().Be("General liability must be at least $1,000,000");
        check.GetProperty("actualValue").GetString().Should().Be("500000");
    }

    [Fact]
    public async Task Verifying_a_manual_required_document_marks_it_completed()
    {
        var auth = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(auth.OrgId, null, ExtractionStatus.ManualRequired);

        var resp = await auth.Client.PutAsync($"/api/documents/{docId}/verify", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        // A human reviewed it → "Needs your review" resolves to Completed.
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        doc.IsManuallyVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Saving_fields_on_a_manual_required_document_marks_it_completed()
    {
        var auth = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(auth.OrgId, null, ExtractionStatus.ManualRequired);

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "1000000" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
    }

    [Fact]
    public async Task Saving_fields_on_a_completed_document_leaves_status_completed()
    {
        // Guard the transition's "only ManualRequired" condition: a save on an
        // already-Completed document must not regress or change its status.
        var auth = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(auth.OrgId, null, ExtractionStatus.Completed);

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "1500000" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Documents.FirstAsync(d => d.Id == docId)).ExtractionStatus
            .Should().Be(ExtractionStatus.Completed);
    }

    [Fact]
    public async Task Editing_an_existing_field_records_the_original_value_and_marks_verified()
    {
        // Covers the UpdateFields "field already exists" branch (the common UI
        // path): the edit is applied, the pre-edit value is preserved in
        // OriginalValue, and the document is marked manually verified.
        var auth = await RegisterAndLoginAsync();
        Guid docId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
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
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Documents.Add(doc);
            db.DocumentFields.Add(new DocumentField
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                FieldName = "policy_number",
                FieldValue = "OLD-123",
                FieldType = "text",
                Confidence = 0.5,
                IsManuallyEdited = false
            });
            await db.SaveChangesAsync();
            docId = doc.Id;
        }

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "policy_number", fieldValue = "NEW-456" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        var field = await verify.DocumentFields.FirstAsync(
            f => f.DocumentId == docId && f.FieldName == "policy_number");
        field.FieldValue.Should().Be("NEW-456");
        field.OriginalValue.Should().Be("OLD-123");
        field.IsManuallyEdited.Should().BeTrue();
        (await verify.Documents.FirstAsync(d => d.Id == docId)).IsManuallyVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Compliance_checks_endpoint_returns_rows_for_the_owning_org()
    {
        var owner = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(owner.OrgId, "vendor@example.com");

        var resp = await owner.Client.GetAsync($"/api/compliance/checks/{docId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("ruleErrorMessage").GetString()
            .Should().Be("General liability must be at least $1,000,000");
    }

    [Fact]
    public async Task Compliance_checks_endpoint_is_not_readable_by_another_org()
    {
        // IDOR guard: ComplianceCheck has no tenant query filter, so the checks
        // endpoint must gate on the Document being visible to the caller's org.
        var owner = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(owner.OrgId, "vendor@example.com");

        var other = await RegisterAndLoginAsync(); // a different organization
        var resp = await other.Client.GetAsync($"/api/compliance/checks/{docId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- #216: manual field edits sync the compliance inputs + re-evaluate the verdict ----

    /// <summary>
    /// Seeds a vendor carrying a "general liability ≥ $1,000,000" COI template and a COI document
    /// with the supplied starting GL limit + compliance status (no pre-seeded check rows). Returns
    /// the document id. Used to drive a manual GL-limit edit and assert the verdict moves.
    /// </summary>
    private async Task<Guid> SeedDocWithGlRuleAndLimit(Guid orgId, decimal? glLimit, ComplianceStatus status)
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
            FieldName = "general_liability_limit",
            Operator = "min_value",
            ExpectedValue = "1000000",
            ErrorMessage = "General liability must be at least $1,000,000",
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
            VendorId = vendor.Id,
            OriginalFileName = "coi.pdf",
            BlobStorageUrl = "memory://x",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed,
            ComplianceStatus = status,
            GeneralLiabilityLimit = glLimit,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    [Fact]
    public async Task Editing_general_liability_limit_above_the_minimum_flips_noncompliant_to_compliant()
    {
        // The marquee #216 regression: correcting a misread GL limit above the required minimum
        // must move the verdict, not recompute the identical NonCompliant answer. SeedDocWithFailedCheck
        // gives a NonCompliant COI with a stale "$500,000 below minimum" check in front of the user.
        var auth = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(auth.OrgId, null);

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "1500000" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
        // The edit reached the typed column compliance reads, not just the DocumentField row.
        doc.GeneralLiabilityLimit.Should().Be(1_500_000m);
    }

    [Fact]
    public async Task Editing_a_field_refreshes_the_compliance_checks_on_the_detail_payload()
    {
        // AC #2: the detail-page explainer (complianceStatus + per-rule check rows) reflects the
        // corrected verdict after Save — the old failing row is replaced with a passing one.
        var auth = await RegisterAndLoginAsync();
        var (docId, _) = await SeedDocWithFailedCheck(auth.OrgId, null);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "1500000" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var data = body.GetProperty("data");
        data.GetProperty("complianceStatus").GetString().Should().Be("Compliant");
        data.GetProperty("generalLiabilityLimit").GetDecimal().Should().Be(1_500_000m);
        var checks = data.GetProperty("complianceChecks");
        checks.GetArrayLength().Should().Be(1);
        checks[0].GetProperty("isPassed").GetBoolean().Should().BeTrue();
        checks[0].GetProperty("actualValue").GetString().Should().Be("1500000");
    }

    [Fact]
    public async Task Editing_a_json_field_that_feeds_a_required_rule_updates_the_verdict()
    {
        // Proves the JSON mirror (not only the typed columns) reaches compliance: a non-typed field
        // with a `required` rule starts missing (NonCompliant) and the edit supplies it.
        var auth = await RegisterAndLoginAsync();
        Guid docId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var template = new ComplianceTemplate
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Name = "Venue COI",
                CreatedAt = now
            };
            db.ComplianceTemplates.Add(template);
            db.ComplianceRules.Add(new ComplianceRule
            {
                Id = Guid.NewGuid(),
                ComplianceTemplateId = template.Id,
                DocumentType = "coi",
                FieldName = "additional_insured",
                Operator = "required",
                SortOrder = 1
            });
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Name = "Acme Catering",
                ComplianceTemplateId = template.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Vendors.Add(vendor);
            var doc = new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                VendorId = vendor.Id,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.NonCompliant,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            docId = doc.Id;
        }

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "additional_insured", fieldValue = "Acme Property LLC" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        (await verify.Documents.FirstAsync(d => d.Id == docId)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public async Task Editing_a_correct_value_down_below_the_minimum_flips_compliant_to_noncompliant()
    {
        // Symmetry: the input sync moves the verdict in both directions, so an edit that makes a
        // document worse is reflected too (not just corrections that help).
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithGlRuleAndLimit(auth.OrgId, 1_500_000m, ComplianceStatus.Compliant);

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "500000" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
        doc.GeneralLiabilityLimit.Should().Be(500_000m);
    }
}
