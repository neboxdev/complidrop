using System.Globalization;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CompliDrop.Api.Services;

/// <summary>The requested audit window is inverted (from after to) even after defaults
/// resolve; the endpoint maps this to the 400 export.invalid_range envelope (#262).</summary>
public sealed class InvalidExportRangeException : Exception;

public interface IExportService
{
    /// <summary>
    /// Builds the audit PDF for a window of ORG-LOCAL calendar days (#262). Bare
    /// <paramref name="from"/>/<paramref name="to"/> dates are interpreted in the org's
    /// IANA timezone with <paramref name="to"/> INCLUSIVE end-of-day; null
    /// <paramref name="to"/> = the org's today, null <paramref name="from"/> = to − 30 days.
    /// </summary>
    Task<byte[]> BuildAuditReportAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken ct);
    Task<byte[]> BuildCsvAsync(Guid organizationId, CancellationToken ct);
    Task<byte[]> BuildVendorReportAsync(Guid organizationId, Guid vendorId, CancellationToken ct);
}

public class ExportService(SystemDbContext db) : IExportService
{
    internal const int AuditCap = 500;

    /// <summary>
    /// The audit-slice query as an internal seam (#262 review): the To-day boundary bug
    /// lived in exactly this predicate, and the PDF is FlateDecode-compressed (not
    /// text-assertable), so the half-open comparison is pinned by a Testcontainers test
    /// driving this method directly with ResolveAuditWindow's outputs. Fetches one past
    /// the cap so truncation is disclosed instead of silently dropping events (#197).
    /// </summary>
    internal async Task<(List<AuditLog> Rows, bool Truncated)> QueryAuditSliceAsync(
        Guid organizationId, DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        var raw = await db.AuditLogs
            .Where(a => a.OrganizationId == organizationId && a.CreatedAt >= fromUtc && a.CreatedAt < toUtcExclusive)
            .OrderByDescending(a => a.CreatedAt)
            .Take(AuditCap + 1)
            .ToListAsync(ct);
        var truncated = raw.Count > AuditCap;
        return (truncated ? raw.Take(AuditCap).ToList() : raw, truncated);
    }

