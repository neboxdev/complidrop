using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CompliDrop.Api.Endpoints;

public static class VendorPortalEndpoints
{
    public static void MapVendorPortalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/portal");

        group.MapGet("/{token}", PortalInfo);
        // Rate limits (10/hr per token + 30/hr per ip) are applied by the GLOBAL chained limiter
        // in Program.cs — not via .RequireRateLimiting(...) here, because chaining two named
        // policies on one endpoint silently drops the first (see ADR 0004).
        group.MapPost("/{token}/upload", UploadViaPortal)
            .DisableAntiforgery();
        group.MapGet("/{token}/status/{uploadId:guid}", GetStatus);
    }

    private static async Task<IResult> PortalInfo(
        string token,
        SystemDbContext db,
        CancellationToken ct)
    {
        var link = await db.VendorPortalLinks
            .Include(l => l.Vendor).ThenInclude(v => v.Organization)
            .FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null || !link.IsActive)
            return Error(404, "vendor.portal_token_invalid", "This upload link is no longer active.");
        if (link.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
            return Error(410, "vendor.portal_token_expired", "This upload link has expired.");

        return Results.Ok(new
        {
            data = new
            {
                vendorName = link.Vendor.Name,
                orgName = link.Vendor.Organization.Name,
                instructions = "Upload your Certificate of Insurance, license, or permit here.",
                isActive = link.IsActive,
                uploadCount = link.UploadCount,
                maxUploads = link.MaxUploads
            },
            error = (object?)null
        });
    }

    private static async Task<IResult> UploadViaPortal(
        string token,
        HttpContext http,
        SystemDbContext db,
        IBlobStorageService blobs,
        IFileValidationService validator,
        IImageTranscoder transcoder,
        IAuditLogger audit,
        ILogger<VendorPortalLink> logger,
        CancellationToken ct)
    {
        var link = await db.VendorPortalLinks
            .Include(l => l.Vendor)
            .FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null || !link.IsActive)
            return Error(404, "vendor.portal_token_invalid", "This upload link is no longer active.");
        if (link.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
            return Error(410, "vendor.portal_token_expired", "This upload link has expired.");
        if (link.UploadCount >= link.MaxUploads)
        {
            // Already-at-cap: deactivate idempotently via atomic UPDATE so two concurrent at-cap
            // requests don't fight over tracked-entity state.
            await DeactivateAsync(db, link.Id, ct);
            return Error(429, "vendor.portal_quota_exceeded", "Upload quota reached for this link.");
        }

        if (!http.Request.HasFormContentType)
            return Error(400, "validation.form", "Multipart form expected.");
        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Error(400, "validation.file", "Upload a PDF, JPEG, or PNG file.");

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var validation = validator.Validate(buffer, file.ContentType, file.FileName);
        if (!validation.IsValid)
            return Error(400, validation.ErrorCode ?? "document.unsupported_format", validation.ErrorMessage ?? "Invalid file.");

        // Normalize HEIC/HEIF (iPhone photos) to JPEG on ingest so OCR, the LLM, and the browser
        // preview all see a supported format; PDF/JPEG/PNG pass through untouched. Runs BEFORE the
        // blob upload + quota transaction so a bad photo costs no storage or permit. (#220 / ADR 0018)
        var (storedBytes, storedContentType) = transcoder.NormalizeForStorage(buffer.ToArray(), validation.DetectedContentType!);
        if (storedBytes is null)
            return Error(400, "document.unreadable_image", ImageTranscoderExtensions.UnreadableImageMessage);

        var orgId = link.Vendor.OrganizationId;
        using var storedStream = new MemoryStream(storedBytes);
        var blobName = $"{orgId}/{DateTime.UtcNow:yyyy-MM}/portal-{Guid.NewGuid()}-{SanitizeFileName(file.FileName)}";
        var upload = await blobs.UploadAsync(blobName, storedStream, storedContentType, ct);

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = link.VendorId,
            OriginalFileName = file.FileName,
            BlobStorageUrl = upload.Url,
            BlobStoragePath = blobName,
            FileSizeBytes = storedBytes.Length,
            ContentType = storedContentType,
            DocumentType = form["documentType"].ToString() is var dt && !string.IsNullOrWhiteSpace(dt) ? dt : "other",
            ExtractionStatus = ExtractionStatus.Pending,
            ComplianceStatus = ComplianceStatus.Pending,
            UploadedBy = "vendor_portal",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Atomic reservation + Document insert + audit row, all in one explicit transaction. The
        // cheap `UploadCount >= MaxUploads` check above is racy: two concurrent requests can both
        // see count=N-1 with cap=N and both pass. The atomic UPDATE-WHERE clause is re-evaluated
        // against the row's current state under Postgres's row-level UPDATE lock, so only one of
        // the racing requests increments past the cap. SetProperty(IsActive, count+1 < max) flips
        // the link inactive when this request takes the last permit. rows-affected == 0 means we
        // lost the race (or the link was revoked between the initial read and now).
        //
        // The transaction means: if the Document insert OR the audit-log SaveChanges fail after
        // the reservation, the permit increment rolls back (so the customer doesn't lose paid
        // quota for an upload that didn't persist). The blob is still uploaded by that point —
        // the `finally` best-effort deletes it so storage doesn't leak. If the blob delete itself
        // fails, we log loud so an operator notices the orphan rather than failing silent.
        var documentPersisted = false;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var reserved = await db.VendorPortalLinks
                .Where(l => l.Id == link.Id && l.IsActive && l.UploadCount < l.MaxUploads)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(l => l.UploadCount, l => l.UploadCount + 1)
                    .SetProperty(l => l.IsActive, l => l.UploadCount + 1 < l.MaxUploads),
                    ct);

            if (reserved == 0)
            {
                // Lost the race or link revoked. The `finally` will best-effort delete the blob.
                // Deactivate idempotently outside this (rolled-back) tx.
                await tx.RollbackAsync(ct);
                await DeactivateAsync(db, link.Id, ct);
                return Error(429, "vendor.portal_quota_exceeded", "Upload quota reached for this link.");
            }

            db.Documents.Add(doc);
            await db.SaveChangesAsync(ct);

            // Explicit audit row for the link mutation — ExecuteUpdateAsync bypasses the
            // AuditSaveChangesInterceptor, so without this call the link-mutation audit trail
            // would be lost. organizationIdOverride is needed because the portal upload is
            // unauthenticated (ICurrentUser has no OrgId). Same tx → commits atomically with
            // the Document insert and the counter increment.
            await audit.LogAsync(
                "vendorPortalLink.upload_processed",
                nameof(VendorPortalLink),
                link.Id,
                organizationIdOverride: orgId,
                ct: ct);

            await tx.CommitAsync(ct);
            documentPersisted = true;
        }
        finally
        {
            if (!documentPersisted)
            {
                try { await blobs.DeleteAsync(blobName, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Portal upload: best-effort blob cleanup failed for {BlobName} on link {LinkId}",
                        blobName, link.Id);
                }
            }
        }

        return Results.Ok(new
        {
            data = new
            {
                uploadId = doc.Id,
                extractionStatus = doc.ExtractionStatus.ToString(),
                message = "Thanks — we're processing your document."
            },
            error = (object?)null
        });
    }

    private static Task<int> DeactivateAsync(SystemDbContext db, Guid linkId, CancellationToken ct) =>
        db.VendorPortalLinks
            .Where(l => l.Id == linkId)
            .ExecuteUpdateAsync(set => set.SetProperty(l => l.IsActive, false), ct);

    private static async Task<IResult> GetStatus(
        string token,
        Guid uploadId,
        SystemDbContext db,
        CancellationToken ct)
    {
        var link = await db.VendorPortalLinks.FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null) return Error(404, "vendor.portal_token_invalid", "Link not found.");

        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == uploadId && d.VendorId == link.VendorId, ct);
        if (doc is null) return Error(404, "document.not_found", "Upload not found.");

        return Results.Ok(new
        {
            data = new
            {
                uploadId = doc.Id,
                extractionStatus = doc.ExtractionStatus.ToString(),
                complianceStatus = doc.ComplianceStatus.ToString(),
                expirationDate = doc.ExpirationDate,
                confidence = doc.ExtractionConfidence
            },
            error = (object?)null
        });
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
