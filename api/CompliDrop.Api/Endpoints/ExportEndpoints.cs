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
        if (from is { } f && to is { } t && f.Date > t.Date)
            return InvalidRange("The start date must be on or before the end date.");
        // Sanity-bound the years so degenerate inputs (?to=9999-12-31) can't overflow
        // the window math (DateOnly.MaxValue has no next day → 500 instead of a 400).
        if (from is { } f2 && (f2.Year < 2000 || f2.Year > 2100)
            || to is { } t2 && (t2.Year < 2000 || t2.Year > 2100))
            return InvalidRange("Pick dates between the years 2000 and 2100.");

        try
        {
            // Window semantics (org-local calendar days, To inclusive) live in the
            // service, which owns the org row and its timezone (#262).
            var bytes = await export.BuildAuditReportAsync(currentUser.OrganizationId.Value, from, to, ct);
            return Results.File(bytes, "application/pdf", $"complidrop-audit-{DateTime.UtcNow:yyyyMMdd}.pdf");
        }
        catch (InvalidExportRangeException)
        {
            // A lone future `from` can invert against the org-local default `to` — only
            // the service knows the org's today, so this arm of the validation lives there.
            return InvalidRange("The start date must be on or before the end date.");
        }
    }

    private static IResult InvalidRange(string message) =>
        Results.Json(new
        {
            data = (object?)null,
            error = new { code = "export.invalid_range", message }
        }, statusCode: 400);

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
