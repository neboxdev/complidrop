using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Unit tests for the export-side humanization helper. Mirrors the frontend
/// `display-labels.test.ts` so the two stay aligned. (#188)
/// </summary>
public class DisplayLabelsTests
{
    [Theory]
    [InlineData(ComplianceStatus.NonCompliant, "Action needed")]
    [InlineData(ComplianceStatus.Compliant, "Compliant")]
    [InlineData(ComplianceStatus.ExpiringSoon, "Expiring soon")]
    [InlineData(ComplianceStatus.Expired, "Expired")]
    [InlineData(ComplianceStatus.Pending, "Awaiting review")]
    public void Compliance_humanizes_each_status(ComplianceStatus status, string expected)
        => DisplayLabels.Compliance(status).Should().Be(expected);

    [Theory]
    [InlineData(ExtractionStatus.Processing, "Reading…")]
    [InlineData(ExtractionStatus.ManualRequired, "Needs your review")]
    [InlineData(ExtractionStatus.Completed, "Read")]
    [InlineData(ExtractionStatus.Failed, "Couldn't read")]
    [InlineData(ExtractionStatus.Pending, "Waiting to read")]
    public void Extraction_humanizes_each_status(ExtractionStatus status, string expected)
        => DisplayLabels.Extraction(status).Should().Be(expected);

    [Theory]
    [InlineData("coi", "Certificate of Insurance")]
    [InlineData("license", "Business License")]
    [InlineData("permit", "Permit")]
    public void DocumentType_humanizes_known_types(string type, string expected)
        => DisplayLabels.DocumentType(type).Should().Be(expected);

    [Fact]
    public void DocumentType_falls_back_to_the_raw_value_for_unknown_and_to_Other_for_empty()
    {
        DisplayLabels.DocumentType("mystery").Should().Be("mystery");
        DisplayLabels.DocumentType("").Should().Be("Other");
    }

    [Theory]
    [InlineData("compliancetemplate.created", "Requirement set created")]
    [InlineData("document.uploaded", "Document uploaded")]
    [InlineData("vendor.created", "Vendor added")]
    public void Action_humanizes_known_actions(string action, string expected)
        => DisplayLabels.Action(action).Should().Be(expected);

    [Fact]
    public void Action_does_not_garble_an_all_lowercase_entity_name()
        => DisplayLabels.Action("compliancetemplate.created").Should().NotContain("Compliancetemplate");
}
