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
    // internal so a test can pin that this each-occurrence figure still MEETS the seeded Caterer
    // liquor-liability threshold (#400 / #416): the sample's Compliant verdict (ADR 0028) hinges on it,
    // and dropping it below the checklist floor would silently ship a NonCompliant demo.
    internal const string LiquorLiabilityEachOccurrence = "$1,000,000";

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
                        foreach (var line in MachineReadableFieldEcho(expiration))
                            s.Item().Text(line);
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

    // The plain "field: value" lines echoed into the PDF below the coverage table so OCR + the LLM
    // reliably extract the four fields the Caterer checklist grades regardless of how the visual table is
    // parsed. Factored out (and internal — InternalsVisibleTo the test project) so a test reads the SAME
    // source the PDF renders and can pin that the LIQUOR line the demo's Compliant verdict depends on is
    // actually emitted with its value: dropping it here breaks both the PDF and that test, instead of
    // silently shipping a NonCompliant demo (ADR 0028 / #416). Keep the exact strings the extraction
    // pipeline was tuned against — a wording change is a prompt↔extraction contract change.
    internal static IReadOnlyList<string> MachineReadableFieldEcho(DateTime expiration) =>
    [
        $"General Liability Limit: {GeneralLiabilityEachOccurrence} per occurrence",
        $"Workers Compensation Limit: {WorkersCompEachAccident}",
        $"Liquor Liability Limit: {LiquorLiabilityEachOccurrence} per occurrence",
        $"Expiration Date: {expiration:yyyy-MM-dd}",
    ];

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
