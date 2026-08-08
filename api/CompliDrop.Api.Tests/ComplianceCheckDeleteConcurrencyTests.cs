using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The mechanism behind <see href="https://github.com/neboxdev/complidrop/issues/468">#468</see>
/// (ADR 0030 § Consequences): a <see cref="ComplianceCheck"/> DELETE that affects zero rows is a SUCCESS,
/// because the only thing that makes it match nothing is another writer's re-grade having already removed
/// the row — which is exactly what this delete wanted.
/// <para/>
/// <c>ComplianceCheckService.ApplyEvaluationCoreAsync</c> clears a document's checks by materializing them
/// and staging a <c>RemoveRange</c>, so EF emits a per-row DELETE keyed on the primary key and demands one
/// row each. <c>ExtractionWorkerStaleBasisTests.A_regrade_that_deletes_the_check_rows_this_persist_staged_
/// costs_no_extraction</c> pins what that used to cost on the writer that matters (a throw out of
/// <c>PersistSuccess</c> — since #375 a counted failure plus a re-paid Document AI + LLM run per
/// retry, bounded by <c>MaxAttempts</c>; pre-#375 the catch's re-save on the same dirty context
/// turned it into a five-minutely re-paid run). This suite pins the MECHANISM instead — its two ends:
/// <list type="bullet">
/// <item>the tolerance itself, on BOTH contexts, since the rule is a property of the row and not of the
/// caller — and the request-path context cannot be reached behaviourally through its own writers, whose
/// <c>REPEATABLE READ</c> guard turns the same interleave into a <c>40001</c> retry;</item>
/// <item>its SCOPE, in both directions a widening could take it — a check-row UPDATE that matches nothing
/// still throws, and so does a delete of anything that is not a check row. Those counts carry real
/// information; only the check row's does not.</item>
/// </list>
/// <para/>
/// The last test is the ATOMICITY pin, and it is the reason this ticket did NOT take the other candidate
/// fix. A set-based <c>ExecuteDeleteAsync</c> clear is also insensitive to the row count, but it issues its
/// own statement immediately and joins the caller's transaction only when one is explicitly open — and two
/// callers have none, so the clear would commit separately from the inserts and the verdict, splitting the
/// unit of work ADR 0030 exists to keep whole.
/// </summary>
public sealed class ComplianceCheckDeleteConcurrencyTests(IntegrationTestFixture fixture)
    : IntegrationTestBase(fixture)
{
    /// <summary>
    /// A document with one vendor + checklist rule governing it, plus one existing
    /// <see cref="ComplianceCheck"/> row — the shape every clear in this codebase starts from.
    /// </summary>
    private async Task<(Guid DocId, Guid CheckId, Guid RuleId)> SeedCheckedDocumentAsync(Guid orgId)
    {
        var now = DateTime.UtcNow;
        var templateId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var checkId = Guid.NewGuid();

        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate
        {
            Id = templateId, OrganizationId = orgId, Name = $"T-{templateId:N}", CreatedAt = now
        });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = ruleId, ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "1000000",
            SortOrder = 0,
        });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = "V", ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now,
        });
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "coi.pdf",
            BlobStorageUrl = "blob://d",
            BlobStoragePath = $"blob/{docId:N}.pdf",
            FileSizeBytes = 1024,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed,
            ComplianceStatus = ComplianceStatus.Compliant,
            GeneralLiabilityLimit = 2_000_000m,
            ExtractionFields = JsonDocument.Parse("""{"general_liability_limit":"2000000"}"""),
            ExpirationDate = now.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = checkId, DocumentId = docId, ComplianceRuleId = ruleId, IsPassed = true, CheckedAt = now,
        });
        await db.SaveChangesAsync();
        return (docId, checkId, ruleId);
    }

    /// <summary>Hard-deletes a row out from under a writer that is already tracking it.</summary>
    private async Task DeleteCheckRowElsewhereAsync(Guid checkId)
    {
        await using var other = CreateSystemDb();
        await other.ComplianceChecks.Where(c => c.Id == checkId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// <paramref name="count"/> further check rows against the same rule — the shape one document's clear
    /// stages when a checklist has several requirements, minted here against ONE rule because nothing
    /// constrains that pair (ADR 0030 Amendment 4 Option O declines the unique index) and the rule is not
    /// what this suite is about.
    /// </summary>
    private async Task<List<Guid>> AddCheckRowsAsync(Guid docId, Guid ruleId, int count)
    {
        await using var db = CreateSystemDb();
        List<Guid> ids = [];
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.ComplianceChecks.Add(new ComplianceCheck
            {
                Id = id, DocumentId = docId, ComplianceRuleId = ruleId,
                IsPassed = true, CheckedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    [Theory]
    // EF dispatches the suppression to the override matching the SaveChanges the caller made, so both
    // halves have to be real. Production writes here are all async; a sync one would otherwise keep
    // throwing, and nothing else in the suite would notice.
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_check_row_another_writer_already_deleted_does_not_fail_the_save(bool asynchronous)
    {
        var auth = await RegisterAndLoginAsync();
        var (docId, checkId, ruleId) = await SeedCheckedDocumentAsync(auth.OrgId);

        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var stale = await db.ComplianceChecks.FirstAsync(c => c.Id == checkId);

        await DeleteCheckRowElsewhereAsync(checkId);

        // The clear + the replacement, exactly as ApplyEvaluationCoreAsync stages them: one unit of work
        // carrying both, with the delete now matching nothing.
        db.ComplianceChecks.Remove(stale);
        var replacementId = Guid.NewGuid();
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = replacementId, DocumentId = docId, ComplianceRuleId = ruleId,
            IsPassed = false, CheckedAt = DateTime.UtcNow,
        });

        var act = async () =>
        {
            if (asynchronous) await db.SaveChangesAsync();
            else db.SaveChanges();
        };

        await act.Should().NotThrowAsync(
            "the row this writer wanted gone IS gone — a delete that matched nothing is the outcome it "
            + "asked for, and on ExtractionWorker.PersistSuccess the throw costs a re-paid OCR + LLM run "
            + "on every zombie reclaim (#468)");

        await using var read = CreateSystemDb();
        (await read.ComplianceChecks.AsNoTracking().Where(c => c.DocumentId == docId).Select(c => c.Id)
            .ToListAsync())
            .Should().Equal([replacementId],
                "suppressing the exception must not abandon the unit of work — the rest of the batch, "
                + "including the replacement check row, still commits");

        db.Entry(stale).State.Should().Be(EntityState.Detached,
            "a suppressed save still COMPLETES, so EF accepts the changes and the deleted entry leaves the "
            + "tracker — an entry left Deleted would be re-attempted by the next SaveChanges on this "
            + "context, which is how a tolerated delete could still poison a later write");
    }

    [Fact]
    public async Task The_same_tolerance_applies_on_the_request_path_context()
    {
        // The rule is a property of the ROW, so it is registered on both contexts — and this is the only
        // way to see the AppDbContext registration. Its own check-row writers (UpdateFields,
        // UpdateDocument, RunCheck) run under REPEATABLE READ, where a competing commit surfaces as 40001
        // and never as a rows-affected mismatch; the one AppDbContext writer that CAN reach this is the
        // batched re-grade fan-out, whose page-sized interleave is not constructible deterministically.
        // Without this test a dropped registration there would be invisible.
        //
        // ComplianceCheck deliberately carries no tenant query filter (it is reached through its
        // Document), so a scope-resolved AppDbContext can hold one without an org-scoped principal.
        var auth = await RegisterAndLoginAsync();
        var (docId, checkId, ruleId) = await SeedCheckedDocumentAsync(auth.OrgId);

        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stale = await db.ComplianceChecks.FirstAsync(c => c.Id == checkId);

        await DeleteCheckRowElsewhereAsync(checkId);

        db.ComplianceChecks.Remove(stale);
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = Guid.NewGuid(), DocumentId = docId, ComplianceRuleId = ruleId,
            IsPassed = false, CheckedAt = DateTime.UtcNow,
        });

        var act = () => db.SaveChangesAsync();

        await act.Should().NotThrowAsync(
            "the request-path context clears check rows too — through the batched re-grade fan-out, which "
            + "keeps READ COMMITTED and so can reach the same zero-row delete (#468)");
    }

    [Fact]
    public async Task A_context_built_by_the_harness_helper_tolerates_it_too()
    {
        // IntegrationTestBase.CreateSystemDb / CreateAppDb wire the interceptors Program.cs wires, so a
        // context built through them behaves like production (#468 review S1). Without that, the two SCOPE
        // tests below would pass identically with the interceptor deleted from Program.cs the moment
        // someone rewrote them through the ubiquitous helper — and a future reproduction of this bug driven
        // through it would see the pre-#468 throw and conclude the bug is still live. The mechanical
        // forcing function for the NEXT interceptor is
        // HarnessSmokeTests.The_db_helpers_wire_every_save_interceptor_the_application_wires; this is the
        // behavioural half, and it is the helper's own semantics under test rather than the DI container's.
        var auth = await RegisterAndLoginAsync();
        var (docId, checkId, ruleId) = await SeedCheckedDocumentAsync(auth.OrgId);

        await using var db = CreateSystemDb();
        var stale = await db.ComplianceChecks.FirstAsync(c => c.Id == checkId);

        await DeleteCheckRowElsewhereAsync(checkId);

        db.ComplianceChecks.Remove(stale);
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = Guid.NewGuid(), DocumentId = docId, ComplianceRuleId = ruleId,
            IsPassed = false, CheckedAt = DateTime.UtcNow,
        });

        var act = () => db.SaveChangesAsync();

        await act.Should().NotThrowAsync(
            "the harness helper must not give a test different SaveChanges semantics from the production "
            + "code path it is written to exercise");
    }

    [Fact]
    public async Task A_check_row_UPDATE_that_matches_nothing_still_fails_the_save()
    {
        // Scope, direction one. Nothing in this codebase updates a check row in place — they are cleared
        // and rewritten — so an UPDATE finding no row means something genuinely unexpected happened to it,
        // and the count is real information. Tolerating it would be a silently-lost write.
        var auth = await RegisterAndLoginAsync();
        var (_, checkId, _) = await SeedCheckedDocumentAsync(auth.OrgId);

        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var stale = await db.ComplianceChecks.FirstAsync(c => c.Id == checkId);

        await DeleteCheckRowElsewhereAsync(checkId);

        stale.Notes = "rewritten";

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "only the DELETE is row-count-tolerant — the suppression keys on the entry STATE, not on the "
            + "entity type alone");
    }

    [Fact]
    public async Task A_non_check_row_delete_that_matches_nothing_still_fails_the_save()
    {
        // Scope, direction two. The persist stages DocumentField deletes in the very same SaveChanges as
        // the check-row ones, so a suppression keyed on "a delete matched nothing" rather than on the
        // entity type would swallow those too — and a DocumentField that vanished under a writer is not
        // a derived display row somebody else rewrote, it is a fact about the document.
        var auth = await RegisterAndLoginAsync();
        var (docId, _, _) = await SeedCheckedDocumentAsync(auth.OrgId);

        var fieldId = Guid.NewGuid();
        await using (var seed = CreateSystemDb())
        {
            seed.DocumentFields.Add(new DocumentField
            {
                Id = fieldId, DocumentId = docId, FieldName = "policy_number", FieldValue = "POL-1",
                FieldType = "string", Confidence = 0.95,
            });
            await seed.SaveChangesAsync();
        }

        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var stale = await db.DocumentFields.FirstAsync(f => f.Id == fieldId);

        await using (var other = CreateSystemDb())
            await other.DocumentFields.Where(f => f.Id == fieldId).ExecuteDeleteAsync();

        db.DocumentFields.Remove(stale);

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the tolerance is scoped to ComplianceCheck — widening it to every zero-row delete would hide "
            + "a genuinely lost row");
    }

    [Fact]
    public async Task A_save_that_loses_many_check_rows_warns_ONCE_for_the_whole_unit_of_work()
    {
        // #468 review S2. The suppression hook runs once per ORPHANED ROW — EF/Npgsql attributes a
        // rows-affected mismatch to the single modification command that produced it, which is the same
        // per-command attribution that lets the IsCheckRowDelete guard survive the worker's mixed batch. So
        // a warning inside the hook is a warning PER ROW: immaterial on a single-document writer, a
        // page-sized burst on ComplianceCheckService.ReevaluateWhereAsync, whose one SaveChanges clears up
        // to DefaultReevaluationPageSize documents' checks and whose fan-out thread would then block on log
        // I/O with eagerly-formatted arguments. The per-row detail belongs at Debug; the warning is one
        // line per unit of work, carrying the aggregate its wording claims.
        //
        // Hand-built context rather than CreateSystemDb(): the helper logs to NullLogger, and here the LOG
        // is the observable under test.
        var auth = await RegisterAndLoginAsync();
        var (docId, checkId, ruleId) = await SeedCheckedDocumentAsync(auth.OrgId);
        List<Guid> all = [checkId, .. await AddCheckRowsAsync(docId, ruleId, 2)];

        var logger = new ListLogger<ComplianceCheckDeleteConcurrencyInterceptor>();
        await using var db = new SystemDbContext(new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors(
                new AuditSaveChangesInterceptor(() => null),
                new ComplianceCheckDeleteConcurrencyInterceptor(logger))
            .Options);
        var stale = await db.ComplianceChecks.Where(c => all.Contains(c.Id)).ToListAsync();
        stale.Should().HaveCount(3, "precondition: three rows for one clear to stage");

        await using (var other = CreateSystemDb())
            await other.ComplianceChecks.Where(c => all.Contains(c.Id)).ExecuteDeleteAsync();

        db.ComplianceChecks.RemoveRange(stale);

        var act = () => db.SaveChangesAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle(
                "one SaveChanges is one event, however many of its rows another writer got to first")
            .Which.Message.Should()
                .Contain("deleted 3 ComplianceCheck row(s)",
                    "…and that one line carries the aggregate — a per-row line can only ever say 1")
                .And.Contain("across 1 document(s)");
        logger.Entries.Count(e => e.Level == LogLevel.Debug).Should().Be(3,
            "the per-row detail is kept, at a level a busy fan-out is not paying for");
    }

    /// <summary>
    /// Test-only: hijacks the hook that fires right AFTER the batch executed and right BEFORE the
    /// interceptor under test flushes its tally — the one window where a unit of work's suppression is
    /// recorded and not yet reported. Registered AHEAD of the real interceptor, so it wins that ordering
    /// (EF invokes save-changes interceptors in registration order) and the real one's own
    /// <c>SavedChangesAsync</c> never runs.
    /// </summary>
    private sealed class AfterBatchInterceptor : SaveChangesInterceptor
    {
        private bool _reentrant;

        /// <summary>Runs inside the armed save's completion hook. Null means inert.</summary>
        public Func<Task>? OnSaved { get; set; }

        /// <summary>When true, the completion hook throws <see cref="OperationCanceledException"/>.</summary>
        public bool CancelInstead { get; set; }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (OnSaved is { } callback && !_reentrant)
            {
                // The callback saves through a SECOND context on the same shared interceptor, which comes
                // back through here; a plain flag is enough because this fixture's tests are serial.
                _reentrant = true;
                try { await callback(); }
                finally { _reentrant = false; }
            }

            if (CancelInstead) throw new OperationCanceledException();
            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// A context wired the way production wires one, with <paramref name="logger"/> where the LOG is the
    /// observable (<c>CreateSystemDb</c> logs to NullLogger) and <paramref name="before"/> registered ahead
    /// of the interceptor under test.
    /// </summary>
    private SystemDbContext BuildContext(
        ComplianceCheckDeleteConcurrencyInterceptor interceptor, params ISaveChangesInterceptor[] before) =>
        new(new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors([new AuditSaveChangesInterceptor(() => null), .. before, interceptor])
            .Options);

    [Fact]
    public async Task A_save_CANCELED_after_its_batch_still_reports_the_suppression()
    {
        // #468 review S2. EF ends a SaveChanges three ways, and the tally has to be emitted on all three:
        // it dies with the CONTEXT, and the callers that matter here dispose their scope the moment the
        // save returns, so a tally left behind by a canceled save is never "dropped by the next save's
        // reset" — there is no next save. The reachable cancellation is the extraction worker's own:
        // PersistSuccess saves on the token it was handed, so a shutdown or a per-attempt timeout lands
        // exactly here, on the one writer whose suppressed deletes are why this class exists. And an
        // Npgsql cancellation can be observed AFTER the batch reached the server, so "canceled" does not
        // mean "committed nothing" — which is why the suppression must still be traced.
        //
        // Cancelling from the completion hook models that shape precisely: the batch ran (the suppression
        // is already tallied) and only then does the unit of work cancel.
        var auth = await RegisterAndLoginAsync();
        var (docId, checkId, ruleId) = await SeedCheckedDocumentAsync(auth.OrgId);

        var logger = new ListLogger<ComplianceCheckDeleteConcurrencyInterceptor>();
        var cancelAfterBatch = new AfterBatchInterceptor { CancelInstead = true };
        await using var db = BuildContext(new ComplianceCheckDeleteConcurrencyInterceptor(logger), cancelAfterBatch);
        var stale = await db.ComplianceChecks.FirstAsync(c => c.Id == checkId);

        await DeleteCheckRowElsewhereAsync(checkId);

        db.ComplianceChecks.Remove(stale);
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = Guid.NewGuid(), DocumentId = docId, ComplianceRuleId = ruleId,
            IsPassed = false, CheckedAt = DateTime.UtcNow,
        });

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<OperationCanceledException>(
            "precondition: this save ends CANCELED, not completed and not failed — so only the canceled "
            + "hook can report what it suppressed");
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle(
                "a suppression with no trace at all is the thing this logging exists to prevent, and a "
                + "canceled save is not a save that suppressed nothing")
            .Which.Message.Should().Contain("deleted 1 ComplianceCheck row(s)");
    }

    [Fact]
    public async Task Two_contexts_sharing_ONE_interceptor_each_report_their_OWN_suppression()
    {
        // #468 review S7. The tally used to be plain instance fields, correct only because the natural
        // wiring happens to give one interceptor per context (DbContextOptions is scoped). That premise was
        // documented rather than enforced — and a SINGLETON registration, or one instance handed to both
        // contexts' option builders, would merge two units of work into one tally and make the aggregate
        // this warning claims to carry ("{Count} row(s) across {DocumentCount} document(s)") a lie.
        //
        // So: ONE interceptor, TWO contexts, and an INTERLEAVE rather than two sequential saves — sequential
        // ones pass either way, since each flushes before the next resets. Context A's save is held at its
        // completion hook, where its suppression is tallied but not yet reported, and B's whole save runs
        // inside it. With instance fields, B's SavingChanges resets the one tally and B's flush drains it,
        // so A's suppression is reported by nobody: exactly one warning instead of two.
        var auth = await RegisterAndLoginAsync();
        var (docA, checkA, ruleA) = await SeedCheckedDocumentAsync(auth.OrgId);
        var (docB, checkB, ruleB) = await SeedCheckedDocumentAsync(auth.OrgId);
        List<Guid> bRows = [checkB, .. await AddCheckRowsAsync(docB, ruleB, 1)];

        var logger = new ListLogger<ComplianceCheckDeleteConcurrencyInterceptor>();
        var shared = new ComplianceCheckDeleteConcurrencyInterceptor(logger);
        var interleave = new AfterBatchInterceptor();

        await using var a = BuildContext(shared, interleave);
        await using var b = BuildContext(shared, interleave);

        var staleA = await a.ComplianceChecks.FirstAsync(c => c.Id == checkA);
        var staleB = await b.ComplianceChecks.Where(c => bRows.Contains(c.Id)).ToListAsync();
        staleB.Should().HaveCount(2, "precondition: B's clear stages two rows, so its aggregate differs from A's");

        await DeleteCheckRowElsewhereAsync(checkA);
        await using (var other = CreateSystemDb())
            await other.ComplianceChecks.Where(c => bRows.Contains(c.Id)).ExecuteDeleteAsync();

        a.ComplianceChecks.Remove(staleA);
        a.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = Guid.NewGuid(), DocumentId = docA, ComplianceRuleId = ruleA,
            IsPassed = false, CheckedAt = DateTime.UtcNow,
        });
        b.ComplianceChecks.RemoveRange(staleB);

        interleave.OnSaved = async () =>
        {
            interleave.OnSaved = null;
            await b.SaveChangesAsync();
        };

        var act = () => a.SaveChangesAsync();

        await act.Should().NotThrowAsync();

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        warnings.Should().HaveCount(2,
            "each context's unit of work is its own event — one interceptor instance serving two of them "
            + "must not merge or drop either");
        warnings.Should().ContainSingle(m => m.Contains("deleted 1 ComplianceCheck row(s)"),
            "…A lost exactly one row, and its tally survived B's whole save running inside A's completion hook");
        warnings.Should().ContainSingle(m => m.Contains("deleted 2 ComplianceCheck row(s)"),
            "…and B lost two, reported against B rather than folded into A's line");
    }

    [Fact]
    public async Task A_batched_fan_out_page_commits_its_re_grades_through_an_orphaned_check_row()
    {
        // #468 review S5, and the test that makes the AppDbContext registration mean what the ADR says it
        // means. That registration is justified by ONE production writer — ComplianceCheckService's BATCHED
        // re-grade fan-out, which stays READ COMMITTED while every other AppDbContext check-row writer runs
        // under REPEATABLE READ — but it was pinned by a hand-made two-command batch on a scope-resolved
        // context. The fan-out's real shape is one SaveChanges carrying N Document UPDATEs + M check
        // DELETEs + K check INSERTs, and whether EF still attributes a rows-affected mismatch there to the
        // single orphaned DELETE (which the Entries.All(IsCheckRowDelete) guard REQUIRES) is exactly the
        // assumption the interceptor's remarks rest on. A mixed page-sized batch is where that assumption
        // is either true or is not.
        //
        // The stake is a page, not a document: before the interceptor a competing edit anywhere in a page
        // of up to DefaultReevaluationPageSize documents forfeited the WHOLE page's re-grade, and the
        // fan-out's per-page catch swallows the failure — so it never surfaced anywhere.
        var sink = new CapturingLogEventSink();
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink)));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

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

        // TWO documents on one vendor's checklist, both already graded — so one page carries both, and the
        // orphaned rows belong to only one of them.
        var (docA, checkA, ruleId, templateId, vendorId) = await SeedFanOutAsync(orgId);
        var (docB, checkB) = await SeedSecondDocumentAsync(orgId, vendorId, ruleId);

        // The competing writer, fired from inside the fan-out page's OWN SaveChanges — after
        // ApplyEvaluationsAsync bulk-loaded and staged the page's check rows, before EF issues the batch.
        // The rule upsert saves the RULE first on this same context, so the first firing is that save and
        // the second is the fan-out's; deleting on the first would leave nothing staged to orphan.
        var hook = factory.Services.GetRequiredService<ConcurrentDocumentWriteInterceptor>();
        var saves = 0;
        hook.OnSavingChanges = async () =>
        {
            if (++saves != 2) return;
            hook.OnSavingChanges = null;
            await using var other = CreateSystemDb();
            await other.ComplianceChecks.Where(c => c.Id == checkA).ExecuteDeleteAsync();
        };

        HttpResponseMessage resp;
        try
        {
            // A rule EDIT — the production trigger for this fan-out, and the one whose remarks call a
            // document left on its pre-edit verdict "a genuine false Compliant".
            resp = await client.PostAsJsonAsync($"/api/compliance/templates/{templateId}/rules", new
            {
                id = ruleId,
                documentType = "coi",
                fieldName = "general_liability_limit",
                @operator = "min_value",
                expectedValue = "3000000", // TIGHTENED: 2M no longer clears it, so the verdict must move
                errorMessage = (string?)null,
                sortOrder = 0,
            });
        }
        finally
        {
            hook.Reset();
        }

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        saves.Should().BeGreaterThanOrEqualTo(2, "precondition: the fan-out's own SaveChanges is the second one");

        await using var read = CreateSystemDb();
        var rows = await read.ComplianceChecks.AsNoTracking()
            .Where(c => c.DocumentId == docA || c.DocumentId == docB)
            .Select(c => new { c.Id, c.DocumentId, c.IsPassed })
            .ToListAsync();

        // The page COMMITTED — both documents, not just the one whose rows survived. This is what the
        // pre-#468 behaviour cost: one orphaned row aborted the whole page, and the per-page catch made
        // that silent.
        rows.Select(r => r.DocumentId).Should().BeEquivalentTo([docA, docB],
            "every document in the page keeps a re-graded check row — a page is one unit of work, so the "
            + "orphan would have forfeited BOTH documents' re-grades, not just its own");
        rows.Should().NotContain(r => r.Id == checkA || r.Id == checkB,
            "…and these are the fan-out's OWN new rows: the clear it staged is what the tightened rule "
            + "replaces, so a page that merely survived without re-grading is not the same as one that ran");
        rows.Should().OnlyContain(r => !r.IsPassed, "2M no longer clears the tightened 3M floor");
        (await read.Documents.AsNoTracking()
                .Where(d => d.Id == docA || d.Id == docB)
                .Select(d => d.ComplianceStatus).ToListAsync())
            .Should().AllBeEquivalentTo(ComplianceStatus.NonCompliant,
                "the verdict rides in the same unit of work as the check rows (ADR 0030)");

        // The tolerance actually FIRED — without this the test passes identically if the interleave never
        // produced a zero-row DELETE at all, which is how a fan-out reproduction goes quietly green.
        sink.Events.Select(e => e.RenderMessage())
            .Where(m => m.Contains("treating those deletes as done", StringComparison.Ordinal))
            .Should().ContainSingle("one SaveChanges is one event, and this page ran one")
            .Which.Should().Contain("deleted 1 ComplianceCheck row(s)").And.Contain("across 1 document(s)",
                "…attributed to the ONE document whose row was taken, out of a batch that also carried the "
                + "other document's Document UPDATE, its DELETE and both INSERTs — the per-command "
                + "attribution the Deleted-entry guard depends on");
        sink.Events.Select(e => e.RenderMessage())
            .Should().NotContain(m => m.Contains("Re-evaluation fan-out failed for a page", StringComparison.Ordinal),
                "the page must not reach its own catch — that arm is where a forfeited page hides");
    }

    /// <summary>The fan-out fixture: a vendor on a one-rule checklist plus one graded document on it.</summary>
    private async Task<(Guid DocId, Guid CheckId, Guid RuleId, Guid TemplateId, Guid VendorId)> SeedFanOutAsync(Guid orgId)
    {
        var (docId, checkId, ruleId) = await SeedCheckedDocumentAsync(orgId);
        await using var db = CreateSystemDb();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        var vendorId = doc.VendorId!.Value;
        var templateId = await db.Vendors.Where(v => v.Id == vendorId)
            .Select(v => v.ComplianceTemplateId!.Value).FirstAsync();
        return (docId, checkId, ruleId, templateId, vendorId);
    }

    /// <summary>A second graded document on the same vendor, so the fan-out's page carries two.</summary>
    private async Task<(Guid DocId, Guid CheckId)> SeedSecondDocumentAsync(Guid orgId, Guid vendorId, Guid ruleId)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        var checkId = Guid.NewGuid();

        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "coi-2.pdf",
            BlobStorageUrl = "blob://d2",
            BlobStoragePath = $"blob/{docId:N}.pdf",
            FileSizeBytes = 1024,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed,
            ComplianceStatus = ComplianceStatus.Compliant,
            GeneralLiabilityLimit = 2_000_000m,
            ExtractionFields = JsonDocument.Parse("""{"general_liability_limit":"2000000"}"""),
            ExpirationDate = now.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ComplianceChecks.Add(new ComplianceCheck
        {
            Id = checkId, DocumentId = docId, ComplianceRuleId = ruleId, IsPassed = true, CheckedAt = now,
        });
        await db.SaveChangesAsync();
        return (docId, checkId);
    }

    [Fact]
    public async Task The_check_row_clear_does_not_execute_until_the_caller_saves()
    {
        // ADR 0030's whole premise, and the reason the fix is a tolerated delete rather than a set-based
        // one. ApplyEvaluationAsync must only STAGE the clear: the rows have to disappear in the same
        // SaveChanges that writes the new checks and the verdict, so a failure anywhere in that unit of
        // work leaves the previous evaluation intact.
        //
        // An ExecuteDeleteAsync clear would pass every behavioural test in this repo and break exactly
        // this: it runs outside the change tracker, issues its statement immediately, and joins the
        // caller's transaction only when one is explicitly OPEN — which it is not here, nor in
        // ExtractionWorker.PersistSuccess, nor in EvaluateForSystemAsync.
        var auth = await RegisterAndLoginAsync();
        var (docId, checkId, _) = await SeedCheckedDocumentAsync(auth.OrgId);

        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var compliance = scope.ServiceProvider.GetRequiredService<IComplianceCheckService>();
        var doc = await db.Documents
            .Include(d => d.Vendor).ThenInclude(v => v!.ComplianceTemplate).ThenInclude(t => t!.Rules)
            .FirstAsync(d => d.Id == docId);

        db.Database.CurrentTransaction.Should().BeNull(
            "precondition: this caller owns no explicit transaction — the same as the worker's persist and "
            + "EvaluateForSystemAsync, which is precisely why a set-based clear would commit on its own");

        await compliance.ApplyEvaluationAsync(db, doc, CancellationToken.None);

        await using (var probe = CreateSystemDb())
        {
            (await probe.ComplianceChecks.AsNoTracking().AnyAsync(c => c.Id == checkId))
                .Should().BeTrue(
                    "the clear must still be PENDING on the caller's change tracker — a delete already "
                    + "committed here is a delete outside the caller's unit of work (#337 / ADR 0030)");
        }

        await db.SaveChangesAsync();

        await using var read = CreateSystemDb();
        (await read.ComplianceChecks.AsNoTracking().AnyAsync(c => c.Id == checkId)).Should().BeFalse(
            "…and the caller's own SaveChanges is what makes it real");
        (await read.ComplianceChecks.AsNoTracking().CountAsync(c => c.DocumentId == docId))
            .Should().Be(1, "the replacement check row committed in that same unit of work");
    }
}
