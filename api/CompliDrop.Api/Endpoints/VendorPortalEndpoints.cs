using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

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
            link.IsActive = false;
            await db.SaveChangesAsync(ct);
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

        buffer.Position = 0;
        var orgId = link.Vendor.OrganizationId;
        var blobName = $"{orgId}/{DateTime.UtcNow:yyyy-MM}/portal-{Guid.NewGuid()}-{SanitizeFileName(file.FileName)}";
        var upload = await blobs.UploadAsync(blobName, buffer, validation.DetectedContentType!, ct);

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = link.VendorId,
            OriginalFileName = file.FileName,
            BlobStorageUrl = upload.Url,
            BlobStoragePath = blobName,
            FileSizeBytes = file.Length,
            ContentType = validation.DetectedContentType!,
            DocumentType = form["documentType"].ToString() is var dt && !string.IsNullOrWhiteSpace(dt) ? dt : "other",
            ExtractionStatus = ExtractionStatus.Pending,
            ComplianceStatus = ComplianceStatus.Pending,
            UploadedBy = "vendor_portal",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Documents.Add(doc);
        link.UploadCount += 1;
        if (link.UploadCount >= link.MaxUploads) link.IsActive = false;
        await db.SaveChangesAsync(ct);

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
