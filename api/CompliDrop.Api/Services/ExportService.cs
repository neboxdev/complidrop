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

public interface IExportService
{
    Task<byte[]> BuildAuditReportAsync(Guid organizationId, DateTime from, DateTime to, CancellationToken ct);
    Task<byte[]> BuildCsvAsync(Guid organizationId, CancellationToken ct);
    Task<byte[]> BuildVendorReportAsync(Guid organizationId, Guid vendorId, CancellationToken ct);
}

public class ExportService(SystemDbContext db) : IExportService
{
    public async Task<byte[]> BuildAuditReportAsync(Guid organizationId, DateTime from, DateTime to, CancellationToken ct)
    {
        var org = await db.Organizations.FirstAsync(o => o.Id == organizationId, ct);
        var docs = await db.Documents
            .Where(d => d.OrganizationId == organizationId && d.DeletedAt == null)
            .Include(d => d.Vendor)
            .OrderBy(d => d.ExpirationDate)
            .ToListAsync(ct);
        var audit = await db.AuditLogs
            .Where(a => a.OrganizationId == organizationId && a.CreatedAt >= from && a.CreatedAt <= to)
            .OrderByDescending(a => a.CreatedAt)
            .Take(500)
            .ToListAsync(ct);

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
                    col.Item().Text($"{audit.Count} events from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}").FontSize(9).FontColor("#64748b");
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
                                    r.RelativeItem(2).Text(a.EntityType).FontSize(8);
                                    r.RelativeItem(3).Text(a.UserId?.ToString() ?? "system").FontSize(8);
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
