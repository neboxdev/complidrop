using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Regression tests for <see cref="ExtractionWorker"/> that lock in the April-25 zombie/hang fix:
/// a document whose extraction always fails is retried up to <see cref="ExtractionWorker.MaxAttempts"/>
/// and then marked <c>Failed</c> (no infinite retry, no runaway attempt count), the up-front zombie
/// guard fails an over-cap document without re-paying OCR/LLM cost, the cost ceiling short-circuits
/// processing, and the <c>FOR UPDATE SKIP LOCKED</c> claim never double-grabs a row. Driven directly
/// against the Testcontainers Postgres harness via the worker's public claim/process seam, with the
/// OCR + extraction boundary swapped for in-memory fakes.
/// </summary>
public sealed class ExtractionWorkerTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeExtractionClient Extraction =>
        Fixture.Factory.Services.GetRequiredService<FakeExtractionClient>();

    private FakeOcrService Ocr =>
        Fixture.Factory.Services.GetRequiredService<FakeOcrService>();

    /// <summary>The configured free-tier monthly cost ceiling — read from config, not hard-coded.</summary>
    private decimal FreeTierCeilingUsd =>
        Fixture.Factory.Services.GetRequiredService<IOptions<CostCeilings>>().Value.FreeTierMonthlyUsd;

    /// <summary>Mirrors the per-document amount ExtractionWorker passes to CanSpendAsync.</summary>
    private const decimal PlannedPerDocUsd = 0.01m;

    /// <summary>Builds a worker bound to the host's DI (so it resolves the test DB + fakes).</summary>
    private ExtractionWorker BuildWorker() =>
        new(
            Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExtractionWorker>.Instance);

    /// <summary>
    /// Seeds an org (and optionally its subscription) plus a single document in the given state.
    /// Pass <paramref name="subscriptionSpendUsd"/> to seed a subscription — required for any path
    /// that reaches the cost check, since <c>CostTrackingService</c> denies orgs with no subscription.
    /// </summary>
    private async Task<(Guid OrgId, Guid DocId)> SeedDocAsync(
        ExtractionStatus status = ExtractionStatus.Pending,
        int attempts = 0,
        string? blobPath = "blob/path/doc.pdf",
        DateTime? processingStartedAt = null,
        DateTime? createdAt = null,
        decimal? subscriptionSpendUsd = null,
        string plan = "free")
    {
        var orgId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = $"Org-{orgId:N}",
            TimeZone = "America/New_York",
            CreatedAt = now,
            UpdatedAt = now,
        });
        if (subscriptionSpendUsd is { } spend)
        {
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Plan = plan,
                Status = "active",
                ExtractionSpendThisMonthUsd = spend,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            OriginalFileName = "doc.pdf",
            BlobStorageUrl = "blob://doc",
            BlobStoragePath = blobPath,
            FileSizeBytes = 1024,
            ContentType = "application/pdf",
            ExtractionStatus = status,
            ProcessingAttempts = attempts,
            ProcessingStartedAt = processingStartedAt,
            CreatedAt = createdAt ?? now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, docId);
    }

    private async Task<Document> GetDocAsync(Guid docId)
    {
        await using var db = CreateSystemDb();
        return await db.Documents.AsNoTracking().SingleAsync(d => d.Id == docId);
    }

    private async Task<Subscription> GetSubscriptionAsync(Guid orgId)
    {
        await using var db = CreateSystemDb();
        return await db.Subscriptions.AsNoTracking().SingleAsync(s => s.OrganizationId == orgId);
    }

    /// <summary>Locks a document row inside a still-open transaction on a separate connection.</summary>
    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> LockRowAsync(Guid docId)
    {
        var conn = new NpgsqlConnection(Fixture.ConnectionString);
        NpgsqlTransaction? tx = null;
        try
        {
            await conn.OpenAsync();
            tx = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """SELECT "Id" FROM "Documents" WHERE "Id" = @id FOR UPDATE""";
            cmd.Parameters.AddWithValue("id", docId);
            var locked = await cmd.ExecuteScalarAsync();
            locked.Should().NotBeNull("the row to be locked must exist");
            return (conn, tx);
        }
        catch
        {
            // Don't leak the open connection + held lock if the setup assertion (or open/begin)
            // throws before the caller receives the handles it would otherwise dispose.
            if (tx is not null) await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
    }

    // ----- AC1: bounded retry then Failed (no infinite loop) ---------------------------------

    [Fact]
    public async Task Always_failing_extraction_retries_up_to_MaxAttempts_then_marks_failed()
    {
        // Subscription seeded with zero spend so the cost check passes and every attempt actually
        // reaches (and is rejected by) the extraction boundary.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.ThrowOnExtract = true;
        var worker = BuildWorker();

        // Safety cap well above MaxAttempts: a regression to infinite retry exits here and fails the
        // assertion below instead of hanging the suite forever.
        const int safetyCap = ExtractionWorker.MaxAttempts + 5;
        var cycles = 0;
        while (cycles < safetyCap)
        {
            var claimed = await worker.ClaimNextAsync(CancellationToken.None);
            if (claimed is null) break; // nothing left to claim — the doc reached a terminal state
            await worker.ProcessDocumentAsync(claimed.Value, CancellationToken.None);
            cycles++;
        }

        cycles.Should().Be(ExtractionWorker.MaxAttempts, "the doc must be retried exactly the budget, then stop");
        Extraction.ExtractCallCount.Should().Be(ExtractionWorker.MaxAttempts);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed);
        doc.ProcessingAttempts.Should().Be(ExtractionWorker.MaxAttempts);
        doc.ProcessingError.Should().StartWith("extraction.failed");
    }

    // ----- AC2: up-front zombie guard --------------------------------------------------------

    [Fact]
    public async Task Document_past_the_attempt_cap_is_failed_up_front_without_reprocessing()
    {
        // ProcessingAttempts already over the cap (e.g. the process crashed mid-extraction in prior
        // attempts so the catch block never ran). The guard must fail it before any OCR/LLM work.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: ExtractionWorker.MaxAttempts + 1,
            processingStartedAt: DateTime.UtcNow,
            subscriptionSpendUsd: 0m);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed);
        doc.ProcessingError.Should().StartWith("extraction.too_many_attempts");
        Extraction.ExtractCallCount.Should().Be(0, "the zombie guard must short-circuit before extraction");
        Ocr.OcrCallCount.Should().Be(0);
    }

    // ----- AC3: success path ------------------------------------------------------------------

    [Fact]
    public async Task Successful_extraction_goes_pending_to_processing_to_completed_and_records_cost()
    {
        var (orgId, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        var worker = BuildWorker();

        // Claim flips Pending -> Processing and stamps the first attempt.
        var claimed = await worker.ClaimNextAsync(CancellationToken.None);
        claimed.Should().Be(docId);

        var afterClaim = await GetDocAsync(docId);
        afterClaim.ExtractionStatus.Should().Be(ExtractionStatus.Processing);
        afterClaim.ProcessingAttempts.Should().Be(1);
        afterClaim.ProcessingStartedAt.Should().NotBeNull();

        // Process completes it.
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var done = await GetDocAsync(docId);
        done.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        done.ExtractionCompletedAt.Should().NotBeNull();
        done.ExtractionPromptVersion.Should().NotBeNullOrEmpty();
        done.ProcessingError.Should().BeNull();
        Extraction.ExtractCallCount.Should().Be(1);

        // Token usage/cost recorded against the org subscription. OCR is disabled, so the recorded
        // spend is exactly the fake's LLM usage cost — asserted against the fake's own result so the
        // fake stays the single source of truth (no duplicated magic literal).
        var sub = await GetSubscriptionAsync(orgId);
        sub.ExtractionSpendThisMonthUsd.Should().Be(Extraction.Result.Usage!.EstimatedCostUsd);

        // Every extracted field persisted.
        await using var db = CreateSystemDb();
        (await db.DocumentFields.CountAsync(f => f.DocumentId == docId))
            .Should().Be(Extraction.Result.Fields.Count);
    }

    // ----- AC4: FOR UPDATE SKIP LOCKED --------------------------------------------------------

    [Fact]
    public async Task Claim_skips_a_row_locked_by_another_transaction_and_returns_null()
    {
        // A single candidate, locked by a concurrent transaction (mimicking another worker mid-claim).
        var (_, docId) = await SeedDocAsync();
        var worker = BuildWorker();

        var (conn, tx) = await LockRowAsync(docId);
        try
        {
            // Bound the claim: a regression from SKIP LOCKED to a blocking FOR UPDATE would wait on
            // the held lock; the timeout makes that fail fast rather than hang the suite.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var claimed = await worker.ClaimNextAsync(cts.Token);

            claimed.Should().BeNull("the only candidate row is locked and must be skipped, not grabbed or waited on");
        }
        finally
        {
            await tx.RollbackAsync();
            await tx.DisposeAsync();
            await conn.DisposeAsync();
        }

        // The locked row was never touched.
        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.Pending);
    }

    [Fact]
    public async Task Claim_skips_a_locked_row_and_grabs_the_next_available_one()
    {
        // docA is older, so ORDER BY CreatedAt would pick it first — but it's locked, so the claim
        // must skip it and take docB. This is the invariant that stops two workers double-grabbing.
        var (_, docA) = await SeedDocAsync(createdAt: DateTime.UtcNow.AddMinutes(-2));
        var (_, docB) = await SeedDocAsync(createdAt: DateTime.UtcNow);
        var worker = BuildWorker();

        var (conn, tx) = await LockRowAsync(docA);
        Guid? claimed;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            claimed = await worker.ClaimNextAsync(cts.Token);
        }
        finally
        {
            await tx.RollbackAsync();
            await tx.DisposeAsync();
            await conn.DisposeAsync();
        }

        claimed.Should().Be(docB, "the locked older row must be skipped and the next available one claimed");
        (await GetDocAsync(docA)).ExtractionStatus.Should().Be(ExtractionStatus.Pending, "the skipped row stays untouched");
        (await GetDocAsync(docB)).ExtractionStatus.Should().Be(ExtractionStatus.Processing);
    }

    // ----- AC5: cost ceiling ------------------------------------------------------------------

    [Fact]
    public async Task Cost_ceiling_exceeded_short_circuits_to_failed_without_extracting()
    {
        // Seeding spend at the ceiling makes the worker's planned per-doc amount tip it over, so
        // CanSpendAsync returns false and the worker fails the doc before extracting. Spend and
        // ceiling are derived from config so the boundary tracks the source of truth, not a literal.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: FreeTierCeilingUsd, plan: "free");
        var worker = BuildWorker();

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed);
        doc.ProcessingError.Should().StartWith("extraction.cost_ceiling_hit");
        Extraction.ExtractCallCount.Should().Be(0, "the cost ceiling must short-circuit before extraction");
        Ocr.OcrCallCount.Should().Be(0);
    }

    // ----- Zombie reclaim (the "claimed-but-never-processed hang" half of the fix) ------------

    [Fact]
    public async Task Stale_processing_document_is_reclaimed_after_the_zombie_timeout()
    {
        // Claimed >5 min ago and never finished (the owning process died). The next claim reclaims it.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-10));
        var worker = BuildWorker();

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);

        claimed.Should().Be(docId);
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(2, "the reclaim increments the attempt counter");
    }

    [Fact]
    public async Task Recently_claimed_processing_document_is_not_reclaimed()
    {
        // Freshly claimed and still within the 5-min window — owned by another worker, must be left alone.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow);
        var worker = BuildWorker();

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);

        claimed.Should().BeNull("a freshly-claimed doc must not be stolen before the zombie timeout");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(1, "the doc is untouched");
    }

    // ----- Session-TZ independence (the #26 fix) ----------------------------------------------

    /// <summary>
    /// Runs <see cref="ExtractionWorker.ClaimSql"/> on a fresh connection whose session TimeZone
    /// is pinned to the given IANA zone. Proves the worker's SQL is correct regardless of session
    /// TZ — under the pre-fix SQL, mixing timestamptz with `now() at time zone 'utc'` (which
    /// yields `timestamp without time zone`) forced Postgres to bridge via the session TZ, and a
    /// non-UTC session shifted both the 5-min reclaim threshold and the writes by the offset.
    /// </summary>
    private async Task<Guid?> RunClaimUnderSessionTimeZoneAsync(string ianaZone)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();

        await using (var setTz = conn.CreateCommand())
        {
            // Inline literal is safe — IANA zone names are caller-controlled test inputs, not user
            // input, and `SET TIME ZONE` does not accept parameters in Postgres.
            setTz.CommandText = $"SET TIME ZONE '{ianaZone}'";
            await setTz.ExecuteNonQueryAsync();
        }

        await using var claim = conn.CreateCommand();
        claim.CommandText = ExtractionWorker.ClaimSql;
        await using var reader = await claim.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetGuid(0) : (Guid?)null;
    }

    [Fact]
    public async Task Claim_under_non_UTC_session_reclaims_a_stale_processing_doc()
    {
        // ProcessingStartedAt 10 min ago, well past the 5-min zombie threshold. Under the pre-fix
        // SQL with an America/New_York session (UTC-5/-4), `now() at time zone 'utc'` would yield a
        // naive timestamp that Postgres then re-cast back to timestamptz using session TZ — pushing
        // the comparison's RHS hours into the future and wrongly reclaiming anything. Under the
        // fixed SQL, both sides are timestamptz and the 5-min boundary holds.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-10));

        var claimed = await RunClaimUnderSessionTimeZoneAsync("America/New_York");

        claimed.Should().Be(docId, "stale doc must still be reclaimed under a non-UTC session TZ");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(2);
    }

    [Fact]
    public async Task Claim_under_non_UTC_session_does_not_reclaim_a_fresh_processing_doc()
    {
        // ProcessingStartedAt 1 min ago, well inside the 5-min window. Under the pre-fix SQL with
        // a non-UTC session, the RHS of the threshold drifted by the session offset and a fresh
        // doc would be wrongly reclaimed — the exact data-corrupting bug #26 fixes. Under the
        // fixed SQL, timestamptz-to-timestamptz comparison is offset-independent.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-1));

        var claimed = await RunClaimUnderSessionTimeZoneAsync("America/New_York");

        claimed.Should().BeNull("a fresh doc must not be reclaimed under a non-UTC session TZ");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(1, "fresh doc is untouched");
    }

    [Fact]
    public async Task Claim_under_non_UTC_session_writes_ProcessingStartedAt_at_real_now_not_offset()
    {
        // The symmetric write-side bug: under the pre-fix SQL, assigning `now() at time zone 'utc'`
        // (a naive timestamp) into the timestamptz `ProcessingStartedAt` column forced Postgres to
        // interpret the naive value in the session TZ. Under America/New_York that stored a moment
        // 4-5 hours in the FUTURE of real-now, leaving every claimed doc looking like it had been
        // claimed in the future. With timestamptz-to-timestamptz writes, the stored value equals
        // real-now regardless of session TZ.
        var (_, docId) = await SeedDocAsync(status: ExtractionStatus.Pending);
        var before = DateTime.UtcNow;

        var claimed = await RunClaimUnderSessionTimeZoneAsync("America/New_York");

        var after = DateTime.UtcNow;
        claimed.Should().Be(docId);
        var doc = await GetDocAsync(docId);
        doc.ProcessingStartedAt.Should().NotBeNull();
        // Generous skew window — the failure mode is hours, not seconds; a 30s envelope catches
        // the bug while staying robust against test-host timing jitter.
        doc.ProcessingStartedAt!.Value.Should().BeOnOrAfter(before.AddSeconds(-30));
        doc.ProcessingStartedAt!.Value.Should().BeOnOrBefore(after.AddSeconds(30));
    }

    // ----- Boundary + branch coverage (from review) ------------------------------------------

    [Fact]
    public async Task Document_at_exactly_the_attempt_cap_is_still_processed_not_guarded_up_front()
    {
        // The up-front zombie guard is `> MaxAttempts` (strict), while the failure path is
        // `>= MaxAttempts`. A doc sitting at exactly MaxAttempts must still get its final attempt — it
        // must NOT be failed up-front. Pins the boundary against a `>`->`>=` regression that would
        // silently drop the legitimate last attempt. (AC2 covers the over-cap side at MaxAttempts+1.)
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: ExtractionWorker.MaxAttempts,
            processingStartedAt: DateTime.UtcNow,
            subscriptionSpendUsd: 0m);
        var worker = BuildWorker();

        // Call process directly so attempts stays at exactly MaxAttempts (a claim would increment it).
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        Extraction.ExtractCallCount.Should().Be(1, "a doc at exactly the cap still gets its final attempt");
        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        // The success path clears ProcessingError; had the up-front guard fired instead it would read
        // "extraction.too_many_attempts: ...". Null therefore proves the guard did NOT trip at the cap.
        doc.ProcessingError.Should().BeNull();
    }

    [Fact]
    public async Task Cost_exactly_at_the_ceiling_is_allowed_to_proceed()
    {
        // Seeding spend at (ceiling - plannedPerDoc) lands the planned amount at exactly the ceiling —
        // CanSpendAsync uses `<=`, so it must be ALLOWED. Pins the boundary against a `<=`->`<`
        // regression that would wrongly block a doc at the ceiling. Both values are derived from
        // config/source, so the test can't silently stop landing on the boundary if the ceiling moves.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: FreeTierCeilingUsd - PlannedPerDocUsd, plan: "free");
        var worker = BuildWorker();

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        Extraction.ExtractCallCount.Should().Be(1, "spend exactly at the ceiling is allowed");
        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.Completed);
    }

    [Fact]
    public async Task Document_with_no_blob_path_is_failed_safely_without_extracting()
    {
        // A doc with no blob path can't be downloaded; the worker throws before the cost check and
        // routes through the normal failure handling rather than NRE-ing or hanging. Below the cap it
        // returns to Pending for retry, and extraction/OCR are never reached.
        var (_, docId) = await SeedDocAsync(blobPath: null);
        var worker = BuildWorker();

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending, "a first failure below the cap is retried");
        doc.ProcessingError.Should().Contain("blob path");
        Extraction.ExtractCallCount.Should().Be(0, "extraction must never run without a document to read");
        Ocr.OcrCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0.40, false)] // low average confidence
    [InlineData(0.95, true)]  // high confidence, but the extractor flagged it for reprocessing
    public async Task Manual_required_extraction_completes_and_still_records_cost(double confidence, bool needsReprocessing)
    {
        // PersistSuccess routes to ManualRequired on EITHER avg field confidence < 0.7 OR
        // NeedsReprocessing — the other terminal-success outcome alongside Completed. Both independent
        // triggers are pinned here, and cost is recorded on this path either way.
        var (orgId, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = Extraction.Result with
        {
            Fields = [new ExtractedField("policy_number", "POL-1", "string", confidence)],
            NeedsReprocessing = needsReprocessing,
        };
        var worker = BuildWorker();

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired);
        doc.ExtractionCompletedAt.Should().NotBeNull();

        var sub = await GetSubscriptionAsync(orgId);
        sub.ExtractionSpendThisMonthUsd.Should().Be(Extraction.Result.Usage!.EstimatedCostUsd);
    }
}
