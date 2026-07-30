using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// #402 / <c>docs/adr/0047-exports-carry-a-non-advice-disclaimer.md</c> (counsel-gate item CLM-3):
/// every generated export — audit PDF, vendor package PDF, CSV — carries ONE shared non-advice
/// disclaimer, and the PDFs carry it on EVERY page.
/// <para/>
/// The PDFs are not text-assertable (QuestPDF FlateDecode-compresses the content stream AND draws
/// text as subset-font glyph ids, so the words are absent from the bytes at any setting — see
/// <c>ExportEndpointsTests.Audit_report_generates_a_pdf_and_resolves_the_actor_join</c>). So the PDF
/// side is pinned the way #262 pinned the audit window: behaviourally through an internal seam
/// (<see cref="ExportService.PdfFooterLines"/>, which the renderer emits verbatim) plus structural
/// pins over the source that keep that seam wired into <c>page.Footer()</c> and keep every page
/// composition going through the shared chrome. The CSV is text-assertable and is checked over HTTP.
/// </summary>
public sealed class ExportDisclaimerTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // ───────── the wording (provisional pending the CLM-3 attorney sign-off) ─────────

    [Fact]
    public void The_disclaimer_says_what_the_ticket_and_the_counsel_gate_agreed()
    {
        // Pinned verbatim so a reword is a deliberate, reviewed act — this string is the one legal
        // qualifier on the artifact most likely forwarded to an insurer, broker or opposing counsel,
        // and it is refined at the CLM-1/CLM-3 counsel pass (G1-COUNSEL-BRIEF §0), not in passing.
        ExportService.Disclaimer.Should().Be(
            "Statuses reflect automated reading of documents as uploaded; certificates do not modify "
                + "policies. Verify current coverage with the issuing carrier.");
    }

    // ───────── the PDF footer seam ─────────

    [Fact]
    public void The_audit_report_footer_puts_the_disclaimer_above_the_org_attribution()
    {
        // Both lines, in this order, as separate lines: the disclaimer must not displace or collide
        // with the "CompliDrop · {org}" attribution the audit report has always carried.
        ExportService.PdfFooterLines("Riverside Event Hall").Should().Equal(
            ExportService.Disclaimer,
            "CompliDrop · Riverside Event Hall");
    }

    [Fact]
    public void The_vendor_package_footer_carries_the_disclaimer_even_with_no_attribution()
    {
        // The vendor package never loads the Organization row, so it has no attribution line — but
        // the disclaimer is not optional. A blank/whitespace name must not emit a dangling
        // "CompliDrop · " either.
        ExportService.PdfFooterLines(null).Should().Equal(ExportService.Disclaimer);
        ExportService.PdfFooterLines("").Should().Equal(ExportService.Disclaimer);
        ExportService.PdfFooterLines("   ").Should().Equal(ExportService.Disclaimer);
    }

    // ───────── structural pins: one source, every page, every export path ─────────

    [Fact]
    public void The_disclaimer_text_exists_exactly_once_in_the_source()
    {
        // ONE constant consumed by all three artifacts. A hand-copied second literal is how the two
        // PDFs drift apart — and how a counsel-mandated reword lands on two of three surfaces.
        var source = ReadExportServiceSource();

        Regex.Matches(source, Regex.Escape(ExportService.Disclaimer)).Count.Should().Be(1,
            "the disclaimer must appear once, as the ExportService.Disclaimer constant — every "
                + "artifact reads that constant rather than restating the sentence");
    }

    [Fact]
    public void Every_pdf_export_path_goes_through_the_shared_page_chrome()
    {
        // The footer is applied by ApplyPageDefaults, so a FOURTH PDF export cannot ship without the
        // disclaimer unless it also skips the page size, margins and text style — this pins that it
        // cannot cherry-pick.
        var source = StripComments(ReadExportServiceSource());

        var pageCompositions = Regex.Matches(source, @"container\.Page\(page =>").Count;
        var chromeCalls = Regex.Matches(source, @"ApplyPageDefaults\(page[,)]").Count;

        pageCompositions.Should().BeGreaterThan(1, "ExportService still builds both PDF exports");
        chromeCalls.Should().Be(pageCompositions,
            "every container.Page composition must call ApplyPageDefaults(page, …) — that is the one "
                + "place the #402 footer is applied");
    }

    [Fact]
    public void The_disclaimer_is_rendered_from_the_page_footer_so_it_lands_on_every_page()
    {
        // The regression this exists to catch: moving the disclaimer into page.Content(), where it
        // flows with the document and prints ONCE, at the end — i.e. only on the last page of a
        // multi-page audit report, which is exactly the page nobody forwards.
        var source = StripComments(ReadExportServiceSource());
        var chrome = MethodBody(source, "private static void ApplyPageDefaults(");

        Regex.Matches(source, @"page\.Footer\(\)").Count.Should().Be(1,
            "there is one footer composition, in the shared chrome");
        chrome.Should().Contain("page.Footer()", "…and it lives in ApplyPageDefaults");

        var footer = BlockAfter(chrome, "page.Footer()");
        footer.Should().Contain("PdfFooterLines(",
            "the footer lines (disclaimer first) must be emitted inside page.Footer(), which QuestPDF "
                + "repeats on EVERY page — inside page.Content() they would print only once");
    }

    // ───────── rendered end to end ─────────

    [Fact]
    public async Task The_audit_report_still_renders_multi_page_with_a_max_length_org_name()
    {
        // The footer grew from one line to two-plus. With the longest org name the register endpoint
        // accepts (InputLengths.OrganizationName), the attribution wraps — and a footer that outgrows
        // its page is a QuestPDF layout exception (a 500 on the audit export), not a cosmetic issue.
        // Seeded past one page so pagination is exercised, not just the first page's layout.
        var auth = await RegisterAndLoginAsync();
        var longName = new string('W', InputLengths.OrganizationName); // widest glyph, worst-case wrap
        await using (var db = CreateSystemDb())
        {
            await db.Organizations.Where(o => o.Id == auth.OrgId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Name, longName));

            var now = DateTime.UtcNow;
            for (var i = 0; i < 120; i++)
            {
                db.Documents.Add(new Document
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = auth.OrgId,
                    OriginalFileName = $"coi-{i:D3}.pdf",
                    BlobStorageUrl = "memory://x",
                    FileSizeBytes = 1,
                    ContentType = "application/pdf",
                    DocumentType = "coi",
                    ExtractionStatus = ExtractionStatus.Completed,
                    ComplianceStatus = ComplianceStatus.Compliant,
                    ExpirationDate = now.AddDays(200 + i),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.GetAsync("/api/export/audit-report");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "a wrapped footer must not fail the render");
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
        PageCount(bytes).Should().BeGreaterThan(1,
            "the fixture must actually paginate, or 'the footer survives page 2' is untested");
    }

    [Fact]
    public async Task The_vendor_package_still_renders_with_the_disclaimer_footer()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "Acme", CreatedAt = now, UpdatedAt = now });
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                VendorId = vendorId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "memory://v",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Completed,
                ComplianceStatus = ComplianceStatus.Compliant,
                ExpirationDate = now.AddDays(120),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.GetAsync($"/api/export/vendor/{vendorId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        Encoding.ASCII.GetString(await resp.Content.ReadAsByteArrayAsync(), 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task The_csv_carries_the_same_disclaimer_after_the_data_leaving_the_header_row_intact()
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
                ComplianceStatus = ComplianceStatus.Compliant,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        // The same one constant, not a third hand-typed copy.
        csv.Should().Contain(ExportService.Disclaimer);

        // Row 1 is still the header FP-102 shaped for Excel/pandas — the note is a trailing row, never
        // a preamble that would push the header down.
        lines[0].Should().StartWith("FileName,Vendor,Type,");
        lines[1].Should().StartWith("acme-coi.pdf,");

        // …and it is the LAST row, below every document, as a single unquoted field (the sentence has
        // no comma, so it neither needs quoting nor splits into extra columns).
        lines[^1].Should().Be(ExportService.Disclaimer);
        lines.Should().HaveCount(3, "header + the one document + the disclaimer");
    }

    [Fact]
    public async Task The_csv_disclaimer_survives_an_empty_export()
    {
        // Zero documents is a real state (a fresh org exporting immediately). The disclaimer is a
        // property of the artifact, not of its rows.
        var auth = await RegisterAndLoginAsync();

        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

        lines.Should().HaveCount(2, "header + the disclaimer");
        lines[^1].Should().Be(ExportService.Disclaimer);
    }

    // ───────── helpers ─────────

    /// <summary>
    /// Page objects live in the PDF's object table, not in the compressed content streams, so the page
    /// count is readable from the raw bytes even though the rendered words are not. <c>\b</c> excludes
    /// the <c>/Pages</c> tree node.
    /// </summary>
    private static int PageCount(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count;

    /// <summary>
    /// Source with <c>//</c> and <c>/* */</c> comments removed, so the structural pins measure CODE and
    /// not the prose next to it (this file's own subject matter — "page.Footer()" — is named in those
    /// comments). Naive by design: it would also cut a <c>"//"</c> inside a string literal, and
    /// ExportService has none.
    /// </summary>
    private static string StripComments(string source) =>
        Regex.Replace(source, @"/\*.*?\*/|//[^\r\n]*", "", RegexOptions.Singleline);

    /// <summary>The brace-matched body of the method whose signature starts with <paramref name="signature"/>.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "ExportService must still declare {0}", signature);
        return BlockAfter(source[start..], ")");
    }

    /// <summary>The brace-matched block that follows <paramref name="marker"/> in <paramref name="source"/>.</summary>
    private static string BlockAfter(string source, string marker)
    {
        var at = source.IndexOf(marker, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "expected to find {0}", marker);

        var open = source.IndexOf('{', at);
        open.Should().BeGreaterThanOrEqualTo(0);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{marker}'");
    }

    private static string ReadExportServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "api", "CompliDrop.Api", "Services", "ExportService.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }

        throw new FileNotFoundException($"Could not locate ExportService.cs from {AppContext.BaseDirectory}");
    }
}
