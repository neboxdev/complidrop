using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Compliance;
using CompliDrop.Api.DTOs.Documents;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.AspNetCore.Http.Features;
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

    /// <summary>
    /// Canonical document-type vocabulary, mirroring the LLM extraction prompt
    /// (<see cref="Services.Extraction.ExtractionPrompts"/>). A manual type edit
    /// must resolve to one of these so a typo can't create an unmatchable type
    /// that silently excludes every compliance rule. Case-insensitive; stored
    /// lower-case.
    /// </summary>
    private static readonly HashSet<string> AllowedDocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "coi", "license", "permit", "certification", "contract", "other"
    };

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
                    ComplianceStatus.Expired => query.Where(d =>
                        d.ExpirationDate != null && d.ExpirationDate < today),
                    ComplianceStatus.ExpiringSoon => query.Where(d =>
                        d.ExpirationDate != null && d.ExpirationDate >= today && d.ExpirationDate < expiringSoonUpperExclusive
                        && (d.ComplianceStatus == ComplianceStatus.Compliant
                            || d.ComplianceStatus == ComplianceStatus.ExpiringSoon
                            || d.ComplianceStatus == ComplianceStatus.Pending)),
                    ComplianceStatus.Compliant => query.Where(d =>
                        d.ComplianceStatus == ComplianceStatus.Compliant
                        && (d.ExpirationDate == null || d.ExpirationDate >= expiringSoonUpperExclusive)),
                    ComplianceStatus.NonCompliant => query.Where(d =>
                        d.ComplianceStatus == ComplianceStatus.NonCompliant
                        && (d.ExpirationDate == null || d.ExpirationDate >= today)),
                    _ => query.Where(d => // Pending — but not if a date overlay would make it Expiring/Expired
                        d.ComplianceStatus == ComplianceStatus.Pending
                        && (d.ExpirationDate == null || d.ExpirationDate >= expiringSoonUpperExclusive)),
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
                d.CreatedAt
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
            ComplianceStatusDeriver.Effective(d.ComplianceStatus, d.ExpirationDate, today).ToString(),
            d.EffectiveDate,
            d.ExpirationDate,
            d.ExpirationDate != null
                ? (int?)(d.ExpirationDate.Value.Date - today).TotalDays
                : null,
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
            doc.ExtractionStatus.ToString(),
            doc.ExtractionConfidence,
            ComplianceStatusDeriver.Effective(doc.ComplianceStatus, doc.ExpirationDate, today).ToString(),
            doc.EffectiveDate,
            doc.ExpirationDate,
            doc.ExpirationDate != null
                ? (int?)(doc.ExpirationDate.Value.Date - today).TotalDays
                : null,
            doc.IsManuallyVerified,
            doc.UploadedBy,
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
            var type = req.DocumentType.Trim().ToLowerInvariant();
            if (!AllowedDocumentTypes.Contains(type))
                return Error(400, "document.invalid_type", "That document type isn't recognized.");
            if (!string.Equals(doc.DocumentType, type, StringComparison.Ordinal))
            {
                doc.DocumentType = type;
                changed = true;
            }
        }

        if (!changed)
            return Results.Ok(new { data = new { message = "No changes." }, error = (object?)null });

        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Assigning a vendor (which may carry a requirement set) or changing the
        // document type (which changes WHICH rules apply — see ComplianceCheckService's
        // applicableRules filter) can turn a forever-"Pending" verdict into a real
        // answer. Re-evaluate inline; the extraction worker is the only other place
        // that ever triggers a compliance check, and it won't re-run for a doc that
        // already finished extracting. Best-effort: a failure here must NOT fail the
        // assignment the user just made — the verdict can be recomputed on the next
        // re-extract.
        try
        {
            await compliance.EvaluateAsync(doc.Id, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("DocumentEndpoints")
                .LogError(ex, "Compliance re-evaluation failed after updating document {DocumentId}", doc.Id);
        }

        // No explicit IAuditLogger call: the vendor/type change is an ENTITY
        // mutation, so AuditSaveChangesInterceptor already emitted a
        // "document.updated" row (full Before/After) on the SaveChanges above —
        // and the compliance re-eval's own SaveChanges audits the verdict change.
        // Per CLAUDE.md, manual IAuditLogger is reserved for NON-entity events;
        // re-emitting "document.updated" here would double the row in the
        // customer's audit export (#186 review — architecture reviewer).
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
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        var orgId = currentUser.OrganizationId.Value;

        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var hit = await idem.TryGetAsync(orgId, idempotencyKey, ct);
            if (hit is not null)
            {
                return Results.Json(
                    hit.ResponseJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<object>(hit.ResponseJson),
                    statusCode: hit.StatusCode);
            }
        }

        var sub = await sysDb.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (sub is { DocumentLimit: { } limit })
        {
            var activeCount = await sysDb.Documents
                .CountAsync(d => d.OrganizationId == orgId && d.DeletedAt == null, ct);
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
        var declaredType = form["documentType"].ToString();
        if (string.IsNullOrWhiteSpace(declaredType)) declaredType = "other";

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
            OriginalFileName = file.FileName,
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
        await db.SaveChangesAsync(ct);

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

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            await idem.StoreAsync(orgId, idempotencyKey, http.Request.Path, StatusCodes.Status201Created, response, ct);

        await audit.LogAsync("document.uploaded", nameof(Document), doc.Id, after: new { doc.Id, doc.OriginalFileName, doc.UploadedBy });

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
        ResolveManualReview(doc);
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("document.fields_edited", nameof(Document), doc.Id,
            before: before,
            after: doc.Fields.Select(f => new { f.FieldName, f.FieldValue }));

        // Now that the edits reached doc.ExtractionFields / the typed columns, re-run compliance so a
        // correction (e.g. a misread GL limit fixed above the required minimum) flips the verdict and
        // refreshes the detail-page explainer. Best-effort, mirroring UpdateDocument: a recompute
        // failure must NOT fail the save the user just made — it recomputes on the next edit/re-extract.
        // Re-extraction re-reads the source and overwrites manual edits by design; see ADR 0017.
        try
        {
            await compliance.EvaluateAsync(doc.Id, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("DocumentEndpoints")
                .LogError(ex, "Compliance re-evaluation failed after editing fields on document {DocumentId}", doc.Id);
        }

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
        db.Documents.Remove(doc); // interceptor translates to soft delete
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { data = new { message = "Document removed." }, error = (object?)null });
    }

    // A human has reviewed the extracted values (via field-save or explicit
    // verify): mark the document verified and resolve a low-confidence
    // "Needs your review" (ManualRequired) document to Completed so the amber
    // review card on the detail page clears. Other statuses are left untouched —
    // single source of truth for "what manual review resolves". (#193)
    private static void ResolveManualReview(Document doc)
    {
        doc.IsManuallyVerified = true;
        if (doc.ExtractionStatus == ExtractionStatus.ManualRequired)
            doc.ExtractionStatus = ExtractionStatus.Completed;
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static IResult Unauthorized() =>
        Results.Json(new { data = (object?)null, error = new { code = "auth.unauthorized", message = "Not authenticated." } }, statusCode: 401);

    private static IResult NotFound() =>
        Results.Json(new { data = (object?)null, error = new { code = "document.not_found", message = "Document not found." } }, statusCode: 404);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
