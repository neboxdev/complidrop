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
/// <para/>
/// The last section is #467 (ADR 0052 Amendment 1), the SIBLING of everything above: the trust decision
/// asked <c>DocumentFieldReadability</c> of the same pre-run snapshot, by the same mechanism, and so
/// described a row that no longer existed. It now reads the basis too, and the four tests assert the
/// acceptance invariant on the PERSISTED row rather than on the worker's intent — that the committed
/// trust/status pair and the read-time <c>unreadableFields</c> list AGREE, via
/// <see cref="AssertTrustAgreesWithTheRowAsync"/>, in both interleave directions (a mid-run edit that
/// FIXES a value and one that BREAKS it) plus the fail-CLOSED fallback when the basis is unavailable.
/// <para/>
/// One more interleave lands in the same window and is NOT about the basis at all
/// (<see cref="A_regrade_that_deletes_the_check_rows_this_persist_staged_costs_no_extraction"/>, #468): a
/// competing re-grade deletes the <c>ComplianceCheck</c> rows this persist has STAGED for removal, so its
/// DELETE matches nothing. It lives here because this suite already owns the after-the-basis-read harness,
/// and because its landing is the same one every test above is measured against — a throw out of
/// <c>PersistSuccess</c> costs another paid OCR + LLM run. The mechanism that answers it has its own suite,
/// <see cref="ComplianceCheckDeleteConcurrencyTests"/>.
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
    /// Adds one more rule to <paramref name="vendorId"/>'s checklist and returns its id, so a test can
    /// grade on a field OTHER than the money floor <see cref="SeedVendorAsync"/> writes (#467 review
    /// round 2, S3). Needed for exactly one thing here: a canonical field whose typed column is NULL is
    /// the only shape in which <c>ComplianceCheckService.LookupValue</c> reaches its raw-string
    /// FALLBACK — the <c>ExtractionFields</c> mirror — which is why the reconciliation has to run before
    /// the grade and not after.
    /// </summary>
    private async Task<Guid> AddRuleAsync(Guid vendorId, string documentType, string fieldName, string op)
    {
        var ruleId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        var templateId = await db.Vendors
            .Where(v => v.Id == vendorId)
            .Select(v => v.ComplianceTemplateId)
            .FirstAsync();
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = ruleId, ComplianceTemplateId = templateId!.Value, DocumentType = documentType,
            FieldName = fieldName, Operator = op, SortOrder = 100,
        });
        await db.SaveChangesAsync();
        return ruleId;
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
    private static ExtractionResult Extracted(string? documentType, params (string Name, string Value)[] fields) =>
        ExtractedAt(documentType, [.. fields.Select(f => (f.Name, f.Value, 0.95))]);

    /// <summary>
    /// <see cref="Extracted"/> with a PER-FIELD confidence, for the one <c>ManualRequired</c> trigger the
    /// 0.95 fixture can never reach: ADR 0042's per-verdict-bearing-field gate (#467 review round 2). That
    /// trigger describes the READING, so it does not consult the grading basis and is not part of the
    /// agreement invariant <see cref="AssertTrustAgreesWithTheRowAsync"/> asserts — a fixture using this
    /// helper must therefore assert the pair directly rather than through that biconditional.
    /// </summary>
    private static ExtractionResult ExtractedAt(
        string? documentType, params (string Name, string Value, double Confidence)[] fields) => new(
        DocumentType: documentType,
        DocumentSubType: null,
        Fields: [.. fields.Select(f => new ExtractedField(f.Name, f.Value, "currency", f.Confidence))],
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
        // expensive failure in this codebase: since #375 item 1 the catch abandons the dirty context
        // unsaved and counts
        // the failure against a guarded fresh read, so the throw costs a counted failure plus a re-paid
        // Document AI + LLM run per retry — bounded by MaxAttempts and spaced by the retry backoff
        // (ExtractionWorker.Clamp's remarks; pre-#375 the bookkeeping save re-threw on the same context
        // and the document was zombie-reclaimed every five minutes). The row being deleted mid-run is the
        // sharpest shape of "the world moved under this persist", so pin that it lands as an ordinary
        // completed persist rather than as an exception.
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
            "the persist completed normally — a throw would have left this soft-deleted row at the claim's "
            + "Processing, since the catch's guarded reload takes the soft-delete filter and writes "
            + "nothing (#375 item 1)");
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
        // second cycle on a row that is still alive). Nor, since #375 item 1, can the failure bookkeeping
        // (#375 review round 2, S1): its guarded reload takes the soft-delete filter, so on THIS row it
        // returns null and writes nothing — the two assertions below hold whether or not the failure path
        // ran, and are kept as shape guards on the interleave rather than as the discriminator. What DOES
        // discriminate is the ExtractionStatus == Completed assertion above (a throw leaves the claim's
        // Processing) plus the Compliant verdict and single check row only a completed persist produces.
        doc.FailedAttempts.Should().Be(0,
            "a soft-deleted row gets no bookkeeping at all — and a completed persist charges nothing either");
        doc.ProcessingError.Should().BeNull(
            "no failure path can stamp an error on this row (#375 item 1's filtered reload)");
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
        // ComplianceCheck row. That started as a dodge around #468 — a competing regrade landing in THIS
        // window deletes the very rows ApplyEvaluationAsync has staged for removal, which used to throw
        // DbUpdateConcurrencyException out of PersistSuccess, the re-paid-extraction landing. #468 is now
        // CLOSED (ComplianceCheckDeleteConcurrencyInterceptor), so the dodge no longer avoids a failure; it
        // is kept because it keeps this pin saying exactly ONE thing. A governing checklist here would make
        // the terminal check rows a MIXED set — the competing regrade's plus the persist's, the residue ADR
        // 0030 § Consequences records — and this test's subject is the emitted COLUMN SET, not the check
        // rows. The check-row interleave has its own named pin:
        // A_regrade_that_deletes_the_check_rows_this_persist_staged_costs_no_extraction, which uses a
        // governing checklist precisely because that is the shape that reaches it.
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
        // sink — the DocumentEndpointsTests.Marking_verified_still_emits_trust_WITHOUT_forcing_the_
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

    [Fact]
    public async Task A_regrade_that_deletes_the_check_rows_this_persist_staged_costs_no_extraction()
    {
        // #468. ApplyEvaluationAsync clears the document's existing ComplianceCheck rows by MATERIALIZING
        // them and staging a RemoveRange, so EF emits a DELETE per row keyed on its primary key. A
        // competing regrade that commits inside the window between that read and the persist's SaveChanges
        // has already deleted exactly those rows, so the persist's DELETE affects 0 rows and EF answers
        // DbUpdateConcurrencyException — out of PersistSuccess, still the most expensive failure in this
        // codebase: since #375 item 1 the throw costs a counted failure plus a re-paid Document AI + LLM
        // run per retry, bounded by MaxAttempts and spaced by the retry backoff, before a terminal Failed
        // (pre-#375 the catch re-saved on the SAME dirty context, so FailedAttempts never incremented and
        // the document was zombie-reclaimed every five minutes — ExtractionWorker.Clamp's remarks).
        //
        // The checklist GOVERNS the document's type on purpose — that is what makes both writers produce
        // check rows, and it is the difference between this test and
        // A_write_that_lands_AFTER_the_basis_read_is_not_clobbered_by_the_persist, whose vendors
        // deliberately share a checklist that governs nothing.
        //
        // It boots its own host so the interceptor's own log reaches a sink: every terminal assertion below
        // holds identically whether the zero-row DELETE happened and was TOLERATED or the interleave never
        // produced one at all, so without an assertion on the EVENT this test could go green having stopped
        // covering the ticket (#468 review S3). Two independent discriminators are asserted, one on the
        // constructed STATE and one on the suppression itself.
        var sink = new CapturingLogEventSink();
        await using var host = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        _ = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        // Seeded BELOW what the model returns below, so the worker genuinely MODIFIES the column and it is
        // therefore in the persist's UPDATE — otherwise the assertion that the extraction's inputs
        // committed would be measuring the competing edit's value instead (ADR 0030 Amendment 2).
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 1_000_000m, host: host.Services);
        // The rows the persist will stage for removal. Without them the clear stages nothing and the
        // interleave below cannot reach the hazard at all.
        await MarkGradedAsync(auth.OrgId, docId);

        async Task<List<Guid>> CheckIdsAsync()
        {
            await using var db = CreateSystemDb();
            return await db.ComplianceChecks.AsNoTracking()
                .Where(c => c.DocumentId == docId).Select(c => c.Id).ToListAsync();
        }

        var stagedCheckIds = await CheckIdsAsync();
        stagedCheckIds.Should().ContainSingle(
            "precondition: there is exactly one check row for the persist's clear to stage for removal");
        List<Guid> afterCompetingRegrade = [];

        var hook = host.Services.GetRequiredService<ConcurrentSystemWriteInterceptor>();
        ExtractionOf(host.Services).Result = Extracted("coi", ("general_liability_limit", "2000000"));
        ExtractionOf(host.Services).DuringExtract = () =>
        {
            // Armed only now, so it cannot fire on a save that precedes the extraction call. The competing
            // request has to land AFTER ApplyEvaluationAsync read the check rows and BEFORE the persist's
            // UPDATE — which is exactly this hook's window.
            hook.OnSavingChanges = async () =>
            {
                hook.OnSavingChanges = null;
                var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
                {
                    fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "4000000" } }
                });
                resp.StatusCode.Should().Be(HttpStatusCode.OK,
                    "the competing edit is an ordinary request — and its re-grade is what deletes the rows "
                    + "the persist has staged");
                // Read INSIDE the window, because it is unobservable afterwards: by the time the persist's
                // own inserts land, the two writers' rows are indistinguishable from a set the clear never
                // targeted.
                afterCompetingRegrade = await CheckIdsAsync();
            };
            return Task.CompletedTask;
        };

        try
        {
            await ClaimAndProcessAsync(docId, host.Services);
        }
        finally
        {
            hook.Reset();
        }

        // DISCRIMINATOR 1 — the STATE. The competing re-grade replaced the WHOLE check-row set before the
        // persist's SaveChanges executed, so the DELETE the persist had staged was aimed at a row that no
        // longer existed. Drop MarkGradedAsync above (or stop the competing writer clearing) and this is
        // what goes red, while every terminal assertion below still passes.
        afterCompetingRegrade.Should().ContainSingle(
                "the competing re-grade cleared and rewrote the set, so exactly its own row is present")
            .Which.Should().NotBe(stagedCheckIds[0],
                "…and it is a DIFFERENT row: the one the persist staged for removal is already gone, which "
                + "is the zero-row DELETE this ticket is about");

        // DISCRIMINATOR 2 — the EVENT. Proof the tolerance actually fired rather than the hazard having
        // been dodged. One line per SaveChanges, not per orphaned row (#468 review S2), so the count is
        // also the pin on that shape.
        var suppressions = sink.Events
            .Select(e => e.RenderMessage())
            .Where(m => m.Contains("treating those deletes as done", StringComparison.Ordinal))
            .ToList();
        suppressions.Should().ContainSingle(
            "the persist's SaveChanges suppressed a check-row concurrency exception, and says so exactly "
            + "once for the whole unit of work");
        suppressions[0].Should().Contain("deleted 1 ComplianceCheck row(s)")
            .And.Contain("across 1 document(s)",
                "the line carries the AGGREGATE it claims to — one line per save reporting every row it "
                + "covered, not one line per row reporting a total it can never hold");

        // ReadTerminalStateAsync also re-asserts the ADR 0030 invariant itself — the persisted verdict is
        // what the persisted inputs grade to — so the tolerated delete cannot have bought a consistent
        // status by skipping the grading.
        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "the persist must commit through the interleave — a throw here is caught as a COUNTED FAILURE "
            + "and the document goes back in the queue for another paid OCR + LLM run");
        terminal.Doc.FailedAttempts.Should().Be(0, "…so nothing was charged against the retry budget");
        terminal.Doc.ProcessingError.Should().BeNull("…and no failure was recorded against the document");
        terminal.Doc.GeneralLiabilityLimit.Should().Be(2_000_000m,
            "the extraction's own inputs still commit — the deletes stay in the persist's own SaveChanges, "
            + "so this is still ONE unit of work and not a partial one");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "2M clears the 1M floor — the verdict this persist computed is the one that lands, forced into "
            + "the UPDATE as always (ExtractionWorker.ForceVerdictWrite)");

        // The recorded RESIDUE, pinned rather than left to prose (ADR 0030 § Consequences): the competing
        // re-grade's own check rows were already committed when this persist's inserts landed, so the
        // document transiently holds BOTH sets. That is the display desync the ADR always described — and
        // the whole trade this ticket makes, since the alternative was a thrown persist. Both rows cite a
        // rule that GOVERNS the row as it now stands (ReadTerminalStateAsync asserts that), so the
        // "What we checked" panel never renders a requirement that does not apply.
        terminal.Checks.Should().HaveCount(2,
            "one row from the competing edit's re-grade and one from this persist — the transient duplicate "
            + "ADR 0030 § Consequences records, cleared by the document's next evaluation");
        terminal.Checks.Select(c => c.ComplianceRuleId).Distinct().Should().ContainSingle(
            "…and both rows cite the SAME single rule — which is why the residue is a DISPLAY desync and "
            + "not a fact about how many requirements were measured");

        // …and the auditor-facing artifact must say ONE, because the vendor's checklist holds exactly one
        // requirement (#468 review C1). RequirementsChecked is the only surface that PRINTS this number
        // rather than thresholding it at zero, and the CSV carries no list of rows beside it for a reader
        // to reconcile the count against, so a raw row count would state a claim about the EVIDENCE that
        // the org's own checklist contradicts. ExportService.CheckCountsAsync counts distinct rules.
        var csv = await (await auth.Client.GetAsync("/api/export/csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        var header = lines[0].Split(',');
        var row = lines.Skip(1).Select(l => l.Split(','))
            .First(f => f[Array.IndexOf(header, "FileName")] == "coi.pdf");
        row[Array.IndexOf(header, "RequirementsChecked")].Should().Be("1",
            "the document was measured against ONE requirement; it holds two check rows only because two "
            + "writers each wrote one for that same rule");
        row[Array.IndexOf(header, "Compliance")].Should().Be("Compliant",
            "…and the verdict cell is unchanged — the count feeds DocumentGrading.IsGraded's > 0 threshold "
            + "everywhere else, which distinct-vs-raw cannot move");

        // The real form of "no failure path may cost a second OCR + LLM run": drive a SECOND poll cycle and
        // require the queue to have nothing for it.
        (await BuildWorker(host.Services).ClaimNextAsync(CancellationToken.None)).Should().BeNull(
            "the document is settled at Completed — neither Pending nor a stale Processing claim — so the "
            + "next poll has nothing to pick up");
        ExtractionOf(host.Services).ExtractCallCount.Should().Be(1,
            "…and the extraction was therefore paid for exactly once");
    }

    // ---- #467 / ADR 0052 Amendment 1: the TRUST decision reads the same basis the verdict does ----

    /// <summary>An expiration in a shape <c>CanonicalDocumentFields.ParseUtcDate</c> refuses — ADR 0040's
    /// own example. Non-blank, so it is UNREADABLE rather than absent, and the two are opposite facts.</summary>
    private const string UnreadableExpiration = "12/31/2029 (per endorsement)";

    /// <summary>Far enough out that no verdict below is date-driven, so the only thing that can move a
    /// status is the readability of the value under test.</summary>
    private static DateTime FarFuture => DateTime.UtcNow.Date.AddYears(2);

    /// <summary>
    /// A queued document whose <c>expiration_date</c> is seeded in a chosen READABILITY state, with the
    /// typed column and the JSON mirror set INDEPENDENTLY. That independence is the point: it is the only
    /// way to construct the state a prior ADR 0040 unreadable read leaves behind — a null column beside
    /// non-blank text the parser refuses — which <see cref="SeedQueuedDocAsync"/> cannot express because
    /// it derives both copies from one value.
    /// <para/>
    /// <c>general_liability_limit</c> is always seeded readable and always matches the vendor floor, so
    /// the checklist grades on a value no interleave here touches and every verdict below stays
    /// <c>Compliant</c> — leaving the trust axis as the only thing under test.
    /// </summary>
    private async Task<Guid> SeedQueuedDocWithExpirationAsync(
        Guid orgId, Guid vendorId, DateTime? expirationColumn, string expirationRaw,
        ExtractionTrust trust = ExtractionTrust.Trusted,
        ComplianceStatus complianceStatus = ComplianceStatus.Pending)
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
                DocumentType = "coi",
                ExtractionStatus = ExtractionStatus.Pending,
                ExtractionTrust = trust,
                ComplianceStatus = complianceStatus,
                GeneralLiabilityLimit = 2_000_000m,
                ExpirationDate = expirationColumn,
                ExtractionFields = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["general_liability_limit"] = "2000000",
                    ["expiration_date"] = expirationRaw,
                })),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await Fixture.Factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        return docId;
    }

    /// <summary>
    /// #467's acceptance invariant, asserted on the PERSISTED row and on the WIRE: the trust/status pair
    /// the persist committed and the read-time <c>unreadableFields</c> list must AGREE, so a document
    /// reading "Needs your review" always names a cause a user can act on. Returns that list.
    /// <para/>
    /// It is a BICONDITIONAL, and that is only legitimate because every fixture in this section holds the
    /// other three <c>ManualRequired</c> triggers off — <see cref="Extracted"/> returns 0.95 on every
    /// field (clearing both confidence gates) and <c>NeedsReprocessing: false</c>. Under that constraint
    /// readability is the ONLY trigger, so the two directions are the two ways #467 can be wrong:
    /// distrusted-without-an-unreadable-value is the DISCLOSURE bug the ticket reports (the vendor drops
    /// to Action needed and the detail page's <c>ManualReviewCard</c>, which renders off this very list,
    /// falls back to copy pointing at an amber outline that a high-confidence unreadable value never
    /// gets), and an-unreadable-value-without-distrust is the fail-OPEN mirror ADR 0040 exists to
    /// prevent.
    /// <para/>
    /// The list is read through <c>GET /api/documents/{id}</c> rather than by calling the walk again, so
    /// the assertion is about the surface the card actually renders from.
    /// </summary>
    private async Task<string[]> AssertTrustAgreesWithTheRowAsync(HttpClient client, Guid docId)
    {
        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var unreadable = body.GetProperty("data").GetProperty("unreadableFields")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

        var carriesUnreadableValue = unreadable.Length > 0;
        (doc.ExtractionTrust == ExtractionTrust.Distrusted).Should().Be(carriesUnreadableValue,
            "extraction trust and the read-time unreadable list describe the SAME row, so they must agree "
            + $"(row: trust {doc.ExtractionTrust}, unreadableFields [{string.Join(", ", unreadable)}])");
        (doc.ExtractionStatus == ExtractionStatus.ManualRequired).Should().Be(carriesUnreadableValue,
            "…and one boolean drives both columns, so the status cannot disagree with the trust beside it");
        return unreadable;
    }

    /// <summary>
    /// The copies of one field the detail page RENDERS, read off the wire (#467 review, C1): the
    /// <c>DocumentField</c> row's value — what the field editor's input binds to
    /// (<c>value={edits[f.fieldName] ?? f.fieldValue ?? ""}</c>) — the <c>ExtractionFields</c> mirror
    /// entry beside it, and the row's <c>originalValue</c>, which the page prints as <i>was: …</i>.
    /// <para/>
    /// <c>ManuallyEdited</c> and <c>Confidence</c> come along because they are what a reconciled row
    /// NAMES ITSELF with (#467 review round 2): the page prints <c>✎ Manually edited</c> off the first
    /// and outlines the input off the second (amber below 0.9, rose below 0.7), so a test that asserts
    /// only the values cannot tell a corrected row from a read one.
    /// <para/>
    /// Asserted separately from <see cref="AssertTrustAgreesWithTheRowAsync"/> because they are different
    /// claims. That one says the badge and the named cause agree; this one says the VALUES the user is
    /// shown agree with the typed column those conclusions were drawn from. A row can satisfy the first
    /// and still contradict itself on screen — which is exactly what C1 found.
    /// </summary>
    private static async Task<RenderedField> ReadRenderedFieldAsync(
        HttpClient client, Guid docId, string fieldName)
    {
        var data = (await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}"))
            .GetProperty("data");

        var field = data.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("fieldName").GetString() == fieldName);

        var fields = data.GetProperty("extractionFields");
        string? mirror = fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty(fieldName, out var entry)
            ? Text(entry)
            : null;

        return new RenderedField(
            Text(field.GetProperty("fieldValue")),
            mirror,
            Text(field.GetProperty("originalValue")),
            field.GetProperty("isManuallyEdited").GetBoolean(),
            field.GetProperty("confidence").GetDouble());

        static string? Text(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => e.GetString(),
            _ => e.GetRawText(),
        };
    }

    private sealed record RenderedField(
        string? Row, string? Mirror, string? WasOriginally, bool ManuallyEdited, double Confidence);

    [Fact]
    public async Task A_canonical_value_a_mid_run_edit_FIXED_leaves_no_review_with_nothing_to_name()
    {
        // THE #467 interleave, straight from the ticket. The document sits at ExpirationDate = null from a
        // prior ADR 0040 unreadable read; the user clicks "Read again" and, while it runs, types the
        // correct expiration and saves; the model returns the same unparseable text.
        //
        // ApplyToTypedColumn writes null over a null snapshot, so the property is NOT modified, EF omits
        // the column, and the row keeps the user's valid date. But the readability walk used to be asked
        // of that PRE-RUN snapshot, so it saw a null column beside non-blank unparseable raw text and the
        // row committed ManualRequired + Distrusted — over a row nothing on which is unreadable.
        //
        // The disclosure is what that costs. DocumentFieldReadability re-derived at READ time against the
        // persisted row returns nothing (the typed column is non-null), so the vendor rollup drops to
        // Action needed with no document surface naming why, and ManualReviewCard falls through to its
        // low-confidence copy — "the ones outlined in amber are the least certain" — on a document whose
        // fields are all 0.95 and 1.0, so nothing is outlined. That is the ADR 0040 Amendment 2 dead end,
        // reached from the other side.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: null, expirationRaw: UnreadableExpiration,
            trust: ExtractionTrust.Distrusted);
        // Captured ONCE (#467 review, S4): FarFuture reads the real clock, and this test PUTs it, then
        // asserts it twice with HTTP round-trips and a whole worker run in between — a UTC midnight
        // rollover across those reads would fail the test for a reason unrelated to the fix.
        var corrected = FarFuture;
        var correctedText = corrected.ToString("yyyy-MM-dd");

        Extraction.Result = Extracted(
            "coi", ("general_liability_limit", "2000000"), ("expiration_date", UnreadableExpiration));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "expiration_date", fieldValue = correctedText } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var probe = CreateSystemDb();
            var mid = await probe.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
            mid.ExpirationDate.Should().Be(corrected,
                "precondition: the correction really committed, so the pre-run snapshot's null column is "
                + "a value the row has MOVED ON FROM rather than one it still holds");
            mid.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
                "precondition: ResolveManualReview restored trust from the resulting state (ADR 0052 §2), "
                + "so the persist below is what takes it away again if anything does");
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExpirationDate.Should().Be(corrected,
            "the worker's own value equals its snapshot's null, so the column stays out of the UPDATE and "
            + "the user's correction survives — the very reason the pre-run walk was judging a ghost");
        terminal.Doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        terminal.Doc.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
            "nothing on the row this commit leaves is unreadable, so there is nothing to distrust — and a "
            + "Distrusted here is a vendor at Action needed that no document surface can explain");

        (await AssertTrustAgreesWithTheRowAsync(auth.Client, docId)).Should().BeEmpty();

        // …and "nothing on this row is unreadable" has to be true of the ROW, not merely of the one copy
        // the predicate reaches first (#467 review, C1). DocumentFieldReadability short-circuits on the
        // non-null typed column before it ever looks at the raw value — while the persist rewrites BOTH
        // raw copies from the response unconditionally. Unreconciled, this document commits the model's
        // unparseable text into the field editor and the JSON mirror, under a Completed/Trusted badge and
        // a Compliant verdict graded from the date the user typed, with unreadableFields empty and nothing
        // naming the disagreement: a document whose own screen contradicts itself.
        var rendered = await ReadRenderedFieldAsync(auth.Client, docId, "expiration_date");
        rendered.Row.Should().Be(correctedText,
            "the field editor binds to this value, so it must be the one the row will be GRADED from — "
            + "not a value the commit already decided to leave behind");
        rendered.Mirror.Should().Be(correctedText,
            "…and the JSON mirror is the same claim in the other copy, and a verdict input in its own "
            + "right (LookupValue's raw-string fallback), so it may not disagree either");
        rendered.WasOriginally.Should().Be(UnreadableExpiration,
            "the model's own answer is not discarded, it is DEMOTED to the provenance the detail page "
            + "already renders as \"was: …\" — the correction won the column, so it owns the value");
    }

    [Fact]
    public async Task A_mid_run_CLEAR_the_read_did_not_overwrite_leaves_no_field_value_claiming_otherwise()
    {
        // The OTHER direction of C1's disagreement, and the one that fails OPEN. The user CLEARS the
        // expiration mid-read; the model returns the value the row held at claim time, so
        // ApplyToTypedColumn assigns the snapshot's own date, the property is unmodified and the column
        // stays out of the UPDATE — the clear survives. Unreconciled, both raw copies still show a date:
        // a certificate that "expires" on a day its column does not carry can never turn Expired, never
        // enters an expiring-soon window and never triggers a reminder, which is ADR 0040's harm reached
        // from the copy side rather than from the parser's.
        //
        // Nothing is DISTRUSTED here, and that is the point: a cleared value is honestly ABSENT, not
        // unreadable (ADR 0040 — blank stays blank), so no review card would ever have named it.
        //
        // This is also the test that pins the ORDER (#467 review round 2, S3). The reconciliation runs
        // BEFORE ApplyEvaluationAsync because the JSON mirror is a verdict input in its own right —
        // LookupValue consults it whenever the typed column is null, which is exactly this row — and
        // nothing pinned that: every other fixture grades on general_liability_limit, whose column is
        // never null, so the fallback is never reached and moving the call below the grade changes no
        // verdict anywhere. The expiration_date `required` rule below is what makes the order visible:
        // graded FIRST it resolves the model's surviving date and PASSES; graded after the
        // reconciliation there is nothing left to resolve and it fails, which is the answer the
        // persisted row grades to.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var expirationRuleId = await AddRuleAsync(vendorId, "coi", "expiration_date", "required");
        var seeded = FarFuture;
        var seededText = seeded.ToString("yyyy-MM-dd");
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: seeded, expirationRaw: seededText);

        Extraction.Result = Extracted(
            "coi", ("general_liability_limit", "2000000"), ("expiration_date", seededText));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "expiration_date", fieldValue = (string?)null } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var probe = CreateSystemDb();
            (await probe.Documents.AsNoTracking().FirstAsync(d => d.Id == docId)).ExpirationDate
                .Should().BeNull(
                    "precondition: the clear really committed, so the date the model returns below is one "
                    + "the row has MOVED ON FROM rather than one it still holds");
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExpirationDate.Should().BeNull(
            "the worker's own value equals its snapshot, so the column stays out of the UPDATE and the "
            + "user's clear survives — the same mechanism as the FIXED interleave, one direction over");
        terminal.Doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        terminal.Doc.ExtractionTrust.Should().Be(ExtractionTrust.Trusted);

        var rendered = await ReadRenderedFieldAsync(auth.Client, docId, "expiration_date");
        rendered.Row.Should().BeNull(
            "the row carries no expiration, so the field editor must not show one — a value here is a "
            + "date nothing will ever be graded against");
        rendered.Mirror.Should().BeNull(
            "…and the mirror is the copy LookupValue's raw-string fallback reads, so leaving the model's "
            + "date there would let it resolve a value the column does not hold");
        rendered.WasOriginally.Should().Be(seededText,
            "the model's answer is preserved as provenance rather than discarded");

        (await AssertTrustAgreesWithTheRowAsync(auth.Client, docId)).Should().BeEmpty(
            "an absent value is not an unreadable one, so this document is correctly NOT routed to a "
            + "human — which is precisely why the copies may not keep claiming a date instead");

        // …and the ORDER, measured by the one thing that can see it: a VERDICT. ReadTerminalStateAsync
        // has already required the stored status to equal what the PERSISTED row grades to, so grading
        // the pre-reconciliation mirror (the model's date, which satisfies `required`) commits Compliant
        // over a row that grades NonCompliant — a torn pair, red on that assertion alone. Restated here
        // so the failure names the mechanism rather than a status mismatch.
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "the row carries no expiration at all, so the `required` rule fails. Move "
            + "ReconcileCanonicalCopiesWithTheRow below ApplyEvaluationAsync — the plausible 'make it "
            + "always run' tidy-up — and LookupValue's raw fallback resolves the date the reconciliation "
            + "was about to remove, certifying a certificate against a value the row will not hold");
        var expirationCheck = terminal.Checks.Single(c => c.ComplianceRuleId == expirationRuleId);
        expirationCheck.IsPassed.Should().BeFalse();
        expirationCheck.ActualValue.Should().BeNull(
            "the explainer row must show the user nothing where the row holds nothing — an ActualValue "
            + "here is the mirror's stale date quoted back as evidence");
    }

    [Fact]
    public async Task A_canonical_value_this_read_leaves_unreadable_still_routes_to_review_and_NAMES_it()
    {
        // Fail-CLOSED must stay fail-closed: #467 is a DISCLOSURE defect, not an over-strict verdict, so
        // the fix may not buy a clean bill of health for a row that really does carry a value nothing can
        // parse. Here the worker's own read is the unreadable one and its value WINS the column (the
        // snapshot held a real date, so the assignment to null IS modified and IS in the UPDATE), which
        // makes the row the commit leaves genuinely unreadable — and the mid-run edit that corrected it is
        // overwritten, ADR 0017's last-writer-wins on a column the worker actually wrote.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var seeded = FarFuture;
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: seeded, expirationRaw: seeded.ToString("yyyy-MM-dd"));

        Extraction.Result = Extracted(
            "coi", ("general_liability_limit", "2000000"), ("expiration_date", UnreadableExpiration));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[]
                {
                    new { fieldName = "expiration_date", fieldValue = seeded.AddDays(30).ToString("yyyy-MM-dd") }
                }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExpirationDate.Should().BeNull(
            "the worker DID write this column — an unparseable value clears it (ADR 0040) — so the row the "
            + "commit leaves is the unreadable one, whatever the mid-run edit had made it");
        terminal.Doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired);
        terminal.Doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "a canonical value nothing can parse certifies nothing, and a fix that reads the basis must "
            + "not turn that into a clean read (ADR 0040 fails CLOSED; ADR 0042 drops it from coverage)");

        // The `Equal` overload taking a because-string needs the expectation as an explicit collection —
        // the params overload would read the reason as a second expected element.
        (await AssertTrustAgreesWithTheRowAsync(auth.Client, docId)).Should().Equal(
            ["expiration_date"],
            "the detail page's review card renders off this list, so the document names the field "
            + "blocking its vendor's coverage");
    }

    [Fact]
    public async Task A_mid_run_edit_that_BREAKS_a_value_the_read_replaces_does_not_strand_the_document()
    {
        // The INVERSE interleave: the mid-run request makes a previously-readable value unreadable, and
        // the read that lands is clean. ADR 0052 Amendment 3 path 2 is the first half — trust follows
        // readability on EVERY status, so the save withdraws trust on a Processing row and the vendor
        // drops to Action needed mid-read, accepted because the card names the field. The persist is the
        // second half: it must RESTORE trust, because its own values overwrite both copies and the row it
        // leaves is readable. Judging the pre-run snapshot happens to agree here; judging the SUBMITTED
        // field names, or leaving trust alone because "the worker only lowers it", would not — the
        // document would sit at Distrusted over a date it does not carry, with an empty unreadable list
        // and no way back short of Mark verified.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var seeded = FarFuture;
        var extracted = seeded.AddDays(60);
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: seeded, expirationRaw: seeded.ToString("yyyy-MM-dd"));

        Extraction.Result = Extracted(
            "coi",
            ("general_liability_limit", "2000000"),
            ("expiration_date", extracted.ToString("yyyy-MM-dd")));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "expiration_date", fieldValue = UnreadableExpiration } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var probe = CreateSystemDb();
            var mid = await probe.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
            mid.ExpirationDate.Should().BeNull();
            mid.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
                "precondition: the save really did withdraw trust on an in-flight row (ADR 0052 "
                + "Amendment 3 path 2) — otherwise the persist below has nothing to restore and this "
                + "test proves nothing about the inverse direction");
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExpirationDate.Should().Be(extracted,
            "the worker extracted this field, so its value is what the UPDATE carries (ADR 0017)");
        terminal.Doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        terminal.Doc.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
            "the row this commit leaves is readable, so the read that just landed restores the trust the "
            + "mid-run save withdrew — the only writer that can do so without a human (ADR 0052 §2)");

        (await AssertTrustAgreesWithTheRowAsync(auth.Client, docId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_basis_read_falls_back_to_what_this_read_produced_and_still_withdraws_trust()
    {
        // The fallback arm #467 adds, and it has to be pinned in the fail-CLOSED direction because that is
        // the direction it exists for. When the basis is unavailable — the read threw, or the row is
        // genuinely gone — the walk falls back to the tracked entity, i.e. the pre-#467 answer. That
        // answer is a strict SUPERSET of the basis answer (a basis-unreadable field implies a
        // tracked-unreadable one, since ApplyToTypedColumn nulls the column for every unparseable value
        // the response carries), so the fallback can over-distrust but never under-distrust.
        //
        // No mid-run edit here on purpose: with the row unmoved the tracked entity IS the row, so the
        // fallback's answer is also the true one and the agreement invariant still holds — which is what
        // makes "fail closed" a statement about the row rather than about the snapshot.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: null, expirationRaw: UnreadableExpiration,
            trust: ExtractionTrust.Distrusted);

        var fault = Fixture.Factory.Services.GetRequiredService<SystemCommandFaultInterceptor>();
        Extraction.Result = Extracted(
            "coi", ("general_liability_limit", "2000000"), ("expiration_date", UnreadableExpiration));
        // ProcessDocumentAsync's own load already happened before the extraction call, so the next SELECT
        // of "Documents" on the worker's SystemDbContext is the grading-basis read. Self-disarms on fire.
        Extraction.DuringExtract = () =>
        {
            fault.ShouldFault = sql => sql.Contains("FROM \"Documents\"", StringComparison.Ordinal);
            return Task.CompletedTask;
        };

        int faults;
        try
        {
            await ClaimAndProcessAsync(docId);
        }
        finally
        {
            faults = fault.FaultCount;
            fault.Reset();
        }

        faults.Should().Be(1,
            "the basis read really did fail — a predicate that matched nothing would make every assertion "
            + "below a statement about the ordinary success path");

        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "with no basis to judge, the trust decision falls back to what this read produced — which is "
            + "unreadable, so the document still reaches a human");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted);
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Pending,
            "…and the verdict still degrades exactly as before: a failing basis read must never become a "
            + "throw out of PersistSuccess, which is a re-paid Document AI + LLM run");
        doc.FailedAttempts.Should().Be(0, "…so nothing was charged against the retry budget");

        (await AssertTrustAgreesWithTheRowAsync(auth.Client, docId)).Should().Equal("expiration_date");

        (await BuildWorker().ClaimNextAsync(CancellationToken.None)).Should().BeNull(
            "the document is settled — neither Pending nor a stale Processing claim — so the next poll has "
            + "nothing to pick up");
        Extraction.ExtractCallCount.Should().Be(1, "…and the extraction was therefore paid for exactly once");
    }

    [Fact]
    public async Task A_failing_GRADE_still_leaves_the_trust_decision_judging_the_basis_it_already_read()
    {
        // #467 review, S1 — the arm no test reached: a grade that fails AFTER a successful basis read.
        // The sibling failure test faults the BASIS READ, where `basis` is null and `basis ?? doc`
        // collapses to the fallback, so nothing said what a document whose RECOMPUTE failed commits.
        // Same FIXED interleave as the headline test (the row moves on under the worker and the column
        // stays out of the UPDATE), but the fault is armed on the vendor-chain read the grade issues,
        // which comes after the basis read on the same context.
        //
        // What it discriminates, measured rather than assumed. The tidy-up S1 named — `basis = null;`
        // inside the catch — is now BEHAVIOURALLY NEUTRAL, because ReconcileCanonicalCopiesWithTheRow
        // runs before the grade and leaves the tracked entity's raw copies agreeing with the row, so the
        // walk gives the same answer whichever object it is handed (C1; recorded in ADR 0052 Amendment 1).
        // The ORDER is what is load-bearing now, and it is what this test pins together with the headline
        // one: hoisting the readability walk back ABOVE the try — S1's other named tidy-up, and the
        // literal pre-#467 shape — makes both go red, because the pre-reconciliation snapshot still shows
        // a null column beside the model's unparseable text.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: null, expirationRaw: UnreadableExpiration,
            trust: ExtractionTrust.Distrusted,
            // Seeded AFFIRMATIVE so the Pending below is a genuine degrade rather than the value the row
            // already held — ADR 0030's "never a confident verdict from stale inputs", in the one shape
            // where the recompute did not run at all.
            complianceStatus: ComplianceStatus.Compliant);
        var corrected = FarFuture;
        var correctedText = corrected.ToString("yyyy-MM-dd");

        var fault = Fixture.Factory.Services.GetRequiredService<SystemCommandFaultInterceptor>();
        Extraction.Result = Extracted(
            "coi", ("general_liability_limit", "2000000"), ("expiration_date", UnreadableExpiration));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "expiration_date", fieldValue = correctedText } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            // The basis read is a key lookup on "Documents" and is deliberately NOT matched. The vendor
            // chain (Vendor → ComplianceTemplate → Rules) is the grade's first read and runs on this same
            // SystemDbContext; the request above ran on AppDbContext, so nothing it did can trip this.
            // Self-disarms on fire.
            fault.ShouldFault = sql => sql.Contains("FROM \"Vendors\"", StringComparison.Ordinal);
        };

        int faults;
        try
        {
            await ClaimAndProcessAsync(docId);
        }
        finally
        {
            faults = fault.FaultCount;
            fault.Reset();
        }

        faults.Should().Be(1,
            "the GRADE really did fail — a predicate that matched nothing, or one that matched the basis "
            + "read instead, would make every assertion below a statement about a different arm");

        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Pending,
            "precondition + ADR 0030: a recompute that threw degrades the verdict rather than leaving the "
            + "affirmative one this document was seeded with (and that the mid-run edit re-affirmed)");
        var graded = await db.Documents.AsNoTracking()
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(t => t!.Rules)
            .FirstAsync(d => d.Id == docId);
        ComplianceCheckService.ComputeOutcome(graded, DateTime.UtcNow).Status
            .Should().Be(ComplianceStatus.Compliant,
                "anti-no-op for the line above: the row this commit LEFT does grade to a confident "
                + "verdict, so the Pending it carries is the degrade rather than an honest 'nothing here "
                + "to grade' — the recompute genuinely did not run");

        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "the extraction itself SUCCEEDED — only the verdict is unknown — so the queue position is "
            + "settled and nothing re-reads this document");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
            "the basis was read successfully BEFORE the grade threw, and it is still the row this commit "
            + "leaves — dropping it in the catch would put this document back at ManualRequired + "
            + "Distrusted over a value it does not carry, which is #467 itself (ADR 0052 Amendment 1)");

        (await AssertTrustAgreesWithTheRowAsync(auth.Client, docId)).Should().BeEmpty();

        var rendered = await ReadRenderedFieldAsync(auth.Client, docId, "expiration_date");
        rendered.Row.Should().Be(correctedText,
            "the copies are reconciled from the same basis and BEFORE the grade, so a failing grade "
            + "cannot leave the field editor showing a value the column disagrees with either");

        doc.FailedAttempts.Should().Be(0, "nothing was charged against the retry budget");
        (await BuildWorker().ClaimNextAsync(CancellationToken.None)).Should().BeNull(
            "the document is settled — neither Pending nor a stale Processing claim — so the next poll "
            + "has nothing to pick up");
        Extraction.ExtractCallCount.Should().Be(1, "…and the extraction was therefore paid for exactly once");
    }

    /// <summary>A SECOND unparseable spelling of the same expiration, distinct from
    /// <see cref="UnreadableExpiration"/> so a test can tell which row kept which answer.</summary>
    private const string UnreadableExpirationVariant = "12/31/2029 (see endorsement CG 20 26)";

    [Fact]
    public async Task One_canonical_field_answered_TWICE_still_keeps_each_rows_own_answer()
    {
        // #467 review round 2, C1. The reconciliation walks `fieldsDict.Keys`, and that dictionary is
        // keyed ORDINALLY (it is built straight from the response), while the staged-row match is
        // OrdinalIgnoreCase. So a response carrying `expiration_date` AND `Expiration_Date` — both
        // canonical, both unparseable, so neither reaches the column — reconciles the SAME two rows
        // twice. Unguarded, the second pass demoted the value the FIRST pass had already written, so
        // every row ended with originalValue == fieldValue and the detail page's guard
        // (`f.originalValue && f.originalValue !== f.fieldValue`) rendered nothing: the model's own
        // answer erased from the row, which is the one thing ADR 0052 Amendment 1 § "The raw copies come
        // too" promises the demotion PRESERVES. DocumentField.ApplyCorrection captures it once.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: null, expirationRaw: UnreadableExpiration,
            trust: ExtractionTrust.Distrusted);
        var corrected = FarFuture;
        var correctedText = corrected.ToString("yyyy-MM-dd");

        // BOTH spellings must be unparseable: a parseable one would MODIFY the column, put it in the
        // UPDATE and leave the two documents agreeing, so no reconciliation would run at all.
        Extraction.Result = Extracted(
            "coi",
            ("general_liability_limit", "2000000"),
            ("expiration_date", UnreadableExpiration),
            ("Expiration_Date", UnreadableExpirationVariant));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "expiration_date", fieldValue = correctedText } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExpirationDate.Should().Be(corrected,
            "precondition: neither answer parses, so the column stays out of the UPDATE and the row "
            + "keeps the user's date — which is what makes both spellings reconcile");

        var canonical = await ReadRenderedFieldAsync(auth.Client, docId, "expiration_date");
        canonical.Row.Should().Be(correctedText);
        canonical.WasOriginally.Should().Be(UnreadableExpiration,
            "this row's own answer is what it must remember. A second demotion pass overwrites it with "
            + "the value already on the row, and `was: …` then renders NOTHING");

        var variant = await ReadRenderedFieldAsync(auth.Client, docId, "Expiration_Date");
        variant.Row.Should().Be(correctedText,
            "a second spelling is a second row the field editor renders, and it may not keep claiming a "
            + "date the column does not carry either");
        variant.WasOriginally.Should().Be(UnreadableExpirationVariant,
            "…and it remembers ITS answer, not the other spelling's — the demotion is per row");

        (await AssertTrustAgreesWithTheRowAsync(auth.Client, docId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reconciled_field_that_tripped_the_confidence_gate_names_itself_on_the_row()
    {
        // The arm every fixture above holds OFF, and the one #467's agreement invariant deliberately
        // does NOT cover (ADR 0052 Amendment 1 § The invariant this buys): the per-verdict-bearing-field
        // confidence gate. It reads the RESPONSE, has no mirror on the row, and so keeps describing this
        // READING — "make the four triggers consistent" is the bug, not the fix.
        //
        // What is worth pinning is where that leaves the DISCLOSURE, because the reconciliation moves it.
        // The model re-reads the expiration at 0.5 (the gate fires, average 0.8 clears) and answers with
        // the value the row held at claim time, so the column stays out of the UPDATE and the row keeps
        // the date a mid-run save committed. The reconciliation then rewrites that row's raw copies from
        // the column — and pins its confidence to 1.0, because the value on screen is now the USER's, not
        // the model's, and an amber/rose outline over it would tell the user to double-check their own
        // typing about a value the model never produced.
        //
        // So this document commits ManualRequired + Distrusted with NOTHING outlined, and that is
        // correct rather than the ADR 0040 dead end: the row names itself with `✎ Manually edited` +
        // `was: …`, and the "Save changes" the card asks for runs ResolveManualReview, which clears the
        // flag because nothing is unreadable. Leaving the model's 0.5 on the row instead — the tidy-
        // looking way to keep the outline — is what this test refuses.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var seeded = FarFuture;
        var seededText = seeded.ToString("yyyy-MM-dd");
        var docId = await SeedQueuedDocWithExpirationAsync(
            auth.OrgId, vendorId, expirationColumn: seeded, expirationRaw: seededText);
        var corrected = seeded.AddDays(45);
        var correctedText = corrected.ToString("yyyy-MM-dd");

        Extraction.Result = ExtractedAt(
            "coi",
            ("general_liability_limit", "2000000", 0.95),
            ("expiration_date", seededText, 0.5),
            ("policy_number", "POL-1", 0.95));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "expiration_date", fieldValue = correctedText } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.ExpirationDate.Should().Be(corrected,
            "precondition: the model answered with the snapshot's own date, so the column stays out of "
            + "the UPDATE and the mid-run correction survives — the interleave the reconciliation exists "
            + "for");
        terminal.Doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired);
        terminal.Doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "the per-field gate fired on the READING (0.5 on a verdict-bearing field), and reconciling a "
            + "row's copies is not a reason to trust a reading the system already distrusts — this "
            + "trigger never consulted the basis and must not start");

        var body = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}");
        var data = body.GetProperty("data");
        data.GetProperty("unreadableFields").EnumerateArray().Should().BeEmpty(
            "nothing on this row is unparseable, so the readability trigger correctly names nothing — "
            + "`Distrusted` beside an EMPTY unreadableFields is the legitimate shape the other three "
            + "triggers commit by design (ADR 0052 Amendment 1)");
        data.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("confidence").GetDouble())
            .Should().OnlyContain(c => c >= 0.9,
                "…and after the reconciliation NOTHING on this document is outlined either (the page "
                + "returns no border class at or above 0.9), so the amber outline is not what names this "
                + "review. The record must say so — the naming mechanism here is the corrected row "
                + "itself");

        var rendered = await ReadRenderedFieldAsync(auth.Client, docId, "expiration_date");
        rendered.Row.Should().Be(correctedText);
        rendered.Mirror.Should().Be(correctedText);
        rendered.WasOriginally.Should().Be(seededText,
            "`was: …` is half of what names this row — the value the model read is still on screen, "
            + "beside the one the row will be graded from");
        rendered.ManuallyEdited.Should().BeTrue("…and `✎ Manually edited` is the other half");
        rendered.Confidence.Should().Be(1.0,
            "the value shown is the USER's, so it carries no reading's uncertainty. Restoring the "
            + "model's 0.5 here outlines the user's own typed date and prints \"Please verify\" about a "
            + "value the model never produced — a false statement about content, traded for an outline");

        var untouched = await ReadRenderedFieldAsync(auth.Client, docId, "policy_number");
        untouched.ManuallyEdited.Should().BeFalse();
        untouched.Confidence.Should().Be(0.95,
            "a field the reconciliation did not touch keeps the reading's own confidence — the pin is "
            + "scoped to reconciled rows, not a blanket 1.0");
    }

    [Fact]
    public async Task A_reconciled_AMOUNT_takes_the_columns_own_rendering_and_keeps_the_models_answer()
    {
        // #467 review round 2, S1: every reconcile fixture above uses expiration_date, so the AMOUNT
        // branch of SameTypedColumn — the one CanonicalDocumentFields' doc comment singles out as the
        // reason the comparison is on the TYPED value — had no integration coverage, and the rendering
        // its reconciliation lands in the field editor was an accident rather than a decision.
        //
        // Same interleave as the headline test, one column over: the model answers with the limit the
        // row held at claim time, so ApplyToTypedColumn's assignment matches the snapshot and the column
        // stays out of the UPDATE, while a mid-run correction owns what the row actually carries.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 1_000_000m);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "1000000"));
        Extraction.DuringExtract = async () =>
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
            {
                fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "2000000" } }
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(2_000_000m,
            "precondition: the worker's own value equalled its snapshot, so the column stayed out of the "
            + "UPDATE and the correction survives");
        terminal.Doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "2M clears the 1M floor on the value the row actually holds");

        var rendered = await ReadRenderedFieldAsync(auth.Client, docId, "general_liability_limit");
        rendered.Row.Should().Be("2000000.00",
            "the copies take the COLUMN's own rendering — the same string LookupValue compares and the "
            + "readability predicate reads — so what the field editor shows parses back to exactly the "
            + "amount being committed. The scale-2 tail is numeric(18,2)'s, and pinning it here makes it "
            + "a decision: change the rendering and this is what says the user-visible copy moved");
        rendered.Mirror.Should().Be("2000000.00");
        rendered.WasOriginally.Should().Be("1000000",
            "the model's own answer is demoted, not discarded");
        rendered.ManuallyEdited.Should().BeTrue();
        rendered.Confidence.Should().Be(1.0);
    }

    [Fact]
    public async Task A_field_whose_column_the_row_AGREES_on_is_left_exactly_as_the_model_read_it()
    {
        // #467 review round 2, S2 — the direction nothing asserted: what an ORDINARY re-extraction does
        // to a field the reconciliation must NOT touch. No interleave at all here; the row's committed
        // limit and the model's answer are the same number, so SameTypedColumn says "same" and the row
        // keeps the reading verbatim, unflagged, at the reading's own confidence.
        //
        // The discriminator this buys is the typed-vs-rendering property. Rewrite SameTypedColumn's
        // amount arm as a string comparison over TypedColumnValue and every #467 fixture stays green —
        // but the numeric(18,2) round-trip renders "2000000.00" where a freshly parsed answer renders
        // "2000000", so the reconciliation false-fires HERE: the money row is rewritten to the column's
        // rendering, flagged "✎ Manually edited", pinned to 1.0 and given a spurious `was: …`, on a
        // document nothing raced. (The pure form of the same property is
        // CanonicalDocumentFieldsTests.An_amount_compares_on_the_NUMBER_not_on_its_rendering.)
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId, "V", ("coi", "1000000"));
        var docId = await SeedQueuedDocAsync(auth.OrgId, vendorId, gl: 2_000_000m);

        Extraction.Result = Extracted("coi", ("general_liability_limit", "2000000"));

        await ClaimAndProcessAsync(docId);

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Doc.GeneralLiabilityLimit.Should().Be(2_000_000m);

        var rendered = await ReadRenderedFieldAsync(auth.Client, docId, "general_liability_limit");
        rendered.Row.Should().Be("2000000",
            "the row and the column agree, so there is nothing to reconcile and the model's own answer "
            + "stands exactly as read — not restated in the column's rendering");
        rendered.Mirror.Should().Be("2000000");
        rendered.WasOriginally.Should().BeNull(
            "…and there is no provenance to record, because nothing corrected this value");
        rendered.ManuallyEdited.Should().BeFalse(
            "a machine read is not a manual edit. The page prints `✎ Manually edited` off this flag and "
            + "the detail page counts these fields in its \"Read again will discard N corrections\" "
            + "warning — a false one warns the user about losing work nobody did");
        rendered.Confidence.Should().Be(0.95,
            "…and the reading's own confidence survives, so the outline still fires for a field the "
            + "model really was unsure of");
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
