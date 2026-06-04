using System.Net;
using CompliDrop.Api.Entities;
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
}
