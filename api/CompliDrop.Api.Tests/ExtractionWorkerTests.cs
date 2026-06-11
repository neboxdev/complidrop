using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
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
                // Anchor to the CURRENT UTC month — since #256 the counter only counts when
                // the anchor matches the evaluation month (the worker runs on the real clock).
                SpendMonthStart = CostTrackingService.MonthStart(DateOnly.FromDateTime(DateTime.UtcNow)),
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

        // The blob fake reports honest not-found (null) since #254, and the worker fails a
        // document whose blob is missing — so a seeded path must have actual stored bytes.
        if (blobPath is not null)
        {
            var blobs = Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
            await blobs.UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        }
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

        // The extraction -> typed-column write goes through the shared CanonicalDocumentFields helper
        // (#216): the fake's "expiration_date" field must land in the typed column as UTC, since
        // ComplianceCheckService reads that column, not the DocumentField rows.
        done.ExpirationDate.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        done.ExpirationDate!.Value.Kind.Should().Be(DateTimeKind.Utc);

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
    public async Task Stale_processing_document_just_past_the_zombie_threshold_is_reclaimed()
    {
        // Boundary-tight: ProcessingStartedAt is 30 seconds PAST the 5-min
        // zombie threshold (-5m30s). The 30 s envelope absorbs the elapsed
        // time between SeedDocAsync's `DateTime.UtcNow` read on the test
        // host and the worker's `now()` read in the Testcontainers Postgres
        // process (loopback Npgsql round-trip + the SaveChangesAsync write
        // + BuildWorker resolution — single to low-double-digit ms in
        // steady state; the kernel clock is shared so there is no host-vs-
        // container skew). The envelope is narrow enough that a regression
        // changing the threshold to 6 minutes would fail this assertion
        // (the seeded value would no longer cross the larger threshold).
        // Pins the threshold against drift in either direction — see also
        // the fresh-boundary pair below.
        //
        // The literal "5m" duplicated here mirrors `interval '5 minutes'`
        // in ExtractionWorker.ClaimSql by design: the test's job is to
        // PIN the threshold value so a regression there must fail here.
        // Hoisting to a shared constant — as MaxAttempts does — would
        // collapse the regression discriminator. (#62)
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-5).AddSeconds(-30));
        var worker = BuildWorker();

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);

        claimed.Should().Be(
            docId,
            "ProcessingStartedAt=-5m30s is past the 5-min zombie threshold and must be reclaimed; " +
            "if this fires with null, the threshold has been widened beyond 5m30s");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(2, "the reclaim increments the attempt counter");
    }

    [Fact]
    public async Task Recently_claimed_processing_document_just_before_the_zombie_threshold_is_not_reclaimed()
    {
        // Boundary-tight: ProcessingStartedAt is 30 seconds BEFORE the 5-min
        // zombie threshold (-4m30s). See the stale-boundary docstring above
        // for the jitter sources the 30 s envelope absorbs. Narrow enough
        // that a regression changing the threshold to 4 minutes would fail
        // this assertion (the seeded value would now cross the smaller
        // threshold and the doc would be wrongly reclaimed). Combined with
        // the stale-boundary pair above, the two tests bracket the 5-min
        // threshold and would catch a ±1-min drift in either direction.
        //
        // The literal "5m" duplicated here mirrors `interval '5 minutes'`
        // in ExtractionWorker.ClaimSql by design — see the stale-boundary
        // docstring for the rationale.
        //
        // The companion TZ-Theory tests below deliberately stay at the
        // looser -10m / -1m offsets: their failure mode is HOURS of drift
        // via session TimeZone bridging (#26), not MINUTES of threshold
        // drift, so a 30 s envelope adds no discriminating power there.
        // (#62)
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-4).AddSeconds(-30));
        var worker = BuildWorker();

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);

        claimed.Should().BeNull(
            "ProcessingStartedAt=-4m30s is inside the 5-min zombie window; a freshly-claimed doc " +
            "must not be stolen before the timeout. If this fires with a non-null Guid, the " +
            "threshold has been narrowed below 4m30s");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(1, "the doc is untouched");
    }

    /// <summary>
    /// Drift-coverage offsets for the stale-reclaim Theory. Every row
    /// is a value (in seconds) that ProcessingStartedAt is in the PAST
    /// — all should cross the 5-min zombie threshold and be reclaimed.
    /// The rows are diverse exemplars across the stale region, not
    /// each-row-catches-a-distinct-regression discriminators (the
    /// canonical 5-min threshold pin lives on the Fact above).
    /// <list type="bullet">
    /// <item><c>-330</c> (5m30s past) — boundary echo of the canonical
    ///   Fact above; included so the Theory's coverage is a strict
    ///   superset, not a parallel set. If the Fact is ever removed,
    ///   this row preserves the just-past-boundary pin.</item>
    /// <item><c>-360</c> (6m past) — deeper-stale exemplar one minute
    ///   past the threshold; strengthens confidence that the SQL
    ///   handles offsets across the stale region uniformly (the Fact
    ///   above already catches a `5→6 widening` because under that
    ///   regression `-330 &lt; -360` is false → Fact's `Be(docId)`
    ///   fails — so this row's value is breadth, not a unique
    ///   regression discriminator).</item>
    /// <item><c>-3600</c> (1h past) — far-stale exemplar; strengthens
    ///   confidence the SQL handles the entire stale region uniformly,
    ///   not just near-boundary docs. Also catches dramatic widening
    ///   regressions (e.g. an `interval '50 minutes'` typo would
    ///   leave -3600 still reclaimed, but a `'1 hour'` typo would
    ///   leave -3600 stuck — neither -330 nor -360 distinguish those
    ///   cases from -3600).</item>
    /// </list>
    /// </summary>
    public static TheoryData<int> StaleZombieDriftOffsetsSeconds() => new()
    {
        -330,   // 5m30s past — boundary echo
        -360,   // 6m past — deeper-stale exemplar
        -3600,  // 1h past — far-stale exemplar
    };

    /// <summary>
    /// Drift-coverage offsets for the fresh-not-reclaim Theory. Every
    /// row is a value (in seconds) that ProcessingStartedAt is in the
    /// PAST — none should cross the 5-min zombie threshold. Like the
    /// stale-row set, these are diverse exemplars across the fresh
    /// region rather than each-row-catches-a-distinct-regression
    /// discriminators.
    /// <list type="bullet">
    /// <item><c>-270</c> (4m30s past) — boundary echo of the canonical
    ///   Fact above; this row carries the regression-discriminator
    ///   weight (under a `5→4 narrowing`, `-270 &lt; -240` becomes
    ///   true → wrongly reclaimed → Theory's `BeNull()` fails). If
    ///   the Fact is ever removed, this row preserves the
    ///   just-inside-boundary pin.</item>
    /// <item><c>-240</c> (4m past) — mid-fresh-region exemplar.
    ///   Under strict `&lt;`, this row does NOT catch a `5→4 narrowing`
    ///   (`-240 &lt; -240` is false), but it DOES catch broader
    ///   regressions: e.g. `5min → ≤3min` narrowing (`-240 &lt; -180`
    ///   is true → wrongly reclaimed), or `&lt;` → `&lt;=` boundary
    ///   flip combined with a 5→4 narrowing. Its main contribution is
    ///   diversity coverage — proving the SQL doesn't wrongly reclaim
    ///   arbitrary fresh docs in the middle of the window.</item>
    /// <item><c>-30</c> (30s past) — recently-claimed exemplar near
    ///   the just-claimed end of the fresh region. Catches dramatic
    ///   regressions where the SQL reclaims every Processing row
    ///   regardless of ProcessingStartedAt — the -270 boundary echo
    ///   catches that too, but -30 makes the failure mode visible
    ///   far from the boundary so a maintainer sees the regression
    ///   isn't a "1 second past 5 minutes" precision issue.</item>
    /// </list>
    /// </summary>
    public static TheoryData<int> FreshZombieDriftOffsetsSeconds() => new()
    {
        -270,  // 4m30s past — boundary echo, regression-discriminator
        -240,  // 4m past — mid-fresh-region exemplar
        -30,   // 30s past — recently-claimed exemplar
    };

    [Theory]
    [MemberData(nameof(StaleZombieDriftOffsetsSeconds))]
    public async Task Stale_processing_document_past_the_zombie_threshold_is_reclaimed_under_various_drift_offsets(
        int driftSeconds)
    {
        // Broader drift-coverage Theory complementing the canonical
        // boundary-tight Fact above (#62). The Fact pins ONE point
        // (-5m30s) against ±1-min drift; this Theory adds diversity
        // exemplars across the stale region so the SQL is verified
        // against multiple offsets, not just the boundary.
        //
        // The -330 row (5m30s past) is a deliberate boundary echo of
        // the canonical Fact above — so a contributor running ONLY the
        // Theory still has the boundary-tight coverage if they
        // accidentally delete the Fact. The -360 / -3600 rows extend
        // coverage further into the stale region. See the MemberData
        // docstring above for the per-row regression-mode discussion
        // (and the caveat that -360/-3600 add BREADTH, not unique
        // regression discriminators over the Fact's -330 boundary).
        //
        // Offsets parameterized in seconds (not minutes) so the
        // Theory data can express minute- and sub-minute precision
        // uniformly via a single `TheoryData<int>` shape — see the
        // sibling `NonUtcSessionZones` Theory below for the same
        // pattern. (#140)
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddSeconds(driftSeconds));
        var worker = BuildWorker();

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);

        claimed.Should().Be(
            docId,
            $"ProcessingStartedAt={driftSeconds}s is past the 5-min zombie threshold and must be " +
            "reclaimed; if this fires with null, the threshold has drifted past " +
            $"{Math.Abs(driftSeconds)}s — i.e. it widened beyond the seeded offset.");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(2, "the reclaim increments the attempt counter");
    }

    [Theory]
    [MemberData(nameof(FreshZombieDriftOffsetsSeconds))]
    public async Task Fresh_processing_document_inside_the_zombie_threshold_is_not_reclaimed_under_various_drift_offsets(
        int driftSeconds)
    {
        // Broader drift-coverage Theory complementing the canonical
        // boundary-tight Fact above (#62). The Fact pins ONE point
        // (-4m30s) against ±1-min drift; this Theory adds diversity
        // exemplars across the fresh region. The -270 boundary echo
        // carries the regression-discriminator weight for a `5→4
        // narrowing` (under strict `<`, only a value > 240s past gets
        // wrongly reclaimed under a 4m threshold — see the MemberData
        // docstring above for the per-row regression-mode discussion).
        // The -240 / -30 rows add breadth across the middle and
        // recently-claimed ends of the fresh region. (#140)
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddSeconds(driftSeconds));
        var worker = BuildWorker();

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);

        claimed.Should().BeNull(
            $"ProcessingStartedAt={driftSeconds}s is inside the 5-min zombie window; a fresh doc " +
            "must not be stolen before the timeout. If this fires with a non-null Guid, the " +
            $"threshold has narrowed below {Math.Abs(driftSeconds)}s.");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(1, "the doc is untouched");
    }

    // ----- Session-TZ independence (the #26 fix) ----------------------------------------------

    /// <summary>
    /// Both directions of the bug surface. The pre-fix SQL mixed timestamptz with `now() at time
    /// zone 'utc'` (a `timestamp without time zone`), which Postgres bridged via the SESSION TZ —
    /// so the threshold and the writes drifted by the session offset. The drift sign depends on
    /// the offset direction:
    /// <list type="bullet">
    /// <item><c>America/New_York</c> (UTC-4/-5): RHS shifts hours into the FUTURE → fresh docs are wrongly reclaimed.</item>
    /// <item><c>Asia/Tokyo</c> (UTC+9): RHS shifts hours into the PAST → stale docs are wrongly NOT reclaimed.</item>
    /// </list>
    /// Driving each test under both zones means every test discriminates the bug from the fix
    /// under at least one zone (e.g. the stale-reclaim test passes under both old and new SQL when
    /// run under NY, but only the new SQL passes it under Tokyo — see comments on each fact).
    /// </summary>
    public static TheoryData<string> NonUtcSessionZones() => new()
    {
        "America/New_York",
        "Asia/Tokyo",
    };

    /// <summary>
    /// Runs <see cref="ExtractionWorker.ClaimSql"/> on a fresh connection whose session TimeZone
    /// is pinned to the given IANA zone. Proves the worker's SQL is correct regardless of session
    /// TZ. The trailing `RESET TIMEZONE` is belt-and-suspenders: Npgsql's pool-return runs
    /// `DISCARD ALL` by default which would clear the session TZ anyway, but a future change to
    /// `Pooling`/`No Reset On Close` settings could disable that reset and silently leak a non-UTC
    /// TZ into the next test's pooled connection. Resetting explicitly removes the dependency.
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

        try
        {
            await using var claim = conn.CreateCommand();
            claim.CommandText = ExtractionWorker.ClaimSql;
            await using var reader = await claim.ExecuteReaderAsync();
            return await reader.ReadAsync() ? reader.GetGuid(0) : (Guid?)null;
        }
        finally
        {
            // Explicit cleanup — see helper docstring.
            await using var reset = conn.CreateCommand();
            reset.CommandText = "RESET TIMEZONE";
            await reset.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [MemberData(nameof(NonUtcSessionZones))]
    public async Task Claim_under_non_UTC_session_reclaims_a_stale_processing_doc(string ianaZone)
    {
        // ProcessingStartedAt 10 min ago, well past the 5-min zombie threshold. Under the pre-fix
        // SQL with Asia/Tokyo (UTC+9), `now() at time zone 'utc'` produces a naive value that
        // Postgres re-casts to timestamptz using session TZ — pushing the comparison's RHS ~9h
        // into the PAST, so a 10-min-old doc fails `< (real_now - 9h5m)` and is NEVER reclaimed
        // (zombies stuck forever). Under New_York the RHS drifts into the future and this test
        // still passes under the broken SQL; the Tokyo branch is what catches that side of the bug.
        //
        // The -10m / -1m offsets are deliberately LOOSER than the boundary-tight #62 pair above:
        // the failure mode here is HOURS of TZ drift, not MINUTES of threshold drift, so a 30 s
        // envelope adds no discriminating power. Keeping the envelope loose isolates this test to
        // the single TZ-independence claim it's meant to pin.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-10));

        var claimed = await RunClaimUnderSessionTimeZoneAsync(ianaZone);

        claimed.Should().Be(docId, "stale doc must still be reclaimed under a non-UTC session TZ");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(NonUtcSessionZones))]
    public async Task Claim_under_non_UTC_session_does_not_reclaim_a_fresh_processing_doc(string ianaZone)
    {
        // ProcessingStartedAt 1 min ago, well inside the 5-min window. Under the pre-fix SQL with
        // America/New_York (UTC-4/-5), the RHS drifts ~4-5h into the future, so a 1-min-old doc
        // satisfies `< (real_now + 4h55m)` and is wrongly reclaimed (data corruption: another
        // worker steals it mid-extraction). Under Tokyo the broken SQL also holds the doc (RHS
        // drifts into the past), so this assertion is satisfied under both old and new SQL there;
        // the New_York branch is what catches this side of the bug.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-1));

        var claimed = await RunClaimUnderSessionTimeZoneAsync(ianaZone);

        claimed.Should().BeNull("a fresh doc must not be reclaimed under a non-UTC session TZ");
        (await GetDocAsync(docId)).ProcessingAttempts.Should().Be(1, "fresh doc is untouched");
    }

    [Theory]
    [MemberData(nameof(NonUtcSessionZones))]
    public async Task Claim_under_non_UTC_session_writes_ProcessingStartedAt_at_real_now_not_offset(string ianaZone)
    {
        // The symmetric write-side bug: under the pre-fix SQL, assigning `now() at time zone 'utc'`
        // (a naive timestamp) into the timestamptz `ProcessingStartedAt` column forced Postgres to
        // interpret the naive value in the session TZ. Under New_York the stored value drifted
        // ~4-5h into the future; under Tokyo ~9h into the past. Either way far outside the 30s
        // envelope. With timestamptz-to-timestamptz writes, the stored value equals real-now
        // regardless of session TZ — caught here under both zones.
        var (_, docId) = await SeedDocAsync(status: ExtractionStatus.Pending);
        var before = DateTime.UtcNow;

        var claimed = await RunClaimUnderSessionTimeZoneAsync(ianaZone);

        var after = DateTime.UtcNow;
        claimed.Should().Be(docId);
        var doc = await GetDocAsync(docId);
        doc.ProcessingStartedAt.Should().NotBeNull();
        // Generous skew window — the failure mode is hours, not seconds; a 30s envelope catches
        // the bug while staying robust against test-host timing jitter.
        doc.ProcessingStartedAt!.Value.Should().BeOnOrAfter(before.AddSeconds(-30));
        doc.ProcessingStartedAt!.Value.Should().BeOnOrBefore(after.AddSeconds(30));
    }

    // ----- Full-path TZ independence via NpgsqlDataSource initializer (the #61 follow-up) -----
    //
    // The strongest end-to-end regression for [ADR 0009](../../../docs/adr/0009-no-at-time-zone-on-timestamptz-in-raw-sql.md)
    // — "raw SQL against timestamptz columns uses bare now() / DateTime.UtcNow, never AT TIME ZONE".
    // The Claim_under_non_UTC_session_* family above pins the SQL string under a non-UTC session;
    // this test pins the production claim PATH (SystemDbContext → pooled physical connection →
    // raw SQL) by routing the worker through an NpgsqlDataSource whose every physical connection
    // has SET TIME ZONE applied at open.

    [Fact]
    public async Task Claim_via_full_DataSource_path_with_non_UTC_session_does_not_reclaim_fresh_doc()
    {
        // End-to-end variant of Claim_under_non_UTC_session_does_not_reclaim_a_fresh_processing_doc:
        // instead of pinning the session TZ on a hand-rolled NpgsqlConnection and running the raw
        // SQL string directly (which proves the SQL is TZ-independent), this test drives the full
        // production claim path — worker.ClaimNextAsync → scopeFactory.CreateAsyncScope →
        // SystemDbContext → pooled physical connection → raw SQL — through a connection whose
        // session TZ has been pinned by an NpgsqlDataSource initializer. Closes the path-coverage
        // gap the SQL-string-level tests cannot see (#61): if some other piece of the app installs
        // a connection-opened hook that sets a non-UTC TZ, or a future Npgsql change disables
        // DISCARD ALL by default, the worker MUST still claim correctly.
        const string ianaZone = "America/New_York";

        // Build a DataSource that pins SET TIME ZONE on every physical-connection open.
        // NoResetOnClose=true is strictly load-bearing once the SHOW-timezone probe below runs: the
        // probe causes a pool-return-and-reborrow cycle, and the connection initializer fires only
        // on a NEW physical open, not on a reuse. Without this flag the probe's return would
        // trigger DISCARD ALL, the worker's borrow would reuse the same physical connection at UTC,
        // and the test would silently pass even under pre-#26 SQL (UTC means no bridging). Keeping
        // the flag also matches the production scenario the #26 reviewer flagged — a non-UTC TZ
        // surviving DISCARD ALL because a future Npgsql config change disables the reset.
        var builder = new NpgsqlDataSourceBuilder(Fixture.ConnectionString);
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        builder.UsePhysicalConnectionInitializer(
            connectionInitializer: conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SET TIME ZONE '{ianaZone}'";
                cmd.ExecuteNonQuery();
            },
            connectionInitializerAsync: async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SET TIME ZONE '{ianaZone}'";
                await cmd.ExecuteNonQueryAsync();
            });
        await using var dataSource = builder.Build();

        // Sanity-check that the initializer actually pinned the TZ on a physical connection from
        // this DataSource before driving the worker. Without this guard, a future Npgsql/EF change
        // that quietly stops firing UsePhysicalConnectionInitializer on EF-wrapped opens would
        // silently degrade this test to "the SQL is correct under UTC" — already covered by the
        // SQL-string-level Theory — and the BeNull() assertion below would still pass.
        await using (var probe = dataSource.CreateConnection())
        {
            await probe.OpenAsync();
            await using var show = probe.CreateCommand();
            show.CommandText = "SHOW timezone";
            var probedTz = (string?)await show.ExecuteScalarAsync();
            probedTz.Should().Be(ianaZone,
                "the DataSource's physical-connection initializer must actually set the session TZ; "
                + "otherwise this test silently degrades into a UTC-only check already covered by "
                + "the Claim_under_non_UTC_session_* SQL-string-level Theory");
        }

        // Stand up a throw-away DI container so SystemDbContext resolves the test DataSource. We
        // deliberately don't override CustomWebApplicationFactory: (a) the worker only needs
        // SystemDbContext for the claim path — the rest of the host is irrelevant — and (b)
        // mutating the shared factory's DataSource would leak state into other tests in this
        // collection. A scoped ServiceProvider disposed at end-of-test cleans up cleanly. No
        // AuditSaveChangesInterceptor is wired because ClaimNextAsync issues raw SQL — it never
        // calls SaveChanges, so the interceptor would be unreachable.
        var services = new ServiceCollection();
        services.AddDbContext<SystemDbContext>(options => options.UseNpgsql(dataSource));
        await using var sp = services.BuildServiceProvider();
        var worker = new ExtractionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExtractionWorker>.Instance);

        // Same seed shape as the SQL-level fresh-doc test: ProcessingStartedAt 1 min ago, well
        // inside the 5-min zombie window. Pre-#26 SQL with America/New_York (UTC-4/-5) bridged
        // the threshold's RHS ~4-5h into the future via the session TZ, so a 1-min-old doc
        // satisfied `< (real_now + 4h55m)` and was wrongly reclaimed — another worker would have
        // stolen it mid-extraction. Post-#26 SQL is timestamptz on both sides and the comparison
        // is offset-independent, so the doc correctly stays unclaimed.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            processingStartedAt: DateTime.UtcNow.AddMinutes(-1));

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);

        claimed.Should().BeNull(
            "the claim path must be TZ-independent end-to-end: a fresh Processing doc must not be "
            + "reclaimed when the worker is driven through a SystemDbContext whose underlying "
            + "NpgsqlDataSource pins a non-UTC session TZ on every physical connection (#61)");
        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Processing, "the fresh doc's status must be untouched");
        doc.ProcessingAttempts.Should().Be(1, "the fresh doc's attempt counter must be untouched");
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

    [Fact]
    public async Task Document_whose_blob_vanished_from_storage_is_failed_safely_without_extracting()
    {
        // The sibling shape of the no-blob-path guard (#254): the row HAS a path but the blob
        // is gone from storage — DownloadAsync's null contract surfaces as a throw that routes
        // through the normal failure handling. Crucially, the failure happens BEFORE OCR/LLM
        // run, so a vanished blob never spends money.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        var blobs = Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();
        await blobs.DeleteAsync("blob/path/doc.pdf", default);
        var worker = BuildWorker();

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending, "a first failure below the cap is retried");
        doc.ProcessingError.Should().Contain("blob not found");
        Extraction.ExtractCallCount.Should().Be(0);
        Ocr.OcrCallCount.Should().Be(0, "the not-found path must fail before OCR spends money");
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
