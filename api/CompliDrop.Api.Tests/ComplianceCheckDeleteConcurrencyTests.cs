using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// <c>PersistSuccess</c>, which the catch's re-save on the same context turns into a five-minutely
/// re-paid Document AI + LLM run). This suite pins the MECHANISM instead — its two ends:
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
