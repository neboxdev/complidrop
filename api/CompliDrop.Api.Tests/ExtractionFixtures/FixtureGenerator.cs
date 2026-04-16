// Helper that generates synthetic placeholder PDFs matching each fixture's expected.yaml.
// Run via `dotnet test --filter GenerateFixtures` when a real document isn't available
// for a particular fixture slot. Real customer-sourced documents should overwrite
// the generated input.pdf in place.

using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace CompliDrop.Api.Tests.ExtractionFixtures;

public class FixtureGenerator
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "ExtractionFixtures");

    [Fact(Skip = "Manual fixture regeneration — run with --filter FullyQualifiedName~FixtureGenerator after removing Skip.")]
    [Trait("Category", "GenerateFixtures")]
    public void GenerateAll()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        GenerateCoiGeneralLiability();
        GenerateCoiWorkersComp();
        GenerateLicenseContractor();
        GeneratePermitConstruction();
        GenerateCertificationSafety();
    }

    private static void GenerateCoiGeneralLiability() => RenderStubDoc(
        "01_coi_general_liability",
        "ACORD 25 — Certificate of Liability Insurance",
        [
            ("Policyholder", "Acme Construction LLC"),
            ("Insurer", "Travelers Indemnity Company"),
            ("Policy Number", "GL-1234567"),
            ("Effective Date", "2026-01-01"),
            ("Expiration Date", "2027-01-01"),
            ("General Liability Limit", "$1,000,000 per occurrence"),
            ("Certificate Holder", "Example Property Mgmt")
        ]);

    private static void GenerateCoiWorkersComp() => RenderStubDoc(
        "02_coi_workers_comp",
        "Certificate of Workers Compensation Insurance",
        [
            ("Policyholder", "Bay Area Plumbing Inc"),
            ("Insurer", "State Compensation Insurance Fund"),
            ("Policy Number", "WC-9876543"),
            ("Effective Date", "2025-06-15"),
            ("Expiration Date", "2026-06-15"),
            ("Workers Comp Limit", "$1,000,000")
        ]);

    private static void GenerateLicenseContractor() => RenderStubDoc(
        "03_license_contractor",
        "California Contractor License",
        [
            ("License Holder", "Juan Martinez"),
            ("License Number", "CSLB-842193"),
            ("License Type", "C-10 Electrical"),
            ("Issuing Authority", "California Contractors State License Board"),
            ("Issue Date", "2024-03-12"),
            ("Expiration Date", "2028-03-12"),
            ("State", "California (CA)")
        ]);

    private static void GeneratePermitConstruction() => RenderStubDoc(
        "04_permit_construction",
        "Commercial Building Permit",
        [
            ("Permit Number", "BP-2025-00471"),
            ("Permit Type", "Commercial Alteration"),
            ("Issuing Authority", "City of Austin Development Services"),
            ("Issue Date", "2025-08-01"),
            ("Expiration Date", "2026-08-01"),
            ("Property Address", "1200 Congress Ave, Austin, TX 78701")
        ]);

    private static void GenerateCertificationSafety() => RenderStubDoc(
        "05_certification_safety",
        "OSHA 30-Hour Construction Safety Certification",
        [
            ("Holder", "Sarah Chen"),
            ("Certification", "OSHA 30-Hour Construction Safety"),
            ("Certifying Body", "OSHA Outreach Training Program"),
            ("Certification Number", "OSHA-30-88224"),
            ("Issue Date", "2025-04-20"),
            ("Expiration Date", "2030-04-20")
        ]);

    private static void RenderStubDoc(string dir, string title, (string Label, string Value)[] fields)
    {
        Directory.CreateDirectory(Path.Combine(FixtureRoot, dir));
        var pdfPath = Path.Combine(FixtureRoot, dir, "input.pdf");

        Document.Create(c =>
        {
            c.Page(p =>
            {
                p.Size(PageSizes.Letter);
                p.Margin(1, QuestPDF.Infrastructure.Unit.Inch);
                p.Header().Text(title).FontSize(18).Bold();
                p.Content().Column(col =>
                {
                    col.Spacing(8);
                    foreach (var (label, value) in fields)
                    {
                        col.Item().Row(r =>
                        {
                            r.ConstantItem(180).Text(label).Bold();
                            r.RelativeItem().Text(value);
                        });
                    }
                });
                p.Footer().AlignCenter().Text("Synthetic test document — CompliDrop regression fixture.");
            });
        }).GeneratePdf(pdfPath);
    }
}
