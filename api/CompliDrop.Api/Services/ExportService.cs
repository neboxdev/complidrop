using System.Globalization;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

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
    /// The non-advice disclaimer carried by every export a customer hands to a THIRD PARTY (#402,
    /// counsel-gate item CLM-3 — <c>docs/rule-engine/G1-COUNSEL-BRIEF.md</c> §0;
    /// <c>docs/adr/0047-exports-carry-a-non-advice-disclaimer.md</c>).
    /// <para/>
    /// ONE constant consumed by ALL THREE such artifacts — the audit PDF, the vendor package PDF and
    /// the CSV — because an export is the artifact most likely forwarded to an insurer, broker, auditor
    /// or opposing counsel, and it is the surface where reliance forms. Three hand-copied literals is
    /// how two of them silently drift apart, so there is exactly one (pinned by
    /// <c>ExportDisclaimerTests</c>).
    /// <para/>
    /// Scope is the reliance artifacts, NOT everything the API can serialize: the account data export
    /// (<c>AuthEndpoints.ExportAccount</c>) is a portability dump of the account's own data back to its
    /// owner — raw JSON, no masthead, bare numeric enum codes rather than rendered verdict labels — and
    /// is deliberately excluded, as are the reminder emails and the sample certificate (ADR 0047
    /// § Consequences → Neutral records each).
    /// <para/>
    /// Consistent with the Terms ("Automatic reading is a head start, not advice") but deliberately
    /// narrower: it states what the statuses ARE and what a certificate cannot do, and it does not
    /// invent legal claims the Terms do not make. The exact wording — and its PROMINENCE, which today
    /// matches the branding line beneath it — is PROVISIONAL pending the CLM-3 attorney sign-off
    /// (ADR 0047 §5): refine the sentence here, and the treatment in <see cref="ApplyPageDefaults"/>.
    /// </summary>
    internal const string Disclaimer = "Statuses reflect automated reading of documents as uploaded; certificates do not modify policies. Verify current coverage with the issuing carrier.";

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
        // #327: annotate superseded (renewed) old certs in the audit report so a reader sees the old cert
        // AND knows it was replaced — same coverage-extending-renewal rule as the CSV (DocumentSupersession).
        var supersededIds = SupersededIds(docs);
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
        // Derive the date-driven verdict at GENERATION time so the audit-ready report can never
        // certify an expired document as Compliant off a stale cache (#257).
        var today = DateTime.UtcNow.Date;

        return QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                // The org name rides in the footer's attribution line, beneath the #402 disclaimer.
                ApplyPageDefaults(page, org.Name);

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
                                    r.RelativeItem(2).Text(DisplayLabels.Compliance(
                                        ComplianceStatusDeriver.Effective(d.ComplianceStatus, d.ExpirationDate, d.EffectiveDate, today))
                                        + (supersededIds.Contains(d.Id) ? " (superseded)" : "")).FontSize(9);
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
    // historically sprung forward AT midnight (e.g. Brazil 2018), making local 00:00
    // nonexistent — and a few skipped ENTIRE days crossing the date line (Pacific/Apia
    // 2011-12-30, modeled as invalid only by Linux tzdata, not Windows), so the guard
    // iterates until it exits the gap instead of assuming a one-hour DST hole.
    private static DateTime StartOfLocalDayUtc(DateOnly day, TimeZoneInfo? tz)
    {
        var midnight = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        if (tz is null) return DateTime.SpecifyKind(midnight, DateTimeKind.Utc);
        while (tz.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, tz);
    }


    public async Task<byte[]> BuildCsvAsync(Guid organizationId, CancellationToken ct)
    {
        var docs = await db.Documents
            .Where(d => d.OrganizationId == organizationId && d.DeletedAt == null)
            .Include(d => d.Vendor)
            .OrderBy(d => d.ExpirationDate)
            .ToListAsync(ct);

        // #327: the audit export keeps EVERY document (it must not hide history — an auditor wants to see
        // the expired old cert AND its renewal), but ANNOTATES the superseded ones so a reader knows the
        // old cert was replaced and isn't a current gap. Same coverage-extending-renewal rule as
        // DocumentSupersession (a later upload whose ExpirationDate is >= this doc's), computed in memory
        // over the already-loaded org set (no extra query) — see SupersededIds.
        var supersededIds = SupersededIds(docs);

        await using var ms = new MemoryStream();
        await using var writer = new StreamWriter(ms, leaveOpen: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            // Neutralize spreadsheet formula injection (#246 review — security). FileName / Vendor /
            // UploadedBy are user- and VENDOR-controlled (the PUBLIC portal stores the raw uploaded
            // file name), so a value beginning =, +, -, @, TAB or CR would execute as a formula when
            // the org opens the export in Excel/Sheets — a stored injection across the vendor→customer
            // trust boundary. Escape prefixes CsvHelper's injection-escape character so the cell renders
            // as literal text. (CsvHelper defaults InjectionOptions to None.)
            InjectionOptions = InjectionOptions.Escape,
        };
        await using var csv = new CsvWriter(writer, config);
        var today = DateTime.UtcNow.Date; // date-overlay the verdict at generation time (#257)

        // FP-102 CSV literacy: the human-meaningful columns lead; the raw GUID moves LAST (it was a
        // confusing leading column); the extraction-state column is named "ProcessingStatus" so it
        // isn't mistaken for the "Compliance" verdict next to it; timestamps are Excel-parseable.
        csv.WriteField("FileName");
        csv.WriteField("Vendor");
        csv.WriteField("Type");
        csv.WriteField("ProcessingStatus");
        csv.WriteField("Compliance");
        csv.WriteField("Superseded"); // #327: "Yes" when a newer cert for the same vendor+type exists
        csv.WriteField("EffectiveDate");
        csv.WriteField("ExpirationDate");
        csv.WriteField("GeneralLiabilityLimit");
        csv.WriteField("UploadedBy");
        csv.WriteField("CreatedAt");
        csv.WriteField("Id");
        await csv.NextRecordAsync();

        foreach (var d in docs)
        {
            // Column order MUST match the header above (FileName … CreatedAt, Id last).
            csv.WriteField(d.OriginalFileName);
            csv.WriteField(d.Vendor?.Name ?? "");
            csv.WriteField(DisplayLabels.DocumentType(d.DocumentType));
            csv.WriteField(DisplayLabels.Extraction(d.ExtractionStatus));
            csv.WriteField(DisplayLabels.Compliance(
                ComplianceStatusDeriver.Effective(d.ComplianceStatus, d.ExpirationDate, d.EffectiveDate, today)));
            csv.WriteField(supersededIds.Contains(d.Id) ? "Yes" : "No");
            csv.WriteField(d.EffectiveDate?.ToString("yyyy-MM-dd"));
            csv.WriteField(d.ExpirationDate?.ToString("yyyy-MM-dd"));
            csv.WriteField(d.GeneralLiabilityLimit?.ToString(CultureInfo.InvariantCulture));
            csv.WriteField(d.UploadedBy ?? "");
            // "yyyy-MM-dd HH:mm:ss" (no trailing 'Z'), which Excel parses as a datetime — the old
            // "u" format's trailing Z left it as opaque text (#320 FP-102).
            csv.WriteField(d.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            csv.WriteField(d.Id);
            await csv.NextRecordAsync();
        }

        // #402: the CSV carries the same one disclaimer the two PDFs do — it is forwarded to the same
        // readers. Written as a single-field record AFTER the data (never a preamble above the header:
        // FP-102 shaped row 1 as the header line Excel and pandas both key on, and a note there would
        // break that). A short trailing row is unambiguously not a document row, and both parsers take
        // it — a rectangular one padded to 12 columns would instead read as a document named after the
        // disclaimer.
        csv.WriteField(Disclaimer);
        await csv.NextRecordAsync();

        await writer.FlushAsync(ct);
        return ms.ToArray();
    }

    // #327 / ADR 0033 (as amended by the #327 re-review and #362 / Amendment 2): the ids of documents
    // superseded by a newer same-(vendor, type) cert that BOTH extends coverage AND is continuous with the
    // old one — the in-memory mirror of DocumentSupersession.IsSuperseded, computed over the already-loaded
    // org document set (no extra query). A doc d is superseded when some other doc o in its (vendor, type)
    // group is a later upload (o.CreatedAt > d.CreatedAt) with a non-null ExpirationDate >= d's (extends
    // coverage) AND whose EffectiveDate is null or <= d.ExpirationDate (no gap — the renewal picks up on or
    // before the old cert lapses). A renewal that doesn't extend coverage (earlier/absent expiry — e.g. a
    // still-processing upload) OR that only becomes effective AFTER the old cert already expired (a future-
    // effective renewal, leaving a live coverage gap — #362) does NOT supersede, so neither can hide an
    // expired liability — exactly the predicate above, so the CSV/PDF annotation matches the dashboard.
    // Per-group pairwise: groups are tiny (a vendor's certs of one type), so this stays ~O(n) at SMB scale.
    // internal for a direct unit test pinning it equal to the DB predicate (InternalsVisibleTo).
    internal static HashSet<Guid> SupersededIds(IReadOnlyList<Entities.Document> docs)
    {
        var superseded = new HashSet<Guid>();
        foreach (var group in docs.Where(d => d.VendorId != null).GroupBy(d => (d.VendorId, d.DocumentType)))
        {
            var items = group.ToList();
            foreach (var d in items)
            {
                if (d.ExpirationDate is null) continue; // null expiry → never superseded (matches the DB predicate)
                if (items.Any(o => o.CreatedAt > d.CreatedAt
                        && o.ExpirationDate is not null
                        && o.ExpirationDate >= d.ExpirationDate
                        && (o.EffectiveDate is null || o.EffectiveDate <= d.ExpirationDate)))
                    superseded.Add(d.Id);
            }
        }
        return superseded;
    }

    /// <summary>
    /// Shared QuestPDF page chrome for the PDF reports: Letter size, 40pt margin, default text style —
    /// and the mandatory export FOOTER. The footer lives here rather than at each builder so a fourth
    /// PDF export cannot ship without the #402 disclaimer: <c>ExportDisclaimerTests</c> scans EVERY
    /// <c>.cs</c> file under <c>api/CompliDrop.Api/</c> and requires every QuestPDF page composition —
    /// any receiver, any lambda parameter name — to call through here. The single recorded exemption
    /// (<c>SampleCertificateGenerator</c>, a simulated vendor document) is named there and cited to
    /// ADR 0047.
    /// <para/>
    /// <paramref name="attribution"/> is the "CompliDrop · {name}" line beneath the disclaimer — the org
    /// name on the audit report; omitted (null) on the vendor package, which never loaded the org.
    /// <para/>
    /// The footer's TREATMENT (8pt, <c>#64748b</c> — the same fine print as the attribution line) is
    /// provisional alongside the wording: ADR 0047 §5 routes "is this conspicuous enough?" to the CLM-3
    /// attorney pass, and this method is the one place a prominence answer would land.
    /// </summary>
    private static void ApplyPageDefaults(PageDescriptor page, string? attribution)
    {
        page.Size(PageSizes.Letter);
        page.Margin(40);
        page.DefaultTextStyle(t => t.FontFamily("Helvetica").FontSize(10).FontColor("#0c4a6e"));

        // QuestPDF renders page.Footer() on EVERY page, so the disclaimer travels with any page of a
        // forwarded export rather than only the last — which is what putting it in the content flow
        // would have given. Each line is its own Column item, so the disclaimer never collides with or
        // displaces the attribution and a max-length (200-char) org name simply wraps within its own
        // line instead of pushing the disclaimer off the page.
        page.Footer().Column(col =>
        {
            foreach (var line in PdfFooterLines(attribution))
            {
                col.Item().Text(t =>
                {
                    t.AlignCenter();
                    t.Span(line).FontSize(8).FontColor("#64748b");
                });
            }
        });
    }

    /// <summary>
    /// The footer lines of a generated PDF export, in render order — an internal seam (#402). QuestPDF
    /// writes the content stream FlateDecode-compressed AND draws text as subset-font glyph ids, so the
    /// rendered words are not assertable from the bytes at any setting (the same reason the #262 audit
    /// window is pinned through <see cref="QueryAuditSliceAsync"/> rather than by grepping the PDF).
    /// <see cref="ApplyPageDefaults"/> renders exactly this sequence, one <c>Text</c> item per element,
    /// so pinning this pins what the footer says.
    /// </summary>
    internal static IReadOnlyList<string> PdfFooterLines(string? attribution) =>
        string.IsNullOrWhiteSpace(attribution)
            ? [Disclaimer]
            : [Disclaimer, $"CompliDrop · {attribution}"];

    public async Task<byte[]> BuildVendorReportAsync(Guid organizationId, Guid vendorId, CancellationToken ct)
    {
        var vendor = await db.Vendors
            .Include(v => v.Documents)
            .FirstOrDefaultAsync(v => v.Id == vendorId && v.OrganizationId == organizationId, ct)
            ?? throw new InvalidOperationException("Vendor not found.");

        var today = DateTime.UtcNow.Date; // date-overlay the verdict at generation time (#257)
        // Reuse the one shared supersession helper (all these docs share one vendor) so the annotation
        // matches the CSV / audit-report exactly. (#327)
        var supersededIds = SupersededIds([.. vendor.Documents]);
        return QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                // No attribution line: the vendor package never loads the Organization row, and adding a
                // query for a branding line is not what #402 asks for — the disclaimer is.
                ApplyPageDefaults(page, attribution: null);
                page.Header().Column(col =>
                {
                    col.Item().Text("Vendor Compliance Package").FontSize(18).SemiBold().FontColor("#0284c7");
                    col.Item().Text(vendor.Name).FontSize(14);
                });
                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Item().Text($"Documents: {vendor.Documents.Count}");
                    // #327: mark a superseded (renewed) old cert so the package shows the old doc AND that
                    // a newer one for the same type replaced it.
                    foreach (var d in vendor.Documents.OrderBy(d => d.ExpirationDate))
                    {
                        var superseded = supersededIds.Contains(d.Id);
                        col.Item().PaddingTop(6).Text($"• {d.OriginalFileName} — {DisplayLabels.DocumentType(d.DocumentType)} — expires {d.ExpirationDate?.ToString("yyyy-MM-dd") ?? "unknown"} — {DisplayLabels.Compliance(ComplianceStatusDeriver.Effective(d.ComplianceStatus, d.ExpirationDate, d.EffectiveDate, today))}{(superseded ? " (superseded)" : "")}");
                    }
                });
            });
        }).GeneratePdf();
    }
}
