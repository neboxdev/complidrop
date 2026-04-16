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
        var compliant = await docs.CountAsync(d => d.ComplianceStatus == Entities.ComplianceStatus.Compliant, ct);
        var nonCompliant = await docs.CountAsync(d => d.ComplianceStatus == Entities.ComplianceStatus.NonCompliant, ct);
        var expiringSoon = await docs.CountAsync(d =>
            d.ExpirationDate != null
            && d.ExpirationDate >= today
            && d.ExpirationDate <= in30, ct);
        var expired = await docs.CountAsync(d => d.ExpirationDate != null && d.ExpirationDate < today, ct);
        var pendingExtraction = await docs.CountAsync(d =>
            d.ExtractionStatus == Entities.ExtractionStatus.Pending
            || d.ExtractionStatus == Entities.ExtractionStatus.Processing, ct);
        var vendors = await db.Vendors.CountAsync(ct);

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

    private static async Task<IResult> RecentActivity(AppDbContext db, CancellationToken ct)
    {
        var logs = await db.AuditLogs
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
            .ToListAsync(ct);
        return Results.Ok(new { data = logs, error = (object?)null });
    }
}
