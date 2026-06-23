using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the #188 acceptance criterion that raw enums / type codes never reach
/// the exported CSV — the export must read the same friendly labels the UI
/// shows. (The PDF goes through the same DisplayLabels helper, unit-tested in
/// DisplayLabelsTests; the CSV is the text-assertable surface.)
/// </summary>
public sealed class ExportEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Csv_export_uses_friendly_labels_not_raw_enums()
    {
        var auth = await RegisterAndLoginAsync();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                OriginalFileName = "acme-coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.NonCompliant,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.GetAsync("/api/export/csv");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await resp.Content.ReadAsStringAsync();

        // Friendly labels present…
        csv.Should().Contain("Certificate of Insurance"); // DocumentType "coi"
        csv.Should().Contain("Action needed");            // ComplianceStatus NonCompliant
        csv.Should().Contain("Read");                     // ExtractionStatus Completed
        // …and the raw enum / type code absent.
        csv.Should().NotContain("NonCompliant");
        // The raw lower-case "coi" type code must not leak as a standalone field.
        csv.Should().NotContain(",coi,");
    }

    [Fact]
    public async Task Csv_export_neutralizes_spreadsheet_formula_injection_in_the_filename()
    {
        // #246 review — security. FileName is user/VENDOR-controlled (the PUBLIC portal stores the raw
        // uploaded file name), so a value beginning '=' would execute as a formula when the org opens
        // the export in Excel/Sheets — a stored injection across the vendor→customer trust boundary.
        // InjectionOptions.Escape must neutralize it: the value is preserved but no cell starts with a
        // raw formula trigger. Would FAIL with the prior config (InjectionOptions defaulted to None).
        var auth = await RegisterAndLoginAsync();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                OriginalFileName = "=DANGER_FORMULA", // no comma/quote → a clean leading-field probe
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();

        // The value survives (escaped, not silently stripped)…
        csv.Should().Contain("DANGER_FORMULA");
        // …but no CSV cell presents it as a raw formula. FileName leads each data row, so a raw lead
        // would surface as a line starting "=DANGER_FORMULA" (or a quoted "\"=DANGER_FORMULA"); Escape
        // prepends the injection-escape character, so neither cell-start shape occurs (independent of
        // which escape char CsvHelper uses).
        csv.Should().NotContain("\n=DANGER_FORMULA",
            "Escape must stop a '='-leading filename from starting a spreadsheet cell as a formula");
        csv.Should().NotContain("\"=DANGER_FORMULA",
            "nor may the raw formula lead a quoted cell");
    }

    [Fact]
    public async Task Csv_export_leads_with_human_columns_GUID_last_and_an_excel_parseable_timestamp()
    {
        // FP-102: the leading raw GUID moved LAST; the extraction-state column is "ProcessingStatus"
        // (not "Status", confusable with the Compliance verdict beside it); CreatedAt is Excel-parseable
        // (no trailing 'Z'). Existing CSV tests only substring-match, so this pins the exact shape.
        var auth = await RegisterAndLoginAsync();
        var docId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = new DateTime(2026, 6, 20, 14, 5, 32, DateTimeKind.Utc);
            db.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "acme-coi.pdf",
                BlobStorageUrl = "memory://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Compliant,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        lines[0].Should().Be(
            "FileName,Vendor,Type,ProcessingStatus,Compliance,Superseded,EffectiveDate,ExpirationDate,GeneralLiabilityLimit,UploadedBy,CreatedAt,Id");

        var fields = lines[1].Split(',');
        fields[0].Should().Be("acme-coi.pdf", "the filename leads, not the GUID");
        fields[^1].Should().Be(docId.ToString(), "the raw GUID is last");
        fields[10].Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", "CreatedAt is Excel-parseable, no trailing Z");
    }

    [Fact]
    public void UserLabel_renders_a_name_or_System_never_a_guid()
    {
        // The audit report must show WHO acted, not a raw GUID. (#197)
        var id = Guid.NewGuid();
        var map = new Dictionary<Guid, string> { [id] = "Jane Doe" };

        ExportService.UserLabel(id, map).Should().Be("Jane Doe"); // known user → name
        ExportService.UserLabel(null, map).Should().Be("System"); // system event → capitalized System

        // Unknown id (e.g. a hard-deleted user) falls back to "System", NEVER the GUID.
        var unknown = Guid.NewGuid();
        ExportService.UserLabel(unknown, map).Should().Be("System");
        ExportService.UserLabel(unknown, map).Should().NotContain(unknown.ToString());
    }

    [Fact]
    public void DisplayName_prefers_the_full_name_but_falls_back_to_email()
    {
        // The audit report shows the actor's name, or their email when the name
        // is blank — never an empty cell. (#197 review)
        ExportService.DisplayName("Jane Doe", "jane@acme.test").Should().Be("Jane Doe");
        ExportService.DisplayName("", "jane@acme.test").Should().Be("jane@acme.test");
        ExportService.DisplayName("   ", "jane@acme.test").Should().Be("jane@acme.test");
        ExportService.DisplayName(null, "jane@acme.test").Should().Be("jane@acme.test");
    }

    // ───────── audit window resolution (#262) ─────────

    [Fact]
    public void Audit_window_includes_the_entire_To_day_in_the_orgs_timezone()
    {
        // The P0 from #262: "to=2026-06-10" used to become midnight at the START of
        // June 10 (server zone), excluding the whole To day. For a New York org the
        // window must run to the start of June 11 NY time (04:00Z during EDT), so an
        // event at 23:59 NY on the To day (03:59Z next UTC day) is inside.
        var (fromUtc, toUtcExclusive, fromDate, toDate) = ExportService.ResolveAuditWindow(
            new DateTime(2026, 5, 11), new DateTime(2026, 6, 10), "America/New_York",
            nowUtc: new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc));

        fromUtc.Should().Be(new DateTime(2026, 5, 11, 4, 0, 0, DateTimeKind.Utc));
        toUtcExclusive.Should().Be(new DateTime(2026, 6, 11, 4, 0, 0, DateTimeKind.Utc));
        var lateToDayEvent = new DateTime(2026, 6, 11, 3, 59, 0, DateTimeKind.Utc); // 23:59 NY June 10
        (lateToDayEvent >= fromUtc && lateToDayEvent < toUtcExclusive).Should().BeTrue(
            "events during the To day must be inside the window");
        fromDate.Should().Be(new DateOnly(2026, 5, 11));
        toDate.Should().Be(new DateOnly(2026, 6, 10), "the caption shows the org-local calendar dates");
    }

    [Fact]
    public void Audit_window_means_the_orgs_calendar_day_not_the_servers()
    {
        // A Tokyo org's "June 10" is 2026-06-09T15:00Z → 2026-06-10T15:00Z — host-independent.
        var (fromUtc, toUtcExclusive, _, _) = ExportService.ResolveAuditWindow(
            new DateTime(2026, 6, 10), new DateTime(2026, 6, 10), "Asia/Tokyo",
            nowUtc: new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc));

        fromUtc.Should().Be(new DateTime(2026, 6, 9, 15, 0, 0, DateTimeKind.Utc));
        toUtcExclusive.Should().Be(new DateTime(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Audit_window_defaults_use_the_orgs_today_not_UTCs()
    {
        // 02:00Z on June 11 is still the EVENING OF JUNE 10 in New York: a null `to`
        // must resolve to the org's today (June 10), and null `from` to 30 days prior.
        var (_, _, fromDate, toDate) = ExportService.ResolveAuditWindow(
            from: null, to: null, "America/New_York",
            nowUtc: new DateTime(2026, 6, 11, 2, 0, 0, DateTimeKind.Utc));

        toDate.Should().Be(new DateOnly(2026, 6, 10));
        fromDate.Should().Be(new DateOnly(2026, 5, 11));
    }

    [Fact]
    public void Audit_window_falls_back_to_UTC_for_an_unknown_timezone_id()
    {
        var (fromUtc, toUtcExclusive, _, _) = ExportService.ResolveAuditWindow(
            new DateTime(2026, 6, 10), new DateTime(2026, 6, 10), "Not/AZone",
            nowUtc: new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc));

        fromUtc.Should().Be(new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc));
        toUtcExclusive.Should().Be(new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc));
        // Kind matters, not just ticks: Npgsql rejects non-UTC kinds for timestamptz
        // parameters, so a regression to Unspecified here only explodes at query time.
        fromUtc.Kind.Should().Be(DateTimeKind.Utc);
        toUtcExclusive.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Audit_report_rejects_an_inverted_date_range_with_a_friendly_400()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/export/audit-report?from=2026-06-10&to=2026-05-11");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("export.invalid_range");
        body.GetProperty("error").GetProperty("message").GetString().Should().NotMatchRegex("[0-9]{3}",
            "no status codes or jargon in user-facing copy");
    }

    [Theory]
    [InlineData("?to=9999-12-31")]               // DateOnly.MaxValue has no next day
    [InlineData("?to=0001-01-15")]               // default from = to − 30 would underflow
    [InlineData("?from=1999-12-31&to=2026-06-10")]
    public async Task Audit_report_rejects_degenerate_dates_with_400_not_500(string query)
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync($"/api/export/audit-report{query}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("export.invalid_range");
    }

    [Fact]
    public async Task Audit_report_rejects_a_lone_future_from_that_inverts_against_todays_default()
    {
        // ?from=<future> with `to` omitted skips the endpoint's both-provided check;
        // the service resolves to = org-local today, detects the inversion, and the
        // endpoint maps it to the same friendly 400 — never a self-contradicting PDF.
        var auth = await RegisterAndLoginAsync();
        var future = DateTime.UtcNow.AddDays(60).ToString("yyyy-MM-dd");

        var resp = await auth.Client.GetAsync($"/api/export/audit-report?from={future}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("export.invalid_range");
    }

    [Fact]
    public void Audit_window_survives_a_zone_whose_midnight_does_not_exist()
    {
        // Brazil's 2018 spring-forward happened AT local midnight: 2018-11-04 00:00
        // São Paulo time is invalid. The guard maps the day start into the gap's end
        // (01:00 BRST, UTC-2 → 03:00Z). Deterministic on every host — pre-2019 Brazil
        // rules are frozen history in tzdata/ICU.
        var (fromUtc, _, _, _) = ExportService.ResolveAuditWindow(
            new DateTime(2018, 11, 4), new DateTime(2018, 11, 4), "America/Sao_Paulo",
            nowUtc: new DateTime(2018, 11, 4, 12, 0, 0, DateTimeKind.Utc));

        fromUtc.Should().Be(new DateTime(2018, 11, 4, 3, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Audit_query_boundary_is_half_open_through_real_postgres()
    {
        // The original #262 bug lived in this exact predicate (`CreatedAt <= to`), and
        // the PDF is FlateDecode-compressed (not text-assertable) — so the half-open
        // comparison is pinned by driving the query seam directly with the resolver's
        // outputs: 23:59 NY on the To day is IN, the next NY midnight is OUT.
        var auth = await RegisterAndLoginAsync();
        var inEvent = new DateTime(2026, 6, 11, 3, 59, 0, DateTimeKind.Utc);  // 23:59 NY June 10
        var outEvent = new DateTime(2026, 6, 11, 4, 0, 0, DateTimeKind.Utc);  // 00:00 NY June 11
        await using (var db = CreateSystemDb())
        {
            db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), OrganizationId = auth.OrgId, Action = "test.in", EntityType = "Test", CreatedAt = inEvent });
            db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), OrganizationId = auth.OrgId, Action = "test.out", EntityType = "Test", CreatedAt = outEvent });
            await db.SaveChangesAsync();
        }
        var (fromUtc, toUtcExclusive, _, _) = ExportService.ResolveAuditWindow(
            new DateTime(2026, 5, 11), new DateTime(2026, 6, 10), "America/New_York",
            nowUtc: new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc));

        await using var sysDb = CreateSystemDb();
        var (rows, truncated) = await new ExportService(sysDb)
            .QueryAuditSliceAsync(auth.OrgId, fromUtc, toUtcExclusive, default);

        truncated.Should().BeFalse();
        rows.Select(r => r.Action).Should().Contain("test.in", "events during the To day are inside the window")
            .And.NotContain("test.out", "the upper bound is exclusive — the next org-local day is out");
    }

    [Fact]
    public async Task Audit_report_accepts_a_single_day_window()
    {
        // from == to is the smallest legal window (one org-local day, inclusive).
        var auth = await RegisterAndLoginAsync();
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var resp = await auth.Client.GetAsync($"/api/export/audit-report?from={day}&to={day}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task Audit_report_generates_a_pdf_and_resolves_the_actor_join()
    {
        // Registration emits an audit row tagged with the real UserId; generating
        // the report exercises the new user-name join + the cap-detection query.
        // A valid %PDF proves both ran without throwing. We DON'T grep the rendered
        // text: QuestPDF FlateDecode-compresses the content stream (verified that
        // even EnableDebugging doesn't expose it), and a PDF-text lib is a
        // disproportionate test dependency — the name/System resolution itself is
        // pinned end-to-end by the UserLabel + DisplayName unit tests above. (#197)
        var auth = await RegisterAndLoginAsync();
        var from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var resp = await auth.Client.GetAsync($"/api/export/audit-report?from={from}&to={to}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task Csv_export_annotates_a_superseded_old_cert_and_leaves_the_current_one_unmarked()
    {
        // #327: the export keeps BOTH the old expired cert and its renewal (full audit history), but marks
        // the old one Superseded so a reader knows it was replaced and isn't a current gap.
        var auth = await RegisterAndLoginAsync();
        var vendorId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "Acme", CreatedAt = now, UpdatedAt = now });
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(), OrganizationId = auth.OrgId, VendorId = vendorId,
                OriginalFileName = "old-coi.pdf", BlobStorageUrl = "memory://o", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed, ComplianceStatus = ComplianceStatus.Compliant,
                // The expired old cert: a later-uploaded, later-expiry renewal supersedes it (ADR 0033
                // Amendment 1 — the superseder must actually extend coverage).
                ExpirationDate = now.AddDays(-2),
                CreatedAt = now.AddDays(-30), UpdatedAt = now.AddDays(-30),
            });
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(), OrganizationId = auth.OrgId, VendorId = vendorId,
                OriginalFileName = "new-coi.pdf", BlobStorageUrl = "memory://n", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed, ComplianceStatus = ComplianceStatus.Compliant,
                ExpirationDate = now.AddDays(300), // the renewal — extends coverage, so it supersedes the old cert
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        var header = lines[0].Split(',');
        var fileIdx = Array.IndexOf(header, "FileName");
        var supIdx = Array.IndexOf(header, "Superseded");
        supIdx.Should().BeGreaterThan(-1, "the export has a Superseded column (#327)");

        string SupersededFor(string fileName) =>
            lines.Skip(1).Select(l => l.Split(',')).First(f => f[fileIdx] == fileName)[supIdx];

        SupersededFor("old-coi.pdf").Should().Be("Yes", "the older cert is superseded by the newer one");
        SupersededFor("new-coi.pdf").Should().Be("No", "the latest cert is current");
    }
}
