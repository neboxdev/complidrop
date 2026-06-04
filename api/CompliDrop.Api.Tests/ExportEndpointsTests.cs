using System.Net;
using System.Text;
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
    public async Task Audit_report_generates_a_pdf_and_resolves_the_actor_join()
    {
        // Registration emits audit rows tagged with the real UserId; generating
        // the report exercises the new user-name join + the cap-detection query.
        // A valid %PDF proves both ran without throwing (the PDF text stream is
        // compressed, so we smoke the generation rather than grep its bytes). (#197)
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
