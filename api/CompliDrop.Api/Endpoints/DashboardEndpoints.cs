using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
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
        var in30 = today.AddDays(30);
        var in60 = today.AddDays(60);

        var docs = db.Documents;
        var totalDocs = await docs.CountAsync(ct);
        // The headline buckets must be mutually exclusive on the EFFECTIVE (date-overlaid) status,
        // or a date-expired-but-stored-Compliant doc gets counted as BOTH compliant AND expired —
        // two answers on one screen (#257). Expired/ExpiringSoon are date-driven; compliant and
        // nonCompliant exclude any doc the date buckets already claim.
        var compliant = await docs.CountAsync(d =>
            d.ComplianceStatus == Entities.ComplianceStatus.Compliant
            && (d.ExpirationDate == null || d.ExpirationDate > in30), ct);
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
            && d.ExpirationDate <= in30
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
                complianceRate = totalDocs == 0 ? 0 : Math.Round((double)compliant / totalDocs * 100, 1)
            },
            error = (object?)null
        });
    }

    private static async Task<IResult> ExpiryPipeline(AppDbContext db, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var in30 = today.AddDays(30);
        var in60 = today.AddDays(60);
        var in90 = today.AddDays(90);

        var bucket30 = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate >= today && d.ExpirationDate <= in30, ct);
        var bucket60 = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate > in30 && d.ExpirationDate <= in60, ct);
        var bucket90 = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate > in60 && d.ExpirationDate <= in90, ct);
        var beyond = await db.Documents.CountAsync(d =>
            d.ExpirationDate != null && d.ExpirationDate > in90, ct);
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
    /// actions, and a <c>user.updated</c> that twins one of those is already dropped by the
    /// per-request/entity collapse below in favour of the specific action.
    /// </summary>
    private static readonly HashSet<string> FeedHiddenActions = new(StringComparer.Ordinal)
    {
        "user.updated",
    };

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
        // id (background jobs, non-entity events) are never collapsed.
        var buffer = await db.AuditLogs
            .OrderByDescending(a => a.CreatedAt)
            .Take(RecentActivityBuffer)
            .Select(a => new { a.Id, a.Action, a.EntityType, a.EntityId, a.CorrelationId, a.CreatedAt })
            .ToListAsync(ct);

        var logs = buffer
            .GroupBy(a => a.CorrelationId != null && a.EntityId != null
                ? $"{a.CorrelationId}|{a.EntityType}|{a.EntityId}"
                : a.Id.ToString("N")) // ungroupable rows each stay their own group
            .Select(g => g
                .OrderBy(a => IsGenericUpdate(a.Action, a.EntityType) ? 1 : 0) // specific action wins the twin
                .ThenByDescending(a => a.CreatedAt)
                .First())
            .Where(a => !FeedHiddenActions.Contains(a.Action))
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