    public async Task<byte[]> BuildAuditReportAsync(Guid organizationId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var org = await db.Organizations.FirstAsync(o => o.Id == organizationId, ct);

        // Resolve the window as org-local calendar days (#262). The previous
        // `.ToUniversalTime()` on bare (Kind=Unspecified) query dates interpreted them in
        // the SERVER zone, and `CreatedAt <= to` cut the window at midnight at the START
        // of the To day — with the default UI range (To = today) the most recent day was
        // always missing while the caption claimed it was covered.
        var (fromUtc, toUtcExclusive, fromDate, toDate) =
            ResolveAuditWindow(from, to, org.TimeZone, DateTime.UtcNow);

        var docs = await db.Documents
            .Where(d => d.OrganizationId == organizationId && d.DeletedAt == null)
            .Include(d => d.Vendor)
            .OrderBy(d => d.ExpirationDate)
            .ToListAsync(ct);
        var (audit, auditTruncated) = await QueryAuditSliceAsync(organizationId, fromUtc, toUtcExclusive, ct);

        // Resolve UserIds to human names so the report shows WHO acted, not a raw
        // GUID. IgnoreQueryFilters so a soft-deleted account (ADR 0013) still
        // attributes its historical actions — an audit report that forgets who
        // did something the moment they delete their account is worthless. This
        // is a system/export context, where IgnoreQueryFilters is sanctioned.
        var userIds = audit.Where(a => a.UserId is not null)
            .Select(a => a.UserId!.Value).Distinct().ToList();
        var userDisplay = (await db.Users
                .IgnoreQueryFilters()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.Email })
                .ToListAsync(ct))
            .ToDictionary(u => u.Id, u => DisplayName(u.FullName, u.Email));

        var reportDate = DateTime.UtcNow.ToString("MMMM d, yyyy");

        return QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontFamily("Helvetica").FontSize(10).FontColor("#0c4a6e"));

                page.Header().Column(col =>
                {
                    col.Item().Text("CompliDrop Audit Report").FontSize(22).SemiBold().FontColor("#0284c7");
                    col.Item().Text(org.Name).FontSize(14);
                    col.Item().Text($"Generated {reportDate}").FontSize(9).FontColor("#64748b");
                });

                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Item().PaddingTop(12).Text("Documents").SemiBold().FontSize(14);
                    col.Item().Element(e =>
                        e.Border(1).BorderColor("#e2e8f0").Padding(8).Column(inner =>
                        {
                            inner.Item().Row(r =>
                            {
                                r.RelativeItem(3).Text("File").SemiBold();
                                r.RelativeItem(2).Text("Vendor").SemiBold();
                                r.RelativeItem(2).Text("Type").SemiBold();
                                r.RelativeItem(2).Text("Expires").SemiBold();
                                r.RelativeItem(2).Text("Compliance").SemiBold();
                            });
                            foreach (var d in docs)
                            {
                                inner.Item().PaddingTop(3).Row(r =>
                                {
                                    r.RelativeItem(3).Text(d.OriginalFileName).FontSize(9);
                                    r.RelativeItem(2).Text(d.Vendor?.Name ?? "—").FontSize(9);
                                    r.RelativeItem(2).Text(DisplayLabels.DocumentType(d.DocumentType)).FontSize(9);
                                    r.RelativeItem(2).Text(d.ExpirationDate?.ToString("yyyy-MM-dd") ?? "—").FontSize(9);
                                    r.RelativeItem(2).Text(DisplayLabels.Compliance(d.ComplianceStatus)).FontSize(9);
                                });
                            }
                        }));

                    col.Item().PaddingTop(18).Text("Audit Log").SemiBold().FontSize(14);
                    col.Item().Text(auditTruncated
                            ? $"Showing the {AuditCap} most recent events from {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}"
                            : $"{audit.Count} events from {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}")
                        .FontSize(9).FontColor("#64748b");
                    col.Item().Element(e =>
                        e.Border(1).BorderColor("#e2e8f0").Padding(8).Column(inner =>
                        {
                            inner.Item().Row(r =>
                            {
                                r.RelativeItem(2).Text("When").SemiBold();
                                r.RelativeItem(3).Text("Action").SemiBold();
                                r.RelativeItem(2).Text("Entity").SemiBold();
                                r.RelativeItem(3).Text("User").SemiBold();
                            });
                            foreach (var a in audit)
                            {
                                inner.Item().PaddingTop(3).Row(r =>
                                {
                                    r.RelativeItem(2).Text(a.CreatedAt.ToString("yyyy-MM-dd HH:mm")).FontSize(8);
                                    r.RelativeItem(3).Text(DisplayLabels.Action(a.Action)).FontSize(8);
                                    r.RelativeItem(2).Text(DisplayLabels.EntityType(a.EntityType)).FontSize(8);
                                    r.RelativeItem(3).Text(UserLabel(a.UserId, userDisplay)).FontSize(8);
                                });
                            }
                        }));
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("CompliDrop · ").FontSize(8).FontColor("#64748b");
                    x.Span(org.Name).FontSize(8).FontColor("#64748b");
                });
            });
        }).GeneratePdf();
    }

    // Human label for an audit row's actor: the user's name/email, or a
    // capitalized "System" for system-initiated events (null UserId) and the
    // rare hard-deleted-user edge. NEVER a raw GUID. internal for direct unit
    // testing (InternalsVisibleTo → CompliDrop.Api.Tests). (#197)
    internal static string UserLabel(Guid? userId, IReadOnlyDictionary<Guid, string> userDisplay) =>
        userId is Guid id && userDisplay.TryGetValue(id, out var name) ? name : "System";

    // The display name for an audit actor: their full name, or their email when
    // the name is blank/whitespace (e.g. a vendor-portal-era account). internal
    // for unit testing. (#197 review)
    internal static string DisplayName(string? fullName, string email) =>
        string.IsNullOrWhiteSpace(fullName) ? email : fullName;

    /// <summary>
    /// Resolves the audit window from bare request dates to UTC instants bracketing
    /// ORG-LOCAL calendar days (#262): [start of fromDate, start of the day AFTER
    /// toDate) in the org's zone — i.e. To is inclusive end-of-day. Defaults: null
    /// to = the org's today; null from = to − 30 days. The returned DateOnly pair is
    /// what the PDF caption shows, so the caption and the query can never disagree.
    /// Mirrors the reminder worker's host-independent window math (unknown timezone
    /// id falls back to UTC). internal for direct unit testing (InternalsVisibleTo).
    /// </summary>
    internal static (DateTime FromUtc, DateTime ToUtcExclusive, DateOnly FromDate, DateOnly ToDate)
        ResolveAuditWindow(DateTime? from, DateTime? to, string orgTimeZone, DateTime nowUtc)
    {
        var tz = TimeZones.TryFind(orgTimeZone);
        var todayLocal = DateOnly.FromDateTime(
            tz is null ? nowUtc : TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));
        var toDate = to is { } t ? DateOnly.FromDateTime(t) : todayLocal;
        var fromDate = from is { } f ? DateOnly.FromDateTime(f) : toDate.AddDays(-30);

        // The endpoint validates the both-provided case, but a lone future `from`
        // resolves against the org's today and can still invert — which would render
        // a self-contradicting caption ("0 events from 2027-01-01 to 2026-06-11").
        if (fromDate > toDate) throw new InvalidExportRangeException();

        return (
            StartOfLocalDayUtc(fromDate, tz),
            StartOfLocalDayUtc(toDate.AddDays(1), tz),
            fromDate,
            toDate);
    }

    // Start of the given org-local calendar day as a UTC instant. Some zones have
    // historically sprung forward AT midnight (e.g. Brazil), making local 00:00
    // nonexistent — map into the gap's end so the day still starts where the
    // clocks do (DST gaps are at most an hour).
    private static DateTime StartOfLocalDayUtc(DateOnly day, TimeZoneInfo? tz)
    {
        var midnight = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        if (tz is null) return DateTime.SpecifyKind(midnight, DateTimeKind.Utc);
        if (tz.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, tz);
    }


    public async Task<byte[]> BuildCsvAsync(Guid organizationId, CancellationToken ct)
    {
        var docs = await db.Documents
            .Where(d => d.OrganizationId == organizationId && d.DeletedAt == null)
            .Include(d => d.Vendor)
            .OrderBy(d => d.ExpirationDate)
            .ToListAsync(ct);

        await using var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, leaveOpen: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        await using var csv = new CsvWriter(writer, config);

        csv.WriteField("Id");
        csv.WriteField("FileName");
        csv.WriteField("Vendor");
        csv.WriteField("Type");
        csv.WriteField("Status");
        csv.WriteField("Compliance");
        csv.WriteField("EffectiveDate");
        csv.WriteField("ExpirationDate");
        csv.WriteField("GeneralLiabilityLimit");
        csv.WriteField("UploadedBy");
        csv.WriteField("CreatedAt");
        await csv.NextRecordAsync();

        foreach (var d in docs)
        {
            csv.WriteField(d.Id);
            csv.WriteField(d.OriginalFileName);
            csv.WriteField(d.Vendor?.Name ?? "");
            csv.WriteField(DisplayLabels.DocumentType(d.DocumentType));
            csv.WriteField(DisplayLabels.Extraction(d.ExtractionStatus));
            csv.WriteField(DisplayLabels.Compliance(d.ComplianceStatus));
            csv.WriteField(d.EffectiveDate?.ToString("yyyy-MM-dd"));
            csv.WriteField(d.ExpirationDate?.ToString("yyyy-MM-dd"));
            csv.WriteField(d.GeneralLiabilityLimit?.ToString(CultureInfo.InvariantCulture));
            csv.WriteField(d.UploadedBy ?? "");
            csv.WriteField(d.CreatedAt.ToString("u"));
            await csv.NextRecordAsync();
        }
        await writer.FlushAsync(ct);
        return ms.ToArray();
    }

    public async Task<byte[]> BuildVendorReportAsync(Guid organizationId, Guid vendorId, CancellationToken ct)
    {
        var vendor = await db.Vendors
            .Include(v => v.Documents)
            .FirstOrDefaultAsync(v => v.Id == vendorId && v.OrganizationId == organizationId, ct)
            ?? throw new InvalidOperationException("Vendor not found.");

        return QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontFamily("Helvetica").FontSize(10).FontColor("#0c4a6e"));
                page.Header().Column(col =>
                {
                    col.Item().Text("Vendor Compliance Package").FontSize(18).SemiBold().FontColor("#0284c7");
                    col.Item().Text(vendor.Name).FontSize(14);
                });
                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Item().Text($"Documents: {vendor.Documents.Count}");
                    foreach (var d in vendor.Documents.OrderBy(d => d.ExpirationDate))
                    {
                        col.Item().PaddingTop(6).Text($"• {d.OriginalFileName} — {DisplayLabels.DocumentType(d.DocumentType)} — expires {d.ExpirationDate?.ToString("yyyy-MM-dd") ?? "unknown"} — {DisplayLabels.Compliance(d.ComplianceStatus)}");
                    }
                });
            });
        }).GeneratePdf();
    }
}
