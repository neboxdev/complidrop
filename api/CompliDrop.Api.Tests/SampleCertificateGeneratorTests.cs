using System.Text;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

public class SampleCertificateGeneratorTests
{
    private static SampleCertificateGenerator Generator() =>
        new(new FixedTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void GeneratePdf_returns_a_valid_nonempty_pdf()
    {
        // The runtime sets the QuestPDF community license at startup (Program.cs); set it here too so
        // the generator can render outside the host.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var bytes = Generator().GeneratePdf("Brightside Catering Co. (Sample)", "The Garden Hall");

        bytes.Should().NotBeNullOrEmpty();
        bytes.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-", "the bytes must be a real PDF the extraction pipeline can OCR");
    }
}
