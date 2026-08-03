using System.Globalization;
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
/// Regression tests for the BATCHED re-grade fan-out's read → compute → write window
/// (<see href="https://github.com/neboxdev/complidrop/issues/470">#470</see>, ADR 0030 Amendment 5).
/// <para/>
/// <c>ComplianceCheckService.ReevaluateWhereAsync</c> loads a page, grades it and saves it under
/// <c>READ COMMITTED</c>, so a <c>PUT /api/documents/{id}/fields</c> committing inside that span leaves its
/// document holding the EDITED inputs beside a verdict graded from the pre-edit ones, with
/// <see cref="ComplianceCheck"/> rows citing values the row no longer holds. Same terminal state as #461's
/// single-document re-grade, in the direction that matters — an affirmative verdict standing over inputs
/// somebody just lowered — and nothing heals it (the nightly sweep does date transitions only).
/// <para/>
/// The fix is NOT the guard #461 took. A page is up to <c>DefaultReevaluationPageSize</c> documents committed
/// as ONE unit, so <c>REPEATABLE READ</c> + re-run would abandon the whole page over one conflicting edit and
/// then skip it (Amendment 3 Option K, refuted). Instead the page commits exactly as before and is then
/// VERIFIED: re-read, re-graded, and only the documents whose verdict no longer follows from their inputs are
/// written again. So the two hard acceptance criteria are structural — one edit costs its own document a
/// second re-grade and the rest of the page nothing, and no caller gains a 409 to answer.
/// <para/>
/// Every interleave here is CONSTRUCTED, not raced: the competing edit is a real HTTP request driven to
/// completion from inside the fan-out page's own <c>SaveChanges</c> hook
/// (<see cref="ConcurrentDocumentWriteInterceptor"/>), so "somebody committed between the page's read and its
/// UPDATE" happens on every run rather than on a lucky one. The trigger is the production one: a rule upsert,
/// whose own remarks call a document left on its pre-edit verdict "a genuine false Compliant".
/// </summary>
public sealed class ComplianceFanoutStaleVerdictTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private const string RequiredGl = "2000000";
    private const decimal PassingGl = 3_000_000m;
    private const string FailingGl = "1000000";

    /// <summary>
    /// A SECOND passing limit. It exists so one interleave can move the evidence without moving the
    /// headline: both this and <see cref="PassingGl"/> clear <see cref="RequiredGl"/>, so
    /// <c>OutcomeMatches</c>' status comparison cannot short-circuit and the CHECK-TUPLE half has to do the
    /// detecting. Every other value in this suite crosses the floor.
    /// </summary>
    private const decimal PartialGl = 2_500_000m;

    /// <summary>
    /// The information line <c>VerifyPageAsync</c> emits when it finds a document whose inputs moved. Every
    /// interleave test asserts it, and that is an ANTI-NO-OP discriminator rather than log-watching for its
    /// own sake: without it a test whose competing write landed OUTSIDE the window (before the page's read,
    /// say) would find a perfectly consistent terminal row and go green having reproduced nothing.
    /// </summary>
    private const string MoverLine = "whose inputs changed while the page was being graded";

    /// <summary>The warning <c>VerifyPageAsync</c> emits when the bound is spent and something is still moving.</summary>
    private const string GaveUpLine = "Gave up confirming";

    // No DisposeAsync reset, unlike DocumentConcurrentEditTests: every test here arms the hook on its OWN
    // WithWebHostBuilder host, whose singleton dies with that host at the end of the test, and disarms it in
    // a finally either way. Resetting the SHARED fixture's instance would reset one this suite never armed.

    // ---------------------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------------------

    private sealed record Fixed(HttpClient Client, Guid OrgId, Guid TemplateId, Guid VendorId, Guid RuleId);

    /// <summary>
    /// A registered org whose vendor sits on a one-rule checklist ("general liability ≥ 2M", COI-typed).
    /// <paramref name="client"/> comes from the per-test host so the verification pass's log lines land in
    /// that test's own sink.
    /// </summary>
    private async Task<Fixed> SeedAsync(HttpClient client)
    {
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"user-{Guid.NewGuid():N}@example.com",
            password = "Password1234",
            fullName = "Test User",
            companyName = "Test Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        reg.EnsureSuccessStatusCode();
        var orgId = (await reg.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("organizationId").GetGuid();

        var now = DateTime.UtcNow;
        var templateId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();

        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate
        {
            Id = templateId, OrganizationId = orgId, Name = $"T-{templateId:N}", CreatedAt = now
        });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = "V", ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now
        });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = ruleId, ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = RequiredGl,
            SortOrder = 0
        });
        await db.SaveChangesAsync();

        return new Fixed(client, orgId, templateId, vendorId, ruleId);
    }

    /// <summary>
    /// A COI on the checklist, editable through <c>PUT /documents/{id}/fields</c> (so it carries both copies
    /// of the canonical input the edit moves: the typed column and the JSON mirror, plus the field row).
    /// <paramref name="stored"/> is the verdict the row starts on — seed a WRONG one and a page that never
    /// re-graded becomes visible.
    /// </summary>
    private async Task<Guid> SeedDocAsync(Guid orgId, Guid vendorId, decimal gl, ComplianceStatus stored)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "coi.pdf",
            BlobStorageUrl = "blob://d",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed,
            ComplianceStatus = stored,
            GeneralLiabilityLimit = gl,
            ExtractionFields = JsonDocument.Parse($$"""{"general_liability_limit":"{{gl:0}}"}"""),
            // Far future: the verdict is purely rule-driven, never date-driven.
            ExpirationDate = now.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.DocumentFields.Add(new DocumentField
        {
            Id = Guid.NewGuid(), DocumentId = docId, FieldName = "general_liability_limit",
            FieldValue = $"{gl:0}", FieldType = "text", Confidence = 0.95
        });
        await db.SaveChangesAsync();
        return docId;
    }

    /// <summary>The production trigger for this fan-out: re-saving the checklist's rule.</summary>
    private static Task<HttpResponseMessage> UpsertRuleAsync(Fixed f) =>
        f.Client.PostAsJsonAsync($"/api/compliance/templates/{f.TemplateId}/rules", new
        {
            id = f.RuleId,
            documentType = "coi",
            fieldName = "general_liability_limit",
            @operator = "min_value",
            expectedValue = RequiredGl,
            errorMessage = (string?)null,
            sortOrder = 0,
        });

    private static Task<HttpResponseMessage> EditGlAsync(HttpClient client, Guid docId, string value) =>
        client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = value } }
        });

    private sealed record TerminalState(ComplianceStatus Status, decimal? Gl, IReadOnlyList<decimal> CitedLimits);

    /// <summary>
    /// The row's stored verdict, its limit, and the limits its <see cref="ComplianceCheck"/> rows CITE.
    /// <para/>
    /// The cited amount is PARSED rather than string-compared, for the reason
    /// <c>DocumentConcurrentEditTests.ReadCitedLimitsAsync</c> gives: <c>EvaluateRule</c> cites the typed
    /// column through <c>decimal.ToString</c>, so a row written from a fresh read of the
    /// <c>numeric(18,2)</c> column reads "1000000.00" while one written by the edit that parsed "1000000" in
    /// memory reads "1000000". The fact under test is which AMOUNT the row cites.
    /// </summary>
    private async Task<TerminalState> ReadTerminalStateAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        var cited = await db.ComplianceChecks.AsNoTracking()
            .Where(c => c.DocumentId == docId).Select(c => c.ActualValue).ToListAsync();
        return new TerminalState(
            doc.ComplianceStatus,
            doc.GeneralLiabilityLimit,
            [.. cited.Select(v => decimal.Parse(v!, CultureInfo.InvariantCulture))]);
    }

    /// <summary>How many times a document has been WRITTEN — the audit trail counts every save that touched it.</summary>
    private async Task<int> DocumentWriteCountAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        return await db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.EntityType == nameof(Document) && a.EntityId == docId && a.Action == "document.updated");
    }

    /// <summary>
    /// A QUEUED (<c>Pending</c>) COI with its blob actually stored, so the extraction worker can claim and
    /// process it for real. <paramref name="host"/> is the per-test host: the blob fake is a HOST singleton,
    /// so the bytes have to land in the store the worker under test will read from.
    /// </summary>
    private async Task<Guid> SeedQueuedDocAsync(IServiceProvider host, Guid orgId, Guid vendorId, decimal gl)
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
                ComplianceStatus = ComplianceStatus.Pending,
                GeneralLiabilityLimit = gl,
                ExtractionFields = JsonDocument.Parse($$"""{"general_liability_limit":"{{gl:0}}"}"""),
                ExpirationDate = now.AddYears(1),
                CreatedAt = now,
                UpdatedAt = now
            });
            db.DocumentFields.Add(new DocumentField
            {
                Id = Guid.NewGuid(), DocumentId = docId, FieldName = "general_liability_limit",
                FieldValue = $"{gl:0}", FieldType = "currency", Confidence = 0.95
            });
            await db.SaveChangesAsync();
        }

        await host.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        return docId;
    }

    /// <summary>
    /// One PRIOR check row against the fixture's rule. It is the precondition for the doubled-set residue
    /// (ADR 0030 § Consequences, Amendment 4): both writers materialize the SAME row and stage a per-row
    /// DELETE of it, so whichever commits first leaves the other's DELETE matching nothing — tolerated
    /// since #468 — and both writers' INSERTs then stand. A document with no prior rows has nothing for
    /// either DELETE to miss.
    /// </summary>
    private async Task SeedPriorCheckAsync(Guid docId, Guid ruleId, decimal cited)
    {
        await using var db = CreateSystemDb();
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = Guid.NewGuid(), DocumentId = docId, ComplianceRuleId = ruleId,
            IsPassed = true, ActualValue = $"{cited:0}", CheckedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The two columns the extraction worker OWNS — neither is the verification pass's to write.</summary>
    private async Task<(ExtractionStatus Status, ExtractionTrust Trust)> ReadExtractionStateAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        var doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        return (doc.ExtractionStatus, doc.ExtractionTrust);
    }

    private static IEnumerable<string> Messages(CapturingLogEventSink sink) =>
        sink.Events.Select(e => e.RenderMessage());

    // ---------------------------------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_field_edit_committing_inside_a_page_leaves_no_stale_verdict()
    {
        // #470's terminal state, constructed. The page grades the document at 3M (clears the 2M floor →
        // Compliant); the user lowers it to 1M and commits its own consistent (inputs, verdict) pair; the
        // page's UPDATE — which carries only ComplianceStatus / UpdatedAt — then lands on top. Before the
        // verification pass the row held a stored Compliant beside a 1M limit, with a check row citing the
        // 3M it had just lost, and nothing scheduled another evaluation of it.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var f = await SeedAsync(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var docId = await SeedDocAsync(f.OrgId, f.VendorId, PassingGl, ComplianceStatus.Pending);

        // The rule upsert saves the RULE on this same context first, so the SECOND firing is the fan-out
        // page's own SaveChanges — after ApplyEvaluationsAsync graded the page and staged its writes,
        // before EF issues the batch. Editing on the first would land BEFORE the page's read, where the
        // fan-out simply grades the new value and there is no window to reproduce.
        var hook = factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();
        var saves = 0;
        var competingWrites = 0;
        hook.OnSavingChanges = async () =>
        {
            if (++saves != 2) return;
            competingWrites++;
            (await EditGlAsync(f.Client, docId, FailingGl)).EnsureSuccessStatusCode();
        };

        HttpResponseMessage resp;
        try
        {
            resp = await UpsertRuleAsync(f);
        }
        finally
        {
            hook.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the fan-out runs POST-COMMIT on a background token with no user to answer a 409 to — a "
            + "conflicting edit must never turn the triggering mutation into an error");
        competingWrites.Should().Be(1, "precondition: the edit landed inside the page's own SaveChanges");
        Messages(sink).Should().Contain(m => m.Contains(MoverLine, StringComparison.Ordinal),
            "precondition: the page really did commit a verdict its inputs no longer imply — without this "
            + "the assertions below would pass on a run that never reproduced the window");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Gl.Should().Be(1_000_000m, "the user's edit is the winner of the input write, as ADR 0017 says");
        terminal.Status.Should().Be(ComplianceStatus.NonCompliant,
            "the persisted verdict must be what the persisted inputs grade to — a stored Compliant over a "
            + "limit somebody just lowered is #337's state, reached through the fan-out");
        terminal.CitedLimits.Should().Equal([1_000_000m],
            "and the check rows must cite the value the row actually holds, not the one the page graded");
    }

    [Fact]
    public async Task An_edit_that_keeps_the_verdict_but_moves_the_evidence_is_still_corrected()
    {
        // The half of the detection the STATUS comparison cannot reach, and the only shape in this suite
        // that exercises it. Every other interleave crosses the 2M floor, so `fresh.Status != applied.Status`
        // is already true on line one of OutcomeMatches and the check-tuple comparison below it is never
        // consulted in a mover case. Here BOTH limits pass: the badge is Compliant before and after, while
        // the check row — the evidence an auditor reads, and what the detail page's explainer renders —
        // would be left asserting "we measured $3,000,000" against a row holding $2,500,000.
        //
        // A fan-out that compared only the status would leave exactly that and this suite would stay green.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var f = await SeedAsync(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var docId = await SeedDocAsync(f.OrgId, f.VendorId, PassingGl, ComplianceStatus.Pending);

        // Pinned rather than assumed: if a future edit to these constants made the competing value cross the
        // floor, this test would silently decay into a duplicate of the first one.
        var floor = decimal.Parse(RequiredGl, CultureInfo.InvariantCulture);
        PassingGl.Should().BeGreaterThan(floor);
        PartialGl.Should().BeGreaterThan(floor, "the whole point is that the STATUS does not move");

        var hook = factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();
        var saves = 0;
        var competingWrites = 0;
        hook.OnSavingChanges = async () =>
        {
            if (++saves != 2) return;
            competingWrites++;
            (await EditGlAsync(f.Client, docId, $"{PartialGl:0}")).EnsureSuccessStatusCode();
        };

        HttpResponseMessage resp;
        try
        {
            resp = await UpsertRuleAsync(f);
        }
        finally
        {
            hook.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        competingWrites.Should().Be(1, "precondition: the edit landed inside the page's own SaveChanges");
        Messages(sink).Should().Contain(m => m.Contains(MoverLine, StringComparison.Ordinal),
            "the page's assertion about this document moved even though its verdict did not — a fan-out "
            + "that stopped at the status would find nothing here");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Gl.Should().Be(PartialGl, "the user's edit is the winner of the input write");
        terminal.Status.Should().Be(ComplianceStatus.Compliant,
            "both limits clear the floor, so the headline verdict is the same before and after — which is "
            + "exactly why the status comparison alone cannot detect this");
        terminal.CitedLimits.Should().Equal([PartialGl],
            "the check row must cite the value the row actually holds. Left uncorrected it would cite "
            + "$3,000,000 beside a $2,500,000 limit — one row, one rule, and the two disagree");
    }

    [Fact]
    public async Task A_competing_PURE_re_grade_leaves_the_doubled_check_rows_in_place()
    {
        // The boundary of what the verification pass is, pinned so the record cannot drift back into
        // claiming more (ADR 0030 Amendment 5 § What stays open). OutcomeMatches compares two IN-MEMORY
        // EvaluationOutcomes and never reads a stored ComplianceCheck row, so the doubled set left by a
        // competing clear-and-replace is INVISIBLE to it. A pure re-grade ("Check again") is the case that
        // makes that observable: it rewrites the check rows while moving no verdict INPUT, so the fresh
        // grade equals the one the page applied, nothing is a mover, nothing is written — and the document
        // is left holding both writers' rows until its next evaluation.
        //
        // That is the residue Amendment 4 accepted, not a new defect: the two read sites that turn these
        // rows into an assertion are already reconciled (the export counts DISTINCT rules; the detail page's
        // renderedChecks collapses them against the standing verdict).
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var f = await SeedAsync(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var docId = await SeedDocAsync(f.OrgId, f.VendorId, PassingGl, ComplianceStatus.Pending);
        await SeedPriorCheckAsync(docId, f.RuleId, PassingGl);

        var hook = factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();
        var saves = 0;
        var competingWrites = 0;
        hook.OnSavingChanges = async () =>
        {
            if (++saves != 2) return;
            competingWrites++;
            // The "Check again" button: a pure re-grade that writes NO input (ADR 0030 Amendment 3).
            var recheck = await f.Client.PostAsync($"/api/compliance/check/{docId}", content: null);
            recheck.EnsureSuccessStatusCode();
        };

        HttpResponseMessage resp;
        try
        {
            resp = await UpsertRuleAsync(f);
        }
        finally
        {
            hook.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        competingWrites.Should().Be(1, "precondition: the re-grade committed inside the page's own SaveChanges");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Gl.Should().Be(PassingGl, "a pure re-grade owns no inputs, so nothing about the row moved");
        terminal.Status.Should().Be(ComplianceStatus.Compliant);
        terminal.CitedLimits.Should().HaveCount(2,
            "both writers cleared the same prior row and inserted their own; the loser's DELETE matched "
            + "nothing and was tolerated (#468), so the document transiently holds BOTH sets");
        terminal.CitedLimits.Should().OnlyContain(v => v == PassingGl);

        Messages(sink).Should().NotContain(m => m.Contains(MoverLine, StringComparison.Ordinal),
            "and the verification pass did NOT see it: its comparison is between two computed outcomes, "
            + "never against the stored rows, so a writer that rewrites check rows without moving a verdict "
            + "INPUT produces an identical fresh grade and no mover. The doubled set is cleared by the "
            + "document's next evaluation, not by this pass");
    }

    [Fact]
    public async Task An_extraction_persist_committing_inside_a_page_leaves_no_stale_verdict()
    {
        // The collider the Amendment 5 measurement calls DECISIVE, constructed. #470's own framing was that
        // the triggering mutations are rare and admin-initiated, so two humans colliding is negligible — but
        // ExtractionWorker polls every 5s and PersistSuccess commits canonical inputs + verdict in one unit
        // of work, so an org with a single portal upload in flight collides with no second person existing.
        // A rule edit while one document is being extracted is an ordinary Tuesday.
        //
        // It also pins the SCOPE of the correction: the verification writes the verdict and its check rows,
        // and nothing else. The worker owns ExtractionStatus + ExtractionTrust (ADR 0052) and a correction
        // pass that clobbered either would be a real regression this suite must see, not one only the ADR
        // 0052 suites would catch.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var f = await SeedAsync(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var docId = await SeedQueuedDocAsync(factory.Services, f.OrgId, f.VendorId, PassingGl);

        // The extraction LOWERS the limit below the floor — the direction that matters, since the page is
        // about to commit a Compliant graded from the 3M the row is losing.
        var extraction = factory.Services.GetRequiredService<FakeExtractionClient>();
        extraction.Result = new ExtractionResult(
            DocumentType: "coi",
            DocumentSubType: null,
            Fields: [new ExtractedField("general_liability_limit", FailingGl, "currency", 0.95)],
            NeedsReprocessing: false,
            Usage: new ExtractionUsage(InputTokens: 1000, OutputTokens: 200, EstimatedCostUsd: 0.01m));

        var hook = factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();
        var saves = 0;
        var competingWrites = 0;
        hook.OnSavingChanges = async () =>
        {
            if (++saves != 2) return;
            competingWrites++;
            // A REAL claim + process on the same database, driven to completion inside the page's window —
            // the worker is a background service, so there is no request to fire at it.
            var worker = new ExtractionWorker(
                factory.Services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ExtractionSettings()),
                NullLogger<ExtractionWorker>.Instance);
            var claimed = await worker.ClaimNextAsync(CancellationToken.None);
            claimed.Should().Be(docId, "precondition: the queued document is the one the worker picks up");
            await worker.ProcessDocumentAsync(docId, CancellationToken.None);
        };

        HttpResponseMessage resp;
        try
        {
            resp = await UpsertRuleAsync(f);
        }
        finally
        {
            hook.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        competingWrites.Should().Be(1, "precondition: the persist committed inside the page's own SaveChanges");
        extraction.ExtractCallCount.Should().Be(1, "…and it really was an extraction, not a no-op claim");
        Messages(sink).Should().Contain(m => m.Contains(MoverLine, StringComparison.Ordinal),
            "precondition: the page committed a verdict the persist's inputs no longer imply");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Gl.Should().Be(1_000_000m, "the extraction is the winner of the input write (ADR 0017)");
        terminal.Status.Should().Be(ComplianceStatus.NonCompliant,
            "the persisted verdict must be what the persisted inputs grade to — the page's Compliant was "
            + "graded from the 3M this persist overwrote");
        terminal.CitedLimits.Should().Equal([1_000_000m]);

        var extractionState = await ReadExtractionStateAsync(docId);
        extractionState.Status.Should().Be(ExtractionStatus.Completed,
            "the correction writes the verdict and its check rows, never a column the worker owns — "
            + "re-arming the queue here would re-pay Document AI + the LLM");
        extractionState.Trust.Should().Be(ExtractionTrust.Trusted,
            "and never ExtractionTrust either (ADR 0052): this pass reached no conclusion about the "
            + "document's basis, so it must not overwrite the one the persist did reach");
    }

    [Fact]
    public async Task One_conflicting_edit_re_grades_only_its_own_document_and_forfeits_no_others()
    {
        // The hard acceptance criterion, and the whole reason this is not Amendment 1's guard: under
        // REPEATABLE READ + re-run, ONE conflicting edit anywhere in a page abandons and re-runs the WHOLE
        // page up to MaxAttempts and then skips it — forfeiting every unrelated re-grade in it. Here the
        // page commits, and the correction is scoped to the one document that moved.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var f = await SeedAsync(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));

        var edited = await SeedDocAsync(f.OrgId, f.VendorId, PassingGl, ComplianceStatus.Pending);
        // Two bystanders on the same vendor, so all three ride in one page. Both are seeded with a
        // deliberately WRONG stored verdict, so a page whose re-grade was forfeited is visible as a stale
        // Compliant on a document that fails the 2M floor.
        var bystanderA = await SeedDocAsync(f.OrgId, f.VendorId, 1_000_000m, ComplianceStatus.Compliant);
        var bystanderB = await SeedDocAsync(f.OrgId, f.VendorId, 1_000_000m, ComplianceStatus.Compliant);

        var hook = factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();
        var saves = 0;
        hook.OnSavingChanges = async () =>
        {
            if (++saves != 2) return;
            (await EditGlAsync(f.Client, edited, FailingGl)).EnsureSuccessStatusCode();
        };

        HttpResponseMessage resp;
        try
        {
            resp = await UpsertRuleAsync(f);
        }
        finally
        {
            hook.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        Messages(sink).Should().Contain(m => m.Contains(MoverLine, StringComparison.Ordinal),
            "precondition: the interleave landed, so the anti-forfeit claim below is about a real conflict");
        Messages(sink).Should().NotContain(m => m.Contains("Re-evaluation fan-out failed for a page", StringComparison.Ordinal),
            "the page must not reach its own catch — that arm is where a forfeited page hides");

        foreach (var bystander in new[] { bystanderA, bystanderB })
        {
            var state = await ReadTerminalStateAsync(bystander);
            state.Status.Should().Be(ComplianceStatus.NonCompliant,
                "a conflicting edit on a NEIGHBOUR must not cost this document its re-grade");
            state.CitedLimits.Should().Equal([1_000_000m], "…including its check row");
            (await DocumentWriteCountAsync(bystander)).Should().Be(1,
                "and it must not be re-graded a second time either: the verification pass re-grades the "
                + "LOSERS, not the page");
        }

        (await ReadTerminalStateAsync(edited)).Status.Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task A_page_nobody_touched_is_confirmed_without_a_second_write()
    {
        // The cost side of the decision, pinned. Verification re-READS every page, so the common case must
        // read and write NOTHING: a pass that rewrote confirmed documents would double this fan-out's write
        // volume, churn every document's UpdatedAt and audit trail, and — since each rewrite is itself
        // graded from a fresh read — hand the next pass something new to disagree with.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var f = await SeedAsync(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var passing = await SeedDocAsync(f.OrgId, f.VendorId, PassingGl, ComplianceStatus.Pending);
        var failing = await SeedDocAsync(f.OrgId, f.VendorId, 1_000_000m, ComplianceStatus.Compliant);

        (await UpsertRuleAsync(f)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The re-grade itself still happened — otherwise "no second write" would be satisfied by a fan-out
        // that did nothing at all.
        (await ReadTerminalStateAsync(passing)).Status.Should().Be(ComplianceStatus.Compliant);
        (await ReadTerminalStateAsync(failing)).Status.Should().Be(ComplianceStatus.NonCompliant);

        foreach (var docId in new[] { passing, failing })
            (await DocumentWriteCountAsync(docId)).Should().Be(1,
                "the page's own write, and nothing after it: a verification pass that finds the row saying "
                + "exactly what this fan-out graded must write nothing");

        Messages(sink).Should().NotContain(m => m.Contains(MoverLine, StringComparison.Ordinal),
            "nothing moved, so nothing is a mover");
        Messages(sink).Should().NotContain(m => m.Contains(GaveUpLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_whose_inputs_keep_moving_is_left_with_the_last_verdict_and_warned()
    {
        // The bound, and the answer to "what happens to a document that keeps moving". Every one of this
        // fan-out's saves — the page's and both corrections' — is beaten by another edit, so the
        // verification can never confirm anything. It must STOP (an unbounded loop on a background token,
        // against a user who keeps typing, is worse than the stale verdict it chases), leave the last
        // verdict it computed rather than degrading it to Pending (ADR 0030 Amendment 3 Option I, refuted
        // for a caller that owns no inputs), and SAY so.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var f = await SeedAsync(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        var docId = await SeedDocAsync(f.OrgId, f.VendorId, PassingGl, ComplianceStatus.Pending);

        // Alternating values, so every pass's fresh grade disagrees with the one before it: the page grades
        // 3M and the edit drops to 1M; correction 1 grades 1M and the edit raises it to 3M; correction 2
        // grades 3M and the edit drops it to 1M again.
        var hook = factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();
        var saves = 0;
        var competingWrites = 0;
        hook.OnSavingChanges = async () =>
        {
            if (++saves < 2) return;
            var next = competingWrites++ % 2 == 0 ? FailingGl : $"{PassingGl:0}";
            (await EditGlAsync(f.Client, docId, next)).EnsureSuccessStatusCode();
        };

        HttpResponseMessage resp;
        try
        {
            resp = await UpsertRuleAsync(f);
        }
        finally
        {
            hook.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        competingWrites.Should().Be(1 + ComplianceCheckService.MaxVerificationPasses,
            "the page's save plus one per verification pass, and then it STOPS — a fan-out that kept "
            + "re-grading while the document kept moving would spin on a background token forever");

        Messages(sink).Should().Contain(m => m.Contains(GaveUpLine, StringComparison.Ordinal),
            "an unconfirmed verdict left behind is an event somebody must be able to find");
        Messages(sink).Should().Contain(
            m => m.Contains(GaveUpLine, StringComparison.Ordinal)
                && m.Contains(docId.ToString(), StringComparison.OrdinalIgnoreCase),
            "…and 'find' means the DOCUMENT, not a count: the ids are in hand, so the line names them "
            + "(ids only — never an ActualValue or a Notes string, which are document content). Both "
            + "fan-out failure lines go through the same bounded ComplianceCheckService.IdSample");

        var terminal = await ReadTerminalStateAsync(docId);
        terminal.Gl.Should().Be(1_000_000m, "the last edit is the winner of the input write");
        terminal.Status.Should().NotBe(ComplianceStatus.Pending,
            "the bound being spent must not DEGRADE the verdict: this caller owns no inputs, so a Pending "
            + "write would replace a possibly-correct verdict with a non-committal one through the very "
            + "write that kept losing (ADR 0030 Amendment 3 Option I)");
        terminal.CitedLimits.Should().NotBeEmpty(
            "giving up leaves the last correction's evidence in place — it does not strip the document's "
            + "explainer rows on the way out. Not ContainSingle: this row holds BOTH writers' check rows, "
            + "which is ADR 0030 § Consequences' recorded display desync (each writer cleared what it could "
            + "see and inserted its own), tolerated since Amendment 4 and cleared by the next evaluation");
        // The exact verdict left behind is deliberately NOT asserted: it is whatever the last correction
        // graded, so it flips with the PARITY of MaxVerificationPasses. What the record claims — and what
        // is asserted above — is that the fan-out stops, does not degrade, and says so.
    }
}
