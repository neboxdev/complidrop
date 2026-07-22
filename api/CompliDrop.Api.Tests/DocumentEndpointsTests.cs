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
            constraintName: "IX_Documents_OrganizationId_SampleUnique"));
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
                // Far future so the date overlay leaves it genuinely Compliant (a +20d doc would now
                // read ExpiringSoon and correctly drop out of the Compliant filter — #257).
                ExpirationDate = now.AddDays(200),
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
                ExpirationDate = now.AddDays(20),
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

        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var check = body.GetProperty("data").GetProperty("complianceChecks")[0];
        check.GetProperty("isPassed").GetBoolean().Should().BeFalse();
        check.GetProperty("actualValue").GetString().Should().Be("approximately $1M");
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

    [Fact]
    public async Task An_unreadable_edit_does_not_de_queue_a_document_awaiting_extraction()
    {
        // The escalation is deliberately scoped to a SETTLED status. ExtractionWorker claims rows on
        // ExtractionStatus == Pending, so overwriting Pending with ManualRequired here would silently
        // remove the document from the extraction queue forever — trading a bad verdict for a document
        // that never gets read at all. The worker re-decides the flag when it lands.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedDocWithExpirationRule(auth.OrgId);
        await using (var seed = CreateSystemDb())
        {
            var seeded = await seed.Documents.FirstAsync(d => d.Id == docId);
            seeded.ExtractionStatus = ExtractionStatus.Pending;
            await seed.SaveChangesAsync();
        }

        var put = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "expiration_date", fieldValue = "continuous until cancelled" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending,
            "the document must stay claimable by the extraction worker");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "the verdict still fails closed regardless of the queue state");
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
}
