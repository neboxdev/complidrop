using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

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
        // #443: back the stored Compliant with a check row so it exports as Compliant — an ungraded one
        // now exports as the demoted Pending label with RequirementsChecked 0.
        await MarkGradedAsync(auth.OrgId, docId);

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        lines[0].Should().Be(
            "FileName,Vendor,Type,ProcessingStatus,Compliance,Superseded,RequirementsChecked,EffectiveDate,ExpirationDate,GeneralLiabilityLimit,UploadedBy,CreatedAt,Id");

        var fields = lines[1].Split(',');
        fields[0].Should().Be("acme-coi.pdf", "the filename leads, not the GUID");
        fields[^1].Should().Be(docId.ToString(), "the raw GUID is last");
        fields[11].Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", "CreatedAt is Excel-parseable, no trailing Z");
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
    public async Task Vendor_package_generates_a_pdf()
    {
        // Happy-path smoke for BuildVendorReportAsync + the shared ApplyPageDefaults page chrome (#43).
        // The audit report covers the other PDF builder; the vendor builder's only prior test was the
        // cross-tenant negative, which never renders the PDF. A valid %PDF proves it ran end-to-end.
        var auth = await RegisterAndLoginAsync();
        var vendorId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "Acme", CreatedAt = now, UpdatedAt = now });
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                VendorId = vendorId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "memory://v",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Compliant,
                ExpirationDate = now.AddDays(120),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.GetAsync($"/api/export/vendor/{vendorId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
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

    [Fact]
    public async Task Csv_export_reads_a_future_effective_compliant_doc_as_Pending_not_Compliant()
    {
        // #362 review S1: the audit export is the 5th future-effective READ surface (the CSV/PDF verdict
        // column runs the stored status through ComplianceStatusDeriver.Effective, ADR 0041), and was the
        // one left untested. A stored-Compliant cert that is NOT YET in force (EffectiveDate a date strictly
        // after today) must export as the Pending label — "Awaiting review" (DisplayLabels.Compliance) — and
        // NEVER "Compliant": an audit-ready export must not certify present-tense coverage a not-yet-active
        // cert doesn't provide. This mirrors A_standalone_future_effective_compliant_doc_reads_Pending_today
        // (detail/list/dashboard) for the export surface. CSV only: the PDF is FlateDecode-compressed and not
        // text-assertable (see Audit_report_generates_a_pdf…), and both PDF sites share this exact overlay.
        var auth = await RegisterAndLoginAsync();
        var today = DateTime.UtcNow.Date;
        var futureEffectiveId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Documents.Add(new Document
            {
                Id = futureEffectiveId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "future-effective-coi.pdf",
                BlobStorageUrl = "memory://fe",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Compliant, // stored verdict passed every rule…
                ExpirationDate = today.AddDays(300),            // …far from expiry (so it isn't ExpiringSoon)…
                EffectiveDate = today.AddDays(30),              // …but not in force until next month → reads Pending
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        // #443: back the stored Compliant with a check row, so the Pending this asserts is the
        // FUTURE-EFFECTIVE demotion and not the never-graded one — otherwise the test passes vacuously.
        await MarkGradedAsync(auth.OrgId, futureEffectiveId);

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        var header = lines[0].Split(',');
        var fileIdx = Array.IndexOf(header, "FileName");
        var compIdx = Array.IndexOf(header, "Compliance");
        compIdx.Should().BeGreaterThan(-1, "the export has a Compliance column");

        var row = lines.Skip(1).Select(l => l.Split(',')).First(f => f[fileIdx] == "future-effective-coi.pdf");
        row[compIdx].Should().Be("Awaiting review",
            "a not-yet-in-force cert exports as the Pending label (DisplayLabels.Compliance), not Compliant");
        row[compIdx].Should().NotBe("Compliant",
            "the export must not certify present-tense coverage a future-effective cert doesn't yet provide");
        row[Array.IndexOf(header, "RequirementsChecked")].Should().Be("1",
            "it WAS graded — this row's Pending is the future-effective demotion, not #443's");
    }

    // ---- #443 / ADR 0048: the auditor-facing export stops asserting a verdict nothing produced ----

    [Fact]
    public async Task Csv_export_reads_a_never_graded_doc_as_Pending_and_discloses_zero_requirements_checked()
    {
        // The ticket's headline artifact claim: a document nothing ever graded printed "Expiring soon" into
        // the auditor-facing export, with an EMPTY "What we checked" panel behind it. It must export as the
        // Pending label, and the RequirementsChecked column must show the 0 that explains why.
        var auth = await RegisterAndLoginAsync();
        var today = DateTime.UtcNow.Date;
        var gradedId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            foreach (var (id, name) in new[] { (gradedId, "graded-coi.pdf"), (Guid.NewGuid(), "never-graded-coi.pdf") })
                db.Documents.Add(new Document
                {
                    Id = id,
                    OrganizationId = auth.OrgId,
                    OriginalFileName = name,
                    BlobStorageUrl = "memory://ng",
                    FileSizeBytes = 1,
                    ContentType = "application/pdf",
                    DocumentType = "coi",
                    ExtractionStatus = ExtractionStatus.Completed,
                    // The exact state ComputeOutcome's zero-applicable-rules branch stores inside the window.
                    ComplianceStatus = ComplianceStatus.ExpiringSoon,
                    ExpirationDate = today.AddDays(10),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }
        await MarkGradedAsync(auth.OrgId, gradedId);

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        var header = lines[0].Split(',');
        var fileIdx = Array.IndexOf(header, "FileName");
        var compIdx = Array.IndexOf(header, "Compliance");
        var checkedIdx = Array.IndexOf(header, "RequirementsChecked");
        checkedIdx.Should().BeGreaterThan(-1, "the export discloses how many requirements were checked (#443)");

        string[] RowFor(string fileName) => lines.Skip(1).Select(l => l.Split(',')).First(f => f[fileIdx] == fileName);

        var ungraded = RowFor("never-graded-coi.pdf");
        ungraded[compIdx].Should().Be("Awaiting review",
            "the export must not certify a document no requirement was ever measured against");
        ungraded[compIdx].Should().NotBe("Expiring soon");
        ungraded[checkedIdx].Should().Be("0");

        // The control: same dates, same stored verdict, actually graded.
        var graded = RowFor("graded-coi.pdf");
        graded[compIdx].Should().Be("Expiring soon");
        graded[checkedIdx].Should().Be("1");
    }

    [Fact]
    public async Task Csv_RequirementsChecked_counts_the_DISTINCT_rules_a_document_was_measured_against()
    {
        // #468 review S3. RequirementsChecked is the ONE surface that PRINTS this number rather than
        // thresholding it at zero (ADR 0048 §5), and every other assertion on the column anywhere in the
        // suite is "0" or "1" — so nothing could tell the column apart from a boolean. Replace
        // CheckCountsAsync's projection with `d.ComplianceChecks.Any() ? 1 : 0` and the whole backend suite
        // stays green while an auditor's CSV understates a five-rule checklist as one requirement.
        //
        // This document is measured against TWO rules and holds THREE rows, so the cell discriminates both
        // directions at once: a one-if-any collapse prints "1", a raw row count prints "3". The doubled row
        // is the ADR 0030 residue itself (two writers, one rule), which is why the answer is 2 and not 3.
        var auth = await RegisterAndLoginAsync();
        var docId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var ruleA = Guid.NewGuid();
        var ruleB = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.ComplianceTemplates.Add(new ComplianceTemplate
            {
                Id = templateId, OrganizationId = auth.OrgId, Name = "Two-rule checklist", CreatedAt = now,
            });
            db.ComplianceRules.AddRange(
                new ComplianceRule
                {
                    Id = ruleA, ComplianceTemplateId = templateId, DocumentType = "coi",
                    FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "1000000",
                    SortOrder = 0,
                },
                new ComplianceRule
                {
                    Id = ruleB, ComplianceTemplateId = templateId, DocumentType = "coi",
                    FieldName = "expiration_date", Operator = "required", SortOrder = 1,
                });
            db.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = auth.OrgId,
                OriginalFileName = "two-rule-coi.pdf",
                BlobStorageUrl = "memory://tr",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Compliant,
                ExpirationDate = DateTime.UtcNow.Date.AddDays(300),
                CreatedAt = now,
                UpdatedAt = now,
            });
            foreach (var ruleId in new[] { ruleA, ruleA, ruleB })
                db.ComplianceChecks.Add(new ComplianceCheck
                {
                    Id = Guid.NewGuid(), DocumentId = docId, ComplianceRuleId = ruleId,
                    IsPassed = true, CheckedAt = now,
                });
            await db.SaveChangesAsync();
        }

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        var header = lines[0].Split(',');
        var row = lines.Skip(1).Select(l => l.Split(','))
            .First(f => f[Array.IndexOf(header, "FileName")] == "two-rule-coi.pdf");

        row[Array.IndexOf(header, "RequirementsChecked")].Should().Be("2",
            "the checklist holds two requirements and both were measured — the third check row is a second "
            + "writer's copy of one of them, which is evidence about ONE requirement, not a second one");
        row[Array.IndexOf(header, "Compliance")].Should().Be("Compliant",
            "…and the count moves no verdict: every other consumer of this map asks DocumentGrading"
            + ".IsGraded's > 0 question, which distinct-vs-raw cannot change");
    }

    [Fact]
    public void The_pdf_compliance_cell_annotates_a_never_graded_document()
    {
        // Both PDFs (the audit report's Compliance column and the vendor package's bullet line) go through
        // ExportService.ComplianceCell. They are FlateDecode-compressed and not text-assertable, so this is
        // where their wording is pinned. The CSV variant deliberately omits the parenthetical — it carries
        // the fact in its own RequirementsChecked column instead, so its Compliance cell stays filterable.
        var today = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var doc = new Document
        {
            Id = Guid.NewGuid(), OriginalFileName = "c.pdf", DocumentType = "coi",
            ComplianceStatus = ComplianceStatus.ExpiringSoon, ExpirationDate = today.AddDays(10),
        };
        var ungraded = new Dictionary<Guid, int> { [doc.Id] = 0 };
        var graded = new Dictionary<Guid, int> { [doc.Id] = 2 };

        ExportService.ComplianceCell(doc, today, ungraded, superseded: false)
            .Should().Be("Awaiting review (no requirements checked)");
        ExportService.ComplianceCell(doc, today, ungraded, superseded: false, annotateNeverGraded: false)
            .Should().Be("Awaiting review", "the CSV carries the count in its own column");
        ExportService.ComplianceCell(doc, today, graded, superseded: false)
            .Should().Be("Expiring soon", "a graded document is untouched");
        // The two qualifiers compose — a superseded, never-graded old cert discloses both.
        ExportService.ComplianceCell(doc, today, ungraded, superseded: true)
            .Should().Be("Awaiting review (no requirements checked) (superseded)");
        // A document missing from the map entirely (defensive) reads as ungraded, never as graded.
        ExportService.ComplianceCell(doc, today, new Dictionary<Guid, int>(), superseded: false)
            .Should().Be("Awaiting review (no requirements checked)");
    }

    /// <summary>Seeds a vendor holding one GRADED and one never-graded COI, both expiring in 10 days.</summary>
    private async Task<(Guid VendorId, Guid GradedId, Guid UngradedId)> SeedVendorWithOneGradedAndOneUngradedAsync(
        Guid orgId)
    {
        var vendorId = Guid.NewGuid();
        var gradedId = Guid.NewGuid();
        var ungradedId = Guid.NewGuid();
        var expires = DateTime.UtcNow.Date.AddDays(10);
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "V", CreatedAt = now, UpdatedAt = now });
            foreach (var (id, name) in new[] { (gradedId, "measured.pdf"), (ungradedId, "never-graded.pdf") })
                db.Documents.Add(new Document
                {
                    Id = id, OrganizationId = orgId, VendorId = vendorId,
                    OriginalFileName = name, BlobStorageUrl = "memory://v", FileSizeBytes = 1,
                    ContentType = "application/pdf", DocumentType = "coi",
                    ExtractionStatus = ExtractionStatus.Completed,
                    // The exact state ComputeOutcome's zero-applicable-rules branch stores in the window.
                    ComplianceStatus = ComplianceStatus.ExpiringSoon,
                    ExpirationDate = expires, CreatedAt = now, UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }
        await MarkGradedAsync(orgId, gradedId);
        return (vendorId, gradedId, ungradedId);
    }

    [Fact]
    public async Task Vendor_package_prints_the_never_graded_annotation_only_for_the_ungraded_document()
    {
        // The vendor package is THE artifact the ticket names, and it is the one export whose check-count
        // input is its OWN query — the CSV and the audit report share theirs. Asserting only that the
        // response bytes are non-empty left that wiring invisible in BOTH directions: an EMPTY count map
        // would print "(no requirements checked)" beside every line of an auditor package, and a bug that
        // dropped the annotation would print an affirmative verdict for a document nothing graded. Neither
        // is visible in the compressed bytes, so the lines are asserted at the seam that builds them —
        // which does the count read itself, so the query is pinned along with the formatting.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _, _) = await SeedVendorWithOneGradedAndOneUngradedAsync(auth.OrgId);

        await using var db = CreateSystemDb();
        var vendor = await db.Vendors.Include(v => v.Documents)
            .FirstAsync(v => v.Id == vendorId);
        var lines = await new ExportService(db).VendorPackageLinesAsync(vendor, DateTime.UtcNow.Date, default);

        lines.Should().HaveCount(2);
        lines.Should().ContainSingle(l => l.Contains("never-graded.pdf"))
            .Which.Should().EndWith("Awaiting review (no requirements checked)",
                "the auditor package must not certify a document no requirement was measured against, and "
                + "is entitled to the count that explains why");
        lines.Should().ContainSingle(l => l.Contains("measured.pdf"))
            .Which.Should().EndWith("Expiring soon",
                "the graded cert keeps its real verdict, un-annotated — an empty count map would annotate "
                + "every line of the package");
    }

    [Fact]
    public async Task Audit_report_rows_annotate_only_the_never_graded_document()
    {
        // #443 review S1. The audit report is equally auditor-facing, equally FlateDecode-unassertable,
        // and does its OWN check-count read — but only a `%PDF` smoke test covered it, which cannot tell a
        // correctly-wired count map from an empty one. An empty map annotates EVERY row of an auditor's
        // report "(no requirements checked)"; a dropped annotation prints an affirmative verdict for a
        // document nothing graded. So the rows are asserted at the seam that builds them, exactly as the
        // vendor package's are — the count read is pinned along with the formatting.
        var auth = await RegisterAndLoginAsync();
        var (_, _, _) = await SeedVendorWithOneGradedAndOneUngradedAsync(auth.OrgId);

        await using var db = CreateSystemDb();
        var rows = await new ExportService(db).AuditReportRowsAsync(auth.OrgId, DateTime.UtcNow.Date, default);

        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(r => r.FileName == "never-graded.pdf")
            .Which.Compliance.Should().Be("Awaiting review (no requirements checked)",
                "the audit report must not certify a document no requirement was measured against");
        rows.Should().ContainSingle(r => r.FileName == "measured.pdf")
            .Which.Compliance.Should().Be("Expiring soon",
                "the graded cert keeps its real verdict, un-annotated — an empty count map would annotate "
                + "every row of the report");
        // The other columns come from the same row build, so a mis-ordered tuple is visible here too.
        rows.Should().OnlyContain(r => r.Vendor == "V" && r.Type == "Certificate of Insurance");
    }

    [Fact]
    public async Task Audit_report_pdf_still_renders_for_a_never_graded_document()
    {
        // The end-to-end half of the seam above: the extra check-count query and the annotation must not
        // break generation for the document that motivated the fix.
        var auth = await RegisterAndLoginAsync();
        await SeedVendorWithOneGradedAndOneUngradedAsync(auth.OrgId);

        var resp = await auth.Client.GetAsync("/api/export/audit-report");
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task Vendor_package_pdf_still_renders_for_a_never_graded_document()
    {
        // The end-to-end guard that the extra check-count query and the annotation don't break generation
        // for the very document that motivated the fix. The %PDF magic bytes are the same assertion every
        // sibling PDF test in this file makes — a 200 carrying HTML or an empty body would otherwise pass.
        var auth = await RegisterAndLoginAsync();
        var (vendorId, _, _) = await SeedVendorWithOneGradedAndOneUngradedAsync(auth.OrgId);

        var resp = await auth.Client.GetAsync($"/api/export/vendor/{vendorId}");
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }
}
