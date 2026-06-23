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
            .Include(l => l.Vendor).ThenInclude(v => v.Organization).ThenInclude(o => o.Subscription)
            .FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null || !link.IsActive)
            return Error(404, "vendor.portal_token_invalid", "This upload link is no longer active.");

        // Defense in depth (#269): a soft-deleted vendor or org disappears behind the
        // SystemDbContext query filters, so the Include materializes null. Treat such a
        // link as dead instead of NRE-500ing — covers links that predate the
        // deactivate-on-vendor-delete write, and account deletion (which never touches
        // vendor rows or links). Checked BEFORE expiry so a dead tenant always answers
        // the same 404 as an unknown token — a 410 would acknowledge the token was once
        // valid and invite "get a new link" against a deleted org.
        if (link.Vendor?.Organization is null)
            return Error(404, "vendor.portal_token_invalid", "This upload link is no longer active.");

        // Monetization fence (#261, ADR 0024): an org whose plan lost the portal
        // entitlement (Stripe cancel flips HasVendorPortal=false) answers the SAME neutral
        // message as a revoked link — a vendor must never learn the business's billing
        // status. Before expiry for the dead-tenant reason above; fail-closed on a missing
        // Subscription row (see VendorEndpoints.PortalIncludedInPlanAsync). Links are
        // NOT mutated: re-subscribing flips the flag back and they revive untouched.
        if (link.Vendor.Organization.Subscription is not { HasVendorPortal: true })
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
        IIdempotencyService idem,
        ILogger<VendorPortalLink> logger,
        CancellationToken ct)
    {
        var link = await db.VendorPortalLinks
            .Include(l => l.Vendor).ThenInclude(v => v.Organization).ThenInclude(o => o.Subscription)
            .FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null || !link.IsActive)
            return Error(404, "vendor.portal_token_invalid", "This upload link is no longer active.");

        // Defense in depth (#269): same dead-link treatment as PortalInfo (and same
        // before-expiry ordering, so a dead tenant never answers 410) — a soft-deleted
        // vendor would NRE on link.Vendor below, and a soft-deleted ORG must not keep
        // accepting uploads into its tenant via a direct POST that skips the info page.
        if (link.Vendor?.Organization is null)
            return Error(404, "vendor.portal_token_invalid", "This upload link is no longer active.");

        // Monetization fence (#261, ADR 0024), same as PortalInfo: a lapsed plan's links
        // answer the neutral revoked-link message (no billing-status leak to the vendor),
        // checked before expiry, links never mutated so re-subscribing revives them. A
        // direct POST that skips the info page must hit the same wall.
        if (link.Vendor.Organization.Subscription is not { HasVendorPortal: true } sub)
            return Error(404, "vendor.portal_token_invalid", "This upload link is no longer active.");

        if (link.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
            return Error(410, "vendor.portal_token_expired", "This upload link has expired.");

        // Idempotency (#333 / ADR 0032): a double-submit (double-tap, retried POST) must not duplicate the
        // Document OR burn a MaxUploads permit. The public route has no authenticated principal, so the key
        // is scoped per (org-of-the-link, "portal:{token}:{clientKey}") — the link's org + a token-namespaced
        // client key, so it can't collide with a dashboard upload's key in the same org. Only honored when
        // the client key is a sane length (the route is untrusted and Key is varchar(200)); otherwise the
        // upload just proceeds without dedupe. The co-commit below makes it concurrency-safe.
        var portalOrgId = link.Vendor.OrganizationId;
        var clientKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        var idempotencyKey = !string.IsNullOrWhiteSpace(clientKey) && clientKey.Length <= MaxClientIdempotencyKeyLength
            ? $"portal:{token}:{clientKey}"
            : null;
        if (idempotencyKey is not null)
        {
            var hit = await idem.TryGetAsync(portalOrgId, idempotencyKey, ct);
            if (hit is not null)
                return IdempotencyResults.Replay(hit);
        }

        if (link.UploadCount >= link.MaxUploads)
        {
            // Already-at-cap: deactivate idempotently via atomic UPDATE so two concurrent at-cap
            // requests don't fight over tracked-entity state.
            await DeactivateAsync(db, link.Id, ct);
            return Error(429, "vendor.portal_quota_exceeded", "Upload quota reached for this link.");
        }

        // #261 second fence: the ORG-level document cap. The dashboard path 403s at the
        // cap (DocumentEndpoints.UploadDocument); without this mirror, vendor uploads
        // sail past it. Same read-then-insert semantics as the dashboard (best-effort,
        // deliberately not atomic — a concurrent pair can land one document over a
        // 5-doc fence, which is acceptable and keeps both ingress paths consistent).
        // Distinct error code from the dashboard's plan.limit_reached: this copy faces
        // the VENDOR, who can't upgrade anything — the cure is telling the business.
        // Checked before the form/blob work so a capped-out upload costs nothing.
        if (sub.DocumentLimit is { } docLimit)
        {
            var activeDocs = await db.Documents
                .CountAsync(d => d.OrganizationId == link.Vendor.OrganizationId && d.DeletedAt == null, ct);
            if (activeDocs >= docLimit)
                return Error(403, "vendor.portal_document_limit_reached",
                    $"{link.Vendor.Organization.Name} can't accept more documents right now. Let them know, and they can make room for yours.");
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
        var (storedStream, storedContentType) = transcoder.NormalizeForStorage(buffer, validation.DetectedContentType!);
        if (storedStream is null)
            return Error(400, "document.unreadable_image", ImageTranscoderExtensions.UnreadableImageMessage);

        var orgId = link.Vendor.OrganizationId;
        var blobName = $"{orgId}/{DateTime.UtcNow:yyyy-MM}/portal-{Guid.NewGuid()}-{SanitizeFileName(file.FileName)}";
        BlobUploadResult upload;
        try
        {
            upload = await blobs.UploadAsync(blobName, storedStream, storedContentType, ct);
        }
        catch (BlobStorageUnavailableException)
        {
            // Storage outage → friendly 503 for the external vendor persona, not the generic 500
            // (#248). No internal jargon; the upload consumed no quota (it failed before the permit
            // transaction below).
            return Error(503, "storage.unavailable",
                "We couldn't save your file just now. Please try again in a few minutes.");
        }

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = link.VendorId,
            OriginalFileName = file.FileName,
            BlobStorageUrl = upload.Url,
            BlobStoragePath = blobName,
            FileSizeBytes = storedStream.Length,
            ContentType = storedContentType,
            DocumentType = form["documentType"].ToString() is var dt && !string.IsNullOrWhiteSpace(dt) ? dt : "other",
            ExtractionStatus = ExtractionStatus.Pending,
            ComplianceStatus = ComplianceStatus.Pending,
            UploadedBy = "vendor_portal",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Built before the transaction so the idempotency record can carry it (replayed verbatim to a
        // concurrent/repeat double-submit). Same shape the success return uses below.
        var response = new
        {
            data = new
            {
                uploadId = doc.Id,
                extractionStatus = doc.ExtractionStatus.ToString(),
                message = "Thanks — we're processing your document."
            },
            error = (object?)null
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
            // Co-commit the idempotency record in this SAME transaction (#333 / ADR 0032), so the
            // (OrganizationId, Key) unique index makes a CONCURRENT same-key upload's commit fail — and
            // because the permit reservation above is in this transaction too, that conflict rolls the
            // permit increment back. So the loser duplicates neither the Document NOR the burned permit;
            // it replays the winner (the catch below).
            if (idempotencyKey is not null)
                db.IdempotencyRecords.Add(
                    idem.BuildRecord(portalOrgId, idempotencyKey, http.Request.Path, StatusCodes.Status200OK, response));
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
        catch (DbUpdateException ex) when (idempotencyKey is not null && idem.IsKeyConflict(ex))
        {
            // Lost the concurrent same-key race: another request committed this key first. The exception
            // unwound the transaction (so OUR permit increment rolled back — no burned permit) and the
            // `finally` deletes our orphaned blob; replay the winner so the caller still gets exactly one
            // Document and the same uploadId.
            db.ChangeTracker.Clear();
            var hit = await idem.TryGetAsync(portalOrgId, idempotencyKey, ct);
            return hit is not null ? IdempotencyResults.Replay(hit) : IdempotencyResults.InProgressConflict();
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

        return Results.Ok(response);
    }

    // The client Idempotency-Key is honored only up to this length: the route is untrusted (#333) and the
    // namespaced "portal:{token}:{key}" must fit the IdempotencyRecord.Key varchar(200). A token is ~43
    // chars + the "portal::" wrapper (~8), leaving ample room; a normal UUID/nonce is well under. An
    // oversize key is simply ignored (the upload proceeds without dedupe), never a 500.
    private const int MaxClientIdempotencyKeyLength = 128;

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
        var link = await db.VendorPortalLinks
            .Include(l => l.Vendor).ThenInclude(v => v.Organization)
            .FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null) return Error(404, "vendor.portal_token_invalid", "Link not found.");

        // Dead-tenant guard (#269), same as PortalInfo/UploadViaPortal: a soft-deleted
        // vendor or org must not keep its document status queryable through an old link.
        // IsActive is deliberately NOT checked here — the post-quota link (auto-flipped
        // inactive on the last permitted upload) must still poll that upload's status.
        // The #261 plan gate (HasVendorPortal) is deliberately absent for the same
        // reason: an upload the portal already ACCEPTED stays pollable even if the org's
        // plan lapses mid-extraction — the fence stops new intake, not status reads.
        if (link.Vendor?.Organization is null)
            return Error(404, "vendor.portal_token_invalid", "Link not found.");

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
