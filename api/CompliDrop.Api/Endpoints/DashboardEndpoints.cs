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
        // #362 / ADR 0041: a doc whose EffectiveDate is a date strictly after today is "not yet in force";
        // an affirmative verdict (Compliant/ExpiringSoon) demotes to Pending. Instant form: EffectiveDate
        // >= today+1 (UTC midnight) — same date↔instant convention as the expiry bound above (ADR 0027),
        // so "in force / no effective date" is (EffectiveDate == null || EffectiveDate < notYetEffectiveBound).
        var notYetEffectiveBound = ComplianceStatusDeriver.NotYetEffectiveLowerBoundInclusive(today);

        var docs = db.Documents;
        var totalDocs = await docs.CountAsync(ct);
        // The headline buckets must be mutually exclusive on the EFFECTIVE (date-overlaid) status,
        // or a date-expired-but-stored-Compliant doc gets counted as BOTH compliant AND expired —
        // two answers on one screen (#257). Expired/ExpiringSoon are date-driven; compliant and
        // nonCompliant exclude any doc the date buckets already claim. A future-effective doc is
        // demoted OUT of compliant/expiringSoon into the effective-Pending population (#362).
        var compliant = await docs.CountAsync(d =>
            d.ComplianceStatus == Entities.ComplianceStatus.Compliant
            && (d.ExpirationDate == null || d.ExpirationDate >= expiringSoonUpperExclusive)
            && (d.EffectiveDate == null || d.EffectiveDate < notYetEffectiveBound), ct);
        var nonCompliant = await docs.CountAsync(d =>
            d.ComplianceStatus == Entities.ComplianceStatus.NonCompliant
            && (d.ExpirationDate == null || d.ExpirationDate >= today), ct);
        // ExpiringSoon must use the SAME stored-status eligibility as ComplianceStatusDeriver and the
        // documents-list ExpiringSoon filter: a NonCompliant doc expiring soon stays NonCompliant
        // (its hard fail isn't softened by the date), so it must NOT also be counted here — otherwise
        // it double-counts under both nonCompliant and expiringSoon. Expired stays status-agnostic
        // (Expired is top precedence; the compliant/nonCompliant arms already exclude past-date docs).
        // A future-effective doc is not yet in force, so it is excluded here too (reads Pending) (#362).
        var expiringSoon = await docs.CountAsync(d =>
            d.ExpirationDate != null
            && d.ExpirationDate >= today
            && d.ExpirationDate < expiringSoonUpperExclusive
            && (d.EffectiveDate == null || d.EffectiveDate < notYetEffectiveBound)
            && (d.ComplianceStatus == Entities.ComplianceStatus.Compliant
                || d.ComplianceStatus == Entities.ComplianceStatus.ExpiringSoon
                || d.ComplianceStatus == Entities.ComplianceStatus.Pending), ct);
        // #327: the Expired liability de-counts superseded (renewed) old certs (CurrentExpiredCountAsync
        // below). The compliant/nonCompliant/expiringSoon counts above are deliberately NOT de-superseded
        // (they're verdict/informational tallies, not the Expired liability the ticket scopes) — see ADR 0033.
        var expired = await CurrentExpiredCountAsync(db, today, ct);
        // Denominator for the compliance rate EXCLUDES not-yet-graded documents (#318 FP-042): a fresh
        // upload sits ComplianceStatus.Pending until the worker reads + evaluates it, and counting those
        // as "not compliant" flashed a demoralizing "0%" the instant Pat uploaded her very first document.
        // Rate = effective-compliant / documents-that-have-a-verdict. A future-effective doc reads Pending
        // (not yet in force, #362), so it is excluded from the denominator too — treated exactly like a
        // Pending doc, not as a graded non-compliant one; an Expired doc (a real verdict) stays counted.
        var evaluated = await docs.CountAsync(d =>
            d.ComplianceStatus != Entities.ComplianceStatus.Pending
            && !(d.EffectiveDate != null && d.EffectiveDate >= notYetEffectiveBound
                && (d.ExpirationDate == null || d.ExpirationDate >= today)
                && (d.ComplianceStatus == Entities.ComplianceStatus.Compliant
                    || d.ComplianceStatus == Entities.ComplianceStatus.ExpiringSoon)), ct);
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
                complianceRate = evaluated == 0 ? 0 : Math.Round((double)compliant / evaluated * 100, 1)
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
        // #327: the EXPIRED bucket de-counts superseded (renewed) certs (CurrentExpiredCountAsync),
        // consistent with the dashboard Expired count. The future buckets (30/60/90/beyond) deliberately
        // retain every document: a not-yet-expired cert is informational, not yet a missed liability, and a
        // vendor commonly renews early so both the old (soon) and new (far) cert legitimately appear. ADR 0033.
        var expired = await CurrentExpiredCountAsync(db, today, ct);

        return Results.Ok(new
        {
            data = new { expired, bucket30, bucket60, bucket90, beyond },
            error = (object?)null
        });
    }

    /// <summary>
    /// The current Expired-liability count (#327 / ADR 0033): documents past their expiry that are NOT
    /// superseded by a newer cert for the same (vendor, type). Shared by the dashboard <c>expired</c> stat
    /// and the expiry pipeline's expired bucket so the two can never drift, and both match the deep-linked
    /// documents list (<c>?status=Expired</c>). Deliberately NOT applied to the compliant/nonCompliant/
    /// expiringSoon stats or the future pipeline buckets.
    /// </summary>
    private static Task<int> CurrentExpiredCountAsync(AppDbContext db, DateTime today, CancellationToken ct) =>
        db.Documents
            .Where(d => d.ExpirationDate != null && d.ExpirationDate < today)
            .Where(DocumentSupersession.IsCurrent(db.Documents))
            .CountAsync(ct);

    /// <summary>How many rows to over-fetch before collapsing twins down to the 20 shown.</summary>
    private const int RecentActivityBuffer = 60;

    /// <summary>
    /// Curated allow-list of the audit actions Pat should see in the activity feed (#318 FP-043).
    /// A WHITELIST, not a blocklist, so any unmapped/internal action (e.g. the welcome-tour
    /// <c>user.updated</c> flip, the per-evaluation <c>compliancecheck.created</c> ×N noise, raw
    /// <c>documentfield.*</c> / <c>vendorportallink.updated</c> machine churn, or login noise per
    /// FP-049) is fail-closed OUT of the feed rather than leaking as title-cased entity-speak. The
    /// AuditLog TABLE still keeps every row — only the FEED is curated; the audit export is unchanged.
    /// Stored lowercase and matched against <c>lower(Action)</c> so it covers BOTH the interceptor's
    /// all-lowercase entity actions AND the explicit camelCase ones (<c>complianceRule.upserted</c>,
    /// <c>vendorPortalLink.upload_processed</c>). An array (not HashSet) so EF translates
    /// <c>Contains</c> to a server-side <c>= ANY</c>. Keep in sync with the labelled actions in
    /// DisplayLabels.Action / display-labels.ts.
    /// </summary>
    private static readonly string[] FeedVisibleActions =
    [
        // "document.uploaded" is no longer EMITTED (the owner-upload explicit row was dropped in
        // FP-043; uploads now record "document.created" via the interceptor) — but it stays whitelisted
        // + labelled so HISTORICAL rows from before #318 still render in the feed.
        "document.created", "document.uploaded", "document.updated", "document.deleted",
        "document.verified", "document.fields_edited", "document.reextract_queued", "document.processed",
        "vendor.created", "vendor.updated", "vendor.deleted",
        "vendorportallink.created", "vendorportallink.revoked", "vendorportallink.deleted",
        "vendorportallink.emailed", "vendorportallink.upload_processed",
        "compliancetemplate.created", "compliancetemplate.updated", "compliancetemplate.deleted",
        "compliancerule.created", "compliancerule.updated", "compliancerule.upserted", "compliancerule.deleted",
        "reminder.sent", "reminder.recipient_suppressed",
        "user.registered", "user.password_changed", "user.password_reset",
        "user.email_verified", "user.email_changed", "user.account_deleted",
    ];

    /// <summary>
    /// The interceptor's GENERIC entity action for a type — <c>{type}.created</c> or
    /// <c>{type}.updated</c>. In a collapse group these LOSE to an explicit REFINED twin so the feed
    /// shows the meaningful verb: <c>compliancerule.upserted</c> beats the interceptor's
    /// <c>compliancerule.created</c> ("Requirement saved", not "Requirement added"), and
    /// <c>document.verified</c> beats <c>document.updated</c>. A standalone generic action (no refined
    /// twin in its group) is the group's only row and still shows. (#318 FP-043 + review S1)
    /// </summary>
    private static bool IsGenericEntityMutation(string action, string entityType)
    {
        var prefix = entityType.ToLowerInvariant();
        return string.Equals(action, $"{prefix}.updated", StringComparison.Ordinal)
            || string.Equals(action, $"{prefix}.created", StringComparison.Ordinal);
    }

    private static async Task<IResult> RecentActivity(AppDbContext db, CancellationToken ct)
    {
        // Over-fetch, then collapse the audit "twin" in the FEED — the AuditLog TABLE keeps both rows
        // so the audit export is unchanged (#252). In ONE request, a single entity mutation can emit
        // BOTH the AuditSaveChangesInterceptor's generic "{type}.created/updated/deleted" row AND an
        // explicit IAuditLogger row — identical-named (vendor.created x2) or refined (document.verified
        // vs the interceptor's document.updated). Keep ONE row per (request, entity), preferring the
        // more specific action (the generic "...updated" loses). Rows with no correlation id or entity
        // id (background jobs, non-entity events) are never collapsed. The buffer (60 for 20) absorbs
        // the ~2:1 twin ratio; non-whitelisted actions are filtered in SQL so they don't eat into it.
        var buffer = await db.AuditLogs
            .Where(a => FeedVisibleActions.Contains(a.Action.ToLower()))
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
                .OrderBy(a => IsGenericEntityMutation(a.Action, a.EntityType) ? 1 : 0) // specific action wins the twin
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
