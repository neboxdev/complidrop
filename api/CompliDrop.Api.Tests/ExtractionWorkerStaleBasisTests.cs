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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog.Core;

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
/// ticket was written (<c>VendorId</c>, <c>DocumentType</c>, a typed column the model omitted), in BOTH
/// verdict directions — the stale basis grading too harshly, and (the one that matters on a compliance
/// product) the stale basis CERTIFYING a row whose current inputs miss the bar, <see
/// cref="A_vendor_reassigned_to_a_STRICTER_checklist_mid_extraction_is_not_certified_against_the_old_one"/>.
/// They also cover the two directions a fix can get wrong: the worker must still WIN on the columns it
/// actually extracted (<see cref="The_workers_own_extracted_value_still_decides_the_verdict_where_it_wrote_it"/>),
/// and it must still write NOTHING it did not write before — re-reading an input and ASSIGNING it would
/// trade the stale-basis bug for a lost update. That second one is pinned twice, because a value
/// assertion alone cannot see it: on the emitted COLUMN SET
/// (<see cref="The_persist_emits_what_it_extracted_and_no_verdict_input_it_only_read"/>, off the host's
/// EF command log) and behaviourally
/// (<see cref="A_write_that_lands_AFTER_the_basis_read_is_not_clobbered_by_the_persist"/>).
/// <para/>
/// Every interleave is CONSTRUCTED, not raced. Most use the fake extractor's <c>DuringExtract</c> hook —
/// the competing request is driven to completion after the worker is holding its snapshot and before any
/// persist, so "a request committed while I was out at the LLM" happens on every run. The one interleave
/// that has to land AFTER the basis read uses <see cref="ConcurrentSystemWriteInterceptor"/>, which fires
/// from inside the worker's own <c>SavingChanges</c>. The terminal assertion is the
/// <c>DocumentConcurrentEditTests.ReadTerminalStateAsync</c> shape: recompute the verdict from the
/// PERSISTED row and require the stored one to equal it, so a verdict graded against the other vendor /
/// the other type fails even where its value looks plausible alone.
/// <para/>
/// <see cref="DocumentGradingBasis"/> itself is pinned DIRECTLY as well
/// (<see cref="The_grading_basis_overlays_only_the_properties_the_writer_modified"/>,
/// <see cref="The_grading_basis_is_null_only_when_the_row_is_genuinely_gone"/>): an integration test
/// cannot discriminate which branch of the helper ran, and the ADR records both.
/// <para/>
/// Grading the right row is only half of the fix — the verdict also has to be WRITTEN, and a plain
/// assignment onto the minutes-old snapshot silently drops it whenever the recomputed value EQUALS what
/// the row held at claim time (#460 review round 2). That is forced by
/// <c>ExtractionWorker.ForceVerdictWrite</c> and pinned once per arm:
/// <see cref="A_verdict_equal_to_the_stale_snapshot_is_still_WRITTEN_over_a_competitors"/> for the graded
/// verdict, <see cref="A_failing_basis_read_degrades_the_verdict_without_requeuing_the_extraction"/> for
/// the degrade-to-<c>Pending</c> — which doubles as the pin that the basis read lives INSIDE the
/// best-effort <c>try</c>, since no existing test made that read fail.
/// </summary>
public sealed class ExtractionWorkerStaleBasisTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeExtractionClient Extraction => ExtractionOf(Fixture.Factory.Services);

    /// <summary>
    /// The extraction fake of a GIVEN host. Every knob is a host singleton, so a test that boots its own
    /// host (the EF-command-log one) must arm THAT host's fake, not the shared fixture's.
    /// </summary>
    private static FakeExtractionClient ExtractionOf(IServiceProvider services) =>
        services.GetRequiredService<FakeExtractionClient>();

    private ExtractionWorker BuildWorker(IServiceProvider? services = null) => new(
        (services ?? Fixture.Factory.Services).GetRequiredService<IServiceScopeFactory>(),
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
    /// A second vendor on the SAME checklist as <paramref name="twinVendorId"/>. Lets an interleave move
    /// the FK without moving the verdict, so a test can assert which vendor the row ends up pointing at
    /// without also depending on the residual window ADR 0030 Amendment 2 records as still open.
    /// </summary>
    private async Task<Guid> SeedVendorOnSameChecklistAsync(Guid orgId, string name, Guid twinVendorId)
    {
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        var templateId = await db.Vendors
            .Where(v => v.Id == twinVendorId)
            .Select(v => v.ComplianceTemplateId)
            .FirstAsync();
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = name, ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return vendorId;
    }

    /// <summary>
    /// A queued (<c>Pending</c>) document with its blob actually stored, so the worker can claim and
    /// process it for real. <paramref name="gl"/> seeds BOTH copies of the canonical limit (typed column
    /// + JSON mirror) — the state the row is in before the interleave moves one of them.
    /// <para/>
    /// <paramref name="complianceStatus"/> is the STORED verdict the worker's tracked snapshot will carry.
    /// It matters whenever the point of a test is that the freshly-computed verdict EQUALS it — EF emits
    /// only properties that DIFFER from the snapshot, so that is exactly when a plain assignment writes
    /// nothing (#460 review round 2, C1).
    /// </summary>
    private async Task<Guid> SeedQueuedDocAsync(
        Guid orgId, Guid? vendorId, string documentType = "coi", decimal? gl = null,
        ComplianceStatus complianceStatus = ComplianceStatus.Pending,
        IServiceProvider? host = null)
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
                ComplianceStatus = complianceStatus,
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

        // The blob fake is a HOST singleton, so the bytes must land in the store the worker under test
        // will read from — the fixture's host by default, the test's own host when it booted one.
        await (host ?? Fixture.Factory.Services).GetRequiredService<IBlobStorageService>()
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

    private async Task ClaimAndProcessAsync(Guid docId, IServiceProvider? services = null)
    {
        var worker = BuildWorker(services);
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
        // minutes RE-PAYING Document AI + the LLM (ExtractionWorker.Clamp's remarks). The row being deleted
        // mid-run is the sharpest shape of "the world moved under this persist", so pin that it lands as an
        // ordinary completed persist rather than as an exception.
        //
        // What this does NOT pin is the null-basis fallback, and the ADR/reviewers.md used to say it did.
        // DELETE /api/documents/{id} is a SOFT delete, and GetDatabaseValues issues an
        // AsNoTracking().IgnoreQueryFilters() key lookup, so the row IS still read and the NON-null branch
        // is the one that runs here — which is why the vendor reassignment below is part of the interleave:
        // the verdict it produces is reachable only through a basis. Both branches of the helper are pinned
        // directly instead, by The_grading_basis_is_null_only_when_the_row_is_genuinely_gone.
        var auth = await RegisterAndLoginAsync();
        var strict = await SeedVendorAsync(auth.OrgId, "Strict", ("coi", "5000000"));
        var lenient = await SeedVendorAsync(auth.OrgId, "Lenient", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, strict, gl: 1_000_000m);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "3000000"));
        Extraction.DuringExtract = async () =>
        {
            // Reassign FIRST — a deleted document 404s the PATCH — so the row the persist is about to
            // leave differs from the worker's snapshot on a column the worker never writes.
            var patch = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = lenient });
            patch.StatusCode.Should().Be(HttpStatusCode.OK);
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
        doc.VendorId.Should().Be(lenient, "precondition: the reassignment committed before the delete");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "a soft-deleted row is STILL READABLE by the basis read, so the verdict is graded against the "
            + "vendor the row holds (3M clears Lenient's 1M floor). The tracked-entity fallback would have "
            + "measured it against Strict's 5M floor and stored NonCompliant — which is what makes this "
            + "assertion, and not the three above it, the one that says which branch ran");
        var checks = await db.ComplianceChecks.AsNoTracking().Where(c => c.DocumentId == docId).ToListAsync();
        checks.Should().ContainSingle("Lenient's checklist carries exactly one COI rule");

        // "No second paid run" is what this test is about, and the call counter alone cannot say it
        // (#460 review round 2, S2): ProcessDocumentAsync calls the extractor at most once and this test
        // drives one cycle, so the assertion below cannot fail. A second ClaimNextAsync cannot say it here
        // either — ClaimSql filters "DeletedAt" IS NULL, so a soft-deleted row is unclaimable whatever its
        // status (A_failing_basis_read_degrades_the_verdict_without_requeuing_the_extraction pins the
        // second cycle on a row that is still alive). What DOES discriminate is the failure bookkeeping:
        // a throw out of PersistSuccess reaches RecordFailedAttempt, which charges the retry budget and
        // stamps the error before requeuing.
        doc.FailedAttempts.Should().Be(0,
            "nothing was charged against the retry budget, so no failure path ran");
        doc.ProcessingError.Should().BeNull(
            "a counted failure would have stamped the error that caused it");
        Extraction.ExtractCallCount.Should().Be(1,
            "precondition: the run under test really did reach the extraction boundary");
    }

    [Fact]
    public async Task A_vendor_reassigned_to_a_STRICTER_checklist_mid_extraction_is_not_certified_against_the_old_one()
    {
        // The SAME axis as the first test, in the direction that actually matters on a compliance product.
        // Every other instance test here is shaped so the pre-#460 bug read too HARSH (stale = NonCompliant,
        // correct = Compliant), which is annoying but safe. This is the over-certification: the stale
        // checklist says the document clears the bar, the checklist the row now points at says it does not,
        // and the row would have been stored Compliant — the #337 direction, a blocker by this project's
        // severity anchors.
        var auth = await RegisterAndLoginAsync();
        var lenient = await SeedVendorAsync(auth.OrgId, "Lenient", ("coi", "1000000"));
        var strict = await SeedVendorAsync(auth.OrgId, "Strict", ("coi", "5000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, lenient);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "2000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = strict });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "the reassignment is an ordinary request");
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.VendorId.Should().Be(strict, "the worker writes no VendorId, so the reassignment stands");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "2M does NOT clear Strict's 5M floor — pre-#460 this row was stored Compliant (Lenient's 1M "
            + "floor) while pointing at Strict, certifying a certificate against a checklist it fails");
        terminal.Checks.Should().ContainSingle().Which.IsPassed.Should().BeFalse(
            "the detail page's explainer must show the failing requirement, not a passing one from the "
            + "checklist the document left");
    }

    [Fact]
    public async Task A_vendor_soft_deleted_mid_extraction_grades_as_no_checklist()
    {
        // The basis path resolves the checklist through a NEW query (context.Set<Vendor>() off the basis's
        // own FK) instead of the tracked navigation, and its correctness rests on the Vendor soft-delete
        // GLOBAL FILTER still applying to it. Nothing else on the worker path exercises that, and
        // reviewers.md blesses IgnoreQueryFilters() inside background workers — so a future "tidy up"
        // adding it here would read as idiomatic and would silently grade a document against a DELETED
        // vendor's checklist, i.e. persist an affirmative verdict nobody's requirements back.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "2000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.DeleteAsync($"/api/vendors/{vendorId}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.VendorId.Should().Be(vendorId, "the FK survives the vendor's soft delete");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Pending,
            "2M would clear the deleted vendor's 1M floor, so an unfiltered basis query lands Compliant — "
            + "a verdict from a checklist that no longer governs anything. The filtered query reads "
            + "no-template, which is Pending");
        terminal.Checks.Should().BeEmpty(
            "no governing rules must also mean no check rows — otherwise the detail page keeps rendering "
            + "requirements from the deleted vendor's checklist");
    }

    [Fact]
    public async Task A_write_that_lands_AFTER_the_basis_read_is_not_clobbered_by_the_persist()
    {
        // The behavioural twin of The_persist_emits_what_it_extracted_and_no_verdict_input_it_only_read,
        // and the only interleave in this file that can see trap 2 by VALUE. Every DuringExtract test
        // commits BEFORE the basis read, so `doc.VendorId = basis.VendorId` would write back the value the
        // row already holds and every assertion would still pass. Land a SECOND competing write after the
        // basis read — from inside the worker's own SavingChanges — and the assign-back becomes what it
        // really is: the worker emitting a column it does not own, carrying a value already superseded.
        //
        // Mid and Final share ONE checklist on purpose: the FK moves, the verdict does not, so this test
        // says nothing about the basis-read → commit window ADR 0030 Amendment 2 records as still OPEN. It
        // pins exactly one thing — the worker's UPDATE must not carry VendorId.
        //
        // And that checklist governs PERMITs while the document is a COI, so neither writer ever produces a
        // ComplianceCheck row. Not tidiness: ApplyEvaluationAsync materializes the document's existing check
        // rows and stages a RemoveRange, so a competing regrade landing in THIS window deletes the very rows
        // the persist has staged and EF answers DbUpdateConcurrencyException out of PersistSuccess — the
        // re-paid-extraction landing. That hazard predates #460 (it arrived with #337, and ADR 0030
        // § Consequences under-calls it "cosmetic") and is ticketed as #468; giving these vendors a
        // governing rule would silently make this test about THAT instead, and flakily so.
        var auth = await RegisterAndLoginAsync();
        var start = await SeedVendorAsync(auth.OrgId, "Start", ("coi", "5000000"));
        var mid = await SeedVendorAsync(auth.OrgId, "Mid", ("permit", "1000000"));
        var final = await SeedVendorOnSameChecklistAsync(auth.OrgId, "Final", mid);
        var docId = await SeedQueuedDocAsync(auth.OrgId, start);

        var hook = Fixture.Factory.Services.GetRequiredService<ConcurrentSystemWriteInterceptor>();
        Extraction.Result = Extracted("coi", ("general_liability_limit", "2000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = mid });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            // Armed only now, so it cannot fire on a save that precedes the extraction call.
            hook.OnSavingChanges = async () =>
            {
                // Disarm first: the cost tracker saves on this context again after the persist, and the
                // competing request's own audit write goes through a SystemDbContext too.
                hook.OnSavingChanges = null;
                var second = await auth.Client.PatchAsJsonAsync(
                    $"/api/documents/{docId}", new { vendorId = final });
                second.StatusCode.Should().Be(HttpStatusCode.OK);
            };
        };

        try
        {
            await ClaimAndProcessAsync(docId);
        }
        finally
        {
            hook.Reset();
        }

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.VendorId.Should().Be(final,
            "the worker emits no VendorId at all, so the LAST request to commit one owns the column. An "
            + "assign-back of the freshly-read basis value would emit it and clobber this reassignment "
            + "with the vendor read one round trip earlier — the LOST UPDATE ADR 0030 Amendment 1 refutes");
        terminal.Doc.GeneralLiabilityLimit.Should().Be(2_000_000m,
            "the worker DID extract this column, so its own value is what the UPDATE carries");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Pending,
            "Mid and Final share one PERMIT-only checklist, so no rule governs this COI whichever of them "
            + "graded — zero applicable rules is Pending, never a vacuous Compliant");
    }

    [Fact]
    public async Task The_persist_emits_what_it_extracted_and_no_verdict_input_it_only_read()
    {
        // The other half of trap 2, and the invariant reviewers.md states as "the worker still emits
        // exactly the columns it emitted before". No value assertion can see it: the shipped code and an
        // assign-back leave the SAME row whenever the competing write committed before the basis read, so
        // what has to be pinned is the STATEMENT. Read off the host's EF command log through a Serilog
        // sink — the DocumentEndpointsTests.Marking_verified_on_an_unsettled_row_emits_trust_WITHOUT_the_
        // status_it_read shape, for the same reason.
        //
        // The interleave matters here too, and not as decoration: the three columns asserted absent are
        // absent only because the worker leaves them UNMODIFIED, and an assign-back is detectable only
        // where the fresh value DIFFERS from the worker's minutes-old snapshot. So the mid-run request
        // moves all three (vendor, type, and a typed column the model omits from its answer).
        var sink = new CapturingLogEventSink();
        await using var host = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        _ = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var auth = await RegisterAndLoginAsync();
        var before = await SeedVendorAsync(auth.OrgId, "Before", ("coi", "5000000"));
        var after = await SeedVendorAsync(auth.OrgId, "After", ("coi", "1000000"), ("permit", "1000000"));
        var docId = await SeedQueuedDocAsync(
            auth.OrgId, before, documentType: "coi", gl: 1_000_000m, host: host.Services);

        // "coi" is what the row already holds, so NormalizeExtracted returns the stored value and the
        // assignment in PersistSuccess is a no-op; general_liability_limit is ABSENT from the answer, so
        // ApplyToTypedColumn never runs for it. Both columns therefore stay out of the UPDATE.
        ExtractionOf(host.Services).Result = Extracted("coi", ("policy_number", "POL-1"));
        ExtractionOf(host.Services).DuringExtract = async () =>
        {
            var patch = await auth.Client.PatchAsJsonAsync(
                $"/api/documents/{docId}", new { vendorId = after, documentType = "permit" });
            patch.StatusCode.Should().Be(HttpStatusCode.OK);
            var fields = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "3000000" } }
            });
            fields.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId, host.Services);

        // Sanity first: the row really did move on all three columns, so "not in the UPDATE" below is a
        // statement about the worker rather than about a value that happened not to change. Every one of
        // those three values was written by the MID-RUN requests, though, so they say nothing about the
        // persist having run at all — ExtractionStatus does, and it is the one column here only the worker
        // can have moved (#460 review round 2, S1).
        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "precondition: the persist ran and SETTLED the document — a throw out of PersistSuccess would "
            + "leave it back at Pending, and the SET clause read below would be the failure bookkeeping's");
        terminal.Doc.VendorId.Should().Be(after);
        terminal.Doc.DocumentType.Should().Be("permit");
        terminal.Doc.GeneralLiabilityLimit.Should().Be(3_000_000m);

        var setClause = PersistSetClause(sink);
        setClause.Should().NotBeNull(
            "the persist's UPDATE must appear in this host's EF command log — if it does not, this test is "
            + "a no-op and proves nothing about which columns the write carried");
        setClause.Should().Contain("\"ExtractionStatus\" =",
            "anti-no-op: the statement we are reading must be the persist, which moves the queue position");
        setClause.Should().NotContain("\"VendorId\" =",
            "the basis is READ-ONLY. Re-reading a verdict input and ASSIGNING it onto the tracked entity "
            + "makes the worker WRITE a column a REQUEST owns, trading the stale-basis bug for a lost "
            + "update (ADR 0030 Amendment 1, Amendment 2 Option G)");
        setClause.Should().NotContain("\"DocumentType\" =",
            "the model answered with the type already stored, so the assignment is a no-op and the column "
            + "must stay out of the UPDATE — the user's mid-read correction owns it");
        setClause.Should().NotContain("\"GeneralLiabilityLimit\" =",
            "the model omitted this field, so ApplyToTypedColumn never ran for it and the worker has no "
            + "value of its own to assert over the human's correction");
    }

    [Fact]
    public async Task A_verdict_equal_to_the_stale_snapshot_is_still_WRITTEN_over_a_competitors()
    {
        // #460 review round 2, C1 — grading the right row is only half of it; the verdict has to be
        // WRITTEN. `doc` is the minutes-old snapshot and EF emits only properties that DIFFER from it, so
        // a freshly-computed verdict EQUAL to the value the row held at claim time produces no SET clause
        // at all — while the ComplianceCheck rows are rewritten unconditionally and UpdatedAt keeps the
        // UPDATE running. The result is the torn pair ADR 0030 exists to prevent, in the false-affirmative
        // direction: the row keeps a REQUEST's verdict beside THIS read's inputs and THIS read's checks.
        //
        // The interleave: strict V1 (5M floor), stored NonCompliant. A mid-run PATCH moves the document to
        // lenient V2 (1M floor), whose re-grade commits Compliant against the 2M the row still holds. The
        // model then returns 800k, which fails V2 too — so the basis grades NonCompliant, EQUAL to the
        // snapshot. Unforced, the row commits Compliant over an 800k limit and a FAILING check row, and
        // nothing re-grades it (the sweep does date transitions only).
        //
        // Pinned twice on purpose: by VALUE (ReadTerminalStateAsync's "the persisted verdict must be what
        // the persisted inputs grade to") and on the emitted COLUMN SET, because the value assertion alone
        // could be satisfied by a future change that grades differently rather than by the column being
        // written.
        var sink = new CapturingLogEventSink();
        await using var host = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        _ = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var auth = await RegisterAndLoginAsync();
        var strict = await SeedVendorAsync(auth.OrgId, "Strict", ("coi", "5000000"));
        var lenient = await SeedVendorAsync(auth.OrgId, "Lenient", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(
            auth.OrgId, strict, gl: 2_000_000m,
            complianceStatus: ComplianceStatus.NonCompliant, host: host.Services);

        ExtractionOf(host.Services).Result = Extracted("coi", ("general_liability_limit", "800000"));
        ExtractionOf(host.Services).DuringExtract = async () =>
        {
            var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = lenient });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "the reassignment is an ordinary request");

            await using var probe = CreateSystemDb();
            (await probe.Documents.AsNoTracking().FirstAsync(d => d.Id == docId)).ComplianceStatus
                .Should().Be(ComplianceStatus.Compliant,
                    "precondition: the reassignment's own re-grade really did move the STORED verdict away "
                    + "from the worker's snapshot — otherwise there is nothing for the persist to correct");
        };

        await ClaimAndProcessAsync(docId, host.Services);

        // The STATEMENT first, because that is the mechanism: the row's value is merely what the absent
        // SET clause leaves behind.
        var setClause = PersistSetClause(sink);
        setClause.Should().NotBeNull(
            "the persist's UPDATE must appear in this host's EF command log — if it does not, this test is "
            + "a no-op and proves nothing about which columns the write carried");
        setClause.Should().Contain("\"ComplianceStatus\" =",
            "the verdict is the worker's OWN conclusion about the basis it just graded, so it must be "
            + "FORCED into the UPDATE (ExtractionWorker.ForceVerdictWrite — the SetTrust / ADR 0052 §2 "
            + "shape one column over) rather than left to a snapshot comparison that can silently drop it. "
            + "This is NOT ADR 0030 Amendment 2 Option G, which forces verdict INPUTS a request owns");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(800_000m,
            "precondition: the persist did overwrite the input the competitor's verdict was computed from");
        terminal.Doc.VendorId.Should().Be(lenient, "the worker writes no VendorId, so the PATCH stands");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "800k fails Lenient's 1M floor. The recomputed verdict EQUALS the pre-run snapshot, so an "
            + "unforced assignment emits nothing and the row keeps the PATCH's Compliant — a certificate "
            + "certified against a limit it does not carry, beside this persist's own failing check row");
        terminal.Checks.Should().ContainSingle().Which.IsPassed.Should().BeFalse(
            "the check rows the persist wrote must agree with the verdict beside them");
    }

    [Fact]
    public async Task A_failing_basis_read_degrades_the_verdict_without_requeuing_the_extraction()
    {
        // Two things at once, both previously unpinned.
        //
        // (1) PLACEMENT (#460 review round 2, C2). The basis read sits INSIDE PersistSuccess's
        // degrade-to-Pending try because a throw out of that method is the most expensive failure in this
        // codebase: ProcessDocumentAsync catches it as a counted failure and requeues the document, so the
        // next claim RE-PAYS Document AI + the LLM (ExtractionWorker.Clamp's remarks). Nothing pinned that
        // — A_document_deleted_mid_extraction_persists_without_a_second_extraction soft-deletes, which
        // yields a NON-null basis and never makes the read FAIL, so hoisting it above the try left the
        // suite green. A hard delete is not a substitute either: the persist's own UPDATE would then throw
        // for an unrelated reason. So fail the read itself, before it reaches Postgres.
        //
        // (2) The degrade-to-Pending arm of the FORCED verdict write (#460 review round 2, C1). The catch
        // assigns Pending onto the same minutes-old snapshot, so when that snapshot already SAID Pending
        // the degrade is dropped too — and a competitor's confident verdict is left standing over inputs
        // this persist just overwrote. Here the document starts unassigned (Pending, correctly), a mid-run
        // PATCH puts it on a 1M-floor checklist and commits Compliant against the 2M it still holds, and
        // the persist then writes 800k with no verdict to replace it.
        var auth = await RegisterAndLoginAsync();
        var lenient = await SeedVendorAsync(auth.OrgId, "Lenient", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId: null, gl: 2_000_000m);

        var fault = Fixture.Factory.Services.GetRequiredService<SystemCommandFaultInterceptor>();
        Extraction.Result = Extracted("coi", ("general_liability_limit", "800000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PatchAsJsonAsync($"/api/documents/{docId}", new { vendorId = lenient });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            await using (var probe = CreateSystemDb())
                (await probe.Documents.AsNoTracking().FirstAsync(d => d.Id == docId)).ComplianceStatus
                    .Should().Be(ComplianceStatus.Compliant,
                        "precondition: the assignment's re-grade moved the STORED verdict off the worker's "
                        + "snapshot, so the degrade below has something to overwrite");

            // Armed only now: ProcessDocumentAsync's own load of the document already happened (before the
            // extraction call), so the next SELECT of "Documents" on the worker's SystemDbContext is the
            // grading-basis read. The hook self-disarms on that one fire.
            fault.ShouldFault = sql => sql.Contains("FROM \"Documents\"", StringComparison.Ordinal);
        };

        int faults;
        try
        {
            await ClaimAndProcessAsync(docId);
        }
        finally
        {
            // Read BEFORE the reset — Reset() zeroes the counter, so a disarm-then-assert order would
            // make the non-vacuity check below read 0 no matter what happened.
            faults = fault.FaultCount;
            fault.Reset();
        }

        faults.Should().Be(1,
            "the basis read really did fail — a predicate that matched nothing would make every assertion "
            + "below a statement about the ordinary success path");

        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "a failing basis read must land on Pending like any other recompute failure, never as a throw "
            + "out of PersistSuccess — that throw is caught as a COUNTED FAILURE and the document goes back "
            + "in the queue, re-paying Document AI + the LLM on the next claim");
        doc.FailedAttempts.Should().Be(0, "…so nothing was charged against the retry budget");
        doc.ProcessingError.Should().BeNull("…and no failure was recorded against the document");
        doc.GeneralLiabilityLimit.Should().Be(800_000m,
            "precondition: the inputs still commit — a degraded verdict never costs the extraction");
        doc.VendorId.Should().Be(lenient, "precondition: the assignment committed inside the window");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Pending,
            "the degrade must be WRITTEN. Assigning Pending onto a snapshot that already said Pending "
            + "emits no SET clause, so the row would keep the PATCH's Compliant over the 800k this persist "
            + "just wrote — a confident verdict from inputs nobody graded (ExtractionWorker.ForceVerdictWrite)");
        (await db.ComplianceChecks.AsNoTracking().Where(c => c.DocumentId == docId).ToListAsync())
            .Should().ContainSingle(
                "ApplyEvaluationAsync never ran, so it never cleared the PATCH's check row — the display "
                + "desync ADR 0030 § Consequences records, which is exactly why the HEADLINE verdict has "
                + "to read Pending rather than inherit the affirmative one those rows were written for");

        // The real form of "no failure path may cost a second OCR + LLM run" (#460 review round 2, S2):
        // drive a SECOND poll cycle and require the queue to have nothing for it. The row is alive here, so
        // unlike the mid-run-delete test this is a statement about its STATUS rather than about its
        // DeletedAt.
        (await BuildWorker().ClaimNextAsync(CancellationToken.None)).Should().BeNull(
            "the document is settled at Completed — neither Pending nor a stale Processing claim — so the "
            + "next poll has nothing to pick up");
        Extraction.ExtractCallCount.Should().Be(1, "…and the extraction was therefore paid for exactly once");
    }

    [Fact]
    public async Task The_grading_basis_overlays_only_the_properties_the_writer_modified()
    {
        // DocumentGradingBasis directly, because no integration test can see the composition itself — only
        // a verdict it happens to imply. The two halves of the one rule, on one row: a property the writer
        // MODIFIED comes from the writer (it will be in the UPDATE), a property it did not comes from the
        // COMMITTED row (it will not be, so the row keeps whatever a request wrote in the window).
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 1_000_000m);

        await using var writer = CreateSystemDb();
        var tracked = await writer.Documents.FirstAsync(d => d.Id == docId);
        tracked.DocumentType = "permit"; // the writer's own change, not yet committed

        await using (var request = CreateSystemDb())
        {
            var row = await request.Documents.FirstAsync(d => d.Id == docId);
            row.GeneralLiabilityLimit = 3_000_000m; // someone else's change, committed after the snapshot
            await request.SaveChangesAsync();
        }

        var basis = await DocumentGradingBasis.AfterPendingCommitAsync(writer, tracked, CancellationToken.None);

        basis.Should().NotBeNull();
        basis!.Id.Should().Be(docId, "the basis is a prediction of THIS row, and the verdict path relies on it");
        basis.DocumentType.Should().Be("permit",
            "a property the writer modified WILL be in its UPDATE, so the prediction takes the writer's value");
        basis.GeneralLiabilityLimit.Should().Be(3_000_000m,
            "a property the writer did NOT modify will be absent from its UPDATE, so the prediction takes "
            + "the row's current committed value — including one a request wrote after the snapshot");
        tracked.GeneralLiabilityLimit.Should().Be(1_000_000m,
            "nothing is copied BACK onto the tracked entity: the writer must keep emitting exactly the "
            + "columns it emitted before");
        writer.ChangeTracker.Entries<Document>().Select(e => e.Entity).Should().NotContain(basis,
            "the basis is DETACHED — ApplyEvaluationAsync hangs an AsNoTracking vendor graph off it, which "
            + "a tracked principal would turn into spurious inserts");
    }

    [Fact]
    public async Task The_grading_basis_is_null_only_when_the_row_is_genuinely_gone()
    {
        // The null case is documented, branched on in PersistSuccess, and recorded in ADR 0030 Amendment 2,
        // so which delete reaches it has to be a fact rather than an assumption. GetDatabaseValues issues an
        // AsNoTracking().IgnoreQueryFilters() key lookup, so the SOFT delete every API path performs still
        // yields a basis; only a row that is really gone returns null. An EF change to either behaviour
        // flips a production branch, and this is what would notice.
        var auth = await RegisterAndLoginAsync();
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId: null);

        await using var writer = CreateSystemDb();
        var tracked = await writer.Documents.FirstAsync(d => d.Id == docId);

        await using (var soft = CreateSystemDb())
        {
            var row = await soft.Documents.FirstAsync(d => d.Id == docId);
            soft.Documents.Remove(row); // the audit interceptor translates this to a soft delete
            await soft.SaveChangesAsync();
        }

        (await DocumentGradingBasis.AfterPendingCommitAsync(writer, tracked, CancellationToken.None))
            .Should().NotBeNull(
                "a SOFT delete leaves the row readable past the query filter, so the persist still grades "
                + "the row it is about to leave — the null fallback is NOT the mid-run-delete path");

        await using (var hard = CreateSystemDb())
            await hard.Documents.IgnoreQueryFilters().Where(d => d.Id == docId).ExecuteDeleteAsync();

        (await DocumentGradingBasis.AfterPendingCommitAsync(writer, tracked, CancellationToken.None))
            .Should().BeNull(
                "a row that is genuinely gone has no committed values to predict, so the caller must fall "
                + "back to the tracked entity rather than invent a basis for a document that no longer exists");
    }

    [Fact]
    public async Task The_basis_overload_refuses_a_basis_that_is_not_this_document()
    {
        // ComputeOutcome stamps every new ComplianceCheck.DocumentId from the BASIS, while the
        // clear-existing predicate keys on the TRACKED doc. Today's single caller derives the basis from
        // the same tracked entity by primary key, so the two always agree and the coupling is invisible —
        // which is exactly why it is enforced rather than commented: a future caller that broke it would
        // insert check rows against one document while deleting another's, silently.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 2_000_000m);
        var otherId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 2_000_000m);

        await using var db = CreateSystemDb();
        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var compliance = scope.ServiceProvider.GetRequiredService<IComplianceCheckService>();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        var foreignBasis = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == otherId);

        var act = () => compliance.ApplyEvaluationAsync(db, doc, foreignBasis, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("gradingBasis")
            .WithMessage("The grading basis must be the same document*");
    }

    [Fact]
    public async Task The_basis_overload_refuses_a_TRACKED_basis()
    {
        // ApplyEvaluationAsync ASSIGNS the basis's Vendor navigation from an AsNoTracking query. On a
        // DETACHED basis (what DocumentGradingBasis produces) that is inert; on a TRACKED one it grafts an
        // untracked graph onto a tracked principal, which EF turns into spurious inserts at the next
        // DetectChanges. The interface says "detached"; this is what makes it true.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 2_000_000m);

        await using var db = CreateSystemDb();
        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var compliance = scope.ServiceProvider.GetRequiredService<IComplianceCheckService>();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);

        var act = () => compliance.ApplyEvaluationAsync(db, doc, doc, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("gradingBasis")
            .WithMessage("The grading basis must be a DETACHED document*");
    }

    /// <summary>
    /// The SET clause of the persist's <c>UPDATE "Documents"</c> in <paramref name="sink"/>'s EF command
    /// log, or null when there is none.
    /// <para/>
    /// <c>ExtractionCompletedAt</c> is the discriminator, and it is not decoration (#460 review round 2,
    /// S1): the worker's FAILURE bookkeeping (<c>RecordFailedAttempt</c> / <c>MarkFailed</c>) also emits an
    /// <c>UPDATE "Documents"</c> carrying <c>ExtractionStatus</c> and none of the columns these tests assert
    /// absent, so a persist that THREW would otherwise hand this reader the failure statement and every
    /// assertion would pass against a write that is not the one under test. Only
    /// <see cref="ExtractionWorker"/>'s success path stamps a completion time.
    /// </summary>
    private static string? PersistSetClause(CapturingLogEventSink sink) =>
        sink.LastUpdateSetClause("Documents", "\"ExtractionCompletedAt\"");
}
