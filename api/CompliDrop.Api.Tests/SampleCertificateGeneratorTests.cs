using System.Text;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
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

    [Fact]
    public void Extraction_prompt_emits_the_fields_the_Caterer_checklist_grades()
    {
        // The sample reaching "Compliant" via the REAL pipeline (ADR 0028) hinges on the LLM emitting
        // these EXACT snake_case keys — ComplianceCheckService.LookupValue matches the Caterer rules'
        // FieldNames verbatim against the extracted JSON. Pin the prompt↔rule contract so a prompt
        // vocabulary change that renamed a key would fail here instead of silently shipping a demo that
        // grades NonCompliant for real users.
        ExtractionPrompts.SystemPrompt.Should().Contain("general_liability_limit");
        ExtractionPrompts.SystemPrompt.Should().Contain("workers_comp_limit");
        ExtractionPrompts.SystemPrompt.Should().Contain("expiration_date");
        // #400: the Caterer checklist now grades liquor liability too, so the prompt must emit the key.
        ExtractionPrompts.SystemPrompt.Should().Contain("liquor_liability_limit");
    }
}
