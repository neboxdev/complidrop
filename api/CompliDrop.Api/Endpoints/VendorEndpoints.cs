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
        group.MapPost("/{id:guid}/portal-link/{linkId:guid}/email", EmailPortalLink);
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

        // Deactivate the vendor's portal links and soft-delete the vendor atomically
        // (#269): the soft-deleted vendor vanishes behind the query filter, which
        // previously left emailed links "live" — every click materialized
        // link.Vendor == null and NRE-500'd. ExecuteUpdate, NOT tracked entities:
        // loading the links into the change tracker arms EF's client-side cascade, and
        // Remove(vendor) would then HARD-delete the link rows at SaveChanges
        // (VendorPortalLink has no DeletedAt for the interceptor's soft-delete
        // translation). The transaction covers the link deactivation + vendor
        // soft-delete only — audit rows are written AFTER commit, because IAuditLogger
        // goes through SystemDbContext on a different connection and cannot join this
        // transaction (#269 review; the explicit link row exists at all because
        // ExecuteUpdate bypasses the audit interceptor).
        var linkIds = await db.VendorPortalLinks
            .Where(l => l.VendorId == id && l.IsActive)
            .Select(l => l.Id)
            .ToListAsync(ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Filtered by the captured ids so the audit payload below states EXACTLY what
            // this operation deactivated. A link minted concurrently between the snapshot
            // and here stays active but is inert: the portal's dead-tenant guards 404 it
            // the moment the vendor soft-delete commits.
            await db.VendorPortalLinks
                .Where(l => linkIds.Contains(l.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false), ct);

            db.Vendors.Remove(v);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (linkIds.Count > 0)
            await audit.LogAsync(
                "vendorPortalLink.deactivated_on_vendor_delete", nameof(Vendor), id,
                after: new { count = linkIds.Count, linkIds });
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

    // Emails an EXISTING portal link to the vendor's captured contact email (#190). The vendor is
    // looked up through the tenant-filtered db.Vendors set first, so a vendor/link pair belonging to
    // another org resolves to null → 404 (no cross-tenant send). The send is the whole point of this
    // request, so — unlike the fire-and-forget reminder/verification sends — a delivery failure is
    // surfaced to the caller (502) rather than swallowed, so Pat knows to fall back to copy-paste.
    private static async Task<IResult> EmailPortalLink(
        Guid id,
        Guid linkId,
        AppDbContext db,
        IEmailService email,
        IOptions<FrontendSettings> frontend,
        IAuditLogger audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var vendor = await db.Vendors
            .Include(v => v.Organization)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vendor is null) return NotFound();

        var link = await db.VendorPortalLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.VendorId == id, ct);
        if (link is null) return NotFoundLink();

        if (string.IsNullOrWhiteSpace(vendor.ContactEmail))
            return Error(400, "vendor.no_contact_email",
                "Add a contact email for this vendor first, then you can email them the upload link.");

        if (!link.IsActive)
            return Error(400, "vendorPortalLink.inactive",
                "This upload link has been revoked. Generate a new one to email it.");

        if (!email.IsEnabled)
            return Error(503, "email.not_configured",
                "Email isn't set up yet, so we couldn't send it. Copy the link and send it to your vendor instead.");

        var url = PortalUrl(frontend.Value, link.Token);
        var orgName = vendor.Organization?.Name ?? "A business you work with";
        var subject = $"{orgName} needs your compliance documents";
        var body = BuildPortalInviteEmail(orgName, vendor.Name, url);

        string? messageId;
        try
        {
            messageId = await email.SendAsync(vendor.ContactEmail, subject, body, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Any send failure EXCEPT a genuine caller abort is surfaced as a friendly 502.
            // SendAsync swallows non-2xx Resend responses to null; this catch covers the throw
            // paths: a transport failure (DNS/socket/TLS → HttpRequestException) AND the 30s
            // "resend" HttpClient timeout — which surfaces as a TaskCanceledException, itself an
            // OperationCanceledException, but tied to the client's internal timeout token, NOT our
            // `ct`. Both are real delivery failures. We gate on `!ct.IsCancellationRequested` (not
            // `ex is not OperationCanceledException`) so the 30s timeout is still caught → 502,
            // while a true client abort (our `ct` signalled) propagates — there is no one left to
            // receive a response.
            loggerFactory.CreateLogger("VendorEndpoints")
                .LogWarning(ex, "Portal-link email send failed for vendor {VendorId}", id);
            messageId = null;
        }

        if (messageId is null)
            return Error(502, "email.send_failed",
                "We couldn't send the email just now. Copy the link and try again, or send it yourself.");

        await audit.LogAsync("vendorPortalLink.emailed", nameof(VendorPortalLink), link.Id,
            after: new { recipient = vendor.ContactEmail });

        return Results.Ok(new { data = new { sentTo = vendor.ContactEmail }, error = (object?)null });
    }

    // Org name + vendor name are user-controlled free text rendered into the email HTML, so they
    // MUST be HTML-encoded to avoid injecting markup/script into the recipient's inbox. The portal
    // URL is built from config BaseUrl + a base64url token (no HTML-significant chars), matching the
    // existing auth-email pattern.
    private static string BuildPortalInviteEmail(string orgName, string vendorName, string url)
    {
        var safeOrg = System.Net.WebUtility.HtmlEncode(orgName);
        var safeVendor = System.Net.WebUtility.HtmlEncode(vendorName);
        return $"""
            <div style="font-family: system-ui, sans-serif; color: #0c4a6e;">
              <h2 style="color: #0284c7;">Upload your documents</h2>
              <p>Hi {safeVendor},</p>
              <p>{safeOrg} uses CompliDrop to keep vendor compliance documents — COIs, licenses, and permits — current. Please upload yours using the secure link below. No account or password needed.</p>
              <p><a href="{url}" style="display:inline-block;background:#0284c7;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;">Upload my documents</a></p>
              <p style="color: #64748b; font-size: 12px;">Or paste this link into your browser:<br>{url}</p>
            </div>
            """;
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

    private static IResult NotFoundLink() =>
        Results.Json(new { data = (object?)null, error = new { code = "vendorPortalLink.not_found", message = "Upload link not found." } }, statusCode: 404);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
