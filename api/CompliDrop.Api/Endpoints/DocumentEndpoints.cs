using System.Text.Json;
using System.Text.Json.Nodes;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Compliance;
using CompliDrop.Api.DTOs.Documents;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/documents").RequireAuthorization();

        group.MapGet("/", ListDocuments);
        group.MapGet("/{id:guid}", GetDocument);
        group.MapGet("/{id:guid}/file", GetDocumentFile);
        group.MapPost("/upload", UploadDocument)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(10 * 1024 * 1024));
        group.MapPatch("/{id:guid}", UpdateDocument);
        group.MapPut("/{id:guid}/fields", UpdateFields);
        group.MapPut("/{id:guid}/verify", MarkVerified);
        group.MapPost("/{id:guid}/reextract", Reextract);
        group.MapDelete("/{id:guid}", DeleteDocument);
    }

    // The private AllowedDocumentTypes literal that used to live here is GONE (#389, the collapse ADR
    // 0045 §"Option E" deferred to this ticket). It was a second copy of the vocabulary kept only
    // because these endpoint files were in flight; all three sites in this file — the PATCH type edit,
    // the upload path, and the portal twin — now ask Services/CanonicalDocumentTypes directly, so the
    // pinned-equal test that guarded the duplication has nothing left to guard and is retired with it.

    private static async Task<IResult> ListDocuments(
        AppDbContext db,
        ICurrentUser currentUser,
        CancellationToken ct,
        [FromQuery] string? status = null,
        [FromQuery] string? type = null,
        [FromQuery] Guid? vendorId = null,
        [FromQuery] int? expiresWithin = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? sortBy = "createdAt",
        [FromQuery] string? sortDir = "desc")
    {
        if (currentUser.OrganizationId is null) return Unauthorized();

        var today = DateTime.UtcNow.Date;
        // Exclusive instant upper bound for the 30-day window so these raw-timestamptz comparisons
        // agree with ComplianceStatusDeriver's date-only window for a time-bearing expiry on the
        // boundary day (#294): "within 30 days" is exp < today+31 (UTC midnight); "beyond the window"
        // is exp >= today+31. The lower edges (< today / >= today) are already date-equivalent.
        var expiringSoonUpperExclusive =
            ComplianceStatusDeriver.WindowUpperBoundExclusive(today, ComplianceStatusDeriver.ExpiringSoonWindowDays);
        // #362 / ADR 0041: "not yet in force today" is EffectiveDate.Date > today, i.e. the instant form
        // EffectiveDate >= today+1 (UTC midnight). Same date↔instant convention as the expiry bound above
        // (ADR 0027). "In force / no effective date" is (EffectiveDate == null || EffectiveDate < this bound).
        var notYetEffectiveBound = ComplianceStatusDeriver.NotYetEffectiveLowerBoundInclusive(today);

        var query = db.Documents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            // Filter on the EFFECTIVE (date-overlaid) status, not the stored cache, so the result
            // set matches the badge each row renders and the dashboard counts — the #257 fix for
            // "the Expired filter finds nothing while the dashboard shows one." Each arm mirrors
            // ComplianceStatusDeriver.Effective in SQL (C#-computed date bounds, no AT TIME ZONE).
            if (Enum.TryParse<ComplianceStatus>(status, ignoreCase: true, out var cs))
                query = cs switch
                {
                    // #327: exclude SUPERSEDED old certs so this deep-linked list matches the dashboard
                    // Expired count exactly (a renewed COI's old expired copy is historical, not a current
                    // liability). Shared DocumentSupersession predicate — see ADR 0033.
                    ComplianceStatus.Expired => query
                        .Where(d => d.ExpirationDate != null && d.ExpirationDate < today)
                        .Where(DocumentSupersession.IsCurrent(db.Documents)),
                    // #362: a future-effective (not-yet-in-force) doc is demoted out of ExpiringSoon/Compliant
                    // into the effective-Pending arm below — same date↔instant rule the dashboard counts use.
                    // #443: so is a NEVER-GRADED one (no ComplianceCheck row — nothing was measured against
                    // it). `d.ComplianceChecks.Any()` is the SQL mirror of DocumentGrading.IsGraded.
                    ComplianceStatus.ExpiringSoon => query.Where(d =>
                        d.ExpirationDate != null && d.ExpirationDate >= today && d.ExpirationDate < expiringSoonUpperExclusive
                        && (d.EffectiveDate == null || d.EffectiveDate < notYetEffectiveBound)
                        && d.ComplianceChecks.Any()
                        && (d.ComplianceStatus == ComplianceStatus.Compliant
                            || d.ComplianceStatus == ComplianceStatus.ExpiringSoon
                            || d.ComplianceStatus == ComplianceStatus.Pending)),
                    ComplianceStatus.Compliant => query.Where(d =>
                        d.ComplianceStatus == ComplianceStatus.Compliant
                        && (d.ExpirationDate == null || d.ExpirationDate >= expiringSoonUpperExclusive)
                        && (d.EffectiveDate == null || d.EffectiveDate < notYetEffectiveBound)
                        && d.ComplianceChecks.Any()),
                    ComplianceStatus.NonCompliant => query.Where(d =>
                        d.ComplianceStatus == ComplianceStatus.NonCompliant
                        && (d.ExpirationDate == null || d.ExpirationDate >= today)),
                    // The effective-Pending population — genuine Pending plus the future-effective (#362)
                    // and never-graded (#443) demotions — through the ONE shared predicate the dashboard's
                    // awaitingReview count also uses, so that count and this deep-linked list can never
                    // disagree about the same document (#294). See ComplianceStatusDeriver.ReadsPending.
                    _ => query.Where(ComplianceStatusDeriver.ReadsPending(today)),
                };
        }
        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(d => d.DocumentType == type);
        if (vendorId is not null)
            query = query.Where(d => d.VendorId == vendorId);
        if (expiresWithin is int days && days > 0)
        {
            // Clamp to a sane maximum (~10 years) so a hostile/absurd value can't push the C#
            // date arithmetic below out of DateTime's range and turn a malformed query param into a
            // 500 (#294 review). Anything past a decade is "everything not yet expired" anyway.
            days = Math.Min(days, 3650);
            // Upper AND lower bound: "expiring within N days" is a future window, so exclude
            // already-expired docs — without the lower bound the "Expiring in 30 days" filter also
            // returned long-expired documents (#257). Already-expired docs live under status=Expired.
            // Exclusive upper bound (< today+N+1) so a time-bearing expiry on day N still matches (#294).
            var cutoffExclusive = ComplianceStatusDeriver.WindowUpperBoundExclusive(today, days);
            query = query.Where(d => d.ExpirationDate != null && d.ExpirationDate >= today && d.ExpirationDate < cutoffExclusive);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Case-insensitive substring match on the file name OR the vendor
            // name — the two things a venue manager scans for. EF.Functions.ILike
            // is translated to a server-side Postgres ILIKE and the pattern is
            // passed as a bound PARAMETER (no string-concatenated SQL), so there's
            // no injection surface. A literal "%"/"_" typed by the user acts as an
            // ILIKE wildcard — acceptable, even handy, for a free-text search box.
            // Cap the term (mirrors the 120-char SanitizeFileName clamp) so an
            // authenticated client can't force a multi-kilobyte leading-wildcard
            // scan over two columns. (#187 review — security reviewer)
            var term = search.Trim();
            if (term.Length > 200) term = term[..200];
            var pattern = $"%{term}%";
            query = query.Where(d =>
                EF.Functions.ILike(d.OriginalFileName, pattern)
                || (d.Vendor != null && EF.Functions.ILike(d.Vendor.Name, pattern)));
        }

        query = (sortBy?.ToLowerInvariant(), sortDir?.ToLowerInvariant()) switch
        {
            ("expirationdate", "asc") => query.OrderBy(d => d.ExpirationDate),
            ("expirationdate", _) => query.OrderByDescending(d => d.ExpirationDate),
            ("filename", "asc") => query.OrderBy(d => d.OriginalFileName),
            ("filename", _) => query.OrderByDescending(d => d.OriginalFileName),
            (_, "asc") => query.OrderBy(d => d.CreatedAt),
            _ => query.OrderByDescending(d => d.CreatedAt)
        };

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var total = await query.CountAsync(ct);
        // Project the raw columns (server-side, narrow), then build the DTO in memory so the
        // displayed ComplianceStatus is the date-overlaid EFFECTIVE status (#257) — the same value
        // the filter above selects on, so a row's badge always matches the filter it came back under.
        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.OriginalFileName,
                d.DocumentType,
                VendorName = d.Vendor != null ? d.Vendor.Name : null,
                d.VendorId,
                d.ExtractionStatus,
                d.ExtractionConfidence,
                d.ComplianceStatus,
                d.EffectiveDate,
                d.ExpirationDate,
                d.IsSample,
                d.CreatedAt,
                // #443 / ADR 0048: the never-graded input to the deriver below. A scalar COUNT, so the
                // projection stays narrow — the check ROWS are never shipped to build a list badge.
                ComplianceCheckCount = d.ComplianceChecks.Count
            })
            .ToListAsync(ct);
        var items = rows.Select(d => new DocumentListItem(
            d.Id,
            d.OriginalFileName,
            d.DocumentType,
            d.VendorName,
            d.VendorId,
            d.ExtractionStatus.ToString(),
            d.ExtractionConfidence,
            ComplianceStatusDeriver.Effective(
                d.ComplianceStatus, d.ExpirationDate, d.EffectiveDate,
                DocumentGrading.IsGraded(d.ComplianceCheckCount), today).ToString(),
            d.EffectiveDate,
            d.ExpirationDate,
            DaysUntilExpiry(d.ExpirationDate, today),
            d.IsSample,
            d.CreatedAt)).ToList();

        return Results.Ok(new
        {
            data = new { items, total, page, pageSize },
            error = (object?)null
        });
    }

    private static async Task<IResult> GetDocument(
        Guid id,
        AppDbContext db,
        CancellationToken ct)
    {
        var doc = await db.Documents
            .Include(d => d.Vendor)
            .Include(d => d.Fields)
            .Include(d => d.ComplianceChecks)
                .ThenInclude(c => c.ComplianceRule)
            // Two sibling COLLECTION includes (Fields + ComplianceChecks) on one
            // Document would otherwise LEFT JOIN into a |Fields| × |Checks| cartesian
            // product under EF's default single-query mode, re-shipping the fat
            // Document row (ExtractionRawJson OCR text + ExtractionFields jsonb) on
            // every duplicated row. Split into one query per collection. (#193 review)
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();

        // #443 review S3 / ADR 0048 §4: how many requirements the assigned vendor's checklist actually
        // holds. Read through db.ComplianceTemplates (not off the FK) so the org + soft-delete query
        // filters apply: a vendor pointing at a soft-deleted template reports 0, which lands the card on
        // the empty-checklist arm — whose copy and remedy are the honest ones for that state too. One
        // extra scalar query, and only when a checklist is actually assigned.
        var vendorChecklistRuleCount = doc.Vendor?.ComplianceTemplateId is Guid templateId
            ? await db.ComplianceTemplates
                .Where(t => t.Id == templateId)
                .Select(t => t.Rules.Count)
                .FirstOrDefaultAsync(ct)
            : 0;

        object? extractionFields = null;
        if (doc.ExtractionFields is not null)
            extractionFields = System.Text.Json.JsonSerializer.Deserialize<object>(doc.ExtractionFields.RootElement.GetRawText());

        // Overlay the date-driven verdict so the detail badge is live truth as of today, not the
        // value frozen at the last evaluation (#257). The sweep keeps the stored column fresh too,
        // but deriving on read removes even the up-to-an-hour gap between sweeps.
        var today = DateTime.UtcNow.Date;
        var detail = new DocumentDetail(
            doc.Id,
            doc.OriginalFileName,
            doc.DocumentType,
            doc.DocumentSubType,
            doc.Vendor?.Name,
            doc.Vendor?.ContactEmail,
            doc.VendorId,
            // #443 / ADR 0048 §4: the detail page's "Not checked yet" card explains WHY nothing graded
            // this document, and zero check rows has four causes, not two. These two fields together
            // distinguish "no checklist" from "an EMPTY checklist" from "a checklist none of whose rules
            // govern a {documentType}" — distinctions the page cannot derive from complianceChecks.length,
            // and ones it used to get wrong by asserting the first whenever it saw zero checks.
            doc.Vendor?.ComplianceTemplateId != null,
            vendorChecklistRuleCount,
            doc.ExtractionStatus.ToString(),
            doc.ExtractionConfidence,
            // #443 / ADR 0048: the ComplianceChecks collection is already Include-loaded (it IS the "What
            // we checked" panel below), so the never-graded input costs nothing extra here — and an EMPTY
            // panel can no longer sit under an affirmative badge, the contradiction the ticket names.
            ComplianceStatusDeriver.Effective(
                doc.ComplianceStatus, doc.ExpirationDate, doc.EffectiveDate,
                DocumentGrading.IsGraded(doc.ComplianceChecks.Count), today).ToString(),
            doc.EffectiveDate,
            doc.ExpirationDate,
            DaysUntilExpiry(doc.ExpirationDate, today),
            doc.IsManuallyVerified,
            doc.UploadedBy,
            doc.IsSample,
            doc.GeneralLiabilityLimit,
            doc.Fields.Select(f => new DocumentFieldDto(
                f.Id, f.FieldName, f.FieldValue, f.FieldType, f.Confidence, f.IsManuallyEdited, f.OriginalValue)).ToArray(),
            doc.ComplianceChecks
                .OrderBy(c => c.CheckedAt)
                .Select(c => new ComplianceCheckDto(
                    c.Id, c.ComplianceRuleId,
                    c.ComplianceRule.FieldName, c.ComplianceRule.Operator, c.ComplianceRule.ExpectedValue,
                    c.ComplianceRule.ErrorMessage,
                    c.ActualValue, c.IsPassed, c.Notes, c.CheckedAt))
                .ToArray(),
            // The #383 state, from the same walk that raises the review flag in ResolveManualReview —
            // so the detail page can NAME the field it couldn't read instead of pointing at a
            // confidence outline an unreadable (high-confidence) value never gets. (ADR 0040 Amendment 2)
            DocumentFieldReadability.UnreadableCanonicalFields(doc),
            extractionFields,
            doc.ExtractionPromptVersion,
            doc.ProcessingError,
            doc.CreatedAt,
            doc.UpdatedAt);

        return Results.Ok(new { data = detail, error = (object?)null });
    }

    /// <summary>
    /// Streams the original uploaded file through the API (#254). The blob container is
    /// PRIVATE (<c>PublicAccessType.None</c>) and no SAS is ever minted — by design: flipping
    /// the container public would expose every customer's COIs, and SAS links once handed out
    /// can't be tenant-revoked. This authenticated, tenant-filtered proxy is the only way a
    /// browser may see the bytes.
    /// </summary>
    private static async Task<IResult> GetDocumentFile(
        Guid id,
        AppDbContext db,
        IBlobStorageService blobs,
        HttpContext http,
        CancellationToken ct)
    {
        // Tenant-filtered set: a cross-org, soft-deleted, or unknown id resolves to nothing —
        // 404, never another org's document bytes.
        var doc = await db.Documents
            .Where(d => d.Id == id)
            .Select(d => new { d.BlobStoragePath, d.ContentType, d.OriginalFileName })
            .FirstOrDefaultAsync(ct);
        if (doc is null || string.IsNullOrWhiteSpace(doc.BlobStoragePath)) return NotFound();

        // Null = the row exists but the blob vanished (manual storage cleanup, partial
        // delete) — a friendly 404, not an unhandled 500. Not-found is part of the
        // IBlobStorageService contract, so no Azure SDK types leak to this layer.
        var stream = await blobs.DownloadAsync(doc.BlobStoragePath, ct);
        if (stream is null) return NotFound();

        // Inline so the browser renders the PDF/image in the tab instead of downloading.
        // SetHttpFileName emits both the quoted `filename` and the RFC 6266 UTF-8 `filename*`
        // for non-ASCII upload names. Private compliance documents must never be cached by a
        // shared proxy, and nosniff pins the stored (magic-byte-validated, ingest-normalized)
        // content type against browser re-interpretation.
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(doc.OriginalFileName);
        http.Response.Headers.ContentDisposition = disposition.ToString();
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Stream(
            stream,
            string.IsNullOrWhiteSpace(doc.ContentType) ? "application/octet-stream" : doc.ContentType);
    }

    private static async Task<IResult> UpdateDocument(
        Guid id,
        DocumentPatchRequest req,
        AppDbContext db,
        IComplianceCheckService compliance,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();

        var changed = false;

        if (req.VendorId is Guid vendorId)
        {
            // The AppDbContext tenant filter scopes Vendors to the caller's org,
            // so a cross-org or nonexistent id simply isn't found here — that's
            // the multi-tenant guard, not just a friendliness check.
            var vendorExists = await db.Vendors.AnyAsync(v => v.Id == vendorId, ct);
            if (!vendorExists)
                return Error(400, "vendor.not_found", "That vendor no longer exists.");
            if (doc.VendorId != vendorId)
            {
                doc.VendorId = vendorId;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(req.DocumentType))
        {
            // REJECTS an unrecognized type where the two UPLOAD paths coerce it to "other" — the same
            // deliberate asymmetry ADR 0045 §5 draws for UpsertRule. This is a human deliberately
            // RE-TYPING a document, and the type decides which rules grade it; silently answering
            // "other" would change what the document is checked against without telling them. An upload
            // must not lose a file over a stray form value, so it coerces instead.
            if (!CanonicalDocumentTypes.IsAllowed(req.DocumentType))
                return Error(400, "document.invalid_type", "That document type isn't recognized.");
            var type = CanonicalDocumentTypes.Normalize(req.DocumentType);
            if (!string.Equals(doc.DocumentType, type, StringComparison.Ordinal))
            {
                doc.DocumentType = type;
                changed = true;
            }
        }

        if (!changed)
            return Results.Ok(new { data = new { message = "No changes." }, error = (object?)null });

        doc.UpdatedAt = DateTime.UtcNow;

        // Combined unit of work (#337 / ADR 0030): assigning a vendor (which may carry a requirement set)
        // or changing the document type (which changes WHICH rules apply — see ComplianceCheckService's
        // applicableRules filter) can turn a forever-"Pending" verdict into a real answer. Compute + apply
        // that verdict on the SAME context BEFORE saving, so the new vendor/type and its verdict commit in
        // ONE transaction and can't be left torn against a concurrent (re)extraction. The extraction worker
        // is the only other place that ever triggers a compliance check, and it won't re-run for a doc that
        // already finished extracting.
        await EvaluateIntoUnitOfWorkAsync(compliance, db, doc, loggerFactory, ct);
        await db.SaveChangesAsync(ct);

        // No explicit IAuditLogger call: the vendor/type change AND the verdict it implies are now one
        // ENTITY mutation, so AuditSaveChangesInterceptor emits a single "document.updated" row (full
        // Before/After spanning vendor/type + ComplianceStatus) on the SaveChanges above. Per CLAUDE.md,
        // manual IAuditLogger is reserved for NON-entity events; re-emitting "document.updated" here would
        // double the row in the customer's audit export (#186 review — architecture reviewer).
        return Results.Ok(new { data = new { message = "Document updated." }, error = (object?)null });
    }

    private static async Task<IResult> UploadDocument(
        HttpContext http,
        AppDbContext db,
        SystemDbContext sysDb,
        IBlobStorageService blobs,
        IFileValidationService validator,
        IImageTranscoder transcoder,
        IIdempotencyService idem,
        ICurrentUser currentUser,
        IServiceScopeFactory scopes,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        var orgId = currentUser.OrganizationId.Value;

        // Clamped ONCE, here, before the key is used for anything (#389). IdempotencyRecord.Key is
        // varchar(200) and the raw header used to go straight to both TryGetAsync and BuildRecord, so a
        // long key 22001'd the co-committed insert — taking the Document down with it (ADR 0029) and
        // orphaning the blob. Clamping at the single point where the key is READ (the
        // CurrentUserService shape of ADR 0044 §1) is what keeps lookup and storage seeing the SAME
        // string: clamping at only one of the two would make a repeat of a long key miss its own record
        // and duplicate the upload — idempotency silently broken rather than loudly failed.
        var idempotencyKey = ColumnClamp.To(
            http.Request.Headers["Idempotency-Key"].FirstOrDefault(), InputLengths.ClientIdempotencyKey);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var hit = await idem.TryGetAsync(orgId, idempotencyKey, ct);
            if (hit is not null)
                return IdempotencyResults.Replay(hit);
        }

        var sub = await sysDb.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (sub is { DocumentLimit: { } limit })
        {
            // The one-click sample-demo document (#238) is a throwaway artifact, not a customer
            // document — it must never consume a paid plan slot, so it's excluded from the count.
            // Shared predicate (#367): the portal fence and the Settings tile count the same
            // population, so no surface can drift out of agreement with this one.
            var activeCount = await sysDb.Documents
                .CountAsync(PlanDocumentScope.CountsTowardLimit(orgId), ct);
            if (activeCount >= limit)
                return Error(403, "plan.limit_reached", $"Document limit of {limit} reached. Upgrade to add more.");
        }

        if (!http.Request.HasFormContentType)
            return Error(400, "validation.form", "Multipart form expected.");
        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            // Names HEIC like the dashboard dropzone's caption (#265). The portal's
            // sibling message stays format-neutral per ADR 0018 §Neutral (vendors don't
            // reason in format names).
            return Error(400, "validation.file", "Upload a PDF, JPEG, PNG, or HEIC file.");

        Guid? vendorId = null;
        if (Guid.TryParse(form["vendorId"].ToString(), out var parsedVendorId))
        {
            // Validate vendor ownership the same way PATCH does — the tenant
            // filter on AppDbContext.Vendors scopes the lookup to this org, so a
            // cross-org or stale id can't silently associate the document with a
            // vendor the uploader can't see (#186 review — test-quality reviewer).
            if (!await db.Vendors.AnyAsync(v => v.Id == parsedVendorId, ct))
                return Error(400, "vendor.not_found", "That vendor no longer exists.");
            vendorId = parsedVendorId;
        }
        // Coerced through the shared vocabulary, exactly like the portal twin (#389 / ADR 0045): the raw
        // form value used to be stored VERBATIM, so ">100 chars" 22001'd the insert and — silently — a
        // mis-cased "COI" wrote a type that ordinally matches no compliance rule, leaving the document
        // graded against nothing. Normalize also subsumes the old blank -> "other" special case.
        var declaredType = CanonicalDocumentTypes.Normalize(form["documentType"].ToString());

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var validation = validator.Validate(buffer, file.ContentType, file.FileName);
        if (!validation.IsValid)
            return Error(400, validation.ErrorCode ?? "document.unsupported_format", validation.ErrorMessage ?? "Invalid file.");

        // Normalize HEIC/HEIF (iPhone photos) to JPEG on ingest so OCR, the LLM, and the browser
        // preview all see a supported format; PDF/JPEG/PNG pass through untouched. (#220 / ADR 0018)
        var (storedStream, storedContentType) = transcoder.NormalizeForStorage(buffer, validation.DetectedContentType!);
        if (storedStream is null)
            return Error(400, "document.unreadable_image", ImageTranscoderExtensions.UnreadableImageMessage);

        var blobName = $"{orgId}/{DateTime.UtcNow:yyyy-MM}/{Guid.NewGuid()}-{SanitizeFileName(file.FileName)}";
        BlobUploadResult upload;
        try
        {
            upload = await blobs.UploadAsync(blobName, storedStream, storedContentType, ct);
        }
        catch (BlobStorageUnavailableException)
        {
            // Storage outage → friendly 503, not the generic 500 (#248).
            return Error(503, "storage.unavailable",
                "We couldn't store your file just now. Please try again in a few minutes.");
        }

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = vendorId,
            // Clamped like the portal twin (#389 / ADR 0046): OriginalFileName is varchar(500) and ASP.NET
            // admits a filename far longer, so this went in raw and 22001'd the insert AFTER the blob was
            // stored. Clamp, don't reject — a truncated name is still recognizable and refusing the
            // upload helps nobody.
            OriginalFileName = ColumnClamp.To(file.FileName, InputLengths.DocumentOriginalFileName) ?? string.Empty,
            BlobStorageUrl = upload.Url,
            BlobStoragePath = blobName,
            FileSizeBytes = storedStream.Length,
            ContentType = storedContentType,
            DocumentType = declaredType,
            ExtractionStatus = ExtractionStatus.Pending,
            ComplianceStatus = ComplianceStatus.Pending,
            UploadedBy = "user",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Documents.Add(doc);

        var response = new
        {
            data = new
            {
                id = doc.Id,
                originalFileName = doc.OriginalFileName,
                extractionStatus = doc.ExtractionStatus.ToString(),
                createdAt = doc.CreatedAt
            },
            error = (object?)null
        };

        // Idempotency (#336): co-commit the dedupe record in the SAME transaction as the Document, so the
        // (OrganizationId, Key) unique index is an atomic claim. Two CONCURRENT same-key uploads both pass
        // validation and both upload a blob, but only one SaveChanges wins — the loser's commit fails the
        // unique violation we catch below, rolls its blob back, and replays the winner. Exactly one
        // Document, never two (the torn outcome the old check-then-store allowed). This generalizes the
        // sample endpoint's existing partial-unique-index race backstop to the shared idempotency key.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            db.IdempotencyRecords.Add(
                idem.BuildRecord(orgId, idempotencyKey, http.Request.Path, StatusCodes.Status201Created, response));

        // Blob cleanup is attempted on EVERY failure path (#389), mirroring the portal twin's
        // documentPersisted + finally (VendorPortalEndpoints.UploadViaPortal). The blob is uploaded
        // BEFORE the insert, and only the IsKeyConflict catch below used to delete it — so ANY other
        // SaveChanges failure (the 22001 this ticket removes at the source, a connection drop, a
        // constraint we haven't thought of) left a paid-for blob in storage with no row pointing at it
        // and nothing that would ever find it. The flag flips only after the commit returns.
        //
        // "documentPersisted == false" is the TRIGGER, not the verdict: it only means SaveChangesAsync
        // did not return normally, which includes the commit landing in Postgres and the ACK never
        // getting back. OrphanBlobCleanup therefore re-reads the row on a fresh connection and deletes
        // only what is genuinely absent — see its docs for why deleting on the bare signal is worse
        // than the orphan (#389 re-review, ADR 0046 §5 Amendment 2).
        //
        // Safe against the concurrent same-key race by construction: blobName and doc.Id are this
        // request's own Guids, so the loser confirms and deletes ONLY its own blob and can never touch
        // the winner's. And a sequential replay returns at the fast path above, long before any blob
        // exists — so ADR 0029/0032's "a committed record replays the winner's exact response" is
        // untouched: the replay still returns the winner's uploadId, pointing at the winner's
        // still-present blob.
        var documentPersisted = false;
        try
        {
            await db.SaveChangesAsync(ct);
            documentPersisted = true;
        }
        catch (DbUpdateException ex) when (!string.IsNullOrWhiteSpace(idempotencyKey) && idem.IsKeyConflict(ex))
        {
            // Lost the concurrent same-key race: another request committed this key first. Our Document
            // never committed (same transaction → rolled back with the conflicting record); the finally
            // deletes our orphaned blob. Replay the winner so the caller still gets exactly one doc.
            db.ChangeTracker.Clear();
            var hit = await idem.TryGetAsync(orgId, idempotencyKey, ct);
            return hit is not null ? IdempotencyResults.Replay(hit) : IdempotencyResults.InProgressConflict();
        }
        finally
        {
            if (!documentPersisted)
                await OrphanBlobCleanup.RunAsync(scopes, blobs, doc.Id, blobName, loggerFactory, "dashboard upload");
        }

        // No explicit "document.uploaded": the interceptor already records this owner upload as the
        // entity mutation "document.created" (#318 FP-043) — the two firing in the same request was
        // the "Document uploaded + Document added in the same second" duplicate. A VENDOR upload via
        // the public portal has no current user, so the interceptor can't see it; that path keeps its
        // explicit "vendorportallink.upload_processed" row (→ "Vendor sent a document" in the feed).
        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateFields(
        Guid id,
        FieldsUpdateRequest req,
        AppDbContext db,
        IComplianceCheckService compliance,
        IAuditLogger audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Both DocumentField columns are bounded and both are written verbatim from the request below, so
        // an over-length submission 22001'd the whole SaveChanges — which under ADR 0030 carries the
        // recomputed VERDICT too, so the user's correction and its verdict both vanished behind a 500
        // (#389). REJECTED rather than clamped, unlike the extraction worker writing the same two columns
        // (ADR 0045 §4): the worker is salvaging a model response nobody typed, while this is the user's
        // own correction — storing a silent half of it on a compliance record is the worse failure.
        // Checked BEFORE the document lookup: nothing about the request depends on the document.
        //
        // Fields and FieldName are non-nullable POSITIONAL record parameters, and System.Text.Json
        // leaves a missing or JSON-null property as null regardless — so `{}`, `{"fields": null}`,
        // `{"fields":[null]}` and `{"fields":[{"fieldValue":"x"}]}` each NRE'd on the walk below, and a
        // blank name would have written a nameless DocumentField row into a NOT NULL column. Every one
        // is a 500 where a 400 belongs — the class this ticket closes, and the same JSON-null hole
        // already fixed in ComplianceEndpoints.UpdateTemplate. An EMPTY array stays legal: the detail
        // page enables Save with no edits precisely while the manual-review card shows, and that no-op
        // save is what resolves the review (ADR 0040).
        if (req.Fields is null)
            return Error(400, "validation.fields", "Send the fields you want to update.");
        // The array's COUNT is bounded too, not just each element's length (#389 re-review). Kestrel
        // admits a 10 MB body and FieldsUpdateRequest caps nothing, so ~45-byte entries buy one
        // authenticated PUT a six-figure element count — walked twice by this guard, grouped, then
        // written row by row against the tracked document. Checked BEFORE the walk, which is the whole
        // point: a bound applied after the loop is a bound the loop already paid for.
        if (req.Fields.Length > InputLengths.DocumentFieldUpdatesPerRequest)
            return Error(400, "validation.too_many_fields",
                $"You can update up to {InputLengths.DocumentFieldUpdatesPerRequest} fields at a time.");
        foreach (var update in req.Fields)
        {
            if (update is null || string.IsNullOrWhiteSpace(update.FieldName))
                return Error(400, "validation.field_name", "Every field you send needs a name.");
            if (InputLength.FirstViolation(
                    (update.FieldName, InputLengths.DocumentFieldName, "Field name"),
                    (update.FieldValue, InputLengths.DocumentFieldValue, "Field value")) is { } tooLong)
                return tooLong;
        }

        var doc = await db.Documents
            .Include(d => d.Fields)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();

        var before = doc.Fields.Select(f => new { f.FieldName, f.FieldValue }).ToList();

        // The canonical compliance inputs are doc.ExtractionFields (JSON) + the typed columns
        // (GeneralLiabilityLimit / EffectiveDate / ExpirationDate), NOT the DocumentField rows that
        // this endpoint writes — so before #216 a correction never moved the verdict. Build the JSON
        // mirror starting from the existing object so untouched keys keep their original value/type.
        var fields = doc.ExtractionFields?.RootElement.ValueKind == JsonValueKind.Object
            ? (JsonObject)JsonNode.Parse(doc.ExtractionFields.RootElement.GetRawText())!
            : new JsonObject();

        // De-dupe by field name (last value wins): a request that lists the same field twice must
        // not create two DocumentField rows for a not-yet-existing field, nor leave the row out of
        // sync with the JSON mirror / typed column (which are themselves last-wins).
        foreach (var update in req.Fields.GroupBy(u => u.FieldName).Select(g => g.Last()))
        {
            var field = doc.Fields.FirstOrDefault(f => f.FieldName == update.FieldName);
            if (field is null)
            {
                // Add through the DbSet, NOT doc.Fields.Add(...). DocumentField.Id
                // is a client-set Guid key (ValueGeneratedOnAdd); DetectChanges
                // marks a NEW entity added to a tracked navigation collection with
                // a non-default key as Modified, which emits an UPDATE … WHERE Id=…
                // that matches 0 rows → DbUpdateConcurrencyException (500). DbSet.Add
                // forces the Added state. Mirrors ExtractionWorker.PersistSuccess,
                // which has always used db.DocumentFields.Add for this reason. (#193)
                db.DocumentFields.Add(new DocumentField
                {
                    Id = Guid.NewGuid(),
                    DocumentId = doc.Id,
                    FieldName = update.FieldName,
                    FieldValue = update.FieldValue,
                    FieldType = "text",
                    Confidence = 1.0,
                    IsManuallyEdited = true,
                    OriginalValue = null
                });
            }
            else
            {
                if (field.OriginalValue is null) field.OriginalValue = field.FieldValue;
                field.FieldValue = update.FieldValue;
                field.IsManuallyEdited = true;
                field.Confidence = 1.0;
            }

            // Mirror the edit into the canonical compliance inputs (#216): the JSON dict (every
            // field) and, for the three date/amount fields, the typed columns. The shared
            // CanonicalDocumentFields helper keeps this parse identical to the extraction worker.
            fields[update.FieldName] = update.FieldValue;
            CanonicalDocumentFields.ApplyToTypedColumn(doc, update.FieldName, update.FieldValue);
        }

        doc.ExtractionFields = JsonDocument.Parse(fields.ToJsonString());
        // Order matters: the JSON mirror and the typed columns above are the state ResolveManualReview
        // reads to decide whether the review it is clearing is genuinely resolved (#383, ADR 0040).
        ResolveManualReview(doc);
        doc.UpdatedAt = DateTime.UtcNow;

        // Combined unit of work (#337 / ADR 0030): compute + apply the verdict the edited inputs imply on
        // the SAME context BEFORE saving, so the corrected inputs (e.g. a misread GL limit fixed above the
        // required minimum) and the verdict they flip to commit in ONE transaction. The old pattern saved
        // inputs, then re-evaluated in a SECOND transaction — which a concurrent (re)extraction could
        // interleave to leave the stored verdict contradicting the stored inputs (a torn pair that did not
        // self-heal: the hourly sweep only does date transitions). Re-extraction still overwrites manual
        // edits by design (ADR 0017); the two writers are now each atomic on the whole (inputs, verdict)
        // tuple, so the terminal state is always one writer's consistent pair, never a mix.
        await EvaluateIntoUnitOfWorkAsync(compliance, db, doc, loggerFactory, ct);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("document.fields_edited", nameof(Document), doc.Id,
            before: before,
            after: doc.Fields.Select(f => new { f.FieldName, f.FieldValue }));

        return Results.Ok(new { data = new { message = "Fields updated." }, error = (object?)null });
    }

    private static async Task<IResult> MarkVerified(
        Guid id,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();
        ResolveManualReview(doc);
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("document.verified", nameof(Document), doc.Id);
        return Results.Ok(new { data = new { message = "Document marked verified." }, error = (object?)null });
    }

    private static async Task<IResult> Reextract(
        Guid id,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();
        doc.ExtractionStatus = ExtractionStatus.Pending;
        doc.ProcessingStartedAt = null;
        doc.ProcessingError = null;
        doc.ProcessingAttempts = 0;
        // Reset BOTH counters: a manual re-extract is a deliberate fresh start, so it must restore
        // the full retry budget (FailedAttempts) as well as the claim count — otherwise a document
        // that previously exhausted its budget would re-fail on the first hiccup with no real
        // retries (#259 introduced FailedAttempts as the budget gate).
        doc.FailedAttempts = 0;
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("document.reextract_queued", nameof(Document), doc.Id);
        return Results.Ok(new { data = new { message = "Re-extraction queued." }, error = (object?)null });
    }

    private static async Task<IResult> DeleteDocument(
        Guid id,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();
        // Soft delete only; the blob is intentionally RETAINED so a soft-deleted customer document
        // remains recoverable and its audit trail keeps a viewable original (ADR 0013). This is the
        // deliberate contrast with SampleEndpoints.ClearSample, which DOES delete the blob — a sample
        // is a throwaway demo artifact that should leave zero storage trace (ADR 0028).
        db.Documents.Remove(doc); // interceptor translates to soft delete
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { data = new { message = "Document removed." }, error = (object?)null });
    }

    // Folds the compliance verdict into the caller's unit of work (#337 / ADR 0030): applies the verdict
    // the document's CURRENT (just-edited) inputs imply onto the same tracked entity, so the caller's next
    // SaveChanges commits inputs + verdict atomically — never a torn pair. Best-effort preserved: if the
    // recompute itself fails, degrade the verdict to Pending (a safe "not yet graded" state the sweep /
    // "Check again" resolves) rather than fail the user's edit — but NEVER leave a confident verdict
    // computed from now-stale inputs. ApplyEvaluationAsync does all its I/O before any change-tracker
    // mutation, so a throw here leaves no partial check rows for the SaveChanges to commit.
    private static async Task EvaluateIntoUnitOfWorkAsync(
        IComplianceCheckService compliance, AppDbContext db, Document doc, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            await compliance.ApplyEvaluationAsync(db, doc, ct);
        }
        catch (Exception ex)
        {
            doc.ComplianceStatus = ComplianceStatus.Pending;
            loggerFactory.CreateLogger("DocumentEndpoints")
                .LogError(ex, "Compliance re-evaluation failed for document {DocumentId}; verdict degraded to Pending to avoid a stale verdict", doc.Id);
        }
    }

    // A human has reviewed the extracted values (via field-save or explicit
    // verify): mark the document verified and resolve a low-confidence
    // "Needs your review" (ManualRequired) document to Completed so the amber
    // review card on the detail page clears. Other statuses are left untouched —
    // single source of truth for "what manual review resolves". (#193)
    //
    // ...unless the document STILL carries a canonical value we can't read (#383, ADR 0040). Looking
    // at a document does not make its expiration parseable, so re-raise the flag here — inside the
    // one helper, so EVERY caller inherits it. The question is asked of the document's RESULTING
    // state, never of the field names one request happened to submit: an empty-fields save (the
    // detail page deliberately enables Save with no edits while the review card is showing), a save
    // touching only policy_number, or a bare PUT /verify all say nothing about whether the stored
    // expiration is readable — yet each used to clear the flag permanently, since nothing but a full
    // re-extraction ever re-raises it (ComplianceCheckService never writes ExtractionStatus). Under a
    // checklist with no rule on the field this flag is the ONLY thing left flagging the document, so
    // clearing it restored the exact silent false-Compliant #383 exists to close.
    //
    // Escalate ONLY from a settled status, measured BEFORE the resolve above: Pending/Processing are
    // the worker's queue states and overwriting Pending would DE-QUEUE the document (ExtractionWorker
    // claims on ExtractionStatus == Pending), while Failed is its own louder error state with a
    // processing-error card. Either way the extraction path re-decides this flag when it lands.
    private static void ResolveManualReview(Document doc)
    {
        var wasSettled = doc.ExtractionStatus
            is ExtractionStatus.Completed or ExtractionStatus.ManualRequired;

        doc.IsManuallyVerified = true;
        if (doc.ExtractionStatus == ExtractionStatus.ManualRequired)
            doc.ExtractionStatus = ExtractionStatus.Completed;

        if (wasSettled && DocumentFieldReadability.HasUnreadableCanonicalValue(doc))
            doc.ExtractionStatus = ExtractionStatus.ManualRequired;
    }


    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    /// <summary>Whole days from <paramref name="today"/> until a document's expiry (null when it has none).
    /// Truncates toward zero, matching the prior inline cast. Computed in memory at both call sites (the
    /// materialized list rows and the loaded detail entity), so it carries no EF-translation concern.</summary>
    private static int? DaysUntilExpiry(DateTime? expirationDate, DateTime today) =>
        expirationDate is { } expiry ? (int)(expiry.Date - today).TotalDays : null;

    private static IResult Unauthorized() =>
        Results.Json(new { data = (object?)null, error = new { code = "auth.unauthorized", message = "Not authenticated." } }, statusCode: 401);

    private static IResult NotFound() =>
        Results.Json(new { data = (object?)null, error = new { code = "document.not_found", message = "Document not found." } }, statusCode: 404);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
