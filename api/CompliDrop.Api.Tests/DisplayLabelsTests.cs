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
    // Real actions emitted explicitly with camelCase + custom verbs — resolved
    // case-insensitively, so the export PDF never shows them raw.
    [InlineData("user.logged_in", "Signed in")]
    [InlineData("complianceRule.upserted", "Requirement saved")]
    [InlineData("vendorPortalLink.revoked", "Portal link revoked")]
    // #318 FP-043 feed labels: the portal upload, the link email, and the worker's
    // system "document processed" event.
    [InlineData("vendorPortalLink.upload_processed", "Vendor sent a document")]
    [InlineData("vendorPortalLink.emailed", "Upload link emailed")]
    [InlineData("document.processed", "Document read")]
    // #340 suppression feed event — pin the EXACT curated copy (the coverage guard below only proves it
    // isn't the raw fallback, not that the wording is right).
    [InlineData("reminder.recipient_suppressed", "Reminders paused — bad email")]
    public void Action_humanizes_known_actions(string action, string expected)
        => DisplayLabels.Action(action).Should().Be(expected);

    [Fact]
    public void Action_does_not_garble_an_all_lowercase_entity_name()
        => DisplayLabels.Action("compliancetemplate.created").Should().NotContain("Compliancetemplate");

    [Fact]
    public void Action_humanizes_an_unmapped_action_instead_of_printing_it_raw()
    {
        // The audit PDF renders EVERY action; an action we forgot to map must
        // still come out humanized (de-dotted / de-camelCased), never raw.
        var label = DisplayLabels.Action("someNewEntity.weird_verb");
        label.Should().NotContain(".");
        label.Should().NotContain("_");
        label.Should().Be("Some New Entity · Weird Verb");
    }

    [Theory]
    [InlineData("ComplianceTemplate", "Requirement set")]
    [InlineData("VendorPortalLink", "Portal link")]
    [InlineData("Document", "Document")]
    public void EntityType_humanizes_the_raw_entity_class_name(string entity, string expected)
        => DisplayLabels.EntityType(entity).Should().Be(expected);

    // Drift / coverage guard mirroring display-labels.test.ts: every audit
    // action the backend actually emits must resolve to a CURATED label in the
    // export, never the regex fallback (which inserts " · ").
    [Theory]
    [InlineData("document.created")]
    [InlineData("document.deleted")]
    [InlineData("documentfield.updated")]
    [InlineData("vendor.updated")]
    [InlineData("vendorPortalLink.revoked")]
    [InlineData("complianceTemplate.created")]
    [InlineData("complianceRule.upserted")]
    [InlineData("reminder.recipient_suppressed")]
    [InlineData("user.logged_in")]
    [InlineData("user.password_changed")]
    [InlineData("user.account_deleted")]
    public void Action_curates_every_emitted_action(string action)
    {
        var label = DisplayLabels.Action(action);
        label.Should().NotContain(" · ");
        label.Should().NotContain("_");
    }
}
