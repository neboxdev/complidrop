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
        var today = DateTime.UtcNow.Date;
        // ONE query (FP-074): project the distinct document types this vendor's checklist requires +
        // a lightweight view of its documents, then roll up coverage in memory. EF turns the nested
        // .Select()s into correlated subqueries on the single statement — no per-vendor round trips.
        var rows = await db.Vendors
            .Select(v => new
            {
                v.Id, v.Name, v.ContactEmail, v.ContactPhone, v.Category,
                v.ComplianceTemplateId,
                TemplateName = v.ComplianceTemplate != null ? v.ComplianceTemplate.Name : null,
                RequiredTypes = v.ComplianceTemplate != null
                    ? v.ComplianceTemplate.Rules.Select(r => r.DocumentType).Distinct().ToList()
                    : new List<string>(),
                Docs = v.Documents
                    .Select(d => new DocCoverageInfo(d.DocumentType, d.ComplianceStatus, d.ExpirationDate, d.CreatedAt))
                    .ToList(),
                DocumentCount = v.Documents.Count,
                ActivePortalLinks = v.PortalLinks.Count(l => l.IsActive),
                v.IsSample,
            })
            .ToListAsync(ct);

        // The org's suppressed addresses (#340), loaded once and matched in memory — avoids a correlated
        // subquery per vendor row. db.EmailSuppressions is already tenant-scoped by the query filter.
        var suppressions = (await db.EmailSuppressions
                .Select(s => new { s.Email, s.Reason })
                .ToListAsync(ct))
            .ToDictionary(s => s.Email, s => s.Reason, StringComparer.OrdinalIgnoreCase);

        var vendors = rows.Select(v => new VendorSummary(
            v.Id, v.Name, v.ContactEmail, v.ContactPhone, v.Category,
            v.ComplianceTemplateId, v.TemplateName, v.DocumentCount, v.ActivePortalLinks, v.IsSample,
            ComputeCoverage(v.ComplianceTemplateId is not null, v.RequiredTypes, v.Docs, today),
            ContactEmailStatusLabel(v.ContactEmail, suppressions)));

        return Results.Ok(new { data = vendors, error = (object?)null });
    }

    /// <summary>Lightweight per-document view the coverage rollup needs (FP-074).</summary>
    private sealed record DocCoverageInfo(
        string DocumentType, Entities.ComplianceStatus ComplianceStatus, DateTime? ExpirationDate, DateTime CreatedAt);

    /// <summary>
    /// Rolls a vendor's documents up against the distinct document types its checklist requires
    /// (#319 FP-074). A required type is "covered" when its LATEST document's EFFECTIVE status
    /// (ComplianceStatusDeriver, ADR 0027) is Compliant or ExpiringSoon; any required type with no
    /// document is "missing"; otherwise (Expired / NonCompliant / not-yet-graded) it's action-needed.
    /// The engine re-grades on rule/assignment change since #257, so this isn't built on stale verdicts.
    /// </summary>
    private static VendorCoverage ComputeCoverage(
        bool hasTemplate, List<string> requiredTypes, List<DocCoverageInfo> docs, DateTime today)
    {
        if (!hasTemplate || requiredTypes.Count == 0) return new VendorCoverage("NoRequirements", [], null);

        var missing = new List<string>();
        var actionNeeded = false;
        // The earliest expiration among the covered required docs (#399). Coverage as a WHOLE lapses
        // the moment its first-to-expire required doc does, so the nearest expiry is the honest
        // "covered through" horizon for the vendor. A covered doc with NO expiration doesn't constrain
        // it (nothing to show), so it's left out of the min — null here means "Covered, no dated docs".
        DateTime? coveredThrough = null;
        foreach (var type in requiredTypes)
        {
            var latest = docs
                .Where(d => string.Equals(d.DocumentType, type, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefault();
            if (latest is null) { missing.Add(ShortTypeLabel(type)); continue; }
            // Overlay the date the SAME way every other surface does (ComplianceStatusDeriver, ADR 0027)
            // instead of hand-rolling a 4th copy of the window math. A valid-but-expiring-soon doc is
            // benign coverage (ExpiringSoon), NOT a hard "action needed" — only Expired / NonCompliant /
            // not-yet-graded (Pending) leave a required type uncovered.
            var effective = ComplianceStatusDeriver.Effective(latest.ComplianceStatus, latest.ExpirationDate, today);
            var covered = effective is Entities.ComplianceStatus.Compliant or Entities.ComplianceStatus.ExpiringSoon;
            if (!covered) { actionNeeded = true; continue; }
            if (latest.ExpirationDate is DateTime exp && (coveredThrough is null || exp < coveredThrough))
                coveredThrough = exp;
        }

        if (missing.Count > 0) return new VendorCoverage("Missing", [.. missing], null);
        // "covered through {date}" is display-only honesty on the Covered verdict (#399) — the set of
        // statuses that read Covered is UNCHANGED. Only the fully-covered case carries the horizon.
        if (actionNeeded) return new VendorCoverage("ActionNeeded", [], null);
        return new VendorCoverage("Covered", [], coveredThrough);
    }

    /// <summary>Short, lower-case noun for a document type, for "Missing: insurance, license" copy.</summary>
    private static string ShortTypeLabel(string type) => type.ToLowerInvariant() switch
    {
        "coi" => "insurance",
        "license" => "license",
        "permit" => "permit",
        "certification" => "certification",
        "contract" => "contract",
        _ => type,
    };

    private static async Task<IResult> GetVendor(
        Guid id,
        AppDbContext db,
        IOptions<FrontendSettings> frontend,
        CancellationToken ct)
    {
        var v = await db.Vendors
            .Include(v => v.ComplianceTemplate).ThenInclude(t => t!.Rules)
            .Include(v => v.PortalLinks)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
        if (v is null) return NotFound();

        // Coverage docs are PROJECTED in a separate lightweight query rather than Include(Documents):
        // a third sibling collection Include would cartesian-multiply the result AND re-ship each
        // Document's fat ExtractionRawJson / ExtractionFields jsonb (the split-query convention in
        // GetDocument / ComplianceCheckService). The projection ships only the four scalar fields the
        // rollup needs. Tenant-scoped via the global Documents filter; VendorId scopes to this vendor.
        var coverageDocs = await db.Documents
            .Where(d => d.VendorId == id)
            .Select(d => new DocCoverageInfo(d.DocumentType, d.ComplianceStatus, d.ExpirationDate, d.CreatedAt))
            .ToListAsync(ct);

        var coverage = ComputeCoverage(
            v.ComplianceTemplateId is not null,
            v.ComplianceTemplate is null ? [] : v.ComplianceTemplate.Rules.Select(r => r.DocumentType).Distinct().ToList(),
            coverageDocs,
            DateTime.UtcNow.Date);

        // #340: is this vendor's ContactEmail one the reminder engine has stopped sending to?
        EmailSuppressionReason? emailReason = null;
        if (!string.IsNullOrWhiteSpace(v.ContactEmail))
        {
            var lowered = v.ContactEmail.ToLowerInvariant();
            emailReason = await db.EmailSuppressions
                .Where(s => s.Email == lowered)
                .Select(s => (EmailSuppressionReason?)s.Reason)
                .FirstOrDefaultAsync(ct);
        }

        var detail = new VendorDetail(
            v.Id, v.Name, v.ContactEmail, v.ContactPhone, v.Category,
            v.ComplianceTemplateId,
            v.ComplianceTemplate?.Name,
            v.PortalLinks.OrderByDescending(l => l.CreatedAt).Select(l => new PortalLinkDto(
                l.Id, l.Token, PortalLink.Url(frontend.Value, l.Token),
                l.IsActive, l.UploadCount, l.MaxUploads, l.ExpiresAt, l.CreatedAt
            )).ToArray(),
            v.CreatedAt, v.UpdatedAt, coverage, LabelOf(emailReason));
        return Results.Ok(new { data = detail, error = (object?)null });
    }

    private static async Task<IResult> CreateVendor(
        VendorUpsertRequest req,
        AppDbContext db,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return Error(400, "validation.name", "Vendor name is required.");
        if (!ContactEmail.TryNormalize(req.ContactEmail, out var contactEmail)) return ContactEmailInvalid(req.ContactEmail);
        if (!await TemplateIsAssignable(req.ComplianceTemplateId, db, ct)) return TemplateNotFound();

        var vendor = new Vendor
        {
            Id = Guid.NewGuid(),
            OrganizationId = currentUser.OrganizationId.Value,
            Name = req.Name.Trim(),
            ContactEmail = contactEmail,
            ContactPhone = req.ContactPhone,
            Category = req.Category,
            ComplianceTemplateId = req.ComplianceTemplateId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync(ct);
        // No explicit IAuditLogger call (#318 FP-043): creating a Vendor is an ENTITY mutation the
        // AuditSaveChangesInterceptor already records as "vendor.created" with a full snapshot —
        // the explicit row was a pure duplicate that doubled the export. Per the CLAUDE.md audit
        // rule, manual IAuditLogger is reserved for NON-entity events (the no-user portal path,
        // ExecuteUpdate writes the interceptor can't see). Same for Update/Delete below.
        return Results.Ok(new { data = new { id = vendor.Id }, error = (object?)null });
    }

    private static async Task<IResult> UpdateVendor(
        Guid id,
        VendorUpsertRequest req,
        AppDbContext db,
        IComplianceCheckService checker,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Same guard as CreateVendor: a blank name renders an invisible, unclickable row
        // in the vendors list (the name is the row's link). The create path always had
        // this check; the update path forgot it (#264 / FP-074).
        if (string.IsNullOrWhiteSpace(req.Name)) return Error(400, "validation.name", "Vendor name is required.");
        // #369: the same guard on the update path. This is the one the ticket reports — the detail
        // edit form is where a contact email actually gets corrected (and mistyped), and a bad value
        // saved here breaks every subsequent reminder send silently.
        if (!ContactEmail.TryNormalize(req.ContactEmail, out var contactEmail)) return ContactEmailInvalid(req.ContactEmail);

        var v = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return NotFound();
        if (!await TemplateIsAssignable(req.ComplianceTemplateId, db, ct)) return TemplateNotFound();
        var templateChanged = v.ComplianceTemplateId != req.ComplianceTemplateId;
        v.Name = req.Name.Trim();
        v.ContactEmail = contactEmail;
        v.ContactPhone = req.ContactPhone;
        v.Category = req.Category;
        v.ComplianceTemplateId = req.ComplianceTemplateId;
        v.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Re-evaluation fan-out (#257): assigning (or changing) the checklist must immediately
        // re-grade this vendor's documents — the vendor page's amber hint promises exactly that.
        // Portal-first onboarding (upload, THEN assign a checklist) otherwise leaves docs stuck at
        // "Awaiting review" forever. Only fan out when the assignment actually changed.
        //
        // Routed through PostCommitRegrade.RunAsync (#364) like the three checklist-mutation fan-outs
        // in ComplianceEndpoints — same post-commit shape, same two hazards. This is the TIGHTEN case
        // and so the worst of the four: reassigning a vendor from a lax checklist to a stricter one
        // and then aborting the request left an arbitrary suffix of that vendor's documents on their
        // pre-reassignment Compliant verdict — a genuine false Compliant, with no automatic healer.
        if (templateChanged)
            await PostCommitRegrade.RunAsync(
                token => checker.ReevaluateForVendorAsync(id, token),
                lifetime, loggerFactory, "vendor checklist reassignment");

        // Interceptor records "vendor.updated" — no explicit duplicate (#318 FP-043).
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

        // Only the link-deactivation row is explicit: ExecuteUpdate bypasses the audit interceptor,
        // so without it that mutation would leave no trace. The vendor soft-delete itself is recorded
        // by the interceptor as "vendor.deleted" — the explicit duplicate was dropped (#318 FP-043).
        if (linkIds.Count > 0)
            await audit.LogAsync(
                "vendorPortalLink.deactivated_on_vendor_delete", nameof(Vendor), id,
                after: new { count = linkIds.Count, linkIds });
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> GeneratePortalLink(
        Guid id,
        AppDbContext db,
        IOptions<FrontendSettings> frontend,
        CancellationToken ct)
    {
        var v = await db.Vendors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return NotFound();

        // Monetization fence (#261): minting upload links is a Pro entitlement. Gated
        // AFTER the vendor lookup so cross-tenant probes keep answering the identical
        // 404 they always did — the 403 only ever fires for the caller's own vendor.
        if (!await PortalIncludedInPlanAsync(db, ct)) return PortalNotIncluded();

        var token = PortalLink.GenerateToken();
        var link = new VendorPortalLink
        {
            Id = Guid.NewGuid(),
            VendorId = v.Id,
            Token = token,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            MaxUploads = PortalLink.DefaultMaxUploads,
            UploadCount = 0
        };
        db.VendorPortalLinks.Add(link);
        await db.SaveChangesAsync(ct);
        // Interceptor records "vendorportallink.created" — no explicit duplicate (#318 FP-043).

        return Results.Ok(new
        {
            data = new
            {
                id = link.Id,
                token,
                url = PortalLink.Url(frontend.Value, token),
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
        // Verify the vendor belongs to the caller's org via the tenant-filtered set BEFORE touching
        // the (filter-less) VendorPortalLinks — otherwise a caller could revoke ANOTHER org's link by
        // passing its (vendorId, linkId) pair, since the link query alone isn't tenant-scoped (#242).
        // Mirrors EmailPortalLink's vendor-first lookup.
        if (!await db.Vendors.AnyAsync(v => v.Id == id, ct)) return NotFound();

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

        // Same #261 fence as GeneratePortalLink: a lapsed plan must not keep distributing
        // links it pre-minted while paid (the public portal rejects them anyway — emailing
        // one would hand the vendor a dead link). Checked before the actionable 400s below,
        // which would otherwise instruct the caller toward a feature their plan lacks.
        if (!await PortalIncludedInPlanAsync(db, ct)) return PortalNotIncluded();

        if (string.IsNullOrWhiteSpace(vendor.ContactEmail))
            return Error(400, "vendor.no_contact_email",
                "Add a contact email for this vendor first, then you can email them the upload link.");

        if (!link.IsActive)
            return Error(400, "vendorPortalLink.inactive",
                "This upload link has been revoked. Generate a new one to email it.");

        if (!email.IsEnabled)
            return Error(503, "email.not_configured",
                "Email isn't set up yet, so we couldn't send it. Copy the link and send it to your vendor instead.");

        var url = PortalLink.Url(frontend.Value, link.Token);
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

    /// <summary>
    /// Tenant-isolation guard for the request-body-bound <c>ComplianceTemplateId</c> (#273):
    /// without it, any GUID satisfying the FK binds — including ANOTHER org's template, whose
    /// rules the evaluation path (ComplianceCheckService via SystemDbContext, no tenant filter)
    /// would then run against this org's documents, leaking the foreign org's rule names /
    /// expected values into ComplianceCheck rows. The tenant-filtered ComplianceTemplates set
    /// already encodes the allow-list — <c>DeletedAt == null AND (IsSystemTemplate OR
    /// OrganizationId == CurrentOrgId)</c> — so "not visible through the filter" IS "not
    /// assignable". Cross-org, nonexistent, and soft-deleted ids all produce the IDENTICAL
    /// response (no existence disclosure). Null (clearing the assignment) is always allowed.
    /// </summary>
    private static async Task<bool> TemplateIsAssignable(Guid? templateId, AppDbContext db, CancellationToken ct) =>
        templateId is not Guid tid || await db.ComplianceTemplates.AnyAsync(t => t.Id == tid, ct);

    private static IResult TemplateNotFound() =>
        Error(400, "complianceTemplate.not_found",
            "That requirement checklist no longer exists. Refresh the page and pick another.");

    /// <summary>
    /// Monetization fence (#261, ADR 0024): the vendor portal is a paid entitlement. Gates
    /// on the <c>Subscription.HasVendorPortal</c> FLAG, never the <c>Plan</c> string —
    /// Stripe webhooks own the plan→flag mapping (StripeService), and a founder comp
    /// (manual flag flip, e.g. the demo org) must keep working without a Stripe
    /// subscription. The tenant filter scopes the query to the caller's org. Fail-closed
    /// when the org has no Subscription row: every org gets one at registration
    /// (AuthEndpoints), so a missing row is corrupt state, not a free pass through a
    /// pricing fence.
    /// </summary>
    private static async Task<bool> PortalIncludedInPlanAsync(AppDbContext db, CancellationToken ct) =>
        await db.Subscriptions.AnyAsync(s => s.HasVendorPortal, ct);

    private static IResult PortalNotIncluded() =>
        Error(403, "plan.portal_not_included",
            "Vendor upload links are a Pro feature. Upgrade your plan to collect documents straight from your vendors.");

    // #369: normalization (#340's trim-so-it-round-trips-against-the-suppression-key rule) now lives in
    // Services/ContactEmail alongside the format check, so the write path and the validity gate can't
    // disagree about what value was inspected. The rationale is preserved on ContactEmail.Normalize.
    // #369: the message is chosen per-input (ContactEmail.DescribeProblem) so an address rejected
    // for an INVISIBLE character says so, instead of telling the user to fix an address that already
    // looks correct. The same copy is mirrored client-side from the shared corpus.
    private static IResult ContactEmailInvalid(string? submitted) =>
        Error(400, "validation.contact_email",
            ContactEmail.DescribeProblem(submitted) ?? ContactEmail.InvalidMessage);

    // #340: maps the per-(org, email) suppression reason to the wire label the vendor badge renders
    // (null = deliverable). Shared by the list (dict lookup) and the detail (single lookup).
    private static string? LabelOf(EmailSuppressionReason? reason) => reason switch
    {
        EmailSuppressionReason.Bounced => "bounced",
        EmailSuppressionReason.Complained => "complained",
        _ => null
    };

    private static string? ContactEmailStatusLabel(
        string? contactEmail, IReadOnlyDictionary<string, EmailSuppressionReason> suppressions) =>
        !string.IsNullOrWhiteSpace(contactEmail) && suppressions.TryGetValue(contactEmail, out var reason)
            ? LabelOf(reason)
            : null;

    private static IResult Unauthorized() =>
        Results.Json(new { data = (object?)null, error = new { code = "auth.unauthorized", message = "Not authenticated." } }, statusCode: 401);

    private static IResult NotFound() =>
        Results.Json(new { data = (object?)null, error = new { code = "vendor.not_found", message = "Vendor not found." } }, statusCode: 404);

    private static IResult NotFoundLink() =>
        Results.Json(new { data = (object?)null, error = new { code = "vendorPortalLink.not_found", message = "Upload link not found." } }, statusCode: 404);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
