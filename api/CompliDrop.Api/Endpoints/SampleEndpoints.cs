using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

/// <summary>
/// The one-click "Try it with a sample certificate" demo (#238). Seeding creates a clearly-labelled
/// sample vendor (assigned the "Caterer" system checklist) plus a generated sample COI, and runs that
/// COI through the REAL extraction pipeline exactly like a normal upload — so a fresh org reaches a
/// real "Compliant" verdict in ~a minute with no file on hand. "Clear sample data" removes everything
/// the demo created (soft-delete + blob cleanup), failing loudly with a friendly error if storage is
/// down rather than the raw 500 the cold-start audit hit (#247/#248).
/// </summary>
public static class SampleEndpoints
{
    private const string SampleVendorName = "Brightside Catering Co. (Sample)";
    // Shared with the send paths that must never mail this address (SampleData, #367).
    private const string SampleVendorEmail = SampleData.VendorEmail;
    private const string SampleFileName = "Sample Certificate of Insurance.pdf";

    public static void MapSampleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sample").RequireAuthorization();
        group.MapPost("/", SeedSample);
        group.MapDelete("/", ClearSample);
    }

    private static async Task<IResult> SeedSample(
        HttpContext http,
        AppDbContext db,
        IBlobStorageService blobs,
        ISampleCertificateGenerator generator,
        IIdempotencyService idem,
        ICurrentUser currentUser,
        IAuditLogger audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        var orgId = currentUser.OrganizationId.Value;

        // Idempotency-Key replay, consistent with the document-upload endpoint.
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var hit = await idem.TryGetAsync(orgId, idempotencyKey, ct);
            if (hit is not null)
                return IdempotencyResults.Replay(hit);
        }

        // Primary idempotency (#238): one sample per org. A repeat click returns the existing sample
        // document instead of seeding again; the partial unique index is the concurrent-race backstop.
        var existing = await CurrentSampleAsync(db, ct);
        if (existing is not null)
            return Results.Ok(SampleEnvelope(existing.Value.DocumentId, existing.Value.VendorId));

        // Reuse a lingering sample vendor (its doc may have been deleted manually) so repeat clicks
        // can't pile up duplicate sample vendors; otherwise create one and assign the Caterer checklist
        // so the generated COI grades to Compliant.
        var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.IsSample, ct);
        if (vendor is null)
        {
            var catererTemplateId = await db.ComplianceTemplates
                .Where(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(ct);
            vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Name = SampleVendorName,
                ContactEmail = SampleVendorEmail,
                Category = ComplianceTemplateSeed.SampleVendorTemplateName,
                ComplianceTemplateId = catererTemplateId,
                IsSample = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Vendors.Add(vendor);
        }

        var orgName = await db.Organizations.Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "Your organization";
        var pdfBytes = generator.GeneratePdf(vendor.Name, orgName);

        var blobName = $"{orgId}/{DateTime.UtcNow:yyyy-MM}/{Guid.NewGuid()}-sample-certificate-of-insurance.pdf";
        BlobUploadResult upload;
        await using (var pdfStream = new MemoryStream(pdfBytes))
        {
            try
            {
                upload = await blobs.UploadAsync(blobName, pdfStream, "application/pdf", ct);
            }
            catch (BlobStorageUnavailableException)
            {
                // Storage outage → friendly 503, not the raw 500 the audit hit (#248).
                return Error(503, "storage.unavailable",
                    "We couldn't set up the sample just now. Please try again in a few minutes.");
            }
        }

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = vendor.Id,
            OriginalFileName = SampleFileName,
            BlobStorageUrl = upload.Url,
            BlobStoragePath = blobName,
            FileSizeBytes = pdfBytes.Length,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Pending, // worker picks it up like any upload
            ComplianceStatus = ComplianceStatus.Pending,
            UploadedBy = "sample",
            IsSample = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Documents.Add(doc);

        var response = SampleEnvelope(doc.Id, vendor.Id);
        // Idempotency (#336): co-commit the dedupe record (if a key was sent) in the SAME transaction as
        // the sample document, the same way the upload endpoint does. Sample seeding was already
        // concurrent-safe via IX_Documents_OrganizationId_SampleUnique; this just folds the old
        // check-then-store StoreAsync into the atomic commit so the key path matches the shared contract.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            db.IdempotencyRecords.Add(
                idem.BuildRecord(orgId, idempotencyKey, http.Request.Path, StatusCodes.Status201Created, response));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a concurrent-seed race — on EITHER the sample partial index
            // (IX_Documents_OrganizationId_SampleUnique) OR the idempotency-key index. Both mean another
            // request seeded first: roll our just-uploaded blob back and replay the winner so the caller
            // still lands on a verdict (idempotent). Disambiguate by the violated INDEX (not just the
            // SqlState) so an UNRELATED future 23505 is surfaced, never silently masked as a sample replay.
            await TryDeleteBlobAsync(blobs, blobName, loggerFactory, ct);
            db.ChangeTracker.Clear();
            if (!string.IsNullOrWhiteSpace(idempotencyKey) && idem.IsKeyConflict(ex))
            {
                var hit = await idem.TryGetAsync(orgId, idempotencyKey, ct);
                if (hit is not null) return IdempotencyResults.Replay(hit);
            }
            if (IsSampleUniqueViolation(ex))
            {
                var winner = await CurrentSampleAsync(db, ct);
                if (winner is not null)
                    return Results.Ok(SampleEnvelope(winner.Value.DocumentId, winner.Value.VendorId));
            }
            // Neither index we expect here, or the expected winner row vanished — surface it rather than
            // swallow it. Log first so an operator can tell a genuine race apart from this.
            loggerFactory.CreateLogger("SampleEndpoints")
                .LogWarning(ex, "Sample seed hit a unique violation it could not resolve to a winner; re-throwing.");
            throw;
        }
        catch
        {
            // Any other persistence failure after the blob upload — don't orphan the blob.
            await TryDeleteBlobAsync(blobs, blobName, loggerFactory, ct);
            throw;
        }

        await audit.LogAsync("sample.seeded", nameof(Document), doc.Id, after: new { doc.Id, vendorId = vendor.Id });

        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ClearSample(
        AppDbContext db,
        IBlobStorageService blobs,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var sampleDocs = await db.Documents.Where(d => d.IsSample).ToListAsync(ct);
        // Loaded WITHOUT Include(PortalLinks): tracking the links would arm EF's client cascade and
        // HARD-delete them on SaveChanges (VendorPortalLink has no DeletedAt) — the #269 trap.
        var sampleVendors = await db.Vendors.Where(v => v.IsSample).ToListAsync(ct);

        if (sampleDocs.Count == 0 && sampleVendors.Count == 0)
            return Results.Ok(new
            {
                data = new { message = "No sample data to clear.", clearedDocuments = 0, clearedVendors = 0 },
                error = (object?)null
            });

        // Delete blobs FIRST so a storage outage fails loudly (friendly 503) BEFORE any row is touched
        // — nothing is half-cleared and a retry re-runs cleanly (DeleteIfExists is a no-op on an
        // already-gone blob, so a partial earlier run self-heals).
        var logger = loggerFactory.CreateLogger("SampleEndpoints");
        foreach (var d in sampleDocs.Where(d => !string.IsNullOrWhiteSpace(d.BlobStoragePath)))
        {
            try
            {
                await blobs.DeleteAsync(d.BlobStoragePath!, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Sample blob delete failed for document {DocumentId}", d.Id);
                return Error(503, "storage.unavailable",
                    "We couldn't clear the sample just now. Please try again in a few minutes.");
            }
        }

        // Soft-delete docs + vendors (tracked Remove → AuditSaveChangesInterceptor sets DeletedAt and
        // emits the document.deleted / vendor.deleted audit rows). Deactivate any portal links the
        // sample vendors picked up first (defensive, mirrors the vendor-delete #269 guard so a
        // soft-deleted vendor never strands a live link). One transaction for atomicity.
        var sampleVendorIds = sampleVendors.Select(v => v.Id).ToList();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (sampleVendorIds.Count > 0)
                await db.VendorPortalLinks
                    .Where(l => sampleVendorIds.Contains(l.VendorId) && l.IsActive)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false), ct);

            db.Documents.RemoveRange(sampleDocs);
            db.Vendors.RemoveRange(sampleVendors);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return Results.Ok(new
        {
            data = new
            {
                message = "Sample data cleared.",
                clearedDocuments = sampleDocs.Count,
                clearedVendors = sampleVendors.Count
            },
            error = (object?)null
        });
    }

    /// <summary>The current org's live sample document (most recent), or null. The doc id is what the
    /// caller deep-links to after seeding.</summary>
    private static async Task<(Guid DocumentId, Guid? VendorId)?> CurrentSampleAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.Documents
            .Where(d => d.IsSample)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.VendorId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Id, row.VendorId);
    }

    private static object SampleEnvelope(Guid documentId, Guid? vendorId) =>
        new { data = new { documentId, vendorId }, error = (object?)null };

    // Gate for the catch: any unique-constraint violation (23505). The seed's SaveChanges can conflict on
    // either the sample partial index or the idempotency-key index; the handler then disambiguates by the
    // specific index (IsKeyConflict / IsSampleUniqueViolation) so an unrelated 23505 is re-thrown.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    // Specifically the one-sample-per-org partial unique index (IX_Documents_OrganizationId_SampleUnique),
    // matched on the index name so it never swallows an unrelated unique violation.
    private const string SampleUniqueIndexName = "IX_Documents_OrganizationId_SampleUnique";
    private static bool IsSampleUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, SampleUniqueIndexName, StringComparison.Ordinal);

    private static async Task TryDeleteBlobAsync(
        IBlobStorageService blobs, string blobName, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            await blobs.DeleteAsync(blobName, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("SampleEndpoints")
                .LogWarning(ex, "Failed to roll back orphaned sample blob {BlobName}", blobName);
        }
    }

    private static IResult Unauthorized() =>
        Results.Json(new { data = (object?)null, error = new { code = "auth.unauthorized", message = "Not authenticated." } }, statusCode: 401);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
