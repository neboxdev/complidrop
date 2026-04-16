using CompliDrop.Api.Auth;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Endpoints;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/export").RequireAuthorization();

        group.MapGet("/audit-report", AuditReport);
        group.MapGet("/csv", Csv);
        group.MapGet("/vendor/{vendorId:guid}", VendorPackage);
    }

    private static async Task<IResult> AuditReport(
        IExportService export,
        ICurrentUser currentUser,
        CancellationToken ct,
        DateTime? from = null,
        DateTime? to = null)
    {
        if (currentUser.OrganizationId is null) return Results.Unauthorized();
        var windowTo = (to ?? DateTime.UtcNow).ToUniversalTime();
        var windowFrom = (from ?? windowTo.AddDays(-30)).ToUniversalTime();
        var bytes = await export.BuildAuditReportAsync(currentUser.OrganizationId.Value, windowFrom, windowTo, ct);
        return Results.File(bytes, "application/pdf", $"complidrop-audit-{DateTime.UtcNow:yyyyMMdd}.pdf");
    }

    private static async Task<IResult> Csv(
        IExportService export,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Results.Unauthorized();
        var bytes = await export.BuildCsvAsync(currentUser.OrganizationId.Value, ct);
        return Results.File(bytes, "text/csv", $"complidrop-documents-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    private static async Task<IResult> VendorPackage(
        Guid vendorId,
        IExportService export,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Results.Unauthorized();
        var bytes = await export.BuildVendorReportAsync(currentUser.OrganizationId.Value, vendorId, ct);
        return Results.File(bytes, "application/pdf", $"complidrop-vendor-{vendorId}.pdf");
    }
}
