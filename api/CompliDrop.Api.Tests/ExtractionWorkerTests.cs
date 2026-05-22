using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """SELECT "Id" FROM "Documents" WHERE "Id" = @id FOR UPDATE""";
        cmd.Parameters.AddWithValue("id", docId);
        var locked = await cmd.ExecuteScalarAsync();
        locked.Should().NotBeNull("the row to be locked must exist");
        return (conn, tx);
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
        // spend is exactly the fake LLM usage cost.
        var sub = await GetSubscriptionAsync(orgId);
        sub.ExtractionSpendThisMonthUsd.Should().Be(0.02m);

        // Extracted fields persisted.
        await using var db = CreateSystemDb();
        (await db.DocumentFields.CountAsync(f => f.DocumentId == docId)).Should().Be(2);
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
        // Free-tier monthly ceiling is $5. Seeding spend at the ceiling makes the planned +$0.01 tip
        // it over, so CanSpendAsync returns false and the worker fails the doc before extracting.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 5m, plan: "free");
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
}
