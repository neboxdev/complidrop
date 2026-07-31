using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using CsvHelper;
using CsvHelper.Configuration;
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
/// composition — anywhere under <c>api/CompliDrop.Api/</c>, not just in <c>ExportService.cs</c> —
/// going through the shared chrome. The CSV is text-assertable and is checked over HTTP.
/// </summary>
public sealed class ExportDisclaimerTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// Production files whose QuestPDF page composition is deliberately NOT the export chrome, each
    /// paired with the ADR that records the decision (pinned by
    /// <see cref="Chrome_exemptions_point_at_real_files_and_cite_the_ADR"/>: an uncited exemption is
    /// indistinguishable from someone quietly opting out).
    /// </summary>
    private static readonly Dictionary<string, string> ChromeExemptions = new()
    {
        // ADR 0047 § Consequences → Neutral. The generated sample COI is a simulated VENDOR document —
        // a fake certificate the demo uploads INTO the pipeline — not a CompliDrop assertion about
        // anything and not an artifact a customer hands to a third party. It carries its own, louder
        // qualifier instead: a SAMPLE banner, a page watermark, and a "not a real certificate" footer.
        ["Services/SampleCertificateGenerator.cs"] =
            "ADR 0047 § Neutral — a simulated vendor document, not an export; carries its own SAMPLE "
                + "banner/watermark/footer rather than the export chrome",
    };

    /// <summary>
    /// Lowest-plausible production-tree .cs count (the <see cref="Adr0009EnforcementTests"/> floor).
    /// If the scan sees fewer, the root resolved wrong and the gate is a silent no-op.
    /// </summary>
    private const int MinExpectedProductionFiles = 50;

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
    public void The_audit_report_asks_the_chrome_for_the_org_attribution()
    {
        // PdfFooterLines pins WHAT the two footer shapes say, but nothing pinned which shape each
        // builder ASKS FOR. "Unify the two builders" — rewriting the audit report's
        // ApplyPageDefaults(page, org.Name) to ApplyPageDefaults(page, attribution: null) — would
        // silently drop the attribution the audit PDF has always carried, with every other test in
        // this file still green. The asymmetry is deliberate (ADR 0047 §2), so it is pinned at both
        // call sites.
        var source = StripComments(ReadExportServiceSource());

        MethodBody(source, "public async Task<byte[]> BuildAuditReportAsync(")
            .Should().Contain("ApplyPageDefaults(page, org.Name)",
                "the audit report must keep passing the org name — the #402 disclaimer is ADDED to "
                    + "that attribution line, it does not replace it");

        MethodBody(source, "public async Task<byte[]> BuildVendorReportAsync(")
            .Should().Contain("ApplyPageDefaults(page, attribution: null)",
                "the vendor package deliberately has no attribution (it never loads the Organization "
                    + "row) — only the disclaimer is mandatory there");
    }

    [Fact]
    public void Every_pdf_page_composition_in_production_code_goes_through_the_shared_page_chrome()
    {
        // The claim ADR 0047 §2 makes — "a fourth PDF export cannot ship without the disclaimer" —
        // is only as good as what this scan can SEE. It therefore walks every .cs file under
        // api/CompliDrop.Api/ (a new export in a new file is the likeliest fourth one), matches the
        // lambda PARAMETER generically, and treats an unrecognised `.Page(` shape as a violation
        // rather than skipping it. Modelled on Adr0009EnforcementTests, including its anti-no-op
        // floor: a mis-resolved root must fail loudly, never pass by scanning nothing.
        var productionRoot = FindProductionRoot();
        var (violations, compositions, scanned) =
            FindChromelessPageCompositions(productionRoot, ChromeExemptions);

        scanned.Should().BeGreaterThanOrEqualTo(MinExpectedProductionFiles,
            $"the api/CompliDrop.Api/ tree should contain at least {MinExpectedProductionFiles} .cs "
                + "files; if the scanner sees fewer, FindProductionRoot resolved to the wrong "
                + "directory and this gate would be a silent no-op");
        compositions.Should().BeGreaterThanOrEqualTo(2,
            "ExportService still composes both PDF exports (audit report + vendor package); fewer "
                + "means the matcher stopped recognising them and is checking nothing");
        violations.Should().BeEmpty(
            "every QuestPDF page composition in production code must call ApplyPageDefaults(…) — the "
                + "ONE place the #402 disclaimer footer is applied (ADR 0047 §2). A page that needs "
                + "different chrome belongs in ChromeExemptions with the ADR rationale.\n\nOffenders:\n"
                + string.Join("\n", violations));
    }

    [Fact]
    public void Chrome_exemptions_point_at_real_files_and_cite_the_ADR()
    {
        // An exemption is a recorded legal decision, so it has to stay auditable: the file must still
        // exist (a renamed/deleted one silently widens the exemption to nothing, or hides that the
        // decision is now unowned) and the rationale must name the ADR that made it.
        var productionRoot = FindProductionRoot();
        foreach (var (relative, reason) in ChromeExemptions)
        {
            File.Exists(Path.Combine(productionRoot, relative.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue($"exemption '{relative}' (reason: {reason}) should point at a real file");
            Regex.IsMatch(reason, @"ADR\s*\d{4}", RegexOptions.IgnoreCase).Should().BeTrue(
                $"exemption '{relative}' rationale '{reason}' must cite the ADR that records the "
                    + "decision — an uncited exemption is indistinguishable from an oversight");
        }
    }

    [Theory]
    // The exact bypass the review named: rename the lambda parameter and the old name-coupled pin
    // (`container\.Page\(page =>` / `ApplyPageDefaults\(page[,)]`) counts zero of each and passes.
    [InlineData("container.Page(p => { p.Size(PageSizes.Letter); });")]
    // …and the other one: compose the page off a differently-named receiver, nested in Document.Create.
    [InlineData("Document.Create(doc => doc.Page(page => { page.Margin(40); }));")]
    // A trailing lambda modifier must not slip past the matcher either.
    [InlineData("container.Page(static page => { page.Margin(40); });")]
    // An expression-bodied composition delegates the chrome somewhere the scan cannot follow.
    [InlineData("container.Page(page => Compose(page));")]
    // An unrecognised `.Page(` shape (a method group, a cast) must fail closed, not be skipped.
    [InlineData("container.Page(BuildPage);")]
    public void Scanner_flags_a_page_composition_that_skips_the_chrome_under_a_temp_root(string composition)
    {
        // Hermetic positive direction (the Adr0009EnforcementTests pattern): without this, the
        // "finds a violation" branch is only ever exercised against a production tree that is clean,
        // which proves nothing about whether the scan can still see.
        var temp = Directory.CreateTempSubdirectory("export-chrome-positive-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(temp, "NewExportService.cs"), composition + "\n");

            var (violations, _, scanned) = FindChromelessPageCompositions(temp, new Dictionary<string, string>());

            scanned.Should().Be(1, "the temp tree contains exactly one .cs file");
            violations.Should().ContainSingle().Which.Should().Contain("NewExportService.cs");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Scanner_accepts_a_renamed_parameter_that_still_calls_the_chrome_under_a_temp_root()
    {
        // Hermetic negative direction. The pin is about the CHROME CALL, not about the identifiers
        // `container` and `page` — a composition written any other way but still applying the shared
        // chrome must stay green, or the gate becomes a naming rule people route around.
        var temp = Directory.CreateTempSubdirectory("export-chrome-negative-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(temp, "NewExportService.cs"),
                "Document.Create(doc => doc.Page(p =>\n"
                    + "{\n"
                    + "    ApplyPageDefaults(p, attribution: null);\n"
                    + "    p.Header().Text(\"x\");\n"
                    + "}));\n");

            var (violations, compositions, _) =
                FindChromelessPageCompositions(temp, new Dictionary<string, string>());

            compositions.Should().Be(1, "the fixture composes exactly one page");
            violations.Should().BeEmpty(
                "the composition applies the shared chrome; only the identifiers differ");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Scanner_respects_the_exemption_list_under_a_temp_root()
    {
        // Pins the exemption bypass itself, so SampleCertificateGenerator's real exemption is not the
        // only thing exercising this branch (a refactor that stopped honouring the dictionary would
        // otherwise only surface as a mysterious failure on that one file).
        var temp = Directory.CreateTempSubdirectory("export-chrome-exempt-").FullName;
        try
        {
            var services = Path.Combine(temp, "Services");
            Directory.CreateDirectory(services);
            File.WriteAllText(
                Path.Combine(services, "Fake.cs"),
                "container.Page(page => { page.Margin(40); });\n");

            var (violations, compositions, scanned) = FindChromelessPageCompositions(
                temp,
                new Dictionary<string, string> { ["Services/Fake.cs"] = "ADR 0047 — test fixture" });

            scanned.Should().Be(1);
            compositions.Should().Be(0, "an exempt file is not scanned for compositions at all");
            violations.Should().BeEmpty("the fixture's relative path is exempt");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
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
    public async Task The_vendor_package_renders_multi_page_on_the_no_attribution_footer_path()
    {
        // The audit report covers the multi-page footer for the ATTRIBUTED shape (two lines). The
        // vendor package is the only caller of the attribution-less shape (one line, ADR 0047 §2), and
        // nothing exercised it past page 1 — so "the disclaimer travels with any page of a forwarded
        // export", the whole reason it lives in page.Footer(), was half untested. Seeded past one page
        // so the repeat-per-page render is actually driven; a footer that fails to lay out on a
        // continuation page is a QuestPDF exception (a 500 on the export), not a cosmetic issue.
        var auth = await RegisterAndLoginAsync();
        var vendorId = Guid.NewGuid();
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "Acme", CreatedAt = now, UpdatedAt = now });
            for (var i = 0; i < 120; i++)
            {
                db.Documents.Add(new Document
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = auth.OrgId,
                    VendorId = vendorId,
                    OriginalFileName = $"coi-{i:D3}.pdf",
                    BlobStorageUrl = "memory://v",
                    FileSizeBytes = 1,
                    ContentType = "application/pdf",
                    DocumentType = "coi",
                    ExtractionStatus = ExtractionStatus.Completed,
                    ComplianceStatus = ComplianceStatus.Compliant,
                    ExpirationDate = now.AddDays(120 + i),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.GetAsync($"/api/export/vendor/{vendorId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
        PageCount(bytes).Should().BeGreaterThan(1,
            "the fixture must actually paginate, or 'the attribution-less footer survives page 2' "
                + "stays untested");
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
        var rows = ParseCsv(csv);

        rows.Should().HaveCount(3, "header + the one document + the disclaimer");

        // Row 1 is still the header FP-102 shaped for Excel/pandas — the note is a trailing row, never
        // a preamble that would push the header down.
        rows[0].Take(3).Should().Equal("FileName", "Vendor", "Type");
        rows[1][0].Should().Be("acme-coi.pdf");

        // …and it is the LAST row, below every document, as a record of exactly ONE field: the same
        // one constant (not a third hand-typed copy), and not a value that split into extra columns.
        // Asserted on the PARSED field rather than the raw line, so a counsel reword containing a
        // comma — which CsvHelper would correctly quote — reads as the reword it is instead of
        // reddening here as if the CSV were malformed (the wording is provisional, ADR 0047 §5).
        rows[^1].Should().Equal(ExportService.Disclaimer);
    }

    [Fact]
    public void The_current_wording_happens_to_need_no_csv_quoting()
    {
        // A property of TODAY'S SENTENCE, not of the format, and pinned separately for exactly that
        // reason: the trailing row is written unquoted and is therefore byte-identical to the
        // constant. Counsel may reword this away (a comma is all it takes) and that is FINE — this
        // assertion may move with the wording; the parsed-field assertions above may not.
        ExportService.Disclaimer.Should().NotContainAny(",", "\"", "\r", "\n");
    }

    [Fact]
    public async Task The_csv_disclaimer_survives_an_empty_export()
    {
        // Zero documents is a real state (a fresh org exporting immediately). The disclaimer is a
        // property of the artifact, not of its rows.
        var auth = await RegisterAndLoginAsync();

        var rows = ParseCsv(await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync());

        rows.Should().HaveCount(2, "header + the disclaimer");
        rows[^1].Should().Equal(ExportService.Disclaimer);
    }

    // ───────── helpers ─────────

    /// <summary>
    /// The export CSV read back as records of PARSED fields, using the same library that wrote it.
    /// <para/>
    /// Deliberately not <c>Split(',')</c> or a raw-line comparison: the disclaimer sentence is
    /// provisional (ADR 0047 §5) and a reworded one containing a comma would be QUOTED by CsvHelper —
    /// correct CSV that a raw-line assertion would report as a defect. Parsing asserts the property
    /// that actually matters (one record, one field, the constant) independently of the wording.
    /// <para/>
    /// <c>HasHeaderRecord = false</c> so the header comes back as row 0, and the trailing single-field
    /// record is read as-is rather than being reshaped against a 12-column header.
    /// </summary>
    private static IReadOnlyList<string[]> ParseCsv(string csv)
    {
        using var parser = new CsvParser(
            new StringReader(csv),
            new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = false });

        var rows = new List<string[]>();
        while (parser.Read()) rows.Add(parser.Record!);
        return rows;
    }

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

    /// <summary>
    /// Any <c>.Page(</c> call at all. Counted separately from <see cref="PageComposition"/> so a shape
    /// the composition matcher does NOT understand (a method group, a cast, a parenthesised parameter
    /// list) fails CLOSED as an unreadable composition instead of being silently skipped — "write it in
    /// a shape the regex doesn't know" is the bypass a text scan is most exposed to.
    /// </summary>
    private static readonly Regex AnyPageCall = new(@"\.Page\(", RegexOptions.Compiled);

    /// <summary>
    /// A QuestPDF page composition, matched on SHAPE rather than on identifiers: any receiver, any
    /// lambda parameter name. The previous pin hard-coded <c>container.Page(page =></c> and
    /// <c>ApplyPageDefaults(page</c>, so renaming the parameter — or nesting the composition as
    /// <c>Document.Create(doc => doc.Page(page => …))</c> — left both counts at zero and shipped a
    /// disclaimer-less PDF past a green test.
    /// </summary>
    private static readonly Regex PageComposition =
        new(@"\.Page\(\s*(?:static\s+)?(\w+)\s*=>", RegexOptions.Compiled);

    /// <summary>
    /// Resolve the repository's <c>api/CompliDrop.Api/</c> directory from the test assembly location
    /// (the <see cref="Adr0009EnforcementTests"/> locator).
    /// </summary>
    private static string FindProductionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "api", "CompliDrop.Api");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate api/CompliDrop.Api/ from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Scan every <c>.cs</c> file under <paramref name="productionRoot"/> for QuestPDF page
    /// compositions that do not apply the shared export chrome, skipping <paramref name="exemptions"/>.
    /// Returns the formatted violations, the number of compositions actually inspected, and the number
    /// of files scanned (so the caller can guard against a zero-file no-op).
    /// <para/>
    /// A composition is clean when the brace-matched lambda body contains <c>ApplyPageDefaults(</c>.
    /// Anything the matcher cannot read in place — an expression-bodied lambda that delegates the
    /// chrome elsewhere, or a <c>.Page(</c> call in an unrecognised shape — is reported rather than
    /// skipped: this gate backs a legal guarantee, so "I could not check it" must read as a failure.
    /// <para/>
    /// Naive in the same way <see cref="StripComments"/> is, and deliberately so: braces inside string
    /// literals are assumed balanced (interpolation holes always are). An unbalanced one throws out of
    /// <see cref="BlockAfter"/> — loud, never a silent pass.
    /// <para/>
    /// Pure relative to its inputs, and <c>internal</c>, so the hermetic fixtures can drive it against
    /// a synthetic <c>%TEMP%</c> root instead of only ever seeing an all-clean production tree.
    /// </summary>
    internal static (IReadOnlyList<string> Violations, int Compositions, int ScannedFiles)
        FindChromelessPageCompositions(string productionRoot, IReadOnlyDictionary<string, string> exemptions)
    {
        var violations = new List<string>();
        var compositions = 0;
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(productionRoot, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            scanned++;
            if (exemptions.ContainsKey(relative)) continue;

            // Comments stripped so a doc-comment EXPLAINING the rule (this file's own subject matter)
            // is not mistaken for code — the Adr0009EnforcementTests judgement.
            var source = StripComments(File.ReadAllText(file));
            var matches = PageComposition.Matches(source);
            var pageCalls = AnyPageCall.Matches(source).Count;

            if (pageCalls != matches.Count)
            {
                violations.Add($"  {relative}: {pageCalls} `.Page(` call(s) but only {matches.Count} "
                    + "recognisable page composition(s) — the chrome pin cannot read this shape");
            }

            foreach (Match match in matches)
            {
                compositions++;
                var rest = source[match.Index..];
                var afterArrow = rest[(rest.IndexOf("=>", StringComparison.Ordinal) + 2)..].TrimStart();
                if (!afterArrow.StartsWith('{'))
                {
                    violations.Add($"  {relative}: `{match.Value.Trim()}` composes the page with an "
                        + "expression-bodied lambda — the chrome pin cannot follow a delegated call; "
                        + "compose the page inline so ApplyPageDefaults(…) is visible here");
                    continue;
                }

                if (!BlockAfter(rest, "=>").Contains("ApplyPageDefaults(", StringComparison.Ordinal))
                {
                    violations.Add($"  {relative}: `{match.Value.Trim()}` does not call "
                        + "ApplyPageDefaults(…) — the one place the #402 disclaimer footer is applied");
                }
            }
        }

        return (violations, compositions, scanned);
    }

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
