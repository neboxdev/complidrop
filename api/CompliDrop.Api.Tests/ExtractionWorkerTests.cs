using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Tests.ExtractionFixtures;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Builds a worker bound to the host's DI (so it resolves the test DB + fakes). The optional
    /// <paramref name="attemptTimeout"/> overrides the per-attempt timeout so the timeout-path tests
    /// can use a sub-second budget instead of the production minimum. Pass a
    /// <see cref="ListLogger{T}"/> as <paramref name="logger"/> when the emitted log line is itself
    /// the contract under test (the #383 unreadable-field warning and its PII rule) — the same
    /// pattern <c>ReminderBackgroundServiceTests</c> uses for the #184 dead-letter flag.
    /// </summary>
    private ExtractionWorker BuildWorker(TimeSpan? attemptTimeout = null, ILogger<ExtractionWorker>? logger = null)
    {
        var worker = new ExtractionWorker(
            Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExtractionSettings()),
            logger ?? NullLogger<ExtractionWorker>.Instance);
        if (attemptTimeout is { } t) worker.AttemptTimeout = t;
        return worker;
    }

    /// <summary>
    /// Seeds an org (and optionally its subscription) plus a single document in the given state.
    /// Pass <paramref name="subscriptionSpendUsd"/> to seed a subscription — required for any path
    /// that reaches the cost check, since <c>CostTrackingService</c> denies orgs with no subscription.
    /// </summary>
    private async Task<(Guid OrgId, Guid DocId)> SeedDocAsync(
        ExtractionStatus status = ExtractionStatus.Pending,
        int attempts = 0,
        int failedAttempts = 0,
        string? blobPath = "blob/path/doc.pdf",
        DateTime? processingStartedAt = null,
        DateTime? createdAt = null,
        decimal? subscriptionSpendUsd = null,
        string plan = "free",
        string? documentType = null,
        ExtractionTrust extractionTrust = ExtractionTrust.Trusted)
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
            // Defaults to the entity's own "other" when unspecified. #373's blank-answer tests pass a
            // stored type explicitly, because what a blank extraction falls back TO is the contract.
            DocumentType = documentType ?? new Document().DocumentType,
            ExtractionStatus = status,
            // #459 / ADR 0052. Defaults to Trusted (a fresh upload has no distrust event on record); the
            // trust tests pass Distrusted to seed the state a re-armed distrusted document is really in —
            // Pending in the queue, still carrying the previous read's dissent.
            ExtractionTrust = extractionTrust,
            ProcessingAttempts = attempts,
            FailedAttempts = failedAttempts,
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

    /// <summary>
    /// Pins verdict #1 of the #243 concurrency audit: the per-attempt timeout ceiling must stay
    /// STRICTLY below the zombie-reclaim window baked into <see cref="ExtractionWorker.ClaimSql"/>,
    /// with a positive margin, so a slow-but-alive attempt is always cancelled (releasing its row
    /// lock) before a second worker could reclaim the same row and double-process it. Parses the
    /// threshold out of ClaimSql so a future edit to EITHER side — widening the ceiling or shrinking
    /// the interval — trips this test instead of silently reintroducing the double-process race.
    /// Pure (no DB); it just rides the class's fixture.
    /// </summary>
    [Fact]
    public void Attempt_timeout_ceiling_stays_below_the_zombie_reclaim_window()
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            ExtractionWorker.ClaimSql, @"interval '(\d+) minutes'");
        match.Success.Should().BeTrue("ClaimSql must define the zombie-reclaim window as an interval literal");
        var zombieSeconds = int.Parse(match.Groups[1].Value) * 60;

        ExtractionWorker.AttemptTimeoutCeilingSeconds.Should().BeLessThan(zombieSeconds,
            "a wedged attempt must be cancelled (releasing its row lock) before the zombie reclaim could double-grab the row");
        (zombieSeconds - ExtractionWorker.AttemptTimeoutCeilingSeconds).Should().BeGreaterThanOrEqualTo(60,
            "keep a >=60s margin so a timed-out attempt can cancel AND requeue before a second worker reclaims");
    }

    /// <summary>
    /// The zombie threshold now has a SECOND consumer outside this worker: <c>DocumentEndpoints.Reextract</c>
    /// refuses to re-arm a document while its claim is still live, and reads
    /// <see cref="ExtractionWorker.ZombieClaimTimeout"/> to decide which claims those are
    /// (<see href="https://github.com/neboxdev/complidrop/issues/365">#365</see>). That only makes the two
    /// sides agree while <see cref="ExtractionWorker.ClaimSql"/> is BUILT from the same constant — so this
    /// pins the SQL text back against it. A future re-inline of <c>interval '5 minutes'</c> alongside a
    /// changed constant fails here rather than silently re-opening the window where the endpoint re-queues
    /// a document a live worker still holds (double OCR + LLM spend, duplicated DocumentField rows).
    /// Pure (no DB); it just rides the class's fixture.
    /// </summary>
    [Fact]
    public void The_claim_SQL_interval_is_the_zombie_timeout_the_reextract_guard_reads()
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            ExtractionWorker.ClaimSql, @"interval '(\d+) minutes'");
        match.Success.Should().BeTrue("ClaimSql must define the zombie-reclaim window as an interval literal");

        TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value))
            .Should().Be(ExtractionWorker.ZombieClaimTimeout,
                "the claim SQL and the constant the re-extract guard reads must be one value, not two copies");
    }

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
        // Each cycle is one claim (ProcessingAttempts++) and one genuine failure (FailedAttempts++),
        // so both reach the budget here. It's FailedAttempts that gates the stop (#259, problem 2).
        doc.FailedAttempts.Should().Be(ExtractionWorker.MaxAttempts);
        doc.ProcessingAttempts.Should().Be(ExtractionWorker.MaxAttempts);
        doc.ProcessingError.Should().StartWith("extraction.failed");
    }

    // ----- AC2: up-front zombie guard --------------------------------------------------------

    [Fact]
    public async Task Document_past_the_claims_backstop_is_failed_up_front_without_reprocessing()
    {
        // ProcessingAttempts (total claims) over the crash-loop backstop — e.g. the doc kills the
        // process mid-extraction every time, so the failure handler never runs and FailedAttempts
        // stays 0. The up-front backstop must fail it before any OCR/LLM work, on the claim count
        // alone, even though it has zero genuine failures (#259, problem 2).
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: ExtractionWorker.MaxClaims + 1,
            failedAttempts: 0,
            processingStartedAt: DateTime.UtcNow,
            subscriptionSpendUsd: 0m);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed);
        doc.ProcessingError.Should().StartWith("extraction.too_many_attempts");
        Extraction.ExtractCallCount.Should().Be(0, "the backstop must short-circuit before extraction");
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

    // ----- #383: a canonical value we couldn't parse routes the document to a human --------------

    /// <summary>
    /// A high-confidence, no-reprocess-signal extraction whose expiration_date is in a shape nothing
    /// can parse — the #383 scenario. Confidence is deliberately ABOVE the 0.7 manual-review gate so
    /// the test proves the new unreadable-field trigger, not the pre-existing low-confidence one.
    /// </summary>
    private static ExtractionResult ResultWith(string field, string value) => new(
        DocumentType: "coi",
        DocumentSubType: null,
        Fields: [new ExtractedField(field, value, "string", 0.95)],
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 100, OutputTokens: 50, EstimatedCostUsd: 0.01m));

    [Theory]
    [InlineData("expiration_date", "12/31/2026 (per endorsement)")]
    [InlineData("expiration_date", "continuous until cancelled")]
    [InlineData("effective_date", "see attached endorsement")]
    [InlineData("general_liability_limit", "1M per occurrence")]
    public async Task An_unparseable_canonical_field_routes_the_document_to_manual_review(string field, string value)
    {
        // Without this the document lands Completed and looks perfectly healthy — high confidence, no
        // reprocess flag, no processing error — while the typed column it needed sits silently null.
        // Nothing else in the pipeline notices: the model was confident, it just wrote a date shape
        // the parser can't read. ManualRequired is what puts it in front of a human, and it is the
        // ONLY backstop when the org's checklist carries no rule on that field at all.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWith(field, value);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "an unreadable compliance-critical value must reach a human, not sit as a silent null");
        doc.ExtractionConfidence.Should().BeGreaterThan(0.7,
            "the flag must come from the unreadable value, not from the low-confidence gate");
        doc.ProcessingError.Should().BeNull("this is not an extraction FAILURE — the read succeeded");
    }

    [Fact]
    public async Task A_parseable_canonical_field_still_completes_normally()
    {
        // The control: the same shape of extraction with a readable date must NOT be dragged into
        // manual review, or every document would demand attention and the signal would be worthless.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWith("expiration_date", "2027-03-15");
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        doc.ExpirationDate.Should().Be(new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task A_blank_canonical_field_does_not_trigger_manual_review()
    {
        // An absent value is an honest reading of a certificate that shows none — distinct from a
        // value we couldn't read. Only the second is a defect worth a human's time.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWith("expiration_date", "");
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        doc.ExpirationDate.Should().BeNull();
    }

    [Fact]
    public async Task The_unreadable_field_warning_names_the_field_and_never_logs_its_value()
    {
        // The warning carries an explicit PII contract in its own comment — field NAMES only, never
        // values, because extracted field values are document PII and must not reach logs/Sentry
        // (CLAUDE.md § frontend error monitoring, applied to the backend's structured logs). Nothing
        // pinned it while every worker test built with NullLogger, so the contract could be broken by
        // adding one interpolated value and stay green (#383 review, S3).
        const string documentValue = "12/31/2026 (per endorsement)";
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWith("expiration_date", documentValue);
        var logger = new ListLogger<ExtractionWorker>();

        await BuildWorker(logger: logger).ProcessDocumentAsync(docId, CancellationToken.None);

        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired);
        logger.Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("could not parse"))
            .Which.Message.Should().Contain("expiration_date",
                "the operator has to know WHICH field needs a human, or the warning is unactionable");
        logger.Entries.Should().NotContain(e => e.Message.Contains(documentValue),
            "an extracted field VALUE is document PII and must never reach the logs");
    }

    [Fact]
    public async Task A_canonical_field_emitted_twice_is_judged_on_its_last_value()
    {
        // Everything else in PersistSuccess is last-value-wins (the JSON mirror, the typed column),
        // and so is the sibling writer (DocumentEndpoints.UpdateFields, "last value wins"). The
        // unreadable set used to accumulate on ANY occurrence, so a model that emitted expiration_date
        // twice — unreadable, then readable — parsed its column correctly and was STILL routed to
        // manual review, on the strength of a value the document no longer holds (#383 review, S4).
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = new ExtractionResult(
            DocumentType: "coi",
            DocumentSubType: null,
            Fields:
            [
                new ExtractedField("expiration_date", "12/31/2026 (per endorsement)", "string", 0.95),
                new ExtractedField("expiration_date", "2026-12-31", "string", 0.95)
            ],
            NeedsReprocessing: false,
            Usage: new ExtractionUsage(InputTokens: 100, OutputTokens: 50, EstimatedCostUsd: 0.01m));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExpirationDate.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            "the LAST value is the one that landed in the typed column");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "the value the document actually carries is readable, so there is nothing for a human to fix");
    }

    [Theory]
    [InlineData("expiration_date", "12/31/2026 (per endorsement)")]
    [InlineData("expiration_date", "2027-03-15")]
    [InlineData("expiration_date", "")]
    [InlineData("effective_date", "see attached endorsement")]
    [InlineData("effective_date", "2026-01-01")]
    [InlineData("general_liability_limit", "1M per occurrence")]
    [InlineData("general_liability_limit", "$1,000,000")]
    [InlineData("policy_number", "GL-99887766")]
    public async Task The_extraction_path_and_the_document_state_predicate_agree_on_unreadability(
        string field, string value)
    {
        // "Does this document carry an unreadable canonical value?" used to be answered by TWO
        // mechanisms — a per-field TypedColumnResult accumulated here on the extraction path, and the
        // DocumentFieldReadability predicate on the request path — with nothing pinning them equal
        // (#383 review round 2, S5). The repo pins every other mirrored predicate exactly this way
        // (PlanLimitConsistencyTests, ExportService.SupersededIds), so this is the established pattern.
        //
        // PersistSuccess now COLLAPSES onto the shared predicate rather than keeping a second copy, so
        // this asserts the collapse is behaviour-preserving across the readable / unreadable / blank /
        // non-canonical cases — and goes red if a future edit re-introduces an independent mechanism
        // that disagrees. ResultWith pins confidence at 0.95 with no reprocess signal, so the
        // unreadable trigger is the ONLY thing that can set ManualRequired here.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWith(field, value);

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        var routedToReview = doc.ExtractionStatus == ExtractionStatus.ManualRequired;
        DocumentFieldReadability.HasUnreadableCanonicalValue(doc).Should().Be(routedToReview,
            "the state the persisted document is IN must match the reason the worker routed it (or didn't)");
    }

    [Fact]
    public async Task Successful_extraction_writes_one_system_document_processed_audit_event()
    {
        // #318 FP-043: extraction finishes in the worker, which has no ICurrentUser, so the audit
        // interceptor can't record it — PersistSuccess writes an explicit system "document.processed"
        // row (org-scoped, UserId null) so the read appears in the activity feed. Exactly one per
        // successful read.
        var (orgId, docId) = await SeedDocAsync(status: ExtractionStatus.Processing, subscriptionSpendUsd: 0m);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);
        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.Completed);

        await using var db = CreateSystemDb();
        var processed = await db.AuditLogs
            .Where(a => a.Action == "document.processed" && a.EntityId == docId)
            .ToListAsync();
        processed.Should().ContainSingle("a successful read writes exactly one document.processed event");
        processed[0].OrganizationId.Should().Be(orgId);
        processed[0].UserId.Should().BeNull("the background worker has no current user");
    }

    [Fact]
    public async Task A_failed_extraction_writes_no_document_processed_audit_event()
    {
        // The system event is for terminal SUCCESS only — a failed read (here a missing blob) must
        // never claim the document was processed.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing, blobPath: null, subscriptionSpendUsd: 0m);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        await using var db = CreateSystemDb();
        (await db.AuditLogs.AnyAsync(a => a.Action == "document.processed" && a.EntityId == docId))
            .Should().BeFalse("a failed extraction must not emit a processed event");
    }

    // ----- #401: per-field confidence gate on the verdict-bearing fields ----------------------

    /// <summary>
    /// Builds a multi-field extraction with an explicit per-field confidence — the shape the #401 gate
    /// operates on. NeedsReprocessing false and every value READABLE, so the ONLY thing that can route a
    /// document to manual review here is the confidence gate (average OR per-verdict-bearing-field), never
    /// the model's reprocess signal or the #383 unreadable trigger.
    /// </summary>
    private static ExtractionResult ResultWithFields(params (string Name, string Value, double Confidence)[] fields) => new(
        DocumentType: "coi",
        DocumentSubType: null,
        Fields: fields.Select(f => new ExtractedField(f.Name, f.Value, "string", f.Confidence)).ToArray(),
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 100, OutputTokens: 50, EstimatedCostUsd: 0.01m));

    /// <summary>
    /// EVERY member of <see cref="VerdictBearingFields.All"/>, each paired with a VALID/parseable sample
    /// value for its type. Iterating the production set (not a hand-picked few) means a field ADDED to the
    /// gate in future is auto-covered by the Theory below; a field REMOVED is caught by
    /// <see cref="Every_expected_verdict_bearing_field_stays_in_the_gate"/> (which pins a fixed baseline,
    /// independent of the set, so a shrinking set can't silently drop its own coverage).
    /// </summary>
    public static IEnumerable<object[]> VerdictBearingFieldSamples() =>
        VerdictBearingFields.All.Select(name => new object[] { name, SampleValueFor(name) });

    /// <summary>
    /// A VALID, parseable sample value for a verdict-bearing field, keyed by type: the two date fields
    /// parse to a DateTime, every <c>*_limit</c> money field to an amount, <c>additional_insured</c> is free
    /// text. Parseable is the POINT — an unparseable CANONICAL value would route the document via the #383
    /// unreadable trigger instead of the confidence gate, making the gate test vacuous. A future member of
    /// <see cref="VerdictBearingFields.All"/> that fits none of these buckets throws loudly here, forcing its
    /// sample value to be chosen deliberately rather than defaulted to a wrong-typed string.
    /// </summary>
    private static string SampleValueFor(string field) => field switch
    {
        CanonicalDocumentFields.EffectiveDate or CanonicalDocumentFields.ExpirationDate => "2027-03-15",
        "additional_insured" => "Riverside Event Hall",
        _ when field.EndsWith("_limit", StringComparison.OrdinalIgnoreCase) => "2000000",
        _ => throw new ArgumentOutOfRangeException(nameof(field),
            $"No sample value mapping for verdict-bearing field '{field}'. Add one when extending VerdictBearingFields.All."),
    };

    [Theory]
    [MemberData(nameof(VerdictBearingFieldSamples))]
    public async Task A_low_confidence_verdict_bearing_field_routes_to_manual_review_even_when_the_average_clears_the_gate(
        string field, string value)
    {
        // #401: the field that DECIDES a verdict (a limit, an effective/expiration date, the
        // additional-insured party) came back at 0.3 — the machine barely read it — but four 0.95 incidental
        // fields pull the AVERAGE to ~0.82, well clear of the 0.7 gate. Pre-#401 this landed Completed and
        // rolled up to "Covered"; averaging hid the one field a mis-read flips the verdict on. Data-driven
        // over EVERY member of VerdictBearingFields.All (not a hand-picked four), so every coverage-limit
        // entry is exercised too — the sample value is READABLE (asserted below), so the #383 unreadable
        // trigger can't be what fires here; the new per-field CONFIDENCE gate is.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWithFields(
            (field, value, 0.3),
            ("policyholder_name", "Acme Vendor", 0.95),
            ("insurer_name", "Acme Insurance", 0.95),
            ("policy_number", "GL-12345", 0.95),
            ("certificate_holder", "Riverside Event Hall", 0.95));
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionConfidence.Should().BeGreaterThan(ExtractionWorker.ManualReviewConfidenceThreshold,
            "the field AVERAGE clears the gate — the per-field verdict-bearing trigger is what routes it");
        // Rules out the OTHER two ManualRequired triggers so the verdict is attributable ONLY to the
        // per-field confidence gate: the average is proven clear above, ResultWithFields sets
        // NeedsReprocessing=false, and the sample value is parseable so there's no #383 unreadable value.
        DocumentFieldReadability.HasUnreadableCanonicalValue(doc).Should().BeFalse(
            "the sample value is parseable, so ManualRequired is attributable to the CONFIDENCE gate, not the #383 unreadable trigger");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.ManualRequired,
            "a low-confidence verdict-bearing field the average hid must still reach a human (#401)");
        doc.ProcessingError.Should().BeNull("this is not an extraction FAILURE — the read succeeded, just at low confidence");
    }

    [Theory]
    [InlineData("effective_date")]
    [InlineData("expiration_date")]
    [InlineData("general_liability_limit")]
    [InlineData("auto_liability_limit")]
    [InlineData("professional_liability_limit")]
    [InlineData("umbrella_limit")]
    [InlineData("liquor_liability_limit")]
    [InlineData("workers_comp_limit")]
    [InlineData("additional_insured")]
    public void Every_expected_verdict_bearing_field_stays_in_the_gate(string field)
    {
        // The removal backstop for the data-driven gate Theory above. That Theory ITERATES
        // VerdictBearingFields.All, so it auto-covers a field ADDED to the set — but a field REMOVED from
        // All simply drops its Theory case and would slip by silently. This fixed baseline (nine literals,
        // NOT read from the set) catches exactly that: drop any of these from All and its row here goes red,
        // because the #401 per-field gate would stop protecting that verdict-bearing field. Adding a tenth
        // member keeps every row green (a superset is fine) while the gate Theory picks the new one up —
        // both goals at once. Uses the production predicate (VerdictBearingFields.Contains), not a copy.
        VerdictBearingFields.Contains(field).Should().BeTrue(
            $"'{field}' must stay gated by the #401 per-field confidence check");
    }

    [Fact]
    public async Task A_low_confidence_non_verdict_bearing_field_does_not_route_to_manual_review()
    {
        // The gate is SCOPED to verdict-bearing fields: an incidental field (certificate_holder, policy
        // number) that no rule grades doesn't flip a verdict, so a low read on it must NOT drag the document
        // into review — otherwise every certificate with one fuzzy incidental field would demand attention
        // and the signal would be worthless. The verdict-bearing fields here are all high-confidence, and the
        // AVERAGE (~0.79) clears the gate, so the per-field SCOPE is the only thing under test.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWithFields(
            ("certificate_holder", "Riverside Event Hall", 0.3),
            ("policy_number", "GL-12345", 0.95),
            ("expiration_date", "2027-03-15", 0.95),
            ("general_liability_limit", "2000000", 0.95));
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionConfidence.Should().BeGreaterThan(ExtractionWorker.ManualReviewConfidenceThreshold,
            "the average clears the gate, so this isolates the per-field SCOPE, not the average trigger");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "a low-confidence field that backs no verdict must not route to manual review (#401 is scoped)");
    }

    [Theory]
    [InlineData(0.70, ExtractionStatus.Completed)]      // AT the threshold: the gate is `< 0.7`, so 0.70 is trusted
    [InlineData(0.69, ExtractionStatus.ManualRequired)] // just below: routed to a human
    public async Task The_per_field_confidence_gate_is_exclusive_at_the_shared_threshold(
        double confidence, ExtractionStatus expected)
    {
        // The verdict-bearing field (expiration_date) sits AT the boundary; three 0.99 incidental fields keep
        // the AVERAGE above 0.7 in BOTH rows, so the per-field gate — not the average gate — is the only
        // thing that can move the 0.69 row to ManualRequired. Pins the threshold as EXCLUSIVE (0.70 trusted,
        // 0.69 not), matching the average gate's `avgConf < threshold`; both reference the SAME shared
        // ExtractionWorker.ManualReviewConfidenceThreshold constant so the two gates can't drift apart.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWithFields(
            ("expiration_date", "2027-03-15", confidence),
            ("policyholder_name", "Acme Vendor", 0.99),
            ("insurer_name", "Acme Insurance", 0.99),
            ("policy_number", "GL-12345", 0.99));
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionConfidence.Should().BeGreaterThan(ExtractionWorker.ManualReviewConfidenceThreshold,
            "padding keeps the average above the gate in both rows, isolating the per-field trigger");
        doc.ExtractionStatus.Should().Be(expected);
    }

    // ----- #459 / ADR 0052: the worker writes extraction TRUST beside pipeline position ---------

    [Theory]
    [InlineData(0.99, ExtractionStatus.Completed, ExtractionTrust.Trusted)]
    [InlineData(0.60, ExtractionStatus.ManualRequired, ExtractionTrust.Distrusted)]
    public async Task PersistSuccess_writes_trust_from_the_same_decision_that_sets_the_status(
        double verdictFieldConfidence, ExtractionStatus expectedStatus, ExtractionTrust expectedTrust)
    {
        // ONE boolean, TWO columns. Pipeline POSITION and extraction TRUST were the same column until
        // #459, which is why Reextract's re-arm could destroy the distrust. They must stay in lockstep at
        // the writer even though they are now separate: a review-routed read that forgot to withdraw trust
        // would roll a distrusted verdict up to Covered — the exact ADR 0042 hole — while a clean read that
        // forgot to restore it would strand the document at ActionNeeded forever.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = ResultWithFields(
            ("expiration_date", "2027-03-15", verdictFieldConfidence),
            ("policyholder_name", "Acme Vendor", 0.99),
            ("insurer_name", "Acme Insurance", 0.99),
            ("policy_number", "GL-12345", 0.99));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(expectedStatus);
        doc.ExtractionTrust.Should().Be(expectedTrust);
    }

    [Fact]
    public async Task A_clean_re_read_restores_the_trust_a_previous_read_withdrew()
    {
        // The state a re-armed distrusted document is really in: Pending in the queue (Reextract moved
        // pipeline position) while still carrying the previous read's dissent (Reextract does not touch
        // trust — that absence IS the #459 fix). A clean re-read must EARN the trust back, because it is
        // what stops the flag being sticky the way IsManuallyVerified was: this is the only writer besides
        // a human confirmation that can clear a distrust, and without it the document could never return
        // to Covered on its own however well the machine read it.
        var (_, docId) = await SeedDocAsync(
            subscriptionSpendUsd: 0m, extractionTrust: ExtractionTrust.Distrusted);
        Extraction.Result = ResultWith("expiration_date", "2027-03-15");

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
            "a clean read of the document is exactly what makes its values trustworthy again");
    }

    [Fact]
    public async Task Trust_survives_a_retry_and_is_withdrawn_only_when_the_failure_is_terminal()
    {
        // RecordFailedAttempt writes trust on its TERMINAL arm only. While retries remain the document is
        // merely back in the queue and its basis is unchanged — whatever read it last still stands — so
        // distrusting it over one transient hiccup would sink a legitimately-covered vendor for the length
        // of the retry cycle, the same over-reach ADR 0042 Amendment 2's Pending/Processing carve-out
        // refused. Once the budget is spent, though, the read the user asked for will never happen, and an
        // extraction the system could not COMPLETE is at least as untrustworthy as one it distrusted.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.ThrowOnExtract = true;
        var worker = BuildWorker();

        // One genuine failure, budget intact: back to Pending, trust untouched.
        var firstClaim = await worker.ClaimNextAsync(CancellationToken.None);
        await worker.ProcessDocumentAsync(firstClaim!.Value, CancellationToken.None);
        var afterOne = await GetDocAsync(docId);
        afterOne.ExtractionStatus.Should().Be(ExtractionStatus.Pending, "the budget is not spent yet");
        afterOne.ExtractionTrust.Should().Be(ExtractionTrust.Trusted,
            "a requeued attempt says nothing about the values already on the row");

        // …then spend the rest of the budget.
        for (var i = 1; i < ExtractionWorker.MaxAttempts; i++)
        {
            var claimed = await worker.ClaimNextAsync(CancellationToken.None);
            claimed.Should().NotBeNull("the doc is still queued until the budget is spent");
            await worker.ProcessDocumentAsync(claimed.Value, CancellationToken.None);
        }

        var terminal = await GetDocAsync(docId);
        terminal.ExtractionStatus.Should().Be(ExtractionStatus.Failed);
        terminal.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "a read that will never complete leaves the stored verdict with no trustworthy basis (#365)");
    }

    [Fact]
    public async Task MarkFailed_withdraws_trust_too()
    {
        // The OTHER terminal-failure writer. MarkFailed is reached by the crash-loop backstop, the
        // cost-ceiling short circuit and every NonRetryableExtractionException, and it must not be the one
        // Failed route that leaves a stale Trusted behind — the rollup reads trust and nothing else now, so
        // a gap here is a silent Covered on a document the system never managed to read.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: ExtractionWorker.MaxClaims + 1,
            processingStartedAt: DateTime.UtcNow,
            subscriptionSpendUsd: 0m);

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed);
        doc.ProcessingError.Should().StartWith("extraction.too_many_attempts");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted);
    }

    /// <summary>
    /// Commits the write a human's "Mark verified" (or a mid-read escalation) would land while the worker
    /// is out at OCR + the LLM: a DIFFERENT connection, a real commit, and the worker's tracked snapshot
    /// left stale. Constructed rather than raced, so the interleaving is the test's, not the scheduler's.
    /// </summary>
    private Func<Task> CommitTrustMidExtract(Guid docId, ExtractionTrust trust) => async () =>
    {
        await using var other = CreateSystemDb();
        var doc = await other.Documents.SingleAsync(d => d.Id == docId);
        doc.ExtractionTrust = trust;
        doc.IsManuallyVerified = trust == ExtractionTrust.Trusted;
        await other.SaveChangesAsync();
    };

    [Theory]
    // The worker's decision equals the value its MINUTES-OLD snapshot already held, while the row itself
    // moved to the opposite value mid-read. Both directions matter and both are one bug:
    [InlineData(0.60, ExtractionTrust.Distrusted)] // a review-routed read must re-withdraw a mid-read confirmation
    [InlineData(0.99, ExtractionTrust.Trusted)]    // a clean read must restore trust over a mid-read escalation
    public async Task PersistSuccess_forces_its_trust_decision_over_a_write_that_landed_mid_extraction(
        double verdictFieldConfidence, ExtractionTrust workerDecides)
    {
        // ProcessDocumentAsync loads the document BEFORE the OCR + LLM round trip and holds that tracked
        // snapshot for the minutes the read takes. EF Core emits only properties whose current value
        // DIFFERS from the snapshot, so assigning the snapshot's own value produces NO SET clause — and
        // the row has moved: PUT /verify committed the opposite value on another connection. Without
        // SetTrust's IsModified the extraction that was supposed to RE-DECIDE trust silently leaves the
        // other writer's value standing, landing ManualRequired+Trusted (a distrusted basis rolling up as
        // Covered — the ADR 0042 hole) or Completed+Distrusted (a clean read stranded at ActionNeeded).
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m, extractionTrust: workerDecides);
        Extraction.Result = ResultWithFields(
            ("expiration_date", "2027-03-15", verdictFieldConfidence),
            ("policyholder_name", "Acme Vendor", 0.99),
            ("insurer_name", "Acme Insurance", 0.99),
            ("policy_number", "GL-12345", 0.99));
        var landsMidRead = workerDecides == ExtractionTrust.Trusted
            ? ExtractionTrust.Distrusted
            : ExtractionTrust.Trusted;
        Extraction.DuringExtract = CommitTrustMidExtract(docId, landsMidRead);

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionTrust.Should().Be(workerDecides,
            "the read that just happened IS the document's basis, so its verdict on trust must reach the "
            + "row even when it matches a snapshot the row has moved on from (#459)");
        doc.ExtractionStatus.Should().Be(
            workerDecides == ExtractionTrust.Trusted
                ? ExtractionStatus.Completed
                : ExtractionStatus.ManualRequired,
            "the two columns stay in lockstep at the writer — one boolean, two columns");
    }

    [Theory]
    // MarkFailed (the non-retryable arm) and RecordFailedAttempt's TERMINAL arm, same shape as above: both
    // write Distrusted, and both can be handed a snapshot that already read Distrusted before a human
    // confirmed the document mid-attempt.
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_terminal_failure_forces_its_distrust_over_a_confirmation_that_landed_mid_attempt(
        bool nonRetryable)
    {
        // The failure writers share ProcessDocumentAsync's stale snapshot, so they share the no-op. A
        // Failed row reading Trusted is precisely what MarkFailed's comment says can only come from a
        // human confirmation AFTER the failure — this is the path that made it a lie.
        var (_, docId) = await SeedDocAsync(
            subscriptionSpendUsd: 0m,
            failedAttempts: nonRetryable ? 0 : ExtractionWorker.MaxAttempts - 1,
            extractionTrust: ExtractionTrust.Distrusted);
        Extraction.ThrowNonRetryable = nonRetryable;
        Extraction.ThrowOnExtract = !nonRetryable;
        Extraction.DuringExtract = CommitTrustMidExtract(docId, ExtractionTrust.Trusted);

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed, "precondition: the budget is spent");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "a read the system could not complete must withdraw trust even when the snapshot it was "
            + "holding already said Distrusted (#459)");
    }

    // ----- #373: the model's documentType is normalized before it overwrites the stored type ----

    /// <summary>
    /// A minimal successful extraction that returns <paramref name="documentType"/> verbatim. Every field
    /// is high-confidence and parseable, and NeedsReprocessing is false, so nothing but the type under
    /// test can move the document off <see cref="ExtractionStatus.Completed"/> — which is what makes
    /// "the over-length type did not throw 22001" a real assertion rather than an accident.
    /// </summary>
    private static ExtractionResult TypedResult(string? documentType, string? subType = null) => new(
        DocumentType: documentType,
        DocumentSubType: subType,
        Fields: [new ExtractedField("policy_number", "POL-1", "string", 0.95)],
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 100, OutputTokens: 50, EstimatedCostUsd: 0.01m));

    [Theory]
    [InlineData("coi", "coi")]                        // canonical: passes through unmangled
    [InlineData("license", "license")]
    [InlineData("COI", "coi")]                        // the ticket's headline shape
    [InlineData("Coi", "coi")]                        // mixed case
    [InlineData("  Permit  ", "permit")]              // padded
    [InlineData("Certificate of Insurance", "other")] // plausible prose from the alternate provider
    [InlineData("menu", "other")]                     // unknown vocabulary
    public async Task The_extracted_document_type_is_stored_canonically(string extracted, string expected)
    {
        // The stored string decides WHICH rules apply (ComplianceCheckService's ordinal filter) and WHICH
        // documents share a supersession group (DocumentSupersession's (VendorId, DocumentType) key), so a
        // verbatim "COI" is not a display wart — it grades against zero rules forever and never supersedes
        // the cert it renews. Normalizing at the single persist choke point is the fix (#373).
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = TypedResult(extracted);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.DocumentType.Should().Be(expected);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed, "normalization is not a failure path");
    }

    [Fact]
    public async Task An_over_length_document_type_is_coerced_instead_of_throwing_22001()
    {
        // DocumentType is varchar(100) (ModelConfiguration): pre-#373 a runaway value went straight into
        // PersistSuccess's single SaveChanges and threw Postgres 22001, which the worker counts as a
        // failure and RETRIES — re-paying Document AI + LLM cost on every doomed attempt until the budget
        // is spent. Because Normalize only ever returns a member of the vocabulary, the column is
        // length-safe by construction; assert the document actually COMPLETED (not merely that the string
        // is short), since a 22001 would have left it Pending with a ProcessingError instead.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = TypedResult(new string('x', 5_000));
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.DocumentType.Should().Be(CanonicalDocumentTypes.Fallback);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        doc.ProcessingError.Should().BeNull("a 22001 would surface here as a counted extraction failure");
        doc.FailedAttempts.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_extracted_type_keeps_the_stored_type_instead_of_demoting_it(string? extracted)
    {
        // documentType is `required` in both providers' structured-output schemas, so a blank answer is a
        // protocol violation carrying no information — NOT a positive classification of "other". The stored
        // type is normally the uploader's own pick from the type dropdown (and is passed to the model as
        // its type hint), so overwriting a deliberate "license" with "other" would drop every license rule
        // from the checklist and strand the document at Pending — the very silent-never-graded outcome
        // #373 closes. (Pre-#373 this wrote the blank string ITSELF into the column, which matched no
        // typed rule either.)
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m, documentType: "license");
        Extraction.Result = TypedResult(extracted);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        (await GetDocAsync(docId)).DocumentType.Should().Be("license");
    }

    [Theory]
    [InlineData("other")]      // the value the worker used to pre-filter to null with a raw literal
    [InlineData("OTHER")]      // …which that ordinal literal missed anyway
    [InlineData("coi")]
    [InlineData("Certificate of Insurance")]  // an ADR 0045 un-laundered legacy row
    public async Task The_worker_hands_the_stored_type_to_the_one_hint_owner_verbatim(string storedType)
    {
        // "Emit the hint line only for a real vocabulary member" has ONE owner: BuildUserPrompt, which
        // normalizes through CanonicalDocumentTypes and prints the VOCABULARY's spelling (#384, ADR 0051).
        // The worker used to pre-filter with its own `doc.DocumentType == "other" ? null : …` — a third
        // copy of that rule, spelled as a raw ordinal literal. Redundant rather than wrong, but a rule
        // with two owners is one drift away from a hint line nobody meant to emit.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m, documentType: storedType);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        Extraction.LastDocumentTypeHint.Should().Be(storedType,
            "the stored type reaches the builder verbatim — the call site decides nothing");

        // …and the ONE owner still suppresses what must be suppressed, on the value it actually received.
        var prompt = ExtractionPrompts.BuildUserPrompt("POLICY TEXT", Extraction.LastDocumentTypeHint);
        var expected = CanonicalDocumentTypes.Normalize(storedType) != CanonicalDocumentTypes.Fallback;
        prompt.Contains("Document type hint:", StringComparison.Ordinal).Should().Be(expected);
    }

    [Fact]
    public async Task A_blank_extracted_type_still_launders_a_non_canonical_stored_type()
    {
        // The other half of the fallback: preserving the stored type must not preserve a BAD stored type.
        // A "COI" written by an API client before the ingress paths validated (#389's half) is still
        // normalized on its way through, so the blank-answer path can never leave a value that grades
        // against zero rules.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m, documentType: "COI");
        Extraction.Result = TypedResult(null);
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        (await GetDocAsync(docId)).DocumentType.Should().Be("coi");
    }

    /// <summary>
    /// Parses a RAW provider payload through the real client's own <c>MapResult</c> (stub HTTP handler,
    /// no network) and hands the parsed result to the worker's extraction seam. The shapes under test
    /// have to travel this path: an OMITTED property and a JSON null are what a provider actually emits
    /// when it violates its schema's <c>required</c>, and a hand-built <see cref="ExtractionResult"/>
    /// cannot express the difference between "the provider said nothing" and "the provider said other".
    /// </summary>
    private static async Task<ExtractionResult> MapThroughProviderAsync(string provider, JsonObject payload) =>
        provider == "gemini"
            ? await ExtractionClientBuilder
                .Gemini(new StubHttpMessageHandler(HttpStatusCode.OK,
                    ExtractionFixtureHarness.GeminiResponseFromPayload(payload).ToJsonString()))
                .ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default)
            : await ExtractionClientBuilder
                .Anthropic(new StubHttpMessageHandler(HttpStatusCode.OK,
                    ExtractionFixtureHarness.AnthropicResponseFromPayload(payload).ToJsonString()))
                .ExtractAsync(ExtractionClientBuilder.Ocr(), null, "application/pdf", null, default);

    private static JsonObject PayloadWithDocumentTypeShape(string shape)
    {
        var payload = new JsonObject
        {
            ["needsReprocessing"] = false,
            ["fields"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "permit_number", ["value"] = "P-1", ["type"] = "text", ["confidence"] = 0.95,
                },
            },
        };
        switch (shape)
        {
            case "absent": break;                              // the property is simply not there
            case "null": payload["documentType"] = null; break; // present, JSON null
            case "empty": payload["documentType"] = ""; break;
            case "other": payload["documentType"] = "other"; break;
            // Present, but not a STRING. JsonElement.GetString() throws InvalidOperationException on a
            // number/bool/object, so this shape used to escape the "a schema is the provider's promise,
            // not ours" hardening entirely: the extraction threw, the worker counted a failure and
            // retried, and the full paid Document AI + LLM retry budget burned before the document
            // landed Failed. A non-string is a non-answer, exactly like an absent property.
            case "wrongType": payload["documentType"] = 7; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown payload shape");
        }
        return payload;
    }

    [Theory]
    // The shape a provider ACTUALLY produces when it violates `required`: the property is omitted. Both
    // clients used to coerce that into the literal "other" (`dt.GetString() ?? "other" : "other"`) BEFORE
    // the worker saw it, so the blank-answer fallback could only ever fire for an explicit empty string —
    // a shape no provider emits. Upload a permit, pick "permit" in the dropdown, get a tool_use input
    // without documentType, and the uploader's deliberate type was overwritten with "other": zero
    // applicable permit rules, permanently Pending. That is the silent-never-graded outcome #373 closes.
    [InlineData("gemini", "absent", null, "permit")]
    [InlineData("anthropic", "absent", null, "permit")]
    // Same non-answer, different encoding.
    [InlineData("gemini", "null", null, "permit")]
    [InlineData("anthropic", "null", null, "permit")]
    // An explicit empty string is likewise no answer (this is the one shape the pre-fix code handled).
    [InlineData("gemini", "empty", "", "permit")]
    [InlineData("anthropic", "empty", "", "permit")]
    // A non-STRING documentType (`"documentType": 7`) is a non-answer too — and, before the ValueKind
    // guard, the only shape that THREW out of MapResult instead of degrading, burning the whole paid
    // retry budget. It must land exactly where "absent" lands: mapped null, stored type KEPT.
    [InlineData("gemini", "wrongType", null, "permit")]
    [InlineData("anthropic", "wrongType", null, "permit")]
    // …but a POSITIVE "other" is a real classification the model made, so it must still overwrite. This
    // is the case that stops the fix from becoming "never trust the model about `other`".
    [InlineData("gemini", "other", "other", "other")]
    [InlineData("anthropic", "other", "other", "other")]
    public async Task An_absent_documentType_keeps_the_uploaders_type_but_a_positive_other_overwrites(
        string provider, string shape, string? expectedMapped, string expectedStored)
    {
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m, documentType: "permit");
        Extraction.Result = await MapThroughProviderAsync(provider, PayloadWithDocumentTypeShape(shape));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        // The contract that matters: what got STORED, since that is what grades and supersedes.
        (await GetDocAsync(docId)).DocumentType.Should().Be(expectedStored);
        // …and the client-level shape it rests on, pinned separately so a regression names its own cause.
        Extraction.Result.DocumentType.Should().Be(expectedMapped);
    }

    [Fact]
    public async Task A_non_canonical_extracted_type_still_finds_its_rules_and_grades()
    {
        // The VERDICT consequence, which is the point of the fix — pinning the stored string alone would
        // miss it. The checklist carries a COI rule ("general_liability_limit >= 2M") and the extraction
        // returns "COI" with a limit that meets it. Pre-#373 the ordinal applicableRules filter
        // (r.DocumentType == doc.DocumentType) matched ZERO rules, so the document returned the
        // zero-applicable-rules Pending with no ComplianceCheck rows at all — fail-safe, but a checklist
        // that silently never grades. Normalized, the same document reaches the real verdict.
        var (_, docId, blobPath) = await SeedGradableDocAsync(expectedValue: "2000000");
        await Fixture.Factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        Extraction.Result = GlResult("3000000", documentType: "COI");
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.DocumentType.Should().Be("coi");
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "the COI rule must apply to a document the model typed 'COI' (#373)");
        await using var db = CreateSystemDb();
        (await db.ComplianceChecks.CountAsync(c => c.DocumentId == docId))
            .Should().Be(1, "a graded document carries the check row for the rule that applied — " +
                            "zero rows is the pre-#373 zero-applicable-rules state");
    }

    [Fact]
    public async Task A_non_canonical_renewal_supersedes_the_cert_it_renews()
    {
        // The second consequence of the same string: DocumentSupersession groups on
        // (VendorId, DocumentType) with ordinal equality, so a renewal the model typed "COI" formed its
        // OWN group and left the cert it replaces counted as a live Expired liability (inflated dashboard
        // count, reminders that keep chasing a vendor who already renewed — ADR 0033). Both documents are
        // normalized to "coi", so they share a group again.
        var (orgId, oldDocId, vendorId) = await SeedExpiredCertAsync();
        var (renewalId, blobPath) = await SeedRenewalAsync(orgId, vendorId);
        await Fixture.Factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        Extraction.Result = new ExtractionResult(
            DocumentType: "COI",
            DocumentSubType: null,
            Fields: [new ExtractedField("expiration_date", DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd"), "date", 0.95)],
            NeedsReprocessing: false,
            Usage: new ExtractionUsage(100, 50, 0.01m));
        var worker = BuildWorker();

        await worker.ProcessDocumentAsync(renewalId, CancellationToken.None);

        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.Id == renewalId)).DocumentType.Should().Be("coi");
        var superseded = await db.Documents
            .Where(d => d.Id == oldDocId)
            .Where(DocumentSupersession.IsSuperseded(db.Documents))
            .AnyAsync();
        superseded.Should().BeTrue(
            "a renewal the model typed 'COI' must still supersede the 'coi' cert it renews (#373 / ADR 0033)");
    }

    /// <summary>Seeds an org + vendor + an already-expired COI — the cert a renewal must supersede.</summary>
    private async Task<(Guid OrgId, Guid DocId, Guid VendorId)> SeedExpiredCertAsync()
    {
        var orgId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", TimeZone = "America/New_York", CreatedAt = now, UpdatedAt = now });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(), OrganizationId = orgId, Plan = "free", Status = "active",
            ExtractionSpendThisMonthUsd = 0m,
            SpendMonthStart = CostTrackingService.MonthStart(DateOnly.FromDateTime(now)),
            CreatedAt = now, UpdatedAt = now,
        });
        db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "V", CreatedAt = now, UpdatedAt = now });
        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = orgId, VendorId = vendorId,
            OriginalFileName = "old.pdf", BlobStorageUrl = "blob://old", BlobStoragePath = null,
            FileSizeBytes = 1024, ContentType = "application/pdf", DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed, ComplianceStatus = ComplianceStatus.Expired,
            ExpirationDate = now.AddDays(-10), CreatedAt = now.AddYears(-1), UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, docId, vendorId);
    }

    /// <summary>Seeds the newer, still-Pending upload for the same vendor — the would-be superseder.</summary>
    private async Task<(Guid DocId, string BlobPath)> SeedRenewalAsync(Guid orgId, Guid vendorId)
    {
        var docId = Guid.NewGuid();
        var blobPath = $"blob/renewal/{docId:N}.pdf";
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = orgId, VendorId = vendorId,
            OriginalFileName = "renewal.pdf", BlobStorageUrl = "blob://renewal", BlobStoragePath = blobPath,
            FileSizeBytes = 1024, ContentType = "application/pdf", DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Pending, ComplianceStatus = ComplianceStatus.Pending,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (docId, blobPath);
    }

    [Fact]
    public async Task An_over_length_document_sub_type_is_dropped_rather_than_thrown()
    {
        // DocumentSubType is free text with no vocabulary, but it lands in another varchar(100) from the
        // same untrusted response — so it could 22001 the very SaveChanges the type fix just made safe.
        // Only the crash case changes: an over-length value becomes null (off-spec noise, not a sub-type),
        // while anything within the column still passes through verbatim.
        var (_, longId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = TypedResult("coi", subType: new string('s', ExtractionWorker.DocumentSubTypeMaxLength + 1));
        await BuildWorker().ProcessDocumentAsync(longId, CancellationToken.None);

        var longDoc = await GetDocAsync(longId);
        longDoc.DocumentSubType.Should().BeNull();
        longDoc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        longDoc.ProcessingError.Should().BeNull("a 22001 would surface here as a counted extraction failure");

        var (_, okId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = TypedResult("coi", subType: "acord-25");
        await BuildWorker().ProcessDocumentAsync(okId, CancellationToken.None);

        (await GetDocAsync(okId)).DocumentSubType.Should().Be("acord-25", "an ordinary sub-type is untouched");
    }

    [Fact]
    public async Task A_sub_type_of_exactly_the_column_width_round_trips_verbatim()
    {
        // The bound is EXCLUSIVE: MaxLength fits the column and must survive untouched; only MaxLength+1
        // is dropped. Tested at the boundary itself because the guard is a `>` — a `>=` slip would start
        // silently nulling legal 100-character sub-types, and neither the MaxLength+1 case above nor an
        // 8-character one would notice. (Same reason
        // The_per_field_confidence_gate_is_exclusive_at_the_shared_threshold pins its own bound.)
        var exact = new string('s', ExtractionWorker.DocumentSubTypeMaxLength);
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = TypedResult("coi", subType: exact);

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.DocumentSubType.Should().Be(exact, "a sub-type that FITS the column must never be dropped");
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        doc.ProcessingError.Should().BeNull("a 22001 would surface here as a counted extraction failure");
    }

    [Fact]
    public async Task Normalizing_the_stored_type_leaves_the_provider_answer_in_the_forensic_trail()
    {
        // The invariant the normalization rests on: it changes only the PERSISTED type. ExtractionRawJson
        // is the audit record of what the provider actually said, so it must still carry the verbatim
        // "COI" — otherwise a provider that went off-spec becomes undiagnosable, and nothing would stop a
        // future refactor from normalizing ExtractionResult BEFORE PersistSuccess (every other #373 test
        // would stay green while the evidence was destroyed).
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = TypedResult("COI", subType: "General Liability");

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.DocumentType.Should().Be("coi", "the STORED type is canonical");

        // Parsed, not substring-matched: the column is jsonb, so Postgres hands the text back reformatted.
        doc.ExtractionRawJson.Should().NotBeNull();
        JsonDocument.Parse(doc.ExtractionRawJson!).RootElement
            .GetProperty("llm").GetProperty("documentType").GetString()
            .Should().Be("COI", "the raw trail records what the provider answered, not what we stored");
    }

    // ----- #373: the OTHER untrusted strings in the same SaveChanges (partially addresses #385) ----

    private static ExtractionResult FieldResult(params ExtractedField[] fields) => new(
        DocumentType: "coi",
        DocumentSubType: null,
        Fields: fields,
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 100, OutputTokens: 50, EstimatedCostUsd: 0.01m));

    [Fact]
    public async Task A_long_extracted_field_value_is_truncated_instead_of_throwing_22001()
    {
        // The 22001 hazard #373 closed for DocumentType/DocumentSubType was still wide open in the SAME
        // SaveChanges through the DocumentField rows, written verbatim from the same untrusted response
        // into varchar(200)/varchar(2000)/varchar(50). And description_of_operations — which the prompt
        // explicitly asks for — is routinely long on an ACORD 25 with an ACORD 101 continuation, so this
        // is an ordinary certificate, not an adversarial one.
        //
        // The failure did NOT degrade gracefully: PersistSuccess's SaveChanges throws, and
        // ProcessDocumentAsync's catch calls SaveChangesAsync on the SAME context, which still tracks the
        // poisoned inserts — so the bookkeeping write throws too, FailedAttempts never increments, and the
        // document is zombie-reclaimed every 5 minutes until ProcessingAttempts exceeds MaxClaims: ~15
        // re-paid Document AI + LLM runs. Reachable from the unauthenticated portal upload route.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = FieldResult(
            new ExtractedField("description_of_operations", new string('d', 2_500), "text", 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed, "the persist must not throw");
        doc.ProcessingError.Should().BeNull("a 22001 would surface here as a counted extraction failure");
        doc.FailedAttempts.Should().Be(0);

        await using var db = CreateSystemDb();
        var stored = await db.DocumentFields.SingleAsync(f => f.DocumentId == docId);
        stored.FieldValue.Should().HaveLength(ExtractionWorker.FieldValueMaxLength)
            .And.Be(new string('d', ExtractionWorker.FieldValueMaxLength),
                "the value is TRUNCATED, not dropped — it is user-facing extracted content");
    }

    [Fact]
    public async Task A_long_extracted_field_name_and_type_are_truncated_instead_of_throwing_22001()
    {
        // The sibling columns from the same untrusted response. FieldType is the tightest (varchar(50))
        // and is NOT enum-constrained on the Anthropic side, so an off-spec provider can overflow it with
        // far less text than the value column needs.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = FieldResult(
            new ExtractedField(new string('n', 400), "v", new string('t', 120), 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        await using var db = CreateSystemDb();
        var stored = await db.DocumentFields.SingleAsync(f => f.DocumentId == docId);
        stored.FieldName.Should().Be(new string('n', ExtractionWorker.FieldNameMaxLength));
        stored.FieldType.Should().Be(new string('t', ExtractionWorker.FieldTypeMaxLength));
    }

    [Fact]
    public async Task Field_values_at_exactly_the_column_width_round_trip_verbatim()
    {
        // The exclusive bound, for the same reason the sub-type has one: the clamp must not clip a value
        // that FITS. An off-by-one here would silently shave the last character off every long
        // description_of_operations on the document detail page.
        var name = new string('n', ExtractionWorker.FieldNameMaxLength);
        var value = new string('v', ExtractionWorker.FieldValueMaxLength);
        var type = new string('t', ExtractionWorker.FieldTypeMaxLength);
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = FieldResult(new ExtractedField(name, value, type, 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        await using var db = CreateSystemDb();
        var stored = await db.DocumentFields.SingleAsync(f => f.DocumentId == docId);
        stored.FieldName.Should().Be(name);
        stored.FieldValue.Should().Be(value);
        stored.FieldType.Should().Be(type);
    }

    [Fact]
    public async Task A_truncated_field_value_never_ends_in_a_lone_surrogate()
    {
        // Cutting between the halves of a surrogate pair leaves a lone surrogate, which is not a
        // character: depending on the encoder Npgsql is using it either throws on the write or silently
        // stores U+FFFD — one write hazard traded for a second, or for corruption. The pair is placed so
        // that a naive cut at exactly MaxLength would land INSIDE it (an odd number of BMP chars before
        // it); the emoji stands in for any astral character an OCR'd description box can carry.
        var padding = new string('d', ExtractionWorker.FieldValueMaxLength - 1);
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = FieldResult(
            new ExtractedField("description_of_operations", padding + "\U0001F600" + "tail", "text", 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "a lone surrogate would fail the insert as invalid UTF-8");
        await using var db = CreateSystemDb();
        var stored = await db.DocumentFields.SingleAsync(f => f.DocumentId == docId);
        stored.FieldValue.Should().Be(padding, "the cut backs off to before the split pair");
        char.IsSurrogate(stored.FieldValue![^1]).Should().BeFalse();
    }

    [Fact]
    public async Task A_truncated_field_value_never_ends_in_an_ALREADY_UNPAIRED_high_surrogate()
    {
        // The sibling of the test above, and the one the "never ends in a lone surrogate" NAME actually
        // promises. The back-off used to be conditional — `IsHighSurrogate(value[cut - 1]) &&
        // IsLowSurrogate(value[cut])` — so it only fired when the clamp itself SPLIT a well-formed pair.
        // An input that already carries an UNPAIRED high surrogate at index MaxLength-1 (nothing after it
        // is a low surrogate) sailed straight through and was cut to a string ENDING in a lone surrogate:
        // the exact 22021 the guard exists to remove, reached by a different door. The condition is now
        // unconditional on a trailing high surrogate, matching the ColumnClamp.To shape #372 adds so the
        // two collapse to ONE body when that branch lands.
        var padding = new string('d', ExtractionWorker.FieldValueMaxLength - 1);
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = FieldResult(
            // '\uD83D' with NO low surrogate after it — the char at MaxLength-1 is a bare high half.
            new ExtractedField("description_of_operations", padding + "\uD83D" + "tail", "text", 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        (await GetDocAsync(docId)).ExtractionStatus.Should().Be(ExtractionStatus.Completed,
            "a lone surrogate would fail the insert as invalid UTF-8");
        await using var db = CreateSystemDb();
        var stored = await db.DocumentFields.SingleAsync(f => f.DocumentId == docId);
        stored.FieldValue.Should().Be(padding, "the cut backs off past the unpaired high surrogate");
        char.IsSurrogate(stored.FieldValue![^1]).Should().BeFalse();
    }

    [Fact]
    public async Task The_json_mirror_keeps_the_full_value_when_the_field_row_is_truncated()
    {
        // THE invariant the whole clamp rests on: truncation is DISPLAY-only. PersistSuccess writes the
        // DocumentField row through Clamp but writes ExtractionFields (jsonb, no width) from the FULL
        // value — and ExtractionFields is the CANONICAL compliance input every read goes through
        // (ComplianceCheckService.LookupValue -> DocumentFieldReadability.RawFieldValue reads
        // doc.ExtractionFields, never the DocumentField rows). Nothing pinned it, so a future
        // "let's make the JSON mirror consistent with the row" edit would silently narrow every
        // verdict input with the #373 suite still green. See the verdict-level sibling below.
        var full = new string('d', 2_500);
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = FieldResult(new ExtractedField("description_of_operations", full, "text", 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionFields.Should().NotBeNull();
        doc.ExtractionFields!.RootElement.GetProperty("description_of_operations").GetString()
            .Should().Be(full, "the jsonb mirror is the canonical verdict input and has no width limit");

        await using var db = CreateSystemDb();
        (await db.DocumentFields.SingleAsync(f => f.DocumentId == docId)).FieldValue
            .Should().HaveLength(ExtractionWorker.FieldValueMaxLength,
                "only the varchar(2000) display row is clipped — that asymmetry IS the invariant");
    }

    [Fact]
    public async Task A_row_the_worker_clipped_is_reported_clipped_by_the_read_time_derivation()
    {
        // #444 / ADR 0049 ties the DISCLOSURE to the write above: DocumentFieldTruncation reproduces
        // ColumnClamp.To against the jsonb copy instead of persisting a flag, so the two are only ever
        // in agreement because both go through the same helper at the same width. Asked of a document
        // the REAL worker wrote — the unit tests hand-build the shape, which cannot catch the worker
        // changing which value it clamps (or clamping the jsonb copy too, at which point the flag would
        // silently go false and the detail page would stop warning while still showing a clipped value).
        var full = new string('d', ExtractionWorker.FieldValueMaxLength + 500);
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.Result = FieldResult(
            new ExtractedField("description_of_operations", full, "text", 0.95),
            new ExtractedField("insured_name", "Acme Catering LLC", "text", 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        await using var db = CreateSystemDb();
        var doc = await db.Documents.Include(d => d.Fields).FirstAsync(d => d.Id == docId);
        var clipped = doc.Fields.Single(f => f.FieldName == "description_of_operations");
        DocumentFieldTruncation.ValueWasClipped(doc, clipped).Should().BeTrue();
        // Same document, same walk: the short field the worker wrote verbatim must NOT be flagged.
        DocumentFieldTruncation
            .ValueWasClipped(doc, doc.Fields.Single(f => f.FieldName == "insured_name"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_contains_rule_still_matches_text_past_the_field_row_width()
    {
        // The VERDICT-level half of the invariant above: pinning the stored JSON alone would not prove
        // that GRADING reads it. The checklist carries `description_of_operations contains "<venue>"`
        // (the real shape — it is the additional-insured contains fallback's other operand), and the
        // extraction returns a description whose only occurrence of the venue name lands PAST character
        // 2000, i.e. outside the clamped DocumentField row. It must still read Compliant, because
        // EvaluateRule -> LookupValue -> RawFieldValue resolves against doc.ExtractionFields.
        //
        // Clamp the jsonb write too and this flips to NonCompliant: an ordinary ACORD 25 + ACORD 101
        // continuation would silently stop satisfying the requirement it actually satisfies.
        const string venue = "GARDEN-HALL-SENTINEL";
        var (_, docId, blobPath) = await SeedGradableDocAsync(
            expectedValue: venue, ruleFieldName: "description_of_operations", ruleOperator: "contains");
        await Fixture.Factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        var description = new string('d', ExtractionWorker.FieldValueMaxLength + 100) + venue;
        Extraction.Result = FieldResult(
            new ExtractedField("description_of_operations", description, "text", 0.95));

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "grading reads the FULL value from ExtractionFields, not the clamped DocumentField row");

        await using var db = CreateSystemDb();
        (await db.DocumentFields.SingleAsync(f => f.DocumentId == docId)).FieldValue
            .Should().NotContain(venue, "the clipped display row genuinely does NOT carry the match — " +
                                        "which is what makes the Compliant above discriminating");
    }

    [Fact]
    public async Task A_long_failure_message_is_truncated_instead_of_throwing_22001()
    {
        // ProcessingError is varchar(2000) and carries an arbitrary exception message (a provider body
        // echoed back, an EF error listing every parameter). A 22001 HERE is the worst kind: this IS the
        // failure-bookkeeping write, so an overflow means the retry budget never advances and the document
        // is reclaimed forever.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.ThrowOnExtract = true;
        Extraction.ThrowMessage = new string('e', 5_000);

        await BuildWorker().ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.FailedAttempts.Should().Be(1, "the failure must be counted, not lost to a second throw");
        doc.ProcessingError.Should().NotBeNull()
            .And.HaveLength(ExtractionWorker.ProcessingErrorMaxLength);
    }

    [Fact]
    public async Task The_column_length_constants_match_the_EF_model()
    {
        // Every length guarantee is pinned against the EF model rather than a hand-copied literal, so
        // shrinking a column in ModelConfiguration reddens here instead of surfacing as a 22001 in
        // production. The vocabulary needs no clamp precisely because its longest literal fits.
        await using var db = CreateSystemDb();
        var document = db.Model.FindEntityType(typeof(Document))!;
        var field = db.Model.FindEntityType(typeof(DocumentField))!;

        document.FindProperty(nameof(Document.DocumentSubType))!.GetMaxLength()
            .Should().Be(ExtractionWorker.DocumentSubTypeMaxLength);
        document.FindProperty(nameof(Document.ProcessingError))!.GetMaxLength()
            .Should().Be(ExtractionWorker.ProcessingErrorMaxLength);
        field.FindProperty(nameof(DocumentField.FieldName))!.GetMaxLength()
            .Should().Be(ExtractionWorker.FieldNameMaxLength);
        field.FindProperty(nameof(DocumentField.FieldValue))!.GetMaxLength()
            .Should().Be(ExtractionWorker.FieldValueMaxLength);
        field.FindProperty(nameof(DocumentField.FieldType))!.GetMaxLength()
            .Should().Be(ExtractionWorker.FieldTypeMaxLength);
        CanonicalDocumentTypes.All.Max(t => t.Length)
            .Should().BeLessThanOrEqualTo(document.FindProperty(nameof(Document.DocumentType))!.GetMaxLength()!.Value,
                "Normalize is length-safe by construction only while every vocabulary literal fits the column");
    }

    // ----- #337: the worker grades inside PersistSuccess (combined unit of work) --------------

    // Seeds an org + zero-spend subscription + a vendor on a checklist carrying a SINGLE COI rule + a
    // Pending document assigned to that vendor, with a far-future expiry so the verdict is purely
    // rule-driven. The rule defaults to "general_liability_limit >= expectedValue"; #373's clamp test
    // overrides it with a `description_of_operations contains` rule to grade text that only exists in
    // the un-clamped jsonb mirror. The blob is NOT uploaded here — each test uploads it to the fakes of
    // the host whose worker it drives (the default host, or a derived one).
    private async Task<(Guid OrgId, Guid DocId, string BlobPath)> SeedGradableDocAsync(
        string expectedValue,
        string ruleFieldName = "general_liability_limit",
        string ruleOperator = "min_value")
    {
        var orgId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var blobPath = $"blob/grade/{docId:N}.pdf";
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", TimeZone = "America/New_York", CreatedAt = now, UpdatedAt = now });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(), OrganizationId = orgId, Plan = "free", Status = "active",
            ExtractionSpendThisMonthUsd = 0m,
            SpendMonthStart = CostTrackingService.MonthStart(DateOnly.FromDateTime(now)),
            CreatedAt = now, UpdatedAt = now,
        });
        db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = orgId, Name = "T", CreatedAt = now });
        db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "V", ComplianceTemplateId = templateId, CreatedAt = now, UpdatedAt = now });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = ruleFieldName, Operator = ruleOperator, ExpectedValue = expectedValue, SortOrder = 0,
        });
        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = orgId, VendorId = vendorId,
            OriginalFileName = "doc.pdf", BlobStorageUrl = "blob://doc", BlobStoragePath = blobPath,
            FileSizeBytes = 1024, ContentType = "application/pdf", DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Pending, ComplianceStatus = ComplianceStatus.Pending,
            ExpirationDate = now.AddYears(1), CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, docId, blobPath);
    }

    // documentType defaults to the canonical "coi"; #373's verdict test overrides it with "COI" to prove
    // a non-canonical extraction still finds the checklist's COI rules.
    private static ExtractionResult GlResult(string generalLiabilityLimit, string documentType = "coi") => new(
        DocumentType: documentType,
        DocumentSubType: null,
        Fields: [new ExtractedField("general_liability_limit", generalLiabilityLimit, "currency", 0.95)],
        NeedsReprocessing: false,
        Usage: new ExtractionUsage(InputTokens: 1000, OutputTokens: 200, EstimatedCostUsd: 0.01m));

    [Theory]
    [InlineData("3000000", ComplianceStatus.Compliant)]    // 3M >= 2M rule → Compliant
    [InlineData("1000000", ComplianceStatus.NonCompliant)] // 1M < 2M rule  → NonCompliant
    public async Task Successful_extraction_commits_the_real_compliance_verdict_atomically(
        string extractedGl, ComplianceStatus expected)
    {
        // #337: the worker now grades INSIDE PersistSuccess (combined unit of work) — the extracted inputs
        // and the verdict they imply commit in ONE transaction. Pin that a normal extraction reaches the
        // REAL verdict (Compliant / NonCompliant), never the intermediate Pending the old separate
        // EvaluateForSystemAsync pass briefly left. If PersistSuccess ever dropped the ApplyEvaluationAsync
        // call, this catches the silent loss of grading.
        var (_, docId, blobPath) = await SeedGradableDocAsync(expectedValue: "2000000");
        await Fixture.Factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        Extraction.Result = GlResult(extractedGl);
        var worker = BuildWorker();

        var claimed = await worker.ClaimNextAsync(CancellationToken.None);
        claimed.Should().Be(docId);
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var done = await GetDocAsync(docId);
        done.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        done.GeneralLiabilityLimit.Should().Be(decimal.Parse(extractedGl));
        done.ComplianceStatus.Should().Be(expected,
            "the worker grades inside PersistSuccess (#337) — the real verdict commits with the extracted inputs, not Pending");
    }

    [Fact]
    public async Task Extraction_completes_with_Pending_when_grading_throws()
    {
        // #337 best-effort (worker half): if ApplyEvaluationAsync throws inside PersistSuccess, the
        // extraction must still COMPLETE (inputs persisted, cost recorded) with ComplianceStatus degraded
        // to a safe Pending — never fail the whole extraction into a costly re-OCR/LLM retry, and never a
        // confident verdict from stale inputs. Swaps in a throwing IComplianceCheckService on a derived
        // host (its own fakes; shares the test DB).
        await using var factory = Fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IComplianceCheckService>();
                s.AddScoped<IComplianceCheckService, ThrowingComplianceCheckService>();
            }));
        var (_, docId, blobPath) = await SeedGradableDocAsync(expectedValue: "2000000");
        // The derived host has its OWN singleton fakes — upload the blob and set the extraction result there.
        await factory.Services.GetRequiredService<IBlobStorageService>()
            .UploadAsync(blobPath, new MemoryStream(UploadFixtures.PdfBytes()), "application/pdf", default);
        factory.Services.GetRequiredService<FakeExtractionClient>().Result = GlResult("3000000"); // would grade Compliant
        var worker = new ExtractionWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExtractionSettings()),
            NullLogger<ExtractionWorker>.Instance);

        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var done = await GetDocAsync(docId);
        done.ExtractionStatus.Should().Be(ExtractionStatus.Completed, "a grading failure must not fail the extraction");
        done.ComplianceStatus.Should().Be(ComplianceStatus.Pending, "the verdict degrades to safe Pending, never a stale confident value");
        done.GeneralLiabilityLimit.Should().Be(3_000_000m, "the extracted inputs still persisted");
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
            Options.Create(new ExtractionSettings()),
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
    public async Task Document_at_exactly_the_claims_backstop_is_still_processed_not_guarded_up_front()
    {
        // The up-front backstop is `> MaxClaims` (strict). A doc sitting at exactly MaxClaims must
        // still get processed — it must NOT be failed up-front. Pins the boundary against a `>`->`>=`
        // regression that would silently drop a legitimate attempt. (The over-cap side at
        // MaxClaims+1 is covered above.)
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: ExtractionWorker.MaxClaims,
            processingStartedAt: DateTime.UtcNow,
            subscriptionSpendUsd: 0m);
        var worker = BuildWorker();

        // Call process directly so attempts stays at exactly MaxClaims (a claim would increment it).
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        Extraction.ExtractCallCount.Should().Be(1, "a doc at exactly the backstop still gets processed");
        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed);
        // The success path clears ProcessingError; had the up-front backstop fired instead it would
        // read "extraction.too_many_attempts: ...". Null proves the backstop did NOT trip at the cap.
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

    // ----- #259 problem 1: deterministic failures fail fast, never retry -----------------------

    [Fact]
    public async Task Non_retryable_extraction_failure_is_failed_immediately_without_retrying()
    {
        // A NonRetryableExtractionException (e.g. token-cap truncation) is deterministic — retrying
        // the byte-identical request would fail the same way. The worker must mark the doc Failed on
        // the FIRST occurrence and never re-run OCR/LLM, and it must NOT consume the retry budget
        // (FailedAttempts stays 0 — the fail is immediate, not a counted retry).
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.ThrowNonRetryable = true;
        var worker = BuildWorker();

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessDocumentAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed);
        doc.ProcessingError.Should().StartWith("extraction.token_limit");
        doc.FailedAttempts.Should().Be(0, "a deterministic fail is immediate, not a counted retry");
        Extraction.ExtractCallCount.Should().Be(1, "a deterministic failure must never be retried");

        // And it must not be re-claimable — it's terminal.
        (await worker.ClaimNextAsync(CancellationToken.None)).Should().BeNull();
    }

    // ----- #259 problem 3: per-attempt timeout requeues/fails a wedged attempt -----------------

    [Fact]
    public async Task Wedged_attempt_is_timed_out_and_requeued_as_a_counted_failure()
    {
        // The extraction boundary hangs; the per-attempt timeout (set tiny here) must cancel it,
        // count one genuine failure, and return the doc to Pending for another attempt — never let it
        // sit wedged in Processing forever.
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Extraction.ExtractDelay = TimeSpan.FromSeconds(3); // far longer than the attempt budget below, short enough that a regression fails fast
        var worker = BuildWorker(attemptTimeout: TimeSpan.FromMilliseconds(250));

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessClaimedAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending, "a timed-out attempt below the budget is retried");
        doc.ProcessingStartedAt.Should().BeNull("the requeue clears the claim timestamp");
        doc.ProcessingError.Should().Contain("timeout");
        doc.FailedAttempts.Should().Be(1, "a timeout is a genuine, counted failure");
        Extraction.ExtractCallCount.Should().Be(1, "the boundary was entered once before timing out");
    }

    [Fact]
    public async Task Wedged_attempt_at_the_budget_edge_is_failed_not_requeued()
    {
        // FailedAttempts one short of the budget: the next timed-out attempt spends the budget and
        // must mark the doc Failed, not requeue it. Pins that the timeout path shares the same retry
        // accounting as ordinary failures.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            failedAttempts: ExtractionWorker.MaxAttempts - 1,
            processingStartedAt: DateTime.UtcNow,
            subscriptionSpendUsd: 0m);
        Extraction.ExtractDelay = TimeSpan.FromSeconds(3);
        var worker = BuildWorker(attemptTimeout: TimeSpan.FromMilliseconds(250));

        await worker.ProcessClaimedAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Failed, "the last attempt's timeout spends the budget");
        doc.FailedAttempts.Should().Be(ExtractionWorker.MaxAttempts);
        doc.ProcessingError.Should().StartWith("extraction.timeout");
    }

    // ----- #259 problem 2: a graceful shutdown mid-attempt doesn't burn the budget -------------

    [Fact]
    public async Task Graceful_shutdown_mid_attempt_requeues_without_counting_a_failure()
    {
        // Simulate a deploy interrupting an in-flight attempt: the stopping token is already
        // cancelled when processing begins. The worker must return the doc to Pending, UNDO the
        // claim's increment (so repeated deploys can't climb the claim count), and leave the retry
        // budget untouched — a restart is an interruption, not an extraction failure.
        //
        // Seeded DISTRUSTED on purpose (#459 review). RequeueInterruptedAsync is the third queue writer
        // ADR 0052 names as a deliberate NON-writer of trust, and it was the one with no pin: it already
        // resets the whole queue tuple, so adding `doc.ExtractionTrust = Trusted` there looks like tidy
        // bookkeeping — and every deploy that interrupted a distrusted document's re-extract would then
        // silently restore its vendor to Covered, the exact hole #459 exists to close. The seed must be
        // Distrusted for the assertion to discriminate at all; the helper's default is Trusted.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Processing,
            attempts: 1,
            failedAttempts: 0,
            processingStartedAt: DateTime.UtcNow,
            subscriptionSpendUsd: 0m,
            extractionTrust: ExtractionTrust.Distrusted);
        var worker = BuildWorker();

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        // The graceful-shutdown branch rethrows so ExecuteAsync's loop breaks.
        var act = async () => await worker.ProcessClaimedAsync(docId, stopping.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending, "an interrupted doc must be requeued, not stranded in Processing");
        doc.ProcessingStartedAt.Should().BeNull();
        doc.ProcessingAttempts.Should().Be(0, "the interrupted claim's increment is undone");
        doc.FailedAttempts.Should().Be(0, "an interruption is not a counted failure");
        doc.ExtractionTrust.Should().Be(ExtractionTrust.Distrusted,
            "a shutdown requeue moves PIPELINE POSITION only — the distrust must survive a deploy, exactly "
            + "as it survives Reextract's re-arm (#459 / ADR 0052)");
        Extraction.ExtractCallCount.Should().Be(0, "extraction never ran — it was interrupted at the start");
    }

    [Fact]
    public async Task Shutdown_requeue_leaves_a_doc_that_already_reached_a_terminal_state_untouched()
    {
        // Pins RequeueInterruptedAsync's idempotent early-return: if the doc reached a terminal state
        // (e.g. a concurrent path completed it) before the shutdown requeue runs, the `ExtractionStatus
        // == Processing` guard must NOT resurrect it to Pending nor decrement its claim count.
        var (_, docId) = await SeedDocAsync(
            status: ExtractionStatus.Completed,
            attempts: 3,
            failedAttempts: 1);
        var worker = BuildWorker();

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var act = async () => await worker.ProcessClaimedAsync(docId, stopping.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Completed, "a terminal doc must not be resurrected to Pending");
        doc.ProcessingAttempts.Should().Be(3, "the claim count must not be decremented for a terminal doc");
        doc.FailedAttempts.Should().Be(1, "untouched");
    }

    [Fact]
    public async Task Wedged_OCR_stage_is_also_timed_out_and_requeued()
    {
        // The per-attempt timeout must bound the WHOLE attempt, not just the LLM step. Here OCR is the
        // wedged stage (enabled + delayed): the attempt is timed out, requeued as a counted failure,
        // and extraction is never reached — identically to a wedged LLM call. (End-to-end behavior; it
        // does not isolate the `when (ex is not OperationCanceledException)` filter, since a cancelled
        // SaveChanges in the generic catch would re-throw the same OCE into the timeout handler too.)
        var (_, docId) = await SeedDocAsync(subscriptionSpendUsd: 0m);
        Ocr.IsEnabled = true;
        Ocr.OcrDelay = TimeSpan.FromSeconds(3);
        var worker = BuildWorker(attemptTimeout: TimeSpan.FromMilliseconds(250));

        (await worker.ClaimNextAsync(CancellationToken.None)).Should().Be(docId);
        await worker.ProcessClaimedAsync(docId, CancellationToken.None);

        var doc = await GetDocAsync(docId);
        doc.ExtractionStatus.Should().Be(ExtractionStatus.Pending, "a timeout during OCR is a counted failure below the budget");
        doc.ProcessingError.Should().Contain("timeout");
        doc.FailedAttempts.Should().Be(1);
        Ocr.OcrCallCount.Should().Be(1, "OCR was entered before timing out");
        Extraction.ExtractCallCount.Should().Be(0, "extraction is never reached when OCR wedges");
    }
}
