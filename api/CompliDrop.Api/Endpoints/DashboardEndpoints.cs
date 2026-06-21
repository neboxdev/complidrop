using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/dashboard").RequireAuthorization();

        group.MapGet("/stats", Stats);
        group.MapGet("/expiry-pipeline", ExpiryPipeline);
        group.MapGet("/recent-activity", RecentActivity);
    }

    private static async Task<IResult> Stats(AppDbContext db, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        // Exclusive instant upper bound for the 30-day window so these raw-timestamptz comparisons
        // match ComplianceStatusDeriver's date-only window on the boundary day (#294): "within 30
        // days" is exp < today+31 (UTC midnight); "beyond the window" is exp >= today+31. The lower
        // edges (< today / >= today) are already date-equivalent at midnight.
        var expiringSoonUpperExclusive =
            ComplianceStatusDeriver.WindowUpperBoundExclusive(today, ComplianceStatusDeriver.ExpiringSoonWindowDays);

        var docs = db.Documents;
        var totalDocs = await docs.CountAsync(ct);
        // The headline buckets must be mutually exclusive on the EFFECTIVE (date-overlaid) status,
        // or a date-expired-but-stored-Compliant doc gets counted as BOTH compliant AND expired —
        // two answers on one screen (#257). Expired/ExpiringSoon are date-driven; compliant and
        // nonCompliant exclude any doc the date buckets already claim.
        var compliant = await docs.CountAsync(d =>
            d.ComplianceStatus == Entities.ComplianceStatus.Compliant
            && (d.ExpirationDate == null || d.ExpirationDate >= expiringSoonUpperExclusive), ct);
        var nonCompliant = await docs.CountAsync(d =>
            d.ComplianceStatus == Entities.ComplianceStatus.NonCompliant
            && (d.ExpirationDate == null || d.ExpirationDate >= today), ct);
        // ExpiringSoon must use the SAME stored-status eligibility as ComplianceStatusDeriver and the
        // documents-list ExpiringSoon filter: a NonCompliant doc expiring soon stays NonCompliant
        // (its hard fail isn't softened by the date), so it must NOT also be counted here — otherwise
        // it double-counts under both nonCompliant and expiringSoon. Expired stays status-agnostic
        // (Expired is top precedence; the compliant/nonCompliant arms already exclude past-date docs).
        var expiringSoon = await docs.CountAsync(d =>
            d.ExpirationDate != null
            && d.ExpirationDate >= today
            && d.ExpirationDate < expiringSoonUpperExclusive
            && (d.ComplianceStatus == Entities.ComplianceStatus.Compliant
                || d.ComplianceStatus == Entities.ComplianceStatus.ExpiringSoon
                || d.ComplianceStatus == Entities.ComplianceStatus.Pending), ct);
        var expired = await docs.CountAsync(d => d.ExpirationDate != null && d.ExpirationDate < today, ct);
        var pendingExtraction = await docs.CountAsync(d =>
            d.ExtractionStatus == Entities.ExtractionStatus.Pending
            || d.ExtractionStatus == Entities.ExtractionStatus.Processing, ct);
        var vendors = await db.Vendors.CountAsync(ct);
        // Cheap boolean for the #191 "Get started" checklist — lets the dashboard
        // derive the "choose requirements" step from /api/dashboard/stats (already
        // fetched) instead of pulling the full vendor list on every load.
        var anyVendorWithRequirements = await db.Vendors.AnyAsync(v => v.ComplianceTemplateId != null, ct);
        // Drives the onboarding checklist's "Link sent — waiting for their upload" state (#239 delta 3)
        // so the funnel doesn't go quiet while waiting on a vendor. Scoped through the tenant-filtered
        // Vendors set (VendorPortalLink has no global tenant filter of its own — it's reached via Vendor).
        var anyActivePortalLink = await db.Vendors.AnyAsync(v => v.PortalLinks.Any(l => l.IsActive), ct);

        // One source of truth for the sample-certificate demo (#238): the dashboard CTA ("Try a
        // sample certificate" vs "Clear sample data" banner), the onboarding checklist's document
        // step, and the clear affordance all read these instead of each pulling the documents list.
        // sampleDocumentId is the doc to deep-link to after seeding; hasSampleData also covers a
        // lingering sample vendor whose doc was manually deleted, so "Clear sample data" still shows.
        var sampleDocumentId = await docs
            .Where(d => d.IsSample)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);
        var hasSampleData = sampleDocumentId != null || await db.Vendors.AnyAsync(v => v.IsSample, ct);

        return Results.Ok(new
        {
            data = new
            {
                totalDocuments = totalDocs,
                compliant,
                nonCompliant,
                expiringSoon,
                expired,
                pendingExtraction,
                totalVendors = vendors,
                anyVendorWithRequirements,
                anyActivePortalLink,
                hasSampleData,
                sampleDocumentId,
                complianceRate = totalDocs == 0 ? 0 : Math.Round((double)compliant / totalDocs * 100, 1)
            },
            error = (object?)null
        });
    }

    private static async Task<IResult> ExpiryPipeline(AppDbContext db, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        // Exclusive upper bounds (exp < today+N+1) so each "within N days" bucket matches a date-only
        // window: a time-bearing expiry on day N lands in the N-day bucket, not the next one (#294).
        // Buckets stay disjoint and gap-free: [< today] expired, [today, 30] , (30, 60], (60, 90], (> 90].
        var in31 = ComplianceStatusDeriver.WindowUpperBoundExclusive(today, 30);
        var in61 = ComplianceStatusDeriver.WindowUpperBoundExclusive(today, 60);
        var in91 = ComplianceStatusDeriver.WindowUpperBoundExclusive(today, 90);

        var bucket30 = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate >= today && d.ExpirationDate < in31, ct);
        var bucket60 = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate >= in31 && d.ExpirationDate < in61, ct);
        var bucket90 = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate >= in61 && d.ExpirationDate < in91, ct);
        var beyond = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate >= in91, ct);
        var expired = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate < today, ct);

        return Results.Ok(new
        {
            data = new { expired, bucket30, bucket60, bucket90, beyond },
            error = (object?)null
        });
    }

    /// <summary>How many rows to over-fetch before collapsing twins down to the 20 shown.</summary>
    private const int RecentActivityBuffer = 60;

    /// <summary>
    /// Internal-flag mutations with no user-meaningful label — they would render as raw
    /// "Entity - Operation" entity-speak in the feed (#252). The canonical case is the interceptor's
    /// bare <c>user.updated</c> from the welcome-tour <c>HasCompletedOnboarding</c> flip; meaningful
    /// user events (sign-in, password/email change, account delete) have their own explicit, labelled
    /// actions. Filtered in the SQL query (below) so a hidden row never consumes a buffer slot. An
    /// array (not HashSet) so EF translates <c>!Contains</c> to a server-side <c>NOT IN</c>.
    /// </summary>
    private static readonly string[] FeedHiddenActions = ["user.updated"];

    /// <summary>The interceptor's generic update action for an entity type, e.g. <c>vendor.updated</c>.</summary>
    private static bool IsGenericUpdate(string action, string entityType) =>
        string.Equals(action, $"{entityType.ToLowerInvariant()}.updated", StringComparison.Ordinal);

    private static async Task<IResult> RecentActivity(AppDbContext db, CancellationToken ct)
    {
        // Over-fetch, then collapse the audit "twin" in the FEED — the AuditLog TABLE keeps both rows
        // so the audit export is unchanged (#252). In ONE request, a single entity mutation can emit
        // BOTH the AuditSaveChangesInterceptor's generic "{type}.created/updated/deleted" row AND an
        // explicit IAuditLogger row — identical-named (vendor.created x2) or refined (document.verified
        // vs the interceptor's document.updated). Keep ONE row per (request, entity), preferring the
        // more specific action (the generic "...updated" loses). Rows with no correlation id or entity
        // id (background jobs, non-entity events) are never collapsed. The buffer (60 for 20) absorbs
        // the ~2:1 twin ratio; hidden actions are excluded in SQL so they don't eat into it.
        var buffer = await db.AuditLogs
            .Where(a => !FeedHiddenActions.Contains(a.Action))
            .OrderByDescending(a => a.CreatedAt)
            .Take(RecentActivityBuffer)
            .Select(a => new { a.Id, a.Action, a.EntityType, a.EntityId, a.CorrelationId, a.CreatedAt })
            .ToListAsync(ct);

        var logs = buffer
            // Value-tuple key (not a "|"-joined string) so a client-supplied CorrelationId (X-Trace-Id)
            // containing the delimiter can't collide two distinct events into one group. Ungroupable
            // rows (null correlation or entity id) carry their own Id in the 4th slot so each stays
            // its own group; groupable rows put null there and collapse by (correlation, type, entity).
            .GroupBy(a => (a.CorrelationId, a.EntityType, a.EntityId,
                Distinct: a.CorrelationId != null && a.EntityId != null ? (Guid?)null : a.Id))
            .Select(g => g
                .OrderBy(a => IsGenericUpdate(a.Action, a.EntityType) ? 1 : 0) // specific action wins the twin
                .ThenByDescending(a => a.CreatedAt)
                .First())
            .OrderByDescending(a => a.CreatedAt)
            .Take(20)
            .Select(a => new
            {
                id = a.Id,
                action = a.Action,
                entityType = a.EntityType,
                entityId = a.EntityId,
                createdAt = a.CreatedAt
            })
            .ToList();
        return Results.Ok(new { data = logs, error = (object?)null });
    }
}
