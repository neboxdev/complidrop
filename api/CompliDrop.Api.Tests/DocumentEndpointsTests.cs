using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
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
    public async Task Documents_list_and_detail_expose_days_until_expiry()
    {
        // Pins the DaysUntilExpiry helper (#43) at BOTH call sites — the list rows and the detail
        // DTO — plus the null path for a document with no expiry date.
        var auth = await RegisterAndLoginAsync();
        var dated = Guid.NewGuid();
        var undated = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Documents.Add(new Document
            {
                Id = dated,
                OrganizationId = auth.OrgId,
                OriginalFileName = "dated.pdf",
                BlobStorageUrl = "memory://d",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Compliant,
                // Noon, 10 days out: ExpirationDate.Date - today == 10 regardless of the seed time-of-day.
                ExpirationDate = now.Date.AddDays(10).AddHours(12),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Documents.Add(new Document
            {
                Id = undated,
                OrganizationId = auth.OrgId,
                OriginalFileName = "undated.pdf",
                BlobStorageUrl = "memory://u",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Pending,
                ComplianceStatus = ComplianceStatus.Pending,
                ExpirationDate = null,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // Detail: exact whole-day count for the dated doc; null for the undated one.
        var datedDetail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{dated}");
        datedDetail.GetProperty("data").GetProperty("daysUntilExpiry").GetInt32().Should().Be(10);
        var undatedDetail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{undated}");
        undatedDetail.GetProperty("data").GetProperty("daysUntilExpiry").ValueKind.Should().Be(JsonValueKind.Null);

        // List: the same field on the dated row.
        var list = await auth.Client.GetFromJsonAsync<JsonElement>("/api/documents/");
        var datedRow = list.GetProperty("data").GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetGuid() == dated);
        datedRow.GetProperty("daysUntilExpiry").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task View_file_streams_the_original_bytes_inline_with_the_stored_content_type()
    {
        // #254: the detail page's "View file" used to link the raw PRIVATE blob URI (no SAS,
        // container PublicAccessType.None) — Azure rejected every click. The authenticated
        // tenant-filtered proxy is the only safe way to the bytes.
        var auth = await RegisterAndLoginAsync();
        var pdf = PdfBytes();
        var upload = await auth.Client.PostAsync("/api/documents/upload", UploadForm(pdf, "coi.pdf", "application/pdf"));
        var id = await UploadedId(upload);

        var resp = await auth.Client.GetAsync($"/api/documents/{id}/file");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await resp.Content.ReadAsByteArrayAsync()).Should().Equal(pdf, "the proxy must stream the stored bytes verbatim");
        // Inline (render in the tab, don't download) + the original filename for save-as.
        resp.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        resp.Content.Headers.ContentDisposition.FileName.Should().Contain("coi.pdf");
        // Private compliance documents: never cacheable by a shared proxy; nosniff pins the
        // magic-byte-validated content type against browser re-interpretation.
        resp.Headers.CacheControl!.ToString().Should().Contain("no-store");
        resp.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [Fact]
    public async Task View_file_is_tenant_scoped_and_anonymous_gets_401()
    {
        var orgA = await RegisterAndLoginAsync();
        var upload = await orgA.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));
        var id = await UploadedId(upload);

        var orgB = await RegisterAndLoginAsync();
        var crossOrg = await orgB.Client.GetAsync($"/api/documents/{id}/file");
        crossOrg.StatusCode.Should().Be(HttpStatusCode.NotFound, "another org's document id resolves to nothing through the tenant filter");

        var anon = await CreateClient().GetAsync($"/api/documents/{id}/file");
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task View_file_404s_when_the_blob_is_gone_instead_of_500ing()
    {
        var auth = await RegisterAndLoginAsync();
        var upload = await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));
        var id = await UploadedId(upload);

        // The row exists but the blob vanished (manual storage cleanup) — the real Azure
        // client's BlobNotFound maps to the interface's null, which the endpoint 404s.
        var blobs = Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
        string blobPath;
        await using (var db = CreateSystemDb())
            blobPath = (await db.Documents.SingleAsync(d => d.Id == id)).BlobStoragePath!;
        await blobs.DeleteAsync(blobPath, default);

        var resp = await auth.Client.GetAsync($"/api/documents/{id}/file");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task View_file_404s_when_the_row_has_no_blob_path()
    {
        // Directly-seeded rows (and any legacy row) can carry a null BlobStoragePath — the
        // guard must 404, not pass null into the storage layer.
        var auth = await RegisterAndLoginAsync();
        var docId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "pathless.pdf",
                BlobStorageUrl = "blob://pathless",
                BlobStoragePath = null,
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.GetAsync($"/api/documents/{docId}/file");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task View_file_serves_the_STORED_content_type_for_an_ingest_normalized_heic()
    {
        // A HEIC upload is transcoded to JPEG at ingest (#220) — the proxy must serve the
        // STORED content type (image/jpeg), not anything inferred from the ".heic" filename,
        // or the tab can't render it. This is the one input where the two strategies differ.
        var auth = await RegisterAndLoginAsync();
        var upload = await auth.Client.PostAsync("/api/documents/upload",
            UploadForm(HeicPhotoBytes(), "coi.heic", "image/heic"));
        upload.EnsureSuccessStatusCode();
        var id = await UploadedId(upload);

        var resp = await auth.Client.GetAsync($"/api/documents/{id}/file");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        resp.Content.Headers.ContentDisposition!.FileName.Should().Contain("coi.heic",
            "the original upload name is preserved for provenance even though the bytes are JPEG");
    }

    [Fact]
    public async Task Document_detail_no_longer_exposes_the_raw_blob_url()
    {
        // The raw private URI leaked storage-account/container naming and 409'd on every
        // click — off the contract now that the proxy exists (#254).
        var auth = await RegisterAndLoginAsync();
        var upload = await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));
        var id = await UploadedId(upload);

        var detail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{id}");

        detail.GetProperty("data").TryGetProperty("blobStorageUrl", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Upload_with_bytes_not_matching_a_supported_type_is_rejected()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync("/api/documents/upload", UploadForm(TextBytes(), "evil.pdf", "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_with_no_file_field_returns_400()
    {
        // Mirrors the portal's pin (#265 review): a multipart body that carries other
        // fields but omits "file" — a real client that filled metadata and forgot the
        // attachment — must 400 with the validation.file code, not 500.
        var auth = await RegisterAndLoginAsync();

        var form = new MultipartFormDataContent { { new StringContent("coi"), "documentType" } };
        var resp = await auth.Client.PostAsync("/api/documents/upload", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("validation.file");
    }

    [Fact]
    public async Task Upload_with_zero_byte_file_returns_400()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync("/api/documents/upload", UploadForm([], "empty.pdf", "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("validation.file");
    }

    [Fact]
    public async Task Upload_accepts_a_heic_photo_and_stores_it_as_jpeg()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync("/api/documents/upload",
            UploadForm(HeicPhotoBytes(), "coi.heic", "image/heic"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await UploadedId(resp);
        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == id);
        // Transcoded to JPEG on ingest (#220) so OCR/LLM/preview all see a supported format, and it
        // reaches the extraction queue. The original filename is preserved for provenance.
        doc.ContentType.Should().Be("image/jpeg");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending);
        doc.OriginalFileName.Should().Be("coi.heic");
    }

    [Fact]
    public async Task Upload_rejects_an_undecodable_heic_with_a_clean_400()
    {
        var auth = await RegisterAndLoginAsync();
        // A valid HEIC magic-byte header (so validation accepts it) but a body the decoder can't read.
        var brokenHeic = FileWith(0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63);

        var resp = await auth.Client.PostAsync("/api/documents/upload",
            UploadForm(brokenHeic, "broken.heic", "image/heic"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("document.unreadable_image");
    }

    [Fact]
    public async Task Upload_passes_jpeg_and_png_through_unchanged_after_the_heic_refactor()
    {
        // The #220 ingest refactor routes every upload through transcoder.NormalizeForStorage; a
        // non-HEIC type must pass through with its content type intact (not transcoded) and reach the
        // queue. Guards the passthrough end-to-end through the refactored endpoint.
        var auth = await RegisterAndLoginAsync();

        var jpegId = await UploadedId(await auth.Client.PostAsync("/api/documents/upload",
            UploadForm(FileWith(0xFF, 0xD8, 0xFF, 0xE0), "p.jpg", "image/jpeg")));
        var pngId = await UploadedId(await auth.Client.PostAsync("/api/documents/upload",
            UploadForm(FileWith(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), "p.png", "image/png")));

        await using var db = CreateSystemDb();
        var jpeg = await db.Documents.FirstAsync(d => d.Id == jpegId);
        var png = await db.Documents.FirstAsync(d => d.Id == pngId);
        jpeg.ContentType.Should().Be("image/jpeg");
        png.ContentType.Should().Be("image/png");
        jpeg.ExtractionStatus.Should().Be(ExtractionStatus.Pending);
        png.ExtractionStatus.Should().Be(ExtractionStatus.Pending);
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

    // The #336 proving regression test (was parked under the #243 audit). The old check-then-store let
    // two CONCURRENT same-key uploads both miss TryGetAsync and each create a Document. The insert-first
    // co-commit (the dedupe record shares the Document's transaction, guarded by the (orgId, key) unique
    // index) makes exactly one commit win; the loser catches the unique violation and REPLAYS the winner
    // (ADR 0029). So both racing requests return the winner's 201 with the SAME document id, and exactly
    // one Document lands. Sequential replay is guarded by Same_idempotency_key_replays_without_creating_a_duplicate.
    [Fact]
    public async Task Concurrent_same_idempotency_key_creates_only_one_document()
    {
        var auth = await RegisterAndLoginAsync();
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(
            PostWithIdempotency(auth.Client, key),
            PostWithIdempotency(auth.Client, key));

        // Decided loser contract (ADR 0029): replay the winner — the unique-violation conflict proves the
        // winner already committed, so its response is readable immediately, no 409 wait. Both 201, same id.
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
        var ids = await Task.WhenAll(responses.Select(UploadedId));
        ids.Should().OnlyContain(id => id == ids[0], "both racers resolve to the single winning document");

        await using var db = CreateSystemDb();
        (await db.Documents.CountAsync(d => d.OrganizationId == auth.OrgId)).Should().Be(1);

        // The loser rolled its orphaned blob back (it uploaded before the SaveChanges conflict), so exactly
        // one blob — the winner's — survives. Pins the TryDeleteBlobAsync cleanup on the lost-race path.
        var blobs = (FakeBlobStorageService)Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
        blobs.BlobCount.Should().Be(1, "the losing racer cleans up the blob it uploaded before losing");
    }

    [Fact]
    public async Task Expired_idempotency_record_still_replays_rather_than_creating_a_duplicate()
    {
        // ADR 0029: a committed record is a PERMANENT claim — TryGetAsync no longer filters by ExpiresAt,
        // so even a past-TTL record replays (single-use keys make "replay forever" safe, and it keeps the
        // (orgId,key) unique index an airtight backstop with no "present but ignored" rows). Pins that the
        // expiry filter stays dropped: re-introducing it would re-open the create-a-duplicate-after-TTL door.
        var auth = await RegisterAndLoginAsync();
        var key = Guid.NewGuid().ToString("N");
        var existingId = Guid.NewGuid();
        await using (var seed = CreateSystemDb())
        {
            seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Key = key,
                RequestPath = "/api/documents/upload",
                StatusCode = 201,
                ResponseJson = $"{{\"data\":{{\"id\":\"{existingId}\",\"originalFileName\":\"prior.pdf\",\"extractionStatus\":\"Pending\",\"createdAt\":\"2020-01-01T00:00:00Z\"}},\"error\":null}}",
                CreatedAt = DateTime.UtcNow.AddHours(-48),
                ExpiresAt = DateTime.UtcNow.AddHours(-24), // already past the old 24h TTL
            });
            await seed.SaveChangesAsync();
        }

        var resp = await PostWithIdempotency(auth.Client, key);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await UploadedId(resp)).Should().Be(existingId, "the expired record is replayed, not bypassed");
        await using var db = CreateSystemDb();
        (await db.Documents.CountAsync(d => d.OrganizationId == auth.OrgId))
            .Should().Be(0, "replay must not create a new Document");
    }

    // Pins IdempotencyService.IsKeyConflict's hard-coded index name to the model's actual one, so a
    // future index rename is caught here rather than silently turning the concurrent-loser replay path
    // back into an unhandled 500 (IsKeyConflict matching on ConstraintName would stop matching). The
    // concurrent test above proves it end-to-end; this is the fast, direct guard.
    [Fact]
    public void IsKeyConflict_constant_matches_the_idempotency_unique_index_name()
    {
        using var db = CreateSystemDb();
        var index = db.Model.FindEntityType(typeof(IdempotencyRecord))!
            .GetIndexes()
            .Single(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(IdempotencyRecord.OrganizationId), nameof(IdempotencyRecord.Key) }));
        index.GetDatabaseName().Should().Be(IdempotencyService.KeyIndexName);
    }

    [Fact]
    public void IsKeyConflict_matches_only_the_key_index_not_other_unique_violations()
    {
        // Pins that IsKeyConflict keys off the INDEX NAME, not just SqlState 23505 — so an unrelated
        // unique violation in the same transaction (e.g. the sample partial index) is NOT swallowed as a
        // key conflict but surfaces. A future broadening to a SqlState-only check would fail this.
        using var db = CreateSystemDb();
        var service = new IdempotencyService(db);

        var keyConflict = new DbUpdateException("dup", new Npgsql.PostgresException(
            "duplicate key", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation,
            constraintName: IdempotencyService.KeyIndexName));
        service.IsKeyConflict(keyConflict).Should().BeTrue();

        var otherIndexConflict = new DbUpdateException("dup", new Npgsql.PostgresException(
            "duplicate key", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation,
            constraintName: SampleData.DocumentUniqueIndexName));
        service.IsKeyConflict(otherIndexConflict).Should().BeFalse("a different unique index must surface, not be swallowed");

        service.IsKeyConflict(new DbUpdateException("x", new InvalidOperationException("not postgres")))
            .Should().BeFalse();
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

    // ---- #246 data-integrity audit: the raw extraction payload must not bloat the audit log ----

    [Fact]
    public async Task Document_update_audit_omits_the_raw_extraction_payload_but_keeps_the_meaningful_fields()
    {
        // Golden-snapshot control for the #246 interceptor fix (AuditSaveChangesInterceptor). A
        // fully-extracted document carries a large ExtractionRawJson (raw OCR + LLM payload, up to
        // ~20 KB of OCR text). A user-context modification (PATCH) audits the Document via the
        // interceptor — that Before/After must NOT carry the raw payload (the JsonDocument skip already
        // drops ExtractionFields; ExtractionRawJson is its string sibling the type-check missed, so it
        // slipped into BOTH Before and After of every durable, user-exportable audit row). The control
        // half asserts the meaningful small columns DO survive, so the skip didn't gut the audit diff.
        var auth = await RegisterAndLoginAsync();
        var ocrSentinel = $"OCR_SENTINEL_{Guid.NewGuid():N}";
        Guid docId;
        await using (var seed = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            docId = Guid.NewGuid();
            seed.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Pending,
                // The raw OCR+LLM payload (string) that must stay OUT of the audit log.
                ExtractionRawJson = $"{{\"ocr\":{{\"text\":\"{ocrSentinel} lots and lots of OCR text\"}}}}",
                // The JsonDocument sibling (already skipped by type) — pinned here too.
                ExtractionFields = System.Text.Json.JsonDocument.Parse($"{{\"policy_number\":\"{ocrSentinel}-FIELDS\"}}"),
                ExpirationDate = now.AddDays(365),
                CreatedAt = now,
                UpdatedAt = now
            });
            await seed.SaveChangesAsync(); // seeded via SystemDbContext (no current user) → not itself audited
        }

        (await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = "permit" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var read = CreateSystemDb();
        var rows = await read.AuditLogs
            .Where(a => a.OrganizationId == auth.OrgId && a.EntityType == nameof(Document) && a.EntityId == docId)
            .Select(a => new { a.BeforeJson, a.AfterJson })
            .ToListAsync();

        rows.Should().NotBeEmpty("the PATCH must audit the document update");
        var combined = string.Concat(rows.Select(r => (r.BeforeJson ?? "") + (r.AfterJson ?? "")));
        // The fix: the raw payload (and its OCR text) is absent from every Before/After.
        combined.Should().NotContain(ocrSentinel, "the raw OCR/LLM payload must never land in the audit log");
        combined.Should().NotContain("ExtractionRawJson", "the raw-payload column is omitted from the audit snapshot");
        combined.Should().NotContain("ExtractionFields", "the JsonDocument extraction payload stays skipped too");
        // The control: the meaningful, small columns DO survive (the skip didn't gut the audit diff).
        rows.Should().Contain(
            r => r.AfterJson != null && r.AfterJson.Contains("OriginalFileName") && r.AfterJson.Contains("ComplianceStatus"),
            "the meaningful small columns must still be captured in the audit After snapshot");
        // Belt-and-suspenders size bound: with the bulk payload omitted, a Document mutation's audit
        // JSON stays small. Trips if a future large string column re-introduces the leak the skip closes.
        combined.Length.Should().BeLessThan(4096,
            "the audit Before/After for a Document mutation must not carry a bulk extraction payload");
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
        var saved = await verify.Documents.FirstAsync(d => d.Id == docId);
        saved.VendorId.Should().Be(vendorId);
        // #337: the verdict folds into the assignment's transaction now. A thrown recompute degrades it to
        // a safe Pending (never a confident verdict from stale inputs), and the assignment still commits.
        saved.ComplianceStatus.Should().Be(ComplianceStatus.Pending);
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
        var coiId = Guid.NewGuid();
        var permitId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Documents.Add(new Document
            {
                Id = coiId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "a-coi.pdf",
                BlobStorageUrl = "memory://a",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ComplianceStatus = ComplianceStatus.Compliant,
                // Far future so the date overlay leaves it genuinely Compliant (a +20d doc would now
                // read ExpiringSoon and correctly drop out of the Compliant filter — #257).
                ExpirationDate = now.AddDays(200),
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Documents.Add(new Document
            {
                Id = permitId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "b-permit.pdf",
                BlobStorageUrl = "memory://b",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "permit",
                ComplianceStatus = ComplianceStatus.NonCompliant,
                ExpirationDate = now.AddDays(20),
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        // #443: both stored verdicts are REAL rule verdicts here, so back them with check rows — an
        // ungraded affirmative one now reads Pending and would drop out of the Compliant filter.
        await MarkGradedAsync(auth.OrgId, coiId, permitId);

        async Task<JsonElement> Items(string qs) =>
            (await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/?{qs}")).GetProperty("data");

        // type filter
        var byType = await Items("type=permit");
        byType.GetProperty("total").GetInt32().Should().Be(1);
        byType.GetProperty("items")[0].GetProperty("originalFileName").GetString().Should().Be("b-permit.pdf");

        // status filter — a-coi is genuinely Compliant (far-future expiry), b-permit is NonCompliant.
        var byStatus = await Items("status=Compliant");
        byStatus.GetProperty("total").GetInt32().Should().Be(1);
        byStatus.GetProperty("items")[0].GetProperty("originalFileName").GetString().Should().Be("a-coi.pdf");

        // expiresWithin filter: the +20d permit is within 30 days, the +200d coi is not.
        var byExpiry = await Items("expiresWithin=30");
        byExpiry.GetProperty("total").GetInt32().Should().Be(1);
        byExpiry.GetProperty("items")[0].GetProperty("originalFileName").GetString().Should().Be("b-permit.pdf");
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
        var editedDoc = await verify.Documents.FirstAsync(d => d.Id == docId);
        editedDoc.IsManuallyVerified.Should().BeTrue();
        // The doc has no vendor/template, so the best-effort re-eval (#216) returns Pending without
        // throwing — the edit must not wrongly flip the verdict for a doc with no requirement set.
        editedDoc.ComplianceStatus.Should().Be(ComplianceStatus.Pending);
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

    [Fact]
    public async Task Editing_a_typed_field_to_an_unparseable_value_clears_the_column_and_fails_the_rule()
    {
        // ADR 0017: an unparseable correction nulls the typed column so it can't silently contradict
        // the field the user now sees. Since #383 the rule then fails on the UNREADABLE guard (rather
        // than falling back to the raw string and letting min_value report it can't parse), and the
        // recomputed check carries the raw value so the user can see what needs correcting.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithGlRuleAndLimit(auth.OrgId, 1_500_000m, ComplianceStatus.Compliant);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "approximately $1M" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.GeneralLiabilityLimit.Should().BeNull();
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
        // Which guard failed the rule is the whole claim of this test's comment, and isPassed:false
        // with the raw value cannot tell the new UNREADABLE guard apart from the pre-#383 min_value
        // "Unable to parse numeric comparison" path — both produced exactly that. The note and the
        // review flag are what distinguish them, so assert those (#383 review, S2).
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "an unreadable canonical value also routes the document to a human");

        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var check = body.GetProperty("data").GetProperty("complianceChecks")[0];
        check.GetProperty("isPassed").GetBoolean().Should().BeFalse();
        check.GetProperty("actualValue").GetString().Should().Be("approximately $1M");
        check.GetProperty("notes").GetString().Should().Be(ComplianceCheckService.UnreadableValueNote,
            "the UNREADABLE guard failed this rule, not the min_value comparison");
    }

    // ---- #383: a manual edit we can't read must not read back as Compliant ----

    /// <summary>
    /// Seeds a vendor on a COI checklist whose single requirement is
    /// "expiration_date required" — the catalog's "Document must not be expired" — plus a Compliant
    /// COI expiring in a year. The starting point of the reported repro.
    /// </summary>
    private async Task<Guid> SeedDocWithExpirationRule(Guid orgId)
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
            ErrorMessage = "No expiration date was found, so we can't confirm the insurance is current.",
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
            ComplianceStatus = ComplianceStatus.Compliant,
            ExpirationDate = DateTime.UtcNow.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    [Fact]
    public async Task Editing_expiration_date_to_an_unreadable_past_value_does_not_stay_Compliant()
    {
        // THE REPORTED REPRO, verbatim. Setting the expiration to "2020-01-01 (per endorsement)" used
        // to flip the badge back to Compliant with the "Expires" tile showing "—" and an affirmative
        // green "Insurance has not expired": the typed column was nulled (so the Expired branch could
        // not fire) while the `required` rule passed off the non-empty raw string. Three failures, one
        // root cause, all pointing at false-Compliant on a certificate that expired in 2020.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "2020-01-01 (per endorsement)" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExpirationDate.Should().BeNull("nothing can parse that shape — the column is honestly unknown");
        doc.ComplianceStatus.Should().NotBe(ComplianceStatus.Compliant,
            "a certificate whose expiration we cannot read must never certify as compliant");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "the document must keep asking for a readable value, not go quiet with a null column");

        // And the detail payload no longer affirms the requirement was met.
        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var check = body.GetProperty("data").GetProperty("complianceChecks")[0];
        check.GetProperty("isPassed").GetBoolean().Should().BeFalse();
        check.GetProperty("actualValue").GetString().Should().Be("2020-01-01 (per endorsement)");
    }

    [Fact]
    public async Task Correcting_an_unreadable_edit_to_a_real_date_clears_the_manual_review_flag()
    {
        // The escape hatch has to work, or the ManualRequired flag becomes a trap: once the user types
        // a date we CAN read, the document resolves normally and stops nagging.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);

        await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "2020-01-01 (per endorsement)" } }
        });
        var fix = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "2020-01-01" } }
        });
        fix.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed, "a readable value resolves the review");
        doc.ExpirationDate.Should().Be(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Expired,
            "and the date the human actually meant now drives the verdict");
    }

    [Theory]
    [InlineData(ExtractionStatus.Pending)]
    [InlineData(ExtractionStatus.Processing)]
    [InlineData(ExtractionStatus.Failed)]
    public async Task An_unreadable_edit_does_not_overwrite_an_unsettled_extraction_status(ExtractionStatus seeded)
    {
        // The escalation is deliberately scoped to a SETTLED status (Completed / ManualRequired), and
        // this Theory covers ALL THREE excluded states so the allow-list can't be loosened to a bare
        // "!= Pending" without going red (#383 review, S1).
        //   Pending    — ExtractionWorker claims rows on ExtractionStatus == Pending, so overwriting it
        //                would silently DE-QUEUE the document forever: a bad verdict traded for a
        //                document that never gets read at all.
        //   Processing — the worker's in-flight state; it re-decides the flag when it lands.
        //   Failed     — its own louder error state, with a processing-error card of its own.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);
        await using (var seed = CreateSystemDb())
        {
            var doc0 = await seed.Documents.FirstAsync(d => d.Id == docId);
            doc0.ExtractionStatus = seeded;
            await seed.SaveChangesAsync();
        }

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "continuous until cancelled" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(seeded,
            "an unsettled extraction status is the worker's, and the manual-edit escalation must not take it");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "the verdict still fails closed regardless of the queue state");
    }

    /// <summary>
    /// Drives a seeded document into the #383 state through the real endpoint: an unparseable
    /// expiration lands in <see cref="Document.ExtractionFields"/>, the typed column is cleared, and
    /// the document is routed to <see cref="ExtractionStatus.ManualRequired"/>.
    /// </summary>
    private async Task PutUnreadableExpirationAsync(HttpClient client, Guid docId)
    {
        var put = await client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "2020-01-01 (per endorsement)" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Documents.FirstAsync(d => d.Id == docId)).ExtractionStatus
            .Should().Be(ExtractionStatus.ManualRequired, "precondition: the review flag is up");
    }

    [Fact]
    public async Task An_empty_field_save_does_not_clear_an_unresolved_unreadable_review()
    {
        // The detail page deliberately enables Save with NO edits while the amber review card is
        // showing, and posts an empty fields array. Nothing about that request says the stored
        // expiration became readable — but the escalation used to be computed from the field names in
        // THIS request, so an empty save resolved the review and the flag was gone for good (nothing
        // but a full re-extraction re-raises it; ComplianceCheckService never writes ExtractionStatus).
        // Under a checklist with no rule on the field, that flag is the ONLY thing left flagging the
        // document — so two clicks restored the exact silent false-Compliant #383 exists to close.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);
        await PutUnreadableExpirationAsync(auth.Client, docId);

        var empty = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields",
            new { fields = Array.Empty<object>() });
        empty.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExpirationDate.Should().BeNull();
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "looking at a document does not make its expiration parseable");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "#459 / ADR 0052: the two move together, so the empty save cannot restore vendor coverage either");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task Saving_an_unrelated_field_does_not_clear_an_unresolved_unreadable_review()
    {
        // Same root cause, the likelier route: the user fixes the policy number and never touches the
        // expiration. A request that does not mention expiration_date says nothing about whether the
        // stored expiration is readable, so it must not resolve the review either.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);
        await PutUnreadableExpirationAsync(auth.Client, docId);

        var unrelated = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "policy_number", fieldValue = "GL-99887766" } }
        });
        unrelated.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "an edit to another field leaves the unreadable expiration exactly as unreadable");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task Marking_verified_does_not_clear_an_unresolved_unreadable_review()
    {
        // The OTHER ResolveManualReview caller. PUT /verify used to downgrade
        // ManualRequired -> Completed unconditionally, clearing the #383 backstop without the value
        // ever being corrected — and nothing re-raises it afterwards, so Check-again, a rule edit and
        // the hourly sweep all leave it cleared until a full re-extraction. Seeded directly (not via
        // PUT /fields) so this proves the /verify route on its own.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);
        await using (var seed = CreateSystemDb())
        {
            var doc0 = await seed.Documents.FirstAsync(d => d.Id == docId);
            doc0.ExpirationDate = null;
            doc0.ExtractionFields = JsonDocument.Parse("""{"expiration_date":"12/31/2026 (per endorsement)"}""");
            doc0.ExtractionStatus = ExtractionStatus.ManualRequired;
            await seed.SaveChangesAsync();
        }

        var verify = await auth.Client.PutAsync($"/api/documents/{docId}/verify", null);
        verify.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "asserting a document is verified cannot make an unreadable expiration readable");
        doc.IsManuallyVerified.Should().BeTrue("the human did look — that part of the verify still lands");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "#459 / ADR 0052: trust follows the RESULTING state, so the re-raised review re-withdraws it — "
            + "a verify that left Trusted behind would restore vendor coverage on a value nothing can read");
    }

    [Theory]
    [InlineData(ExtractionStatus.Pending)]
    [InlineData(ExtractionStatus.Processing)]
    [InlineData(ExtractionStatus.Failed)]
    public async Task Marking_verified_cannot_buy_trust_over_an_unreadable_value_on_an_unsettled_row(
        ExtractionStatus seeded)
    {
        // #459 review, C2. The sibling test above proves the SETTLED case; these are the three statuses
        // the escalation deliberately refuses to overwrite — and gating TRUST on the same `wasSettled`
        // check handed each of them a clean bill of health for one click:
        //   Pending    — exactly where POST /reextract leaves a re-armed distrusted document. Trust was
        //                the one thing that survived the re-arm (that survival IS the #459 fix); a bare
        //                PUT /verify then handed it straight back, on a value nothing can parse.
        //   Processing — the same window, one poll later.
        //   Failed     — the DURABLE one: the detail page's manual-entry affordance exists for this case,
        //                so a human typing an expiration the parser rejects is ordinary use. The status
        //                can never move here, so nothing else would ever take the trust back.
        // The status gate stays (withdrawing trust de-queues nothing; overwriting Pending would), which
        // is asserted below alongside the trust — the two must move independently, not together.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);
        await using (var seed = CreateSystemDb())
        {
            var doc0 = await seed.Documents.FirstAsync(d => d.Id == docId);
            doc0.ExpirationDate = null;
            doc0.ExtractionFields = JsonDocument.Parse("""{"expiration_date":"12/31/2026 (per endorsement)"}""");
            doc0.ExtractionStatus = seeded;
            doc0.ExtractionTrust = ExtractionTrust.Distrusted; // as the read that routed it here left it
            await seed.SaveChangesAsync();
        }

        (await auth.Client.PutAsync($"/api/documents/{docId}/verify", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "asserting a document is verified cannot make an unreadable expiration readable, and the "
            + "vendor rollup reads trust and nothing else — so a Trusted here is a false 'Covered'");
        doc.ExtractionStatus.Should().Be(seeded,
            "…while the STATUS gate is untouched: it exists only so the escalation can't de-queue the "
            + "document, and withdrawing trust de-queues nothing");
        doc.IsManuallyVerified.Should().BeTrue("the human did look — that part of the verify still lands");
    }

    [Fact]
    public async Task Marking_verified_restores_trust_even_when_the_status_cannot_move()
    {
        // The human exit from the ADR 0042 coverage exclusion, now carried by the trust column (#459 /
        // ADR 0052). For a Failed row the status can NEVER be the exit — ResolveManualReview deliberately
        // refuses to move one ("Failed is its own louder error state"), and the detail page's manual-entry
        // affordance exists precisely for that case — so before #459 the exclusion had to gate on
        // IsManuallyVerified, a flag nothing then cleared. The trust column is the same exit without the
        // stickiness: a later extraction re-decides it. (#464 has since made the flag re-decidable as well,
        // by the same event; the clause still asks trust because trust is the BASIS question.)
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedExtractionStateAsync(auth.OrgId, ExtractionStatus.Failed, claimAge: null);
        await using (var seed = CreateSystemDb())
        {
            var doc0 = await seed.Documents.FirstAsync(d => d.Id == docId);
            doc0.ExtractionTrust = ExtractionTrust.Distrusted; // as MarkFailed leaves it
            await seed.SaveChangesAsync();
        }

        (await auth.Client.PutAsync($"/api/documents/{docId}/verify", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed,
            "the extraction really did fail — the error card and 'Couldn't read' badge must stay");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
            "…but a human has now vouched for the values, which is what the rollup asks about");
    }

    [Fact]
    public async Task A_null_field_value_reads_as_an_honest_absence_not_an_unreadable_value()
    {
        // FieldUpdateRequest.FieldValue is string?, so this stores a JSON null in ExtractionFields.
        // The writer classifies that as Blank (honestly absent) — but the reader's fallback arm
        // returned the literal 4-character string "null" for JsonValueKind.Null, so IsUnreadable
        // called the very same edit UNREADABLE. Writer and reader disagreeing about one value is the
        // exact ambiguity #383 exists to remove, and ADR 0040 is explicit that Blank stays Blank.
        // Symptoms: a check row noted "we couldn't read this" with ActualValue "null" — rendered to
        // the user verbatim — and a document dragged into manual review over a value it does not have.
        // Sticky, too: the JSON null persists, so every later evaluation re-read "null".
        // The expectations below are exactly those of the fieldValue: "" case
        // (Clearing_expiration_date_to_blank_does_not_demand_manual_review).
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = (string?)null } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // What the user is shown first — this is where the disagreement surfaced as user-visible copy.
        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var check = body.GetProperty("data").GetProperty("complianceChecks")[0];
        check.GetProperty("isPassed").GetBoolean().Should().BeFalse();
        check.GetProperty("notes").GetString().Should()
            .Be("Field missing.", "an absence is reported as missing, never as unreadable");
        check.GetProperty("notes").GetString().Should().NotBe(ComplianceCheckService.UnreadableValueNote);
        check.GetProperty("actualValue").ValueKind.Should().Be(JsonValueKind.Null,
            "the user must never be shown the 4-character string \"null\" as what the document says");

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExpirationDate.Should().BeNull();
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant, "required still fails on an absence");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "an absent value is an honest reading of a certificate that shows none, not a read failure");
    }

    [Fact]
    public async Task Clearing_expiration_date_to_blank_does_not_demand_manual_review()
    {
        // Blank is an honest "this certificate shows no expiration" — it fails the `required` rule on
        // its own merits, but it is not a value we failed to READ, so it must not raise the review
        // flag. Without this distinction every COI with a missing field would demand attention.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExpirationDate.Should().BeNull();
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant, "required still fails on a blank");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed, "an honest absence is not a read failure");
    }

    [Theory]
    [InlineData("effective_date", "effective as of 01/01/2026, see endorsement")]
    [InlineData("expiration_date", "2020-01-01 (per endorsement)")]
    [InlineData("general_liability_limit", "$1M per occurrence")]
    public async Task An_unreadable_value_on_ANY_canonical_field_routes_the_document_to_a_human(
        string fieldName, string unreadableValue)
    {
        // CanonicalDocumentFields.All is the anti-drift device for the escalation — but nothing pinned
        // its MEMBERSHIP, and the escalation was only ever exercised for expiration_date and
        // general_liability_limit. Dropping effective_date from All (or adding a fourth field and
        // forgetting it) left the whole suite green while the flag silently stopped re-raising for that
        // field, restoring the #383 hole one field at a time. This drives every name in All through the
        // REAL endpoint, so a shortened list goes red here (#383 review round 2, S1).
        //
        // Note the effective_date / general_liability_limit cases also stay COMPLIANT: this checklist
        // carries a rule on expiration_date only, which still passes. That is precisely the
        // configuration ADR 0040 part 3 exists to cover — with no rule on the field, the review flag is
        // the ONLY thing that surfaces the unreadable value at all.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName, fieldValue = unreadableValue } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            $"an unreadable {fieldName} nulls its typed column, which reads downstream as 'the certificate has no such value'");

        // And the same walk reaches the client, so the detail page can name THIS field.
        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        body.GetProperty("data").GetProperty("unreadableFields")
            .EnumerateArray().Select(e => e.GetString()).Should().Equal(fieldName);
    }

    [Fact]
    public async Task The_detail_payload_names_the_canonical_field_we_could_not_read()
    {
        // The client half of the #383 dead end (review round 2, confirmed bug 2 / S3). The backend
        // correctly refuses to clear ManualRequired while a value stays unreadable — but the detail
        // page's review card was written for the ONLY previous cause of that status, low extraction
        // confidence, and told the user to fix "the ones outlined in amber". An unreadable value is
        // read with HIGH confidence (and UpdateFields pins an edited field's confidence to 1.0), so
        // fieldBorderClass outlines NOTHING: the user is told to correct a field nothing marks, on a
        // document whose flag they cannot clear. The page needs the field NAMES, and they must come
        // from the same DocumentFieldReadability walk that raises the flag — a TypeScript re-derivation
        // of "can this parse?" would drift from the .NET parse it mirrors.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);
        await PutUnreadableExpirationAsync(auth.Client, docId);

        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var data = body.GetProperty("data");
        data.GetProperty("extractionStatus").GetString().Should().Be("ManualRequired");
        data.GetProperty("unreadableFields").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("expiration_date");
        // The field the page will mark carries FULL confidence — the pre-existing amber mechanism
        // cannot see this document, which is the whole reason the names have to be sent.
        data.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("fieldName").GetString() == "expiration_date")
            .GetProperty("confidence").GetDouble().Should().Be(1.0);
    }

    [Fact]
    public async Task The_detail_payload_reports_no_unreadable_fields_for_a_healthy_document()
    {
        // The discriminating other half: unreadableFields must be EMPTY on a document with a perfectly
        // readable expiration, or the page would raise its "we couldn't read this" copy on every doc.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);

        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        body.GetProperty("data").GetProperty("unreadableFields")
            .EnumerateArray().Should().BeEmpty();
    }

    // ---- #444 / ADR 0049: a CLIPPED field value is disclosed, so a Save can't narrow it silently ----

    /// <summary>The venue name a real additional-insured `contains` rule looks for.</summary>
    private const string ClipVenue = "GARDEN-HALL-SENTINEL";

    /// <summary>
    /// Seeds the exact post-extraction shape #444 is about: <c>ExtractionFields</c> (jsonb, no width)
    /// holds the FULL description with the venue name PAST the column width, while the
    /// <c>DocumentField</c> row the detail page renders holds only <c>ColumnClamp.To</c>'s output — and
    /// the stored verdict is the Compliant that full value genuinely earned. Built by hand rather than
    /// by running the worker because this suite owns the REQUEST paths; the worker's own half of the
    /// invariant is pinned in <c>ExtractionWorkerTests</c>.
    /// <para/>
    /// #443 / ADR 0048: that affirmative verdict is only REAL when something graded the document, so the
    /// seed ends in <c>MarkGradedAsync</c> — which reuses the <c>description_of_operations</c> rule
    /// above (the org's only one). Without it the doc has zero <c>ComplianceCheck</c> rows,
    /// <c>ComplianceStatusDeriver.Effective</c> demotes it, every read surface answers <c>Pending</c>,
    /// and the pass→fail narrative below would be told over a pre-state no production path reaches.
    /// </summary>
    private async Task<Guid> SeedDocWithClippedDescriptionAsync(Guid orgId)
    {
        var full = new string('d', InputLengths.DocumentFieldValue + 100) + ClipVenue;
        await using var db = CreateSystemDb();
        var now = DateTime.UtcNow;
        var template = new ComplianceTemplate
        {
            Id = Guid.NewGuid(), OrganizationId = orgId, Name = "Venue COI", CreatedAt = now
        };
        db.ComplianceTemplates.Add(template);
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(),
            ComplianceTemplateId = template.Id,
            DocumentType = "coi",
            FieldName = "description_of_operations",
            Operator = "contains",
            ExpectedValue = ClipVenue,
            ErrorMessage = "The description of operations doesn't name this venue.",
            SortOrder = 1
        });
        var vendor = new Vendor
        {
            Id = Guid.NewGuid(), OrganizationId = orgId, Name = "Acme Catering",
            ComplianceTemplateId = template.Id, CreatedAt = now, UpdatedAt = now
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
            ComplianceStatus = ComplianceStatus.Compliant,
            ExpirationDate = now.AddYears(1),
            ExtractionFields = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["description_of_operations"] = full,
                ["insured_name"] = "Acme Catering LLC"
            })),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Documents.Add(doc);
        db.DocumentFields.Add(new DocumentField
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, FieldName = "description_of_operations",
            FieldValue = ColumnClamp.To(full, InputLengths.DocumentFieldValue),
            FieldType = "text", Confidence = 0.95
        });
        db.DocumentFields.Add(new DocumentField
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, FieldName = "insured_name",
            FieldValue = "Acme Catering LLC", FieldType = "text", Confidence = 0.95
        });
        await db.SaveChangesAsync();
        await MarkGradedAsync(orgId, doc.Id);
        return doc.Id;
    }

    private static JsonElement FieldNamed(JsonElement data, string fieldName) =>
        data.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("fieldName").GetString() == fieldName);

    [Fact]
    public async Task The_detail_payload_marks_a_field_whose_shown_value_was_clipped()
    {
        // #444: ExtractionWorker.Clamp truncates FieldValue at the column width with NO marker, and the
        // detail page binds its editable input to that clamped value ("the value on screen must be
        // exactly the value a Save would send") — so a clipped description_of_operations was rendered as
        // if it were the complete extracted value. The flag has to come from the server: deciding
        // "is this row the clamp of that value?" means reproducing the .NET column width AND
        // ColumnClamp's surrogate back-off, and a TypeScript copy would drift (the ADR 0040
        // unreadableFields reasoning).
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithClippedDescriptionAsync(auth.OrgId);

        var data = (await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}")).GetProperty("data");

        var clipped = FieldNamed(data, "description_of_operations");
        clipped.GetProperty("fieldValueTruncated").GetBoolean().Should().BeTrue();
        clipped.GetProperty("fieldValue").GetString().Should()
            .HaveLength(InputLengths.DocumentFieldValue)
            .And.NotContain(ClipVenue,
                "the shown value genuinely lacks the text the record still holds — that IS the gap");

        // The discriminating other half, on the SAME document: an ordinary field must not be marked, or
        // the page prints "shown shortened" over every value.
        FieldNamed(data, "insured_name").GetProperty("fieldValueTruncated").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public async Task The_clip_flag_clears_once_a_save_makes_the_record_hold_what_is_shown()
    {
        // Why the flag is DERIVED at read time rather than persisted: after a save the record genuinely
        // holds what the page shows, so the warning must stop — a persisted flag would keep asserting
        // a fuller value that no longer exists. UpdateFields writes the submitted text into BOTH copies
        // (and REJECTS an over-length one, ADR 0046), which is what makes them agree.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithClippedDescriptionAsync(auth.OrgId);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "description_of_operations", fieldValue = "Catering services." } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}")).GetProperty("data");
        FieldNamed(data, "description_of_operations").GetProperty("fieldValueTruncated").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public async Task Saving_a_clipped_field_untouched_narrows_the_canonical_value_and_fails_the_check()
    {
        // The reported consequence, pinned so it can't drift: description_of_operations IS a verdict
        // input (the additional-insured `contains` fallback), the jsonb copy carried the venue name past
        // the column width, and PUTting back exactly what the page displayed replaces the canonical
        // value with the clip — flipping a genuine pass to a fail.
        //
        // This test does NOT assert the narrowing is desirable; #444 deliberately changes DISCLOSURE
        // only, and the direction is fail-CLOSED (removing text can only turn a `contains` pass into a
        // fail). It pins the behaviour the WARNING is about, so a future change that quietly removed the
        // narrowing — or made it fail OPEN — surfaces here rather than in a customer's audit export.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithClippedDescriptionAsync(auth.OrgId);

        var before = (await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}")).GetProperty("data");
        // The PRE-STATE this whole narrative rests on, asserted rather than assumed: the pass being
        // flipped has to be a pass the user can actually see. #443 / ADR 0048 demotes an ungraded
        // affirmative verdict to Pending on every read surface, so a seed that skipped MarkGradedAsync
        // would leave "a genuine pass" that reads Pending here and this test would compare a fail
        // against a fail.
        before.GetProperty("complianceStatus").GetString().Should().Be("Compliant");
        var shown = FieldNamed(before, "description_of_operations").GetProperty("fieldValue").GetString();

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "description_of_operations", fieldValue = shown } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionFields!.RootElement.GetProperty("description_of_operations").GetString()
            .Should().Be(shown, "the submitted text becomes the canonical compliance input");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "the venue name lived past the clipped window, so the requirement it met is now unmet");
    }

    [Fact]
    public async Task Typing_the_full_value_back_is_rejected_rather_than_silently_clamped()
    {
        // Why the hint points at "View file" instead of telling the user to retype the whole thing:
        // ADR 0046 REJECTS an over-length correction (user-typed content is never clamped), so there is
        // no path by which a person restores the full text through this field. Copy that implied
        // otherwise would send them into a 400 loop.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithClippedDescriptionAsync(auth.OrgId);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[]
            {
                new
                {
                    fieldName = "description_of_operations",
                    fieldValue = new string('d', InputLengths.DocumentFieldValue + 1)
                }
            }
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("validation.too_long");
    }

    [Fact]
    public async Task Editing_a_gl_limit_with_a_currency_symbol_parses_instead_of_nulling_the_column()
    {
        // #383 secondary, through the endpoint: "$1,000,000" is the most natural way to type a
        // coverage limit, and it used to null the column and fail the min_value comparison — a false
        // NonCompliant on a certificate that met the floor exactly.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithGlRuleAndLimit(auth.OrgId, null, ComplianceStatus.NonCompliant);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "$1,000,000" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.GeneralLiabilityLimit.Should().Be(1_000_000m);
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed, "a value we CAN read needs no review");
    }

    [Fact]
    public async Task Editing_expiration_date_to_a_past_value_flips_the_verdict_to_expired()
    {
        // The date typed-column path through the endpoint: a past expiration is evaluated directly
        // from doc.ExpirationDate (ahead of the rule checks) and makes the document Expired.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithGlRuleAndLimit(auth.OrgId, 1_500_000m, ComplianceStatus.Compliant);

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "2020-06-15" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExpirationDate.Should().Be(new DateTime(2020, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public async Task Editing_the_same_field_twice_in_one_request_keeps_one_row_last_wins()
    {
        // De-dupe guard: a request listing the same field name twice must not create two rows; the
        // last value wins and the row stays in sync with the JSON mirror / typed column.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithGlRuleAndLimit(auth.OrgId, null, ComplianceStatus.NonCompliant);

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[]
            {
                new { fieldName = "general_liability_limit", fieldValue = "500000" },
                new { fieldName = "general_liability_limit", fieldValue = "1500000" },
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.DocumentFields.CountAsync(f => f.DocumentId == docId && f.FieldName == "general_liability_limit"))
            .Should().Be(1);
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.GeneralLiabilityLimit.Should().Be(1_500_000m); // last value wins
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public async Task Editing_fields_persists_even_when_compliance_re_eval_throws()
    {
        // Best-effort guarantee for the field-edit path (#216), mirroring the PATCH counterpart: a
        // throwing inline re-eval must not fail the save the user just made.
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IComplianceCheckService>();
                services.AddScoped<IComplianceCheckService, ThrowingComplianceCheckService>();
            }));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

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

        Guid docId;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var doc = new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.ManualRequired,
                ComplianceStatus = ComplianceStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            docId = doc.Id;
        }

        var resp = await client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "1500000" } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        var saved = await verify.Documents.FirstAsync(d => d.Id == docId);
        // The edit persisted (typed column written, ManualRequired resolved) despite the throwing re-eval.
        saved.GeneralLiabilityLimit.Should().Be(1_500_000m);
        saved.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        // #337: the verdict folds into the edit's transaction. A thrown recompute degrades it to a safe
        // Pending (never a confident verdict from stale inputs); the edit still commits atomically.
        saved.ComplianceStatus.Should().Be(ComplianceStatus.Pending);
        (await verify.DocumentFields.FirstAsync(f => f.DocumentId == docId && f.FieldName == "general_liability_limit"))
            .FieldValue.Should().Be("1500000");
    }

    [Fact]
    public async Task Reextract_resets_both_the_claim_count_and_the_retry_budget()
    {
        // A doc that previously exhausted its retry budget (Failed, FailedAttempts at the cap). A
        // manual re-extract is a deliberate fresh start, so it must clear BOTH ProcessingAttempts
        // (claims) and FailedAttempts (the budget gate introduced in #259) — otherwise the worker
        // would re-fail it on the first hiccup with no real retries.
        var auth = await RegisterAndLoginAsync();
        var docId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            db.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "failed.pdf",
                BlobStorageUrl = "blob://failed",
                BlobStoragePath = "blob/failed.pdf",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                ExtractionStatus = ExtractionStatus.Failed,
                ProcessingAttempts = ExtractionWorker.MaxClaims,
                FailedAttempts = ExtractionWorker.MaxAttempts,
                ProcessingError = "extraction.failed: boom",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = CreateSystemDb();
        var doc = await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending);
        doc.ProcessingAttempts.Should().Be(0);
        doc.FailedAttempts.Should().Be(0, "a manual re-extract must restore the full retry budget");
        doc.ProcessingError.Should().BeNull();
    }

    // ----- #365: the re-extract in-flight guard ------------------------------------------------
    //
    // POST /reextract used to reset the row to Pending UNCONDITIONALLY, which re-arms the queue while a
    // worker's claim is still live — ExtractionWorker holds no lock across the ~240s processing span, so
    // the next 5s poll of a second instance claims the same row and both pay OCR + LLM, both write a
    // feed row, and on a FIRST extraction both insert the whole DocumentField list (no unique index on
    // (DocumentId, FieldName), so the duplicates stick). The guard is the WORKER's own staleness rule.

    /// <summary>
    /// Seeds a document in a given extraction state. <paramref name="claimAge"/> is how long ago the
    /// claim was staked — what the guard reads — so every test below differs only in that value + the
    /// status. <c>null</c> seeds a NULL <c>ProcessingStartedAt</c>.
    /// <para/>
    /// The claim timestamp is written by the DATABASE, as a Postgres interval back from bare
    /// <c>now()</c> — never from this process's <c>DateTime.UtcNow</c>. Since the guard's own cutoff is
    /// computed inside its predicate (ADR 0050 §2, <c>"ProcessingStartedAt" &lt; now() - $interval</c>),
    /// seeding from the host clock would straddle TWO clocks and make the boundary pair below pass only
    /// while |host − Postgres| stays under 30 seconds — a flake that has nothing to do with the guard.
    /// It is also what production does: <c>ClaimSql</c> writes <c>"ProcessingStartedAt" = now()</c>.
    /// ADR 0009-clean — a bare <c>now()</c> on a timestamptz, no <c>AT TIME ZONE</c> anywhere.
    /// <para/>
    /// <paramref name="updatedAt"/> backdates the row's last-write stamp — it needs its own
    /// <c>ExecuteUpdateAsync</c> because <c>AuditSaveChangesInterceptor</c> stamps <c>UpdatedAt</c> on
    /// every tracked save, which is exactly the interceptor the endpoint under test bypasses.
    /// </summary>
    private async Task<Guid> SeedExtractionStateAsync(
        Guid orgId,
        ExtractionStatus status,
        string? claimAge,
        int processingAttempts = 1,
        int failedAttempts = 0,
        DateTime? updatedAt = null)
    {
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            OriginalFileName = "inflight.pdf",
            BlobStorageUrl = "blob://inflight",
            BlobStoragePath = "blob/inflight.pdf",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            ExtractionStatus = status,
            ProcessingStartedAt = null,
            ProcessingAttempts = processingAttempts,
            FailedAttempts = failedAttempts,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        if (claimAge is not null)
        {
            // Interpolated (i.e. parameterized), not string-concatenated: the interval text never reaches
            // the statement as SQL.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Documents" SET "ProcessingStartedAt" = now() - CAST({claimAge} AS interval) WHERE "Id" = {docId}""");
        }
        if (updatedAt is DateTime backdated)
        {
            await db.Documents.Where(d => d.Id == docId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, backdated));
        }
        return docId;
    }

    /// <summary>The claim timestamp Postgres actually stored — the reference the refusal test asserts
    /// "nothing changed" against, since the seed no longer produces a host-clock value to compare to.</summary>
    private async Task<DateTime?> ClaimTimestampAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        return await db.Documents.AsNoTracking()
            .Where(d => d.Id == docId)
            .Select(d => d.ProcessingStartedAt)
            .SingleAsync();
    }

    private static async Task<string> ErrorCode(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task Reextract_is_refused_while_the_extraction_claim_is_still_live()
    {
        // Boundary-tight, and the literal "5m" is duplicated here ON PURPOSE — same reasoning as
        // ExtractionWorkerTests' own -4m30s / -5m30s pair (#62): these two tests exist to be the
        // regression discriminator for the threshold, so reading it off ExtractionWorker.ZombieClaimTimeout
        // would make them pass for any value. -4m30s is inside the 5-minute window, so the worker still
        // believes this claim; the endpoint must agree and refuse. The offset is applied by POSTGRES
        // (SeedExtractionStateAsync), so this boundary and the guard's cutoff read the SAME clock.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedExtractionStateAsync(
            auth.OrgId, ExtractionStatus.Processing, "4 minutes 30 seconds", processingAttempts: 1);
        var seededClaim = await ClaimTimestampAsync(docId);

        var resp = await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "re-arming a live claim is what lets a second worker double-process the same blob");
        (await ErrorCode(resp)).Should().Be("document.extraction_in_progress");

        await using var verify = CreateSystemDb();
        var doc = await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == docId);
        // The refusal must change NOTHING — a partially-applied reset (counters zeroed but status left
        // Processing) would still hand the worker a fresh retry budget it never earned. Compared against
        // the value Postgres stored (exactly, not "close to" a host-clock guess).
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Processing);
        doc.ProcessingStartedAt.Should().Be(seededClaim);
        doc.ProcessingAttempts.Should().Be(1);

        // ExecuteUpdateAsync bypasses the audit interceptor, so "document.reextract_queued" is the whole
        // trace of a re-extract. A refused one must not claim to have queued anything.
        var audited = await verify.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityId == docId && a.Action == "document.reextract_queued");
        audited.Should().BeFalse("nothing was queued");
    }

    [Fact]
    public async Task Reextract_is_allowed_once_the_claim_has_gone_stale_past_the_zombie_threshold()
    {
        // The other half of the boundary pair. -5m30s is PAST the 5-minute window, so ExtractionWorker
        // would zombie-reclaim this row on its own next poll — refusing here would buy no safety and
        // would leave a wedged document with no manual route back. Literal duplicated deliberately; see
        // the live-claim test above.
        var auth = await RegisterAndLoginAsync();
        var seededUpdatedAt = DateTime.UtcNow.AddDays(-7);
        var docId = await SeedExtractionStateAsync(
            auth.OrgId,
            ExtractionStatus.Processing,
            "5 minutes 30 seconds",
            processingAttempts: 3,
            failedAttempts: 2,
            updatedAt: seededUpdatedAt);

        var resp = await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        var doc = await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending);
        doc.ProcessingStartedAt.Should().BeNull();
        doc.ProcessingAttempts.Should().Be(0);
        doc.FailedAttempts.Should().Be(0);

        // UpdatedAt has to ADVANCE, and only the endpoint's explicit .SetProperty does it: ExecuteUpdateAsync
        // bypasses AuditSaveChangesInterceptor, the thing that stamps UpdatedAt everywhere else. Dropping
        // that one line would be silent without this — and the detail page derives its "taking longer than
        // usual" banner from `updatedAt`, so re-reading a week-old document would render a false alarm on
        // the very first poll.
        doc.UpdatedAt.Should().BeAfter(seededUpdatedAt, "the re-arm is a write and must stamp UpdatedAt");
        doc.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1),
            "…and stamp it with NOW, not merely with something later than the seeded value");

        // The POSITIVE half of the audit pair. ExecuteUpdateAsync bypasses the interceptor's entity-mutation
        // row, so this explicit one is the WHOLE audit trace of a re-extract (ADR 0050 §4) — and the only
        // other assertion about it in the suite is the refusal test's BeFalse, which would stay green if the
        // LogAsync call were deleted outright.
        var audited = await verify.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EntityId == docId && a.Action == "document.reextract_queued");
        audited.Should().BeTrue("a queued re-extract must leave the one audit row that names the action");
    }

    [Fact]
    public async Task Reextract_of_a_Processing_row_with_no_claim_timestamp_is_allowed()
    {
        // No writer produces Processing + NULL ProcessingStartedAt (ClaimSql sets both), but if one ever
        // did, ClaimSql's `ProcessingStartedAt < now() - interval` yields NULL for it — so the worker
        // could never reclaim it either. Refusing here would be the only state in the system with no
        // route back from either side, so the guard fails OPEN on missing evidence of a live claim.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedExtractionStateAsync(
            auth.OrgId, ExtractionStatus.Processing, claimAge: null);

        var resp = await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        (await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == docId))
            .ExtractionStatus.Should().Be(ExtractionStatus.Pending);
    }

    [Theory]
    [InlineData(ExtractionStatus.Completed)]
    [InlineData(ExtractionStatus.ManualRequired)]
    [InlineData(ExtractionStatus.Pending)]
    [InlineData(ExtractionStatus.Failed)]
    public async Task Reextract_still_queues_every_status_that_is_not_a_live_claim(ExtractionStatus status)
    {
        // The guard keys on Processing ONLY. A recent ProcessingStartedAt is left on each row on purpose:
        // a guard that looked at the timestamp WITHOUT the status would refuse a just-completed document,
        // which is the single most common "Read again" click there is. Failed carries one in PRODUCTION
        // too — RecordFailedAttempt clears ProcessingStartedAt only on the requeue branch, never on the
        // terminal Failed one — and a failed read is the other most-clicked "Read again" target, so it
        // belongs in this timestamp-bearing theory. (The pre-existing Failed test above covers the counter
        // reset from an exhausted retry budget, not this timestamp dimension.)
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedExtractionStateAsync(auth.OrgId, status, "0 seconds");

        var resp = await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        (await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == docId))
            .ExtractionStatus.Should().Be(ExtractionStatus.Pending);
    }

    [Theory]
    [InlineData(ExtractionStatus.ManualRequired)]
    [InlineData(ExtractionStatus.Failed)]
    public async Task Reextract_re_arms_the_queue_without_clearing_the_distrust(ExtractionStatus status)
    {
        // #459 / ADR 0052 at the write site. Re-arming moves PIPELINE POSITION back to the queue; it says
        // nothing about whether the values currently on the row can be trusted. While trust lived in
        // ExtractionStatus this statement DESTROYED it — Pending written over ManualRequired — so one click
        // flipped the vendor rollup to Covered on the strength of the extraction the system had flagged,
        // and if the re-read then failed nothing restored it (ADR 0042 Amendment 2's whole subject).
        // The absence of ExtractionTrust from the SetProperty list is the fix, and this is its pin:
        // add a .SetProperty(d => d.ExtractionTrust, Trusted) and this goes red.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedExtractionStateAsync(auth.OrgId, status, claimAge: null);
        await using (var seed = CreateSystemDb())
        {
            var doc0 = await seed.Documents.FirstAsync(d => d.Id == docId);
            doc0.ExtractionTrust = ExtractionTrust.Distrusted; // as PersistSuccess / MarkFailed leave it
            await seed.SaveChangesAsync();
        }

        (await auth.Client.PostAsync($"/api/documents/{docId}/reextract", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = CreateSystemDb();
        var doc = await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending, "the queue re-arm still happens");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "the distrust must survive the re-arm — recovering it is what #459 exists to do");
    }

    [Fact]
    public async Task Reextract_is_tenant_scoped_and_a_missing_document_is_not_reported_as_busy()
    {
        // The re-arm is now an ExecuteUpdateAsync, and "0 rows" alone cannot tell "another tenant's id"
        // from "mine, but busy" — answering 409 for either would leak that the id exists AND hand the
        // caller a retry that can never succeed.
        var orgA = await RegisterAndLoginAsync();
        var docId = await SeedExtractionStateAsync(
            orgA.OrgId, ExtractionStatus.Processing, "0 seconds");
        // A SECOND org-A document, in a RE-ARMABLE state, is what makes this a tenant test at all. The
        // live-claim row above is refused by the guard on its own merits, so its 404 would survive the
        // global query filter ceasing to apply to ExecuteUpdateAsync (a stray IgnoreQueryFilters, a
        // hand-rolled predicate without the org bind): the update would still match 0 rows and the
        // existence re-read is what answers. This row would be re-armed by a leaking bulk UPDATE, so its
        // untouched columns below are the assertion that pins the filter on the WRITE.
        var rearmableId = await SeedExtractionStateAsync(
            orgA.OrgId, ExtractionStatus.Completed, "0 seconds", processingAttempts: 3, failedAttempts: 2);

        var orgB = await RegisterAndLoginAsync();
        var crossOrg = await orgB.Client.PostAsync($"/api/documents/{docId}/reextract", content: null);
        crossOrg.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another org's live-claim document must read as absent, not as busy");
        (await ErrorCode(crossOrg)).Should().Be("document.not_found");

        var crossOrgRearmable = await orgB.Client.PostAsync($"/api/documents/{rearmableId}/reextract", content: null);
        crossOrgRearmable.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another org's re-armable document must read as absent too — the tenant filter, not the guard");
        (await ErrorCode(crossOrgRearmable)).Should().Be("document.not_found");

        var missing = await orgA.Client.PostAsync($"/api/documents/{Guid.NewGuid()}/reextract", content: null);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // And org A's own rows are untouched by any of those calls.
        await using var verify = CreateSystemDb();
        (await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == docId))
            .ExtractionStatus.Should().Be(ExtractionStatus.Processing);
        var rearmable = await verify.Documents.AsNoTracking().SingleAsync(d => d.Id == rearmableId);
        rearmable.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "org B's POST must not re-arm org A's document — 404 alone doesn't prove the write was scoped");
        rearmable.ProcessingAttempts.Should().Be(3);
        rearmable.FailedAttempts.Should().Be(2, "…and it must not hand org A's row a fresh retry budget");
    }

    [Fact]
    public async Task The_reextract_guard_reads_the_DATABASE_clock_not_the_API_container_clock()
    {
        // The guard compares against ProcessingStartedAt, which the DATABASE writes (ClaimSql:
        // `"ProcessingStartedAt" = now()`), and it must agree with ClaimSql's own staleness test
        // (`now() - interval …`). A cutoff captured from the APP clock into a local before the query
        // compares TWO clocks: an API container running ahead of Postgres stops refusing claims the worker
        // still holds (the guard silently weakened), one running behind refuses longer than the worker
        // holds. No behavioural test can see that axis — it is invisible until host and Postgres actually
        // disagree, and nothing in a test run makes them — so this asserts on the SQL the endpoint ACTUALLY
        // issues. (SeedExtractionStateAsync stakes its claims with `now() - interval …` for the same
        // reason: the boundary pair below must not straddle two clocks either, or it would flake on skew
        // that says nothing about the guard.) Npgsql renders DateTime.UtcNow as bare now() (ADR 0009-clean:
        // a timestamptz expression, no AT TIME ZONE) only while it stays INSIDE the predicate; hoisting it
        // back into a local turns the comparison into a parameter and fails here.
        //
        // Captured through a Serilog sink because Program.cs composes Serilog with ReadFrom.Services, so a
        // DI-registered ILogEventSink receives every event the host logs — the StartupEnvironmentBanner
        // wiring test's shape, pointed at EF's command log instead of the boot banner.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"user-{Guid.NewGuid():N}@example.com",
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
        var docId = await SeedExtractionStateAsync(orgId, ExtractionStatus.Completed, "0 seconds");

        (await client.PostAsync($"/api/documents/{docId}/reextract", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var update = sink.LastMessageContaining("UPDATE \"Documents\"", "\"ProcessingStartedAt\"");
        update.Should().NotBeNull(
            "the endpoint's re-arm UPDATE must appear in the host's EF command log — if it does not, this "
            + "test is a no-op and proves nothing about which clock the guard reads");
        update.Should().Contain("\"ProcessingStartedAt\" < now()",
            "the staleness cutoff must be computed by POSTGRES (now() - the interval), the same clock that "
            + "wrote ProcessingStartedAt and the same one ClaimSql compares against — not captured from the "
            + "API container's DateTime.UtcNow and shipped as a timestamp parameter");
    }

    [Fact]
    public async Task Marking_verified_still_emits_trust_WITHOUT_forcing_the_status_it_read()
    {
        // #465 / ADR 0052 Amendment 2. This test began life (in the #459 review) pinning the PROPERTY that
        // made an incoherent (status, trust) pair reachable, as an accepted residue. #465 closed that
        // residue, so it changes here — deliberately, and to pin the three halves of the shape that closed
        // it, because ALL are load-bearing and each one alone is a different bug.
        //
        //  1. The UPDATE is STILL partial: it carries ExtractionTrust and NOT ExtractionStatus. Forcing
        //     the status in (the ExtractionWorker.SetTrust / ForceVerdictWrite shape) is the obvious
        //     "make it a whole-tuple writer" fix and it is WRONG here — that shape is right only when the
        //     writer's own conclusion is the FRESHER value, and this request's snapshot status is the
        //     OLDER one. Forcing it would overwrite a worker's fresh ManualRequired with a stale Pending:
        //     a lost update, and a de-queued document (ExtractionWorker claims on Pending).
        //  2. What makes that partiality SAFE is the basis check after the write, taken while this
        //     transaction holds the row's write lock: it re-reads the ROW — the status, and the columns
        //     DocumentFieldReadability consults — and re-runs the decision's inputs against it. A
        //     disagreement rolls the attempt back and re-decides against a fresh read.
        //  3. And the status write, when the decision moves it, lands AFTER that check. This is the half
        //     the #465 review added (round 2, C2): a status inside the UPDATE at (1) does not merely risk
        //     a lost update, it BLINDS the check at (2) — the re-read comes back holding this request's
        //     own value, so in exactly the two arms where the confirmation moves the status no competitor
        //     could ever be seen. Both arms are exercised below, one document each.
        //
        // Dropping any half leaves the suite green on a real defect. The BEHAVIOUR is pinned by
        // DocumentConcurrentEditTests' six A_confirmation_… interleaves; what only this test can see is
        // which statement carried which column, and in what order.
        //
        // Read off the host's EF command log through a Serilog sink — the shape
        // The_reextract_guard_compares_against_the_DATABASE_clock uses, for the same reason: no
        // behavioural assertion can see which columns an UPDATE carried.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"user-{Guid.NewGuid():N}@example.com",
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

        // The state a re-armed distrusted document is really in — Pending in the queue, still carrying the
        // previous read's dissent — and with READABLE values, so the confirmation genuinely flips trust
        // (a no-op assignment would emit no SET clause at all and this test would pass vacuously).
        var docId = await SeedExtractionStateAsync(orgId, ExtractionStatus.Pending, claimAge: null);
        await using (var seed = CreateSystemDb())
        {
            var doc0 = await seed.Documents.FirstAsync(d => d.Id == docId);
            doc0.ExtractionTrust = ExtractionTrust.Distrusted;
            await seed.SaveChangesAsync();
        }

        (await client.PutAsync($"/api/documents/{docId}/verify", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var verify = CreateSystemDb())
        {
            var doc = await verify.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
            doc.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
                "precondition: the confirmation really did change trust, so the UPDATE below is not empty");
            doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending,
                "…and really did leave the status alone, which is the de-queue guard doing its job");
        }

        AssertConfirmationStatementShape(sink, movesTheStatus: false);

        // The SECOND arm, and the one the review's C2 was about: a settled row with readable values, so
        // the confirmation's decision MOVES the status (ManualRequired -> Completed). Same host, same
        // sink; asserted after the first so FindLastIndex reads this request's statements.
        var movingId = await SeedExtractionStateAsync(orgId, ExtractionStatus.ManualRequired, claimAge: null);
        await using (var seed = CreateSystemDb())
        {
            var doc0 = await seed.Documents.FirstAsync(d => d.Id == movingId);
            doc0.ExtractionTrust = ExtractionTrust.Distrusted;
            await seed.SaveChangesAsync();
        }

        (await client.PutAsync($"/api/documents/{movingId}/verify", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var verify = CreateSystemDb())
        {
            var doc = await verify.Documents.AsNoTracking().FirstAsync(d => d.Id == movingId);
            doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
                "precondition: this arm really does move the status, so the statement order below is not "
                + "being read off a request that had nothing to move");
        }

        AssertConfirmationStatementShape(sink, movesTheStatus: true);
    }

    /// <summary>
    /// The three halves of <see cref="Marking_verified_still_emits_trust_WITHOUT_forcing_the_status_it_read"/>,
    /// read off the host's EF command log for the LAST confirmation in it. Shared by the two arms
    /// (status unchanged / status moved) because the first two halves must hold identically in both — the
    /// arm is exactly what half 3 turns on.
    /// </summary>
    private static void AssertConfirmationStatementShape(CapturingLogEventSink sink, bool movesTheStatus)
    {
        var messages = sink.Events
            .Select(e => e.RenderMessage().Replace("\\\"", "\"", StringComparison.Ordinal))
            .ToList();

        var updateIndex = messages.FindLastIndex(m =>
            m.Contains("UPDATE \"Documents\"", StringComparison.Ordinal)
            && m.Contains("\"IsManuallyVerified\"", StringComparison.Ordinal));
        updateIndex.Should().BeGreaterOrEqualTo(0,
            "the confirmation's UPDATE must appear in the host's EF command log — if it does not, this "
            + "test is a no-op and proves nothing about which columns the write carried");

        var update = messages[updateIndex];
        update.Should().Contain("\"ExtractionTrust\" =",
            "anti-no-op: the statement we are reading must be the one that writes trust — and it carries "
            + "trust in BOTH arms because the confirmation FORCES its own conclusion into the UPDATE "
            + "rather than leaving it to EF's diff against a snapshot the row may have moved past");
        update.Should().NotContain("\"ExtractionStatus\" =",
            "half 1 — the write stays PARTIAL. Forcing the status in would write the OLDER value over a "
            + "worker's fresher one and de-queue a live extraction (the worker claims on Pending), which "
            + "trades #465's torn pair for a lost update. ADR 0052 Amendment 2; ADR 0030 Amendment 2 "
            + "Option G is the same refusal for a verdict INPUT");

        var checkIndex = messages.FindIndex(updateIndex + 1, m =>
            m.Contains("SELECT ", StringComparison.Ordinal)
            && m.Contains("d.\"ExtractionStatus\"", StringComparison.Ordinal)
            && m.Contains("d.\"ExtractionFields\"", StringComparison.Ordinal));
        checkIndex.Should().BeGreaterOrEqualTo(0,
            "half 2 — the basis check must run AFTER that UPDATE, which is the whole reason the "
            + "partiality above is safe. It re-reads the ROW this transaction will actually commit (our "
            + "own UPDATE already holds the row's write lock, so the answer cannot move again): the "
            + "status, AND the columns readability is a function of — ExtractionFields is the one this "
            + "assertion names, because a status-only projection is what let a competing read replace the "
            + "values under a confirmation that then vouched for them. Read BEFORE the write it would be "
            + "a plain reload that closes nothing; dropped entirely, #465 is back");

        var statusWrite = messages.FindIndex(checkIndex + 1, m =>
            m.Contains("UPDATE \"Documents\"", StringComparison.Ordinal)
            && m.Contains("\"ExtractionStatus\" =", StringComparison.Ordinal));
        if (movesTheStatus)
        {
            statusWrite.Should().BeGreaterOrEqualTo(0,
                "half 3 — when the decision moves the status, that write lands AFTER the check, under the "
                + "row lock the UPDATE above already holds. Moving it back into that UPDATE is not a "
                + "tidy-up: the check at half 2 would then re-read this request's OWN value and could "
                + "never fail in this arm, which is exactly the tautology #465's review found (C2)");
        }
        else
        {
            statusWrite.Should().Be(-1,
                "…and on an unsettled row there is no status write at all: moving Pending would DE-QUEUE "
                + "the document (the worker claims on ExtractionStatus == Pending)");
        }
    }
}
