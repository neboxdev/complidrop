using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Regression tests for #460 (ADR 0030 Amendment 2): <c>ExtractionWorker.PersistSuccess</c> must never
/// commit a verdict graded from a canonical input the row no longer holds.
/// <para/>
/// <c>ProcessDocumentAsync</c> loads the document BEFORE OCR + the LLM call and holds that tracked
/// snapshot for the minutes the read takes. EF Core writes back only the properties the worker MODIFIED,
/// so every verdict input it leaves unmodified ends up as: the row holds whatever a request committed in
/// that window, beside a <c>ComplianceStatus</c> and <c>ComplianceCheck</c> rows computed from the
/// pre-run value. Nothing heals it — the nightly sweep only does date transitions.
/// <para/>
/// That is a MECHANISM, not a column list, so these tests cover the three instances known when the
/// ticket was written (<c>VendorId</c>, <c>DocumentType</c>, a typed column the model omitted) plus the
/// two directions a fix can get wrong: the worker must still WIN on the columns it actually extracted
/// (<see cref="The_workers_own_extracted_value_still_decides_the_verdict_where_it_wrote_it"/>), and it
/// must still write NOTHING it did not write before — re-reading an input and assigning it would trade
/// the stale-basis bug for a lost update.
/// <para/>
/// Every interleave is CONSTRUCTED, not raced: the competing request is driven to completion from inside
/// the fake extractor's <c>DuringExtract</c> hook, i.e. after the worker is holding its snapshot and
/// before any persist, so "a request committed while I was out at the LLM" happens on every run. The
/// terminal assertion is the <c>DocumentConcurrentEditTests.ReadTerminalStateAsync</c> shape: recompute
/// the verdict from the PERSISTED row and require the stored one to equal it, so a verdict graded
/// against the other vendor / the other type fails even where its value looks plausible alone.
/// </summary>
public sealed class ExtractionWorkerStaleBasisTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeExtractionClient Extraction =>
        Fixture.Factory.Services.GetRequiredService<FakeExtractionClient>();

    private ExtractionWorker BuildWorker() => new(
        Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new ExtractionSettings()),
        NullLogger<ExtractionWorker>.Instance);

    /// <summary>
    /// A vendor on its own checklist carrying one <c>general_liability_limit &gt;= minLimit</c> rule per
    /// entry in <paramref name="floorsByDocumentType"/>. Two vendors with different floors give a verdict
    /// that DISCRIMINATES which checklist graded the document; two document types on one checklist do the
    /// same for the type axis.
    /// </summary>
    private async Task<Guid> SeedVendorAsync(
        Guid orgId, string name, params (string DocumentType, string MinLimit)[] floorsByDocumentType)
    {
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate
        {
            Id = templateId, OrganizationId = orgId, Name = $"T-{templateId:N}", CreatedAt = now
        });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = name, ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now
        });
        var sortOrder = 0;
        foreach (var (documentType, minLimit) in floorsByDocumentType)
            db.ComplianceRules.Add(new ComplianceRule
            {
                Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = documentType,
                FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = minLimit,
                SortOrder = sortOrder++,
            });
        await db.SaveChangesAsync();
        return vendorId;
    }

    /// <summary>
    /// A queued (<c>Pending</c>) document with its blob actually stored, so the worker can claim and
    /// process it for real. <paramref name="gl"/> seeds BOTH copies of the canonical limit (typed column
    /// + JSON mirror) — the state the row is in before the interleave moves one of them.
    /// </summary>
    private async Task<Guid> SeedQueuedDocAsync(
        Guid orgId, Guid? vendorId, string documentType = "coi", decimal? gl = null)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        var blobPath = $"blob/{docId:N}.pdf";
        await using (var db = CreateSystemDb())
        {
            db.Documents.Add(new Document
            {
                Id = docId,
                OrganizationId = orgId,
                VendorId = vendorId,
                OriginalFileName = "coi.pdf",
                BlobStorageUrl = "blob://d",
                BlobStoragePath = blobPath,
                FileSizeBytes = 1024,
                ContentType = "application/pdf",
                DocumentType = documentType,
                ExtractionStatus = ExtractionStatus.Pending,
                ComplianceStatus = ComplianceStatus.Pending,
                GeneralLiabilityLimit = gl,
                ExtractionFields = JsonDocument.Parse(
                    gl is null ? "{}" : $$"""{"general_liability_limit":"{{gl:0}}"}"""),
                // Far future: nothing here is date-driven, so every verdict below is purely rule-driven.
                ExpirationDate = now.AddYears(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            if (gl is not null)
                db.DocumentFields.Add(new DocumentField
                {
                    Id = Guid.NewGuid(), DocumentId = docId, FieldName = "general_liability_limit",
                    FieldValue = $"{gl:0}", FieldType = "currency", Confidence = 0.95,
                });
            await db.SaveChangesAsync();
        }

        await Fixture.Factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        return docId;
    }

    /// <summary>
    /// A successful extraction returning exactly <paramref name="fields"/>. Confidence is comfortably
    /// above the manual-review gate on every field, so the document settles at <c>Completed</c> and the
    /// only thing that can move the verdict is which inputs graded it. A field ABSENT from this list is
    /// absent from the response, which is precisely how a typed column stays unmodified.
    /// </summary>
    private static ExtractionResult Extracted(string? documentType, params (string Name, string Value)[] fields) => new(
        DocumentType: documentType,
        DocumentSubType: null,
        Fields: [.. fields.Select(f => new ExtractedField(f.Name, f.Value, "currency", 0.95))],
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 1000, OutputTokens: 200, EstimatedCostUsd: 0.01m));

    private sealed record Terminal(Document Doc, IReadOnlyList<ComplianceCheck> Checks);

    /// <summary>
    /// Reads the row back with the vendor chain it ACTUALLY ends up pointing at, and asserts the one
    /// invariant this ticket is about before returning anything: the persisted verdict is exactly what the
    /// persisted inputs grade to, and every persisted check row was produced by a rule that governs the
    /// document as the row now stands. A verdict graded against the pre-run vendor or the pre-run type
    /// fails here even though the value alone would look reasonable.
    /// </summary>
    private async Task<Terminal> ReadTerminalStateAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        var doc = await db.Documents
            .AsNoTracking()
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(t => t!.Rules)
            .FirstAsync(d => d.Id == docId);

        var recomputed = ComplianceCheckService.ComputeOutcome(doc, DateTime.UtcNow).Status;
        doc.ComplianceStatus.Should().Be(recomputed,
            "the persisted verdict must be what the persisted inputs grade to against the vendor and the "
            + "document type the row actually holds (#460)");

        var checks = await db.ComplianceChecks.AsNoTracking().Where(c => c.DocumentId == docId).ToListAsync();
        var governing = doc.Vendor?.ComplianceTemplate?.Rules
            .Where(r => string.IsNullOrEmpty(r.DocumentType) || r.DocumentType == doc.DocumentType)
            .Select(r => r.Id)
            .ToArray() ?? [];
        checks.Select(c => c.ComplianceRuleId).Should().BeSubsetOf(governing,
            "the check rows the detail page renders must come from rules that govern this row, not from the "
            + "checklist the worker was holding before the interleave");

        return new Terminal(doc, checks);
    }

    private async Task ClaimAndProcessAsync(Guid docId)
    {
        var worker = BuildWorker();
        // Claim through the real SQL (the test DB is reset per test, so this row is the only candidate),
        // so the document is genuinely Processing while the interleaved request lands on it.
        var claimed = await worker.ClaimNextAsync(CancellationToken.None);
        claimed.Should().Be(docId, "precondition: the seeded document is the one the worker picks up");
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);
    }

    [Fact]
    public async Task A_vendor_reassigned_mid_extraction_is_graded_against_the_vendor_the_row_holds()
    {
        // The always-on instance. PersistSuccess never assigns VendorId, and ApplyEvaluationAsync used to
        // resolve the checklist off the TRACKED FK — the vendor as it stood before the run. So a PATCH
        // committing V2 inside the window left the row on V2 with a verdict, and check rows, graded against
        // V1's checklist.
        var auth = await RegisterAndLoginAsync();
        var strict = await SeedVendorAsync(auth.OrgId, "Strict", ("coi", "5000000"));
        var lenient = await SeedVendorAsync(auth.OrgId, "Lenient", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, strict);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "2000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = lenient });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "the reassignment is an ordinary request");
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.VendorId.Should().Be(lenient,
            "the worker must not start WRITING a column it does not own — re-reading an input and assigning "
            + "it onto the tracked entity would clobber this reassignment (ADR 0030 Amendment 1)");
        terminal.Doc.GeneralLiabilityLimit.Should().Be(2_000_000m, "the extraction's own input still landed");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "2M clears V2's 1M floor — pre-#460 this row said NonCompliant (V1's 5M floor) while pointing at V2");
        terminal.Checks.Should().ContainSingle("V2's checklist carries exactly one COI rule");
    }

    [Theory]
    // The two answers that leave DocumentType unmodified in the change tracker, which is what makes the
    // assignment in PersistSuccess a no-op and the column absent from the UPDATE:
    [InlineData(null)]  // blank/absent — ADR 0045 deliberately falls back to the STORED type
    [InlineData("coi")] // a canonical answer equal to what the worker read minutes earlier
    public async Task A_document_retyped_mid_extraction_is_graded_against_the_type_the_row_holds(string? answered)
    {
        // DocumentType decides WHICH rules apply (ComplianceCheckService's ordinal filter), so grading the
        // pre-run type measures the document against requirements that no longer govern it. Reachable from
        // the UI: the detail page's type select is disabled only while its OWN mutation is in flight, not
        // while the document is processing.
        var auth = await RegisterAndLoginAsync();
        // ONE checklist, a different floor per type, so the verdict names which type graded the document.
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "5000000"), ("permit", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, documentType: "coi");

        Extraction.Result = Extracted(answered, ("general_liability_limit", "2000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { documentType = "permit" });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.DocumentType.Should().Be("permit",
            "neither answer overwrites the stored type, so the user's correction must survive the persist");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "2M clears the permit rule's 1M floor — pre-#460 the row read 'permit' beside a verdict and check "
            + "rows measured against the COI rule's 5M floor");
        terminal.Checks.Should().ContainSingle("only the permit rule governs a permit");
    }

    [Fact]
    public async Task A_typed_column_corrected_mid_extraction_is_graded_at_its_corrected_value()
    {
        // CanonicalDocumentFields.ApplyToTypedColumn runs only for fields the response RETURNED, so a
        // column whose field the model omitted keeps the stale tracked value and is likewise absent from
        // the UPDATE — the row keeps the human's correction beside a verdict computed from the old number.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "2000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 1_000_000m);

        // No general_liability_limit in the response: the model could not find one this time.
        Extraction.Result = Extracted("coi", ("policy_number", "POL-1"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "3000000" } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(3_000_000m,
            "the worker never wrote this column, so the human's correction stands (ADR 0017 last-writer-wins "
            + "applies per column to what the worker actually extracted)");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "3M clears the 2M floor — pre-#460 the row held 3M beside a NonCompliant graded from the stale 1M");
    }

    [Fact]
    public async Task The_workers_own_extracted_value_still_decides_the_verdict_where_it_wrote_it()
    {
        // The other direction, and the reason the basis is derived from the CHANGE TRACKER rather than from
        // a plain "re-read the row and grade that". A column the worker DID modify IS in the UPDATE, so the
        // extraction's value is what the row ends up holding (ADR 0017: a re-extraction overwriting a manual
        // edit is by design) and it must be what the verdict was computed from. A fix that graded the fresh
        // row wholesale would certify this document against 9M — a value no longer on it, in the
        // false-Compliant direction #337 exists to prevent.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "2000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 3_000_000m);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "1000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "9000000" } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(1_000_000m,
            "the worker extracted this column, so its value is what the UPDATE carries");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "1M is below the 2M floor — the verdict follows the value the row actually holds, not the "
            + "mid-extraction edit the re-extraction overwrote");
    }

    [Fact]
    public async Task A_document_deleted_mid_extraction_persists_without_a_second_extraction()
    {
        // The basis read is one more thing that can fail, and a throw out of PersistSuccess is the most
        // expensive failure in this codebase: the catch's bookkeeping save runs on the SAME context and
        // throws again, FailedAttempts never increments, and the document is zombie-reclaimed every five
        // minutes RE-PAYING Document AI + the LLM (ExtractionWorker.Clamp's remarks). The row vanishing
        // mid-run is the reachable shape of "the basis cannot be read", so pin that it lands as an ordinary
        // completed persist rather than as an exception.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "2000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 1_000_000m);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "3000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.DeleteAsync($"/api/documents/{docId}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().IgnoreQueryFilters().FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "the persist completed normally — a throw here would have been swallowed into a counted failure "
            + "and the document requeued for another paid run");
        doc.DeletedAt.Should().NotBeNull("precondition: the interleaved request really did delete the row");
        Extraction.ExtractCallCount.Should().Be(1, "no failure path may cost a second OCR + LLM run");
    }
}
