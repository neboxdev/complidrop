using CompliDrop.Api.Auth;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Vendors;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Endpoints;

public static class VendorEndpoints
{
    public static void MapVendorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/vendors").RequireAuthorization();

        group.MapGet("/", ListVendors);
        group.MapGet("/{id:guid}", GetVendor);
        group.MapPost("/", CreateVendor);
        group.MapPut("/{id:guid}", UpdateVendor);
        group.MapDelete("/{id:guid}", DeleteVendor);
        group.MapPost("/{id:guid}/portal-link", GeneratePortalLink);
        group.MapDelete("/{id:guid}/portal-link/{linkId:guid}", RevokePortalLink);
    }

    private static async Task<IResult> ListVendors(AppDbContext db, CancellationToken ct)
    {
        var vendors = await db.Vendors
            .Include(v => v.ComplianceTemplate)
            .Include(v => v.Documents)
            .Include(v => v.PortalLinks)
            .Select(v => new VendorSummary(
                v.Id, v.Name, v.ContactEmail, v.ContactPhone, v.Category,
                v.ComplianceTemplateId,
                v.ComplianceTemplate != null ? v.ComplianceTemplate.Name : null,
                v.Documents.Count,
                v.PortalLinks.Count(l => l.IsActive)))
            .ToListAsync(ct);
        return Results.Ok(new { data = vendors, error = (object?)null });
    }

    private static async Task<IResult> GetVendor(
        Guid id,
        AppDbContext db,
        IOptions<FrontendSettings> frontend,
        CancellationToken ct)
    {
        var v = await db.Vendors
            .Include(v => v.ComplianceTemplate)
            .Include(v => v.PortalLinks)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
        if (v is null) return NotFound();

        var detail = new VendorDetail(
            v.Id, v.Name, v.ContactEmail, v.ContactPhone, v.Category,
            v.ComplianceTemplateId,
            v.ComplianceTemplate?.Name,
            v.PortalLinks.OrderByDescending(l => l.CreatedAt).Select(l => new PortalLinkDto(
                l.Id, l.Token, PortalUrl(frontend.Value, l.Token),
                l.IsActive, l.UploadCount, l.MaxUploads, l.ExpiresAt, l.CreatedAt
            )).ToArray(),
            v.CreatedAt, v.UpdatedAt);
        return Results.Ok(new { data = detail, error = (object?)null });
    }

    private static async Task<IResult> CreateVendor(
        VendorUpsertRequest req,
        AppDbContext db,
        ICurrentUser currentUser,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return Error(400, "validation.name", "Vendor name is required.");

        var vendor = new Vendor
        {
            Id = Guid.NewGuid(),
            OrganizationId = currentUser.OrganizationId.Value,
            Name = req.Name.Trim(),
            ContactEmail = req.ContactEmail,
            ContactPhone = req.ContactPhone,
            Category = req.Category,
            ComplianceTemplateId = req.ComplianceTemplateId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("vendor.created", nameof(Vendor), vendor.Id, after: new { vendor.Name });
        return Results.Ok(new { data = new { id = vendor.Id }, error = (object?)null });
    }

    private static async Task<IResult> UpdateVendor(
        Guid id,
        VendorUpsertRequest req,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var v = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return NotFound();
        v.Name = req.Name.Trim();
        v.ContactEmail = req.ContactEmail;
        v.ContactPhone = req.ContactPhone;
        v.Category = req.Category;
        v.ComplianceTemplateId = req.ComplianceTemplateId;
        v.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("vendor.updated", nameof(Vendor), id);
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> DeleteVendor(
        Guid id,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var v = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return NotFound();
        db.Vendors.Remove(v);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("vendor.deleted", nameof(Vendor), id);
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> GeneratePortalLink(
        Guid id,
        AppDbContext db,
        IOptions<FrontendSettings> frontend,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var v = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return NotFound();

        var token = GenerateToken();
        var link = new VendorPortalLink
        {
            Id = Guid.NewGuid(),
            VendorId = v.Id,
            Token = token,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            MaxUploads = 20,
            UploadCount = 0
        };
        db.VendorPortalLinks.Add(link);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("vendorPortalLink.created", nameof(VendorPortalLink), link.Id);

        return Results.Ok(new
        {
            data = new
            {
                id = link.Id,
                token,
                url = PortalUrl(frontend.Value, token),
                maxUploads = link.MaxUploads
            },
            error = (object?)null
        });
    }

    private static async Task<IResult> RevokePortalLink(
        Guid id,
        Guid linkId,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var link = await db.VendorPortalLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.VendorId == id, ct);
        if (link is null) return NotFound();
        link.IsActive = false;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("vendorPortalLink.revoked", nameof(VendorPortalLink), link.Id);
        return Results.Ok(new { data = new { id = linkId }, error = (object?)null });
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string PortalUrl(FrontendSettings cfg, string token) =>
        $"{cfg.BaseUrl.TrimEnd('/')}/portal/{token}";

    private static IResult Unauthorized() =>
        Results.Json(new { data = (object?)null, error = new { code = "auth.unauthorized", message = "Not authenticated." } }, statusCode: 401);

    private static IResult NotFound() =>
        Results.Json(new { data = (object?)null, error = new { code = "vendor.not_found", message = "Vendor not found." } }, statusCode: 404);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
