using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CompliDrop.Api.Services;

/// <summary>
/// Renders the deterministic, obviously-fictional sample Certificate of Insurance behind the
/// one-click demo (#238). The PDF is built so the REAL extraction pipeline (Document AI OCR → LLM)
/// reads it cleanly and the resulting document PASSES the "Caterer" system checklist
/// (<see cref="Data.Seed.ComplianceTemplateSeed.SampleVendorTemplateName"/>): general-liability
/// each-occurrence ≥ $1M, an expiration date in the future, workers-comp coverage present, and
/// liquor-liability ≥ $1M (the Caterer checklist now covers bar / alcohol service — #400, so the
/// sample vendor is modelled as a full-service caterer that carries liquor liability).
///
/// A compliance product must never look like it ships a real customer certificate, so every copy
/// carries a fictional insurer/policy number plus a "SAMPLE — NOT A REAL CERTIFICATE" banner,
/// watermark, and footer (legal-compliance requirement called out in #238).
/// </summary>
public interface ISampleCertificateGenerator
{
    /// <summary>
    /// Renders the sample COI as PDF bytes. <paramref name="insuredName"/> is the demo's sample
    /// vendor; <paramref name="certificateHolderName"/> is the current organization.
    /// </summary>
    byte[] GeneratePdf(string insuredName, string certificateHolderName);
}

public sealed class SampleCertificateGenerator(TimeProvider timeProvider) : ISampleCertificateGenerator
{
    internal const string SampleBanner = "SAMPLE — NOT A REAL CERTIFICATE OF INSURANCE";
    private const string GeneralLiabilityEachOccurrence = "$2,000,000";
    private const string WorkersCompEachAccident = "$1,000,000";
    private const string LiquorLiabilityEachOccurrence = "$1,000,000";

    public byte[] GeneratePdf(string insuredName, string certificateHolderName)
    {
        var today = timeProvider.GetUtcNow().UtcDateTime.Date;
        var effective = today;
        // Always ~1 year out: comfortably in the future (clear of the 30-day "expiring soon" overlay)
        // so the demo always lands on a clean "Compliant" verdict no matter when it is clicked.
        var expiration = today.AddYears(1);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0.6f, Unit.Inch);
                page.DefaultTextStyle(t => t.FontFamily("Helvetica").FontSize(10).FontColor("#1e293b"));

                // Faint centered watermark so even a glance — or a greyscale printout — reads SAMPLE.
                page.Background().AlignCenter().AlignMiddle()
                    .Text("SAMPLE").FontSize(130).Bold().FontColor("#fbe3e3");

                page.Header().Column(col =>
                {
                    col.Item().Background("#b91c1c").Padding(6).AlignCenter()
                        .Text(SampleBanner).FontColor("#ffffff").Bold().FontSize(12);
                    col.Item().PaddingTop(8).Text("CERTIFICATE OF LIABILITY INSURANCE").Bold().FontSize(16);
                    col.Item().Text("ACORD 25 (sample layout) — for product demonstration only")
                        .FontSize(9).FontColor("#64748b");
                });

                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Element(e => LabeledBlock(e, "Insurer (fictional)", "Sample Mutual Insurance Company"));
                    col.Item().Element(e => LabeledBlock(e, "Insured", insuredName));
                    col.Item().Element(e => LabeledBlock(e, "Certificate Holder", certificateHolderName));
                    col.Item().Element(e => LabeledBlock(e, "Policy Number", "SAMPLE-GL-0000000 (not a real policy)"));

                    col.Item().PaddingTop(6).Text("Coverages").Bold().FontSize(12);
                    col.Item().Element(e => CoverageRow(e,
                        "Commercial General Liability",
                        $"Each Occurrence Limit: {GeneralLiabilityEachOccurrence}",
                        "General Aggregate: $4,000,000"));
                    col.Item().Element(e => CoverageRow(e,
                        "Workers Compensation & Employers' Liability",
                        $"E.L. Each Accident: {WorkersCompEachAccident}",
                        "Limits: Statutory"));
                    col.Item().Element(e => CoverageRow(e,
                        "Liquor Liability",
                        $"Each Occurrence Limit: {LiquorLiabilityEachOccurrence}",
                        "Aggregate: $2,000,000"));

                    col.Item().PaddingTop(6).Row(r =>
                    {
                        r.RelativeItem().Element(e =>
                            LabeledBlock(e, "Policy Effective Date", effective.ToString("MMMM d, yyyy")));
                        r.RelativeItem().Element(e =>
                            LabeledBlock(e, "Policy Expiration Date", expiration.ToString("MMMM d, yyyy")));
                    });

                    // A plain, machine-readable echo of the four fields the Caterer checklist grades,
                    // so OCR + the LLM extract them reliably regardless of how the table above is parsed.
                    col.Item().PaddingTop(8).Background("#f1f5f9").Padding(8).Column(s =>
                    {
                        s.Spacing(2);
                        s.Item().Text($"General Liability Limit: {GeneralLiabilityEachOccurrence} per occurrence");
                        s.Item().Text($"Workers Compensation Limit: {WorkersCompEachAccident}");
                        s.Item().Text($"Liquor Liability Limit: {LiquorLiabilityEachOccurrence} per occurrence");
                        s.Item().Text($"Expiration Date: {expiration:yyyy-MM-dd}");
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span(SampleBanner + ". ").FontColor("#b91c1c").SemiBold().FontSize(8);
                    t.Span("All names, policy numbers, and figures are fictional and for product demonstration only.")
                        .FontColor("#64748b").FontSize(8);
                });
            });
        }).GeneratePdf();
    }

    private static void LabeledBlock(IContainer container, string label, string value) =>
        container.Column(col =>
        {
            col.Item().Text(label).FontSize(8).FontColor("#64748b").Bold();
            col.Item().Text(value).FontSize(11);
        });

    private static void CoverageRow(IContainer container, string coverage, string primary, string secondary) =>
        container.Border(1).BorderColor("#e2e8f0").Padding(6).Row(r =>
        {
            r.RelativeItem(2).Text(coverage).SemiBold();
            r.RelativeItem(2).Text(primary);
            r.RelativeItem(2).Text(secondary);
        });
}
