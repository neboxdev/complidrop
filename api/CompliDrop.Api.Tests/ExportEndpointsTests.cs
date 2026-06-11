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
    }

    [Fact]
    public async Task Audit_report_rejects_an_inverted_date_range_with_a_friendly_400()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/export/audit-report?from=2026-06-10&to=2026-05-11");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("export.invalid_range");
        body.GetProperty("error").GetProperty("message").GetString().Should().NotMatch("*[0-9][0-9][0-9]*",
            "no status codes or jargon in user-facing copy");
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
}
