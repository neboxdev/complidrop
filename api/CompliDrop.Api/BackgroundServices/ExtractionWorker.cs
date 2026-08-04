using System.Globalization;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Services.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.BackgroundServices;

public class ExtractionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ExtractionSettings> extractionOptions,
    ILogger<ExtractionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retry budget: the number of GENUINELY-FAILED attempts (<see cref="Document.FailedAttempts"/>)
    /// before a document is marked <c>Failed</c>. Interrupted-by-restart claims do NOT count toward
    /// this — only attempts where extraction actually ran and failed (or timed out). Public so the
    /// regression suite asserts the budget against the source of truth, not a hard-coded literal.
    /// </summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Crash-loop backstop: the maximum number of times a document may be CLAIMED
    /// (<see cref="Document.ProcessingAttempts"/>) before it is failed up-front, regardless of how
    /// few of those claims produced a genuine failure. Guards the pathological case where a document
    /// kills the process before any failure handler runs (so <see cref="Document.FailedAttempts"/>
    /// never advances) — without it, such a document would be reclaimed forever. Set well above
    /// <see cref="MaxAttempts"/> so ordinary restarts/deploys can't trip it (#259, problem 2).
    /// </summary>
    public const int MaxClaims = 15;

    /// <summary>
    /// Confidence bar below which an extraction is routed to <see cref="ExtractionStatus.ManualRequired"/>.
    /// Shared by BOTH the field-average gate and the per-verdict-bearing-field gate (#401 / ADR 0042) so
    /// the two can never drift apart. Public so the regression suite asserts against the source of truth,
    /// not a hard-coded literal.
    /// </summary>
    public const double ManualReviewConfidenceThreshold = 0.7;

    /// <summary>
    /// Zombie-reclaim threshold: how long a <c>Processing</c> claim may sit before this worker stops
    /// believing it is live and reclaims the row (<see cref="ClaimSql"/>'s second arm). ONE definition —
    /// which is why <see cref="ClaimSql"/> INTERPOLATES it rather than spelling <c>interval '5 minutes'</c>
    /// inline — because a second consumer depends on agreeing with it exactly:
    /// <c>DocumentEndpoints.Reextract</c> refuses to re-arm a document while its claim is still live
    /// (<see href="https://github.com/neboxdev/complidrop/issues/365">#365</see>). The endpoint must refuse
    /// exactly the rows this worker still considers claimed and let through exactly the rows it would
    /// reclaim anyway; two independently-maintained copies of "5 minutes" could drift into a window where
    /// the endpoint re-queues a document a live worker still holds — which is the double-OCR/LLM,
    /// duplicate-field race #365 exists to close.
    /// <para/>
    /// Because it is a value TWO layers must agree on, the number itself is SOURCED in
    /// <see cref="ExtractionClaims"/> and this member ALIASES it — the same
    /// <c>FieldNameMaxLength = InputLengths.DocumentFieldName</c> shape used below, and for the same
    /// reason: the endpoint reads the <c>Services/</c> constant, so <c>Endpoints/</c> never has to compile
    /// against <c>BackgroundServices/</c>. Kept public here so this worker's own regression suite reads a
    /// source of truth rather than a hand-copied literal.
    /// <para/>
    /// The WORKER-SIDE boundary tests deliberately keep their own <c>5m30s</c> / <c>4m30s</c> literals
    /// (#62): their job is to be a regression discriminator for this value, so hoisting them onto this
    /// constant would make them vacuous. Same reason <see cref="AttemptTimeoutCeilingSeconds"/> below stays
    /// a literal rather than being derived from this.
    /// </summary>
    public static TimeSpan ZombieClaimTimeout => ExtractionClaims.ZombieTimeout;

    /// <inheritdoc cref="ZombieClaimTimeout"/>
    private const int ZombieClaimTimeoutMinutes = ExtractionClaims.ZombieTimeoutMinutes;

    /// <summary>
    /// Upper bound (seconds) on the configurable per-attempt timeout. Sits below the 300s
    /// (5-minute) <see cref="ZombieClaimTimeout"/> baked into <see cref="ClaimSql"/>, with a 60s margin
    /// so a timed-out attempt can cancel AND requeue before a second worker could reclaim the same row.
    /// The whole point of the clamp is to keep the timeout strictly under that threshold regardless of
    /// misconfiguration. Deliberately a LITERAL rather than <c>ZombieClaimTimeout - margin</c>: the pin
    /// that guards this relationship parses the threshold back out of <see cref="ClaimSql"/> and compares,
    /// so deriving one side from the other would collapse it into a tautology.
    /// </summary>
    internal const int AttemptTimeoutCeilingSeconds = 240; // = 300s zombie threshold − 60s margin

    private const int AttemptTimeoutFloorSeconds = 60;

    /// <summary>
    /// Length of the <c>DocumentSubType</c> column (<c>ModelConfiguration</c>: <c>varchar(100)</c>),
    /// above which an extracted sub-type is dropped rather than allowed to throw a 22001 out of
    /// <c>PersistSuccess</c>'s single <c>SaveChanges</c> (#373). Internal so a test pins it equal to the
    /// EF model's own max length instead of trusting a hand-copied literal.
    /// </summary>
    internal const int DocumentSubTypeMaxLength = 100;

    /// <summary>
    /// Widths of the columns <see cref="PersistSuccess"/> writes VERBATIM from the provider's response
    /// (<c>ModelConfiguration</c>: <c>DocumentField.FieldName</c> <c>varchar(200)</c>,
    /// <c>FieldValue</c> <c>varchar(2000)</c>, <c>FieldType</c> <c>varchar(50)</c>), plus
    /// <see cref="Document.ProcessingError"/> (<c>varchar(2000)</c>), which carries an arbitrary
    /// exception message. Internal so a test pins each equal to the EF model's own max length instead
    /// of trusting a hand-copied literal — the same treatment <see cref="DocumentSubTypeMaxLength"/>
    /// gets. See <see cref="Clamp"/> for why these are truncated rather than dropped (#373; partially
    /// addresses #385).
    /// <para/>
    /// The two <c>DocumentField</c> widths ALIAS <see cref="InputLengths"/> rather than repeat the
    /// numbers (#389): the manual-correction endpoint writes the same two columns from request input,
    /// and its guard REJECTS where this one truncates — two policies on one width, which only stays
    /// coherent while there is exactly one width. Same one-line-delegate shape as <see cref="Clamp"/>.
    /// </summary>
    internal const int FieldNameMaxLength = InputLengths.DocumentFieldName;

    /// <inheritdoc cref="FieldNameMaxLength"/>
    internal const int FieldValueMaxLength = InputLengths.DocumentFieldValue;

    /// <inheritdoc cref="FieldNameMaxLength"/>
    internal const int FieldTypeMaxLength = 50;

    /// <inheritdoc cref="FieldNameMaxLength"/>
    internal const int ProcessingErrorMaxLength = 2000;

    /// <summary>
    /// Per-attempt wall-clock bound (from <c>Extraction:AttemptTimeoutSeconds</c>), clamped into
    /// [<see cref="AttemptTimeoutFloorSeconds"/>, <see cref="AttemptTimeoutCeilingSeconds"/>] so it
    /// stays below the 5-minute zombie-reclaim threshold and a timed-out attempt cancels and
    /// requeues before a second worker could reclaim the same row (#259, problems 3 &amp; 4).
    /// Internal so the regression suite can drive the timeout path with a short budget.
    /// </summary>
    internal TimeSpan AttemptTimeout { get; set; } =
        TimeSpan.FromSeconds(Math.Clamp(
            extractionOptions.Value.AttemptTimeoutSeconds, AttemptTimeoutFloorSeconds, AttemptTimeoutCeilingSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExtractionWorker starting.");
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? claimedId = null;
            try
            {
                claimedId = await ClaimNextAsync(stoppingToken);
                if (claimedId is null)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "ExtractionWorker claim failed.");
                await Task.Delay(PollInterval, stoppingToken);
                continue;
            }

            // Claim's scope is fully disposed before this point. Process in a fresh scope, bounded
            // by the per-attempt timeout (and requeued cleanly on a graceful shutdown mid-attempt).
            try
            {
                logger.LogInformation("Claimed document {DocumentId}, beginning processing.", claimedId.Value);
                await ProcessClaimedAsync(claimedId.Value, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "ExtractionWorker process failed for {DocumentId}.", claimedId.Value);
            }
        }
        logger.LogInformation("ExtractionWorker stopping.");
    }

    /// <summary>
    /// Runs one processing attempt under a hard per-attempt timeout. A timeout requeues/fails the
    /// document as a counted failure; a graceful-shutdown interruption requeues it WITHOUT counting
    /// (so a deploy can't burn the retry budget) and rethrows so the loop stops. Internal so the
    /// regression suite can drive the timeout + shutdown paths directly with a short budget.
    /// </summary>
    internal async Task ProcessClaimedAsync(Guid documentId, CancellationToken stoppingToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attemptCts.CancelAfter(AttemptTimeout);
        try
        {
            await ProcessDocumentAsync(documentId, attemptCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown mid-attempt (the common case on a Railway deploy): the attempt never
            // got to finish, so it's an interruption, not a failure. Requeue it without burning the
            // budget, then rethrow so ExecuteAsync's loop breaks.
            await RequeueInterruptedAsync(documentId);
            throw;
        }
        catch (OperationCanceledException)
        {
            // The attempt timeout fired (NOT shutdown): the attempt is wedged. Count it as a failed
            // attempt and requeue/fail. Bounding the attempt also releases any row lock the wedged
            // work was holding — the #259 unreclaimed-claims symptom.
            logger.LogWarning("Extraction attempt for {DocumentId} timed out after {Seconds}s.",
                documentId, AttemptTimeout.TotalSeconds);
            await FailOrRequeueAsync(documentId, "extraction.timeout",
                $"Attempt exceeded the {AttemptTimeout.TotalSeconds:0}s per-attempt timeout.");
        }
    }

    /// <summary>
    /// The atomic claim/zombie-reclaim SQL. Both `ProcessingStartedAt` (timestamptz, via Npgsql's
    /// default mapping for `DateTime`) and `now()` (timestamptz, per Postgres) are compared and
    /// written as timestamptz — no `at time zone 'utc'` conversion. Mixing in a naive
    /// `timestamp without time zone` would force Postgres to bridge it via the SESSION TimeZone,
    /// which is UTC on Neon/postgres:17-alpine today but is latent on any connection that ever
    /// runs with a non-UTC session TZ. See [ADR 0009](../../../docs/adr/0009-no-at-time-zone-on-timestamptz-in-raw-sql.md)
    /// for the project-wide rule. Exposed as `internal` so the regression suite can drive the
    /// exact same string through a connection with a non-UTC session and prove the SQL is
    /// TZ-independent end-to-end.
    /// <para/>
    /// The stale-claim window is INTERPOLATED from <see cref="ZombieClaimTimeout"/> rather than spelled
    /// as a literal, so the request-side in-flight guard (<c>DocumentEndpoints.Reextract</c>, #365) and
    /// this claim can never disagree about which claims are still live. Formatted with
    /// <see cref="CultureInfo.InvariantCulture"/> because this is SQL, not display text — a culture that
    /// shapes digits differently must not be able to produce an interval Postgres cannot parse.
    /// </summary>
    internal static readonly string ClaimSql = $"""
        UPDATE "Documents"
        SET "ExtractionStatus" = 'Processing',
            "ProcessingStartedAt" = now(),
            "ProcessingAttempts" = "ProcessingAttempts" + 1,
            "UpdatedAt" = now()
        WHERE "Id" = (
          SELECT "Id" FROM "Documents"
          WHERE "DeletedAt" IS NULL
            AND (
                "ExtractionStatus" = 'Pending'
                OR ("ExtractionStatus" = 'Processing'
                    AND "ProcessingStartedAt" < now() - interval '{ZombieClaimTimeoutMinutes.ToString(CultureInfo.InvariantCulture)} minutes')
            )
          ORDER BY "CreatedAt"
          FOR UPDATE SKIP LOCKED
          LIMIT 1
        )
        RETURNING "Id";
        """;

    /// <summary>
    /// Atomically claims the next processable document via <c>UPDATE … FOR UPDATE SKIP LOCKED</c>
    /// (a Pending doc, or a Processing doc whose claim went stale past the zombie timeout), flips it
    /// to Processing, and increments its attempt counter. Returns the claimed id, or null when
    /// nothing is available. Public so the regression suite can drive the claim path in isolation.
    /// </summary>
    public async Task<Guid?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        // Run the claim as raw SQL — single UPDATE...RETURNING statement, atomic in
        // Postgres without an explicit transaction. The scope's `await using` disposes
        // the DbContext (and returns the connection to the pool) when this method
        // exits — don't close the connection manually, that would leave the
        // DbContext in a broken state.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ClaimSql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct)) return reader.GetGuid(0);
        return null;
    }

    /// <summary>
    /// Runs extraction for a previously-claimed document in a fresh DI scope: enforces the attempt
    /// cap and the org cost ceiling, runs OCR + LLM extraction, and persists success or failure.
    /// Public so the regression suite can drive the process path in isolation.
    /// </summary>
    public async Task ProcessDocumentAsync(Guid documentId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
        var ocrService = scope.ServiceProvider.GetRequiredService<IOcrService>();
        var extractionFactory = scope.ServiceProvider.GetRequiredService<IExtractionClientFactory>();
        var costTracker = scope.ServiceProvider.GetRequiredService<ICostTrackingService>();

        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return;

        // Crash-loop backstop: if the doc has been CLAIMED far more times than the retry budget (e.g.
        // it kills the process mid-extraction every time, so the failure handler never runs and
        // FailedAttempts never advances), fail it up-front so we don't reclaim it forever. Ordinary
        // restarts can't trip this — MaxClaims sits well above MaxAttempts (#259, problem 2).
        if (doc.ProcessingAttempts > MaxClaims)
        {
            await MarkFailed(db, doc, "extraction.too_many_attempts",
                $"Exceeded {MaxClaims} claims ({doc.ProcessingAttempts} so far).", ct);
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(doc.BlobStoragePath))
                throw new InvalidOperationException("Document has no blob path.");

            var canSpend = await costTracker.CanSpendAsync(doc.OrganizationId, plannedUsd: 0.01m, ct);
            if (!canSpend)
            {
                await MarkFailed(db, doc, "extraction.cost_ceiling_hit", "Monthly extraction cost ceiling reached.", ct);
                return;
            }

            logger.LogInformation("Extracting document {DocumentId}", doc.Id);

            await using var blob = await blobs.DownloadAsync(doc.BlobStoragePath, ct)
                ?? throw new InvalidOperationException("Document blob not found in storage.");
            using var buffer = new MemoryStream();
            await blob.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            OcrResult ocr;
            if (ocrService.IsEnabled)
            {
                using var ocrCopy = new MemoryStream(buffer.ToArray());
                ocr = await ocrService.OcrAsync(ocrCopy, doc.ContentType, ct);
            }
            else
            {
                logger.LogWarning("Document AI disabled or unconfigured — OCR text is empty.");
                ocr = new OcrResult(string.Empty, 0, 0, 0);
            }

            var extractor = extractionFactory.Get();
            buffer.Position = 0;
            var imageStream = doc.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? buffer
                : null;
            // The stored type is passed through RAW: "emit the hint only for a real vocabulary member"
            // has exactly ONE owner, ExtractionPrompts.BuildUserPrompt, which normalizes through
            // CanonicalDocumentTypes and drops the line for `other` / unknown / blank (#384, ADR 0051).
            // A pre-filter here was a third copy of that rule spelled as a raw "other" literal.
            var extraction = await extractor.ExtractAsync(
                ocr,
                imageStream,
                doc.ContentType,
                doc.DocumentType,
                ct);

            // The compliance verdict is now computed and committed INSIDE PersistSuccess, in the same
            // transaction as the extracted inputs (#337 / ADR 0030) — no separate evaluation pass, so the
            // worker can never leave a verdict contradicting the inputs it just wrote.
            var compliance = scope.ServiceProvider.GetRequiredService<IComplianceCheckService>();
            await PersistSuccess(db, compliance, logger, doc, ocr, extraction, ct);
            var totalCost = ocr.EstimatedCostUsd + (extraction.Usage?.EstimatedCostUsd ?? 0m);
            if (totalCost > 0) await costTracker.RecordSpendAsync(doc.OrganizationId, totalCost, ct);

            logger.LogInformation("Extraction complete for {DocumentId} — {FieldCount} fields, avg conf {Conf:0.00}",
                doc.Id, extraction.Fields.Count, doc.ExtractionConfidence);
        }
        catch (NonRetryableExtractionException ex)
        {
            // Deterministic failure (e.g. token-cap truncation, content block): a byte-identical
            // retry would fail the same way, so fail immediately instead of burning the retry budget
            // on doomed re-runs of OCR + LLM (#259, problem 1).
            logger.LogError(ex, "Non-retryable extraction failure for {DocumentId} ({Code})", doc.Id, ex.Code);
            await MarkFailed(db, doc, ex.Code, ex.Message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A genuine, possibly-transient failure: count it toward the retry budget and requeue
            // (or fail if the budget is spent). OperationCanceledException is deliberately NOT caught
            // here — a per-attempt timeout or graceful shutdown must propagate to ProcessClaimedAsync,
            // which records it correctly (a shutdown is an interruption, not a counted failure).
            logger.LogError(ex, "Extraction failed for {DocumentId}", doc.Id);
            RecordFailedAttempt(db, doc, "extraction.failed", ex.Message);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Writes this worker's TRUST DECISION and FORCES the column into the UPDATE (#459 / ADR 0052 §2).
    /// <para/>
    /// The plain assignment is not enough. <see cref="ProcessDocumentAsync"/> loads the document BEFORE
    /// OCR + the LLM call and holds that tracked snapshot for the minutes the read takes, and EF Core
    /// emits only properties whose current value DIFFERS from the snapshot. So assigning the snapshot's
    /// own value produces no SET clause at all — and the row in the database may have moved: a human who
    /// clicks "Mark verified" mid-read commits <c>Trusted</c> on another connection
    /// (<c>DocumentEndpoints.ResolveManualReview</c>), and the extraction that was supposed to RE-DECIDE
    /// trust would silently leave that confirmation standing. The row then reads
    /// <c>ManualRequired</c>/<c>Failed</c> paired with <c>Trusted</c> — a distrusted basis rolling up as
    /// Covered, the exact ADR 0042 hole. The mirror image is just as wrong: a clean re-read that means to
    /// RESTORE trust over a mid-read escalation would no-op and strand the document at ActionNeeded.
    /// <para/>
    /// Forcing the column closes this writer's half only. ADR 0052 § Consequences records the OTHER two
    /// paths to the same incoherent pairs, and neither is a deploy overlap alone: the request-side
    /// <c>MarkVerified</c> was an unforced READ COMMITTED partial write, so a commit from HERE landing
    /// inside ITS load→save window left the pair disagreeing the other way round
    /// (<see href="https://github.com/neboxdev/complidrop/issues/465">#465</see>, the ADR 0030
    /// last-writer-wins class). Do not read this comment as "the pairs are otherwise deploy-only".
    /// <para/>
    /// #465 has since closed (ADR 0052 Amendment 2) and it did NOT close by giving that writer this
    /// force — the deploy-overlap pairs remain. The force is right where the writer's own conclusion is
    /// the FRESHER value, which is this writer's case: it is deciding trust now, from the read it just
    /// performed. <c>MarkVerified</c>'s snapshot status is the OLDER value, so forcing it there would
    /// overwrite a live <c>ManualRequired</c> with a stale <c>Pending</c> and DE-QUEUE the extraction that
    /// was re-deciding trust. It makes its pair atomic by DETECTING the move instead: it re-reads, after
    /// its own write and under the row lock that write takes, the status the row will actually leave, and
    /// re-runs the confirmation against a fresh read when somebody moved it. Two writers, one invariant,
    /// two mechanisms — because only one of them owns the fresher answer.
    /// <para/>
    /// This is NOT the ADR 0030 stale-snapshot residual (<see
    /// href="https://github.com/neboxdev/complidrop/issues/460">#460</see>), which is about a writer
    /// grading from verdict INPUTS the row has moved on from and re-asserting them. Nothing is being
    /// re-asserted here: the value is this read's OWN conclusion, and ADR 0052 §2 says these writers own
    /// it. "Last writer wins on the whole tuple" is the intended semantics — forcing the column is what
    /// makes this writer actually be one.
    /// <para/>
    /// #460 has since shipped on this very method (ADR 0030 Amendment 2, see <see cref="PersistSuccess"/>),
    /// and the two sit side by side on purpose rather than by oversight — the axis between them is
    /// OWNERSHIP, not mechanism. #460 reads a FRESH value and writes no verdict INPUT back, because a
    /// vendor id or a document type is a fact a REQUEST owns and re-asserting it would be a lost update.
    /// It DOES force the verdict it computes (<see cref="ForceVerdictWrite"/>), for exactly the reason this
    /// forces trust: both are facts THIS read owns, and not writing them would leave someone else's answer
    /// standing. Making the two agree along the wrong axis — unforcing either conclusion, or forcing a
    /// verdict INPUT (ADR 0030 Amendment 2 Option G) — is a defect in whichever direction it goes.
    /// <para/>
    /// OWNERSHIP and BASIS are separate questions, and #467 (ADR 0052 Amendment 1) answers the second one
    /// without touching this. <see cref="PersistSuccess"/>'s readability trigger now reads the #460
    /// grading basis — the row this commit will LEAVE — instead of the minutes-old tracked snapshot,
    /// because "is a canonical value unreadable?" is a question about a ROW and the snapshot is not the
    /// row. That changes what the conclusion is ABOUT; it does not make the conclusion somebody else's,
    /// so the force below is unchanged and unforcing it would still be the #459 bug. The two moves are
    /// the same correction ADR 0030 Amendment 2 made for the verdict — grade the right row, then write
    /// the answer — applied to the other column this writer owns.
    /// </summary>
    private static void SetTrust(SystemDbContext db, Document doc, ExtractionTrust trust)
    {
        doc.ExtractionTrust = trust;
        db.Entry(doc).Property(d => d.ExtractionTrust).IsModified = true;
    }

    /// <summary>
    /// Records one genuine failure against the retry budget: increments <see cref="Document.FailedAttempts"/>
    /// and either marks the document <c>Failed</c> (budget spent) or returns it to <c>Pending</c> for
    /// another attempt. Does not save — the caller owns the unit of work. Takes the context only so the
    /// terminal arm can go through <see cref="SetTrust"/>.
    /// </summary>
    private static void RecordFailedAttempt(SystemDbContext db, Document doc, string code, string message)
    {
        doc.FailedAttempts += 1;
        // Clamped for the same reason the extracted fields are: `message` is an arbitrary exception
        // message (a provider body echoed back, an EF error listing parameters) and ProcessingError is
        // varchar(2000). A 22001 HERE is the worst place for one — this IS the failure-bookkeeping write.
        doc.ProcessingError = Clamp($"{code}: {message}", ProcessingErrorMaxLength);
        doc.UpdatedAt = DateTime.UtcNow;
        if (doc.FailedAttempts >= MaxAttempts)
        {
            doc.ExtractionStatus = ExtractionStatus.Failed;
            // TERMINAL failure ⇒ distrusted (#459 / ADR 0052), the durable form of ADR 0042 Amendment 2's
            // premise: an extraction the system could not COMPLETE is at least as untrustworthy as one it
            // distrusted, and nothing here touches the fields, the ComplianceCheck rows or the stored
            // verdict — so whatever read them last is still the basis. Written on the TERMINAL arm only:
            // while retries remain the document is merely back in the queue, its basis unchanged, and
            // distrusting a good prior read over one transient hiccup would sink a legitimately-covered
            // vendor for the length of the retry cycle.
            SetTrust(db, doc, ExtractionTrust.Distrusted);
        }
        else
        {
            doc.ExtractionStatus = ExtractionStatus.Pending;
            doc.ProcessingStartedAt = null;
        }
    }

    /// <summary>
    /// Loads the document in a fresh scope and records a counted failure (used by the per-attempt
    /// timeout path, which runs outside <see cref="ProcessDocumentAsync"/>'s scope). Runs on its own
    /// fresh, bounded token — NOT the worker's stopping token — so a shutdown that races the timeout
    /// can't tear down the bookkeeping write and strand the document in <c>Processing</c> (it would
    /// then only self-heal via the 5-minute zombie reclaim).
    /// </summary>
    private async Task FailOrRequeueAsync(Guid documentId, string code, string message)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cts.Token);
        if (doc is null) return;
        RecordFailedAttempt(db, doc, code, message);
        await db.SaveChangesAsync(cts.Token);
    }

    /// <summary>
    /// Returns an interrupted-by-shutdown document to <c>Pending</c> and UNDOES its claim increment,
    /// so a deploy that interrupts an in-flight extraction neither burns the retry budget nor strands
    /// the document in <c>Processing</c> for the 5-minute zombie window (#259, problem 2). Runs during
    /// the shutdown grace window on a fresh bounded token, since the worker's stopping token is
    /// already cancelled.
    /// </summary>
    private async Task RequeueInterruptedAsync(Guid documentId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
            var doc = await db.Documents.FirstOrDefaultAsync(
                d => d.Id == documentId && d.ExtractionStatus == ExtractionStatus.Processing, cts.Token);
            if (doc is null) return; // already terminal or never flipped to Processing — nothing to undo.

            doc.ExtractionStatus = ExtractionStatus.Pending;
            doc.ProcessingStartedAt = null;
            if (doc.ProcessingAttempts > 0) doc.ProcessingAttempts -= 1; // this claim didn't really run
            doc.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cts.Token);
            logger.LogInformation("Requeued interrupted document {DocumentId} on shutdown.", documentId);
        }
        catch (Exception ex)
        {
            // Best-effort: if the requeue can't complete in the grace window the zombie reclaim still
            // recovers the doc after the timeout — just slower. Don't let it block shutdown.
            logger.LogWarning(ex, "Failed to requeue interrupted document {DocumentId} on shutdown.", documentId);
        }
    }

    private static async Task MarkFailed(
        SystemDbContext db,
        Document doc,
        string code,
        string message,
        CancellationToken ct)
    {
        doc.ExtractionStatus = ExtractionStatus.Failed;
        // Terminal, so distrusted — same rule and same reason as RecordFailedAttempt's terminal arm
        // (#459 / ADR 0052). Every writer of ExtractionStatus.Failed pairs it with Distrusted; a Failed
        // row that reads Trusted can only come from a human confirmation AFTER the failure.
        SetTrust(db, doc, ExtractionTrust.Distrusted);
        doc.ProcessingError = Clamp($"{code}: {message}", ProcessingErrorMaxLength);
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Truncates an untrusted string to a column's width, so a value the provider (or an exception
    /// message) made too long can't throw Postgres 22001 out of a <c>SaveChanges</c> (#373; partially
    /// addresses <see href="https://github.com/neboxdev/complidrop/issues/385">#385</see>). That throw is
    /// not a graceful degrade: <see cref="ProcessDocumentAsync"/>'s catch calls <c>SaveChangesAsync</c> on
    /// the SAME context, which still tracks the poisoned inserts, so the bookkeeping write throws again —
    /// <see cref="Document.FailedAttempts"/> never increments, and the document is zombie-reclaimed every
    /// 5 minutes until <see cref="Document.ProcessingAttempts"/> exceeds <see cref="MaxClaims"/>, re-paying
    /// Document AI + LLM cost on every doomed run. Reachable from the unauthenticated portal upload route.
    /// <para/>
    /// TRUNCATES rather than drops: unlike <c>DocumentSubType</c> (matchable-or-nothing metadata), an
    /// extracted field is user-facing content shown on the document detail page — a clipped
    /// <c>description_of_operations</c> beats a vanished one.
    /// <para/>
    /// AS EXTRACTED, the verdict path is unaffected: <c>ExtractionFields</c> (jsonb, no width) and the
    /// typed columns are both written from the FULL value in <see cref="PersistSuccess"/>, so grading
    /// reads exactly what the model returned even when the <c>DocumentField</c> row is clipped (pinned by
    /// <c>The_json_mirror_keeps_the_full_value_when_the_field_row_is_truncated</c> and its verdict-level
    /// sibling). That guarantee does NOT survive a MANUAL EDIT: the detail page binds its input to the
    /// clamped <c>DocumentField.FieldValue</c>, and <c>DocumentEndpoints.UpdateFields</c> writes the
    /// submitted text back into <c>ExtractionFields</c> — so saving an untouched clipped field narrows the
    /// canonical value to the clipped one, and <c>description_of_operations</c> IS a verdict input (the
    /// additional-insured <c>contains</c> fallback in <c>ComplianceCheckService.EvaluateRule</c>). That
    /// narrowing is still reachable and still fail-closed, but it is no longer SILENT
    /// (<see href="https://github.com/neboxdev/complidrop/issues/444">#444</see>, ADR 0049):
    /// <see cref="DocumentFieldTruncation"/> reproduces the clamp below against the jsonb copy at read
    /// time, and the detail page warns before a save replaces the fuller record. This truncation is
    /// unchanged by that — do not "fix" the clip itself.
    /// <para/>
    /// Surrogate-safe: cutting between the halves of a surrogate pair would emit a lone surrogate, which
    /// Postgres rejects as invalid UTF-8 (22021) — trading one write failure for another. The back-off is
    /// UNCONDITIONAL on a trailing high surrogate rather than conditional on the next char being a low
    /// one: an input that already carries an UNPAIRED high surrogate at <c>maxLength - 1</c> would
    /// otherwise be cut to a string ending in a lone surrogate — the exact 22021 this guard exists to
    /// remove. (Only <see cref="Document.ProcessingError"/> can realistically carry one, via an arbitrary
    /// .NET exception message; <c>JsonElement.GetString()</c> cannot return an unpaired surrogate.)
    /// <para/>
    /// Delegates to the shared <see cref="ColumnClamp.To"/> (#372,
    /// <see href="https://github.com/neboxdev/complidrop/issues/372">ADR 0044</see>) so the codebase
    /// carries ONE surrogate-safe truncation rather than a copy per bounded column — the same one-line
    /// delegate shape as <c>ComplianceCheckService.ClampToColumn</c>. Do not re-inline the body.
    /// </summary>
    private static string? Clamp(string? value, int maxLength) => ColumnClamp.To(value, maxLength);

    private static async Task PersistSuccess(
        SystemDbContext db,
        IComplianceCheckService compliance,
        ILogger logger,
        Document doc,
        OcrResult ocr,
        ExtractionResult extraction,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // #373: the model's documentType is UNTRUSTED input — coerce it to the canonical vocabulary
        // BEFORE it overwrites the stored type. A structured-output schema pins the enum on both
        // providers (this change added the Anthropic half), but a schema is the provider's promise, not
        // ours: an off-spec response, a provider bug, or a future client would otherwise write a raw
        // string straight into the column that decides WHICH rules apply (ComplianceCheckService's
        // ordinal `r.DocumentType == doc.DocumentType` filter — a "COI" matches zero "coi" rules, so the
        // checklist yields zero applicable rules and the document is NEVER GRADED against anything) and
        // WHICH documents share a supersession group (DocumentSupersession keys on
        // (VendorId, DocumentType) — a "COI" renewal never supersedes the "coi" cert it replaces).
        //
        // Never-graded is not a fail-safe silence, and it USED to be an affirmative-coverage overclaim:
        // an ungraded document expiring within 30 days read "Expiring soon" on the list, counted as
        // IN-FORCE coverage in VendorEndpoints.ComputeCoverage, rolled its vendor up to "Covered", and
        // printed "Expiring soon" into the auditor-facing vendor package — over an empty "What we
        // checked" panel. #443 / ADR 0048 CLOSED that: a document with zero ComplianceCheck rows now
        // reads Pending on every read surface, so the residue is visible rather than silent. What
        // survives that fix, and is why this coercion still matters, is the SUPERSESSION half:
        // DocumentSupersession groups on (VendorId, DocumentType), so a "COI" renewal still never
        // supersedes the "coi" cert it replaces. Coercing here also spares the document the demoted
        // Pending in the first place — this line stops NEW documents from joining that population.
        //
        // Every value this can yield is one of six short vocabulary literals, so THIS column
        // is length-safe by construction: a runaway documentType no longer throws 22001. It is not the
        // only untrusted string in this unit of work, though — the DocumentField rows below carry three
        // more, clamped there (see Clamp) — so "the SaveChanges below cannot 22001" is a guarantee that
        // holds only because every such writer is guarded, not because of this line.
        //
        // Only the PERSISTED type is normalized; ExtractionRawJson below still records what the provider
        // actually said, so the forensic trail stays honest about a provider that went off-spec.
        //
        // A BLANK or ABSENT answer falls back to the STORED type rather than to "other" — see
        // CanonicalDocumentTypes.NormalizeExtracted for why demoting a type we already believe would
        // re-create the very silent-never-graded state this fixes. The clients map an absent/JSON-null
        // documentType to null (not to the literal "other") precisely so it reaches that branch.
        doc.DocumentType = CanonicalDocumentTypes.NormalizeExtracted(extraction.DocumentType, doc.DocumentType);
        // The sub-type has no vocabulary (it's free text the model coins per document), but it lands in
        // another varchar(100) from the same untrusted response — so guard only the crash: an over-length
        // value is off-spec noise, not a sub-type, and storing null beats both a 22001 and a truncated
        // half-value that no obligation could match anyway. Anything within the column passes through
        // verbatim, exactly as before.
        doc.DocumentSubType = extraction.DocumentSubType is { Length: > DocumentSubTypeMaxLength }
            ? null
            : extraction.DocumentSubType;

        var fieldsDict = new Dictionary<string, object?>();
        foreach (var f in extraction.Fields)
        {
            fieldsDict[f.Name] = f.Value;
        }
        doc.ExtractionFields = JsonDocument.Parse(JsonSerializer.Serialize(fieldsDict));

        var rawPayload = new
        {
            ocr = new { text = ocr.Text, pages = ocr.PageCount, avgConfidence = ocr.AvgConfidence },
            llm = new
            {
                provider = extraction.Usage is null ? "unknown" : "tracked",
                documentType = extraction.DocumentType,
                documentSubType = extraction.DocumentSubType,
                needsReprocessing = extraction.NeedsReprocessing,
                fields = extraction.Fields
            }
        };
        doc.ExtractionRawJson = JsonSerializer.Serialize(rawPayload);
        doc.ExtractionPromptVersion = ExtractionPrompts.Version;

        // Map the date/amount fields onto the typed columns ComplianceCheckService reads.
        // Shared with the manual-edit path (DocumentEndpoints.UpdateFields) via
        // CanonicalDocumentFields so both parse identically — see ADR 0017.
        //
        foreach (var f in extraction.Fields.GroupBy(x => x.Name).Select(g => g.Last()))
            CanonicalDocumentFields.ApplyToTypedColumn(doc, f.Name, f.Value);

        db.DocumentFields.RemoveRange(db.DocumentFields.Where(df => df.DocumentId == doc.Id));
        // Held in hand as well as staged: ReconcileCanonicalCopiesWithTheRow may still have to correct one
        // of these rows, once the basis below says which value the commit will actually leave in the
        // matching typed column (#467 review, C1).
        var stagedFields = new List<DocumentField>(extraction.Fields.Count);
        foreach (var f in extraction.Fields)
        {
            // Clamped to the DocumentField column widths (#373). These three strings come VERBATIM from
            // the same untrusted provider response the documentType does, and land in varchar(200) /
            // varchar(2000) / varchar(50) — and `description_of_operations`, which the prompt asks for, is
            // routinely long on an ACORD 25 with an ACORD 101 continuation. See Clamp for why an overflow
            // here is not a graceful failure but ~15 re-paid OCR + LLM runs.
            var row = new DocumentField
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                FieldName = Clamp(f.Name, FieldNameMaxLength)!,
                FieldValue = Clamp(f.Value, FieldValueMaxLength),
                FieldType = Clamp(f.Type, FieldTypeMaxLength),
                Confidence = f.Confidence,
                IsManuallyEdited = false,
                OriginalValue = null
            };
            stagedFields.Add(row);
            db.DocumentFields.Add(row);
        }

        var avgConf = extraction.Fields.Count > 0
            ? extraction.Fields.Average(f => f.Confidence)
            : 0;
        doc.ExtractionConfidence = avgConf;

        // Per-field confidence gate on the VERDICT-BEARING fields, ON TOP OF the average gate below
        // (#401 / ADR 0042). The average hides a single mis-read critical field — one 0.3-confidence
        // expiration_date among a dozen 0.95 incidental fields still averages well clear of the gate —
        // yet that lone field is exactly what flips a compliance verdict (a coverage limit, an effective/
        // expiration date, the additional-insured party). So if ANY verdict-bearing field the model
        // actually returned came back below the SAME threshold, route the document to a human regardless
        // of the average, rather than let an extraction the system itself distrusts grade and roll up as
        // "Covered" (VendorEndpoints.ComputeCoverage drops a Distrusted doc from in-force coverage).
        // An ABSENT field never trips this (only a present-but-low-confidence one): the model omits what
        // it can't find, and a missing required field is the rule engine's concern, not the gate's.
        var hasLowConfidenceVerdictField = extraction.Fields.Any(f =>
            VerdictBearingFields.Contains(f.Name) && f.Confidence < ManualReviewConfidenceThreshold);

        doc.ExtractionCompletedAt = now;
        doc.ProcessingError = null;
        doc.UpdatedAt = now;

        // System "document processed" event for the activity feed (#318 FP-043): extraction completes
        // in this background worker, which has no ICurrentUser, so the AuditSaveChangesInterceptor
        // skips it (its audit branch requires a current user) and the read step would otherwise never
        // appear in Pat's feed. Written explicitly against the doc's org with a null user; saved in the
        // same unit of work as the fields so a processed doc and its feed entry are atomic. AuditLog is
        // in the interceptor's NonAuditedTypes, so this insert doesn't recurse.
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = doc.OrganizationId,
            UserId = null,
            Action = "document.processed",
            EntityType = nameof(Document),
            EntityId = doc.Id,
            CreatedAt = now,
        });

        // Combined unit of work (#337 / ADR 0030): compute + apply the compliance verdict from the
        // freshly-extracted inputs on this SAME context, so the extracted fields/typed-columns AND the
        // verdict they imply commit in ONE transaction. The verdict was previously written in a SECOND
        // transaction (EvaluateForSystemAsync, after this method returned) — which a concurrent manual edit
        // could interleave to leave the stored verdict contradicting the stored inputs (a torn pair that
        // didn't self-heal). Now the worker writes (inputs, verdict) atomically; the manual-edit path does
        // the same, so the two writers are each last-writer-wins on the whole (inputs, verdict) tuple.
        // Best-effort verdict (matching the prior EvaluateForSystemAsync try/catch): if the recompute
        // itself fails, persist the inputs with ComplianceStatus = Pending (a safe "not yet graded" state
        // the sweep / "Check again" resolves) rather than fail the whole extraction into a costly re-OCR/LLM
        // retry — but never commit a confident verdict from stale inputs.
        //
        // ...and "stale inputs" is not only about the values this method just wrote (#460 / ADR 0030
        // Amendment 2). `doc` is the snapshot ProcessDocumentAsync loaded BEFORE OCR + the LLM call, minutes
        // ago, and EF emits only the properties this method MODIFIED — so every canonical verdict input it
        // leaves unmodified (VendorId always; DocumentType whenever NormalizeExtracted returns the stored
        // value; any typed column whose field the model omitted) would be graded from the pre-run value
        // while the row keeps whatever a request committed in the window. Grade the row this commit will
        // actually LEAVE instead: the current committed values, overlaid with exactly the properties EF is
        // about to write. Read-only — nothing from the basis is assigned back onto `doc`, so this worker
        // still writes exactly the columns it wrote before and cannot clobber a concurrent edit (the
        // re-read-and-assign shape is a LOST UPDATE, refuted in ADR 0030 Amendment 1).
        //
        // The basis read sits INSIDE this try on purpose: it is one more way grading can fail, and a
        // failure here must land on Pending exactly like any other, never as a throw out of PersistSuccess
        // (which costs a re-paid Document AI + LLM run — see Clamp). A null basis means the row is
        // GENUINELY gone — a hard delete; the basis read ignores query filters, so a document soft-deleted
        // mid-run still yields a basis and is still graded as the row this commit leaves. The fallback is
        // therefore defensive rather than a live path: grade the tracked entity, i.e. pre-#460 behaviour.
        //
        // `basis` is declared OUTSIDE the try because the trust decision below reads it too (#467 / ADR
        // 0052 Amendment 1) — and because a grading failure must not cost it: if ApplyEvaluationAsync is
        // what threw, the basis was already read successfully and is still the right row to judge.
        Document? basis = null;
        try
        {
            basis = await DocumentGradingBasis.AfterPendingCommitAsync(db, doc, ct);
            // BEFORE the grade, not after: the reconciliation edits the JSON mirror, which is a verdict
            // input in its own right (ComplianceCheckService.LookupValue's raw-string fallback reads it
            // when the typed column is null). Grading first would let the fallback resolve a value the
            // reconciliation is about to remove.
            if (basis is not null) ReconcileCanonicalCopiesWithTheRow(doc, basis, fieldsDict, stagedFields);
            if (basis is null) await compliance.ApplyEvaluationAsync(db, doc, ct);
            else await compliance.ApplyEvaluationAsync(db, doc, basis, ct);
        }
        catch (Exception ex)
        {
            doc.ComplianceStatus = ComplianceStatus.Pending;
            logger.LogError(ex, "Compliance evaluation failed for {DocumentId} during extraction persist; verdict left Pending", doc.Id);
        }

        // Which canonical fields will the ROW carry NON-BLANK but unparseable once this commit lands
        // (#383, ADR 0040)? Such a value clears its typed column, which is indistinguishable downstream
        // from "the certificate has no such value" — so a real expiration the parser choked on
        // ("12/31/2026 (per endorsement)") would otherwise leave a high-confidence, no-reprocess-signal
        // document that can never turn Expired and never triggers a reminder.
        //
        // Asked through the same DocumentFieldReadability predicate the request path uses
        // (DocumentEndpoints.ResolveManualReview) and the same one the detail DTO reports. It used to be
        // a second, independent mechanism — a per-field TypedColumnResult accumulated in the typed-column
        // loop — with nothing pinning the two equal, so "is this document in the #383 state?" had two
        // answers that could drift apart (#383 review round 2, S5). Last-value-wins falls out
        // structurally rather than being something a loop has to remember: the JSON mirror and the typed
        // columns are both last-wins, and the predicate reads only those.
        //
        // Asked of the BASIS, not of the tracked entity (#467 / ADR 0052 Amendment 1). The two are
        // different rows: ApplyToTypedColumn ASSIGNS, and a value equal to the minutes-old snapshot
        // leaves the property unmodified, so EF omits the column and whatever a request committed inside
        // the window survives. Judging the pre-run snapshot therefore committed ManualRequired +
        // Distrusted over a row nothing on which is unreadable — and the read-time unreadableFields list
        // is re-derived from that row, so the review card had no cause to name. Trust is still THIS
        // READ's own conclusion and is still FORCED into the UPDATE (SetTrust); what the basis fixes is
        // WHICH ROW the conclusion is about, exactly as ADR 0030 Amendment 2 did for the verdict. A null
        // basis (the row is genuinely gone) or a basis read that threw falls back to the tracked entity,
        // i.e. the pre-#467 answer — a strict SUPERSET of the basis answer, so the fallback is
        // fail-CLOSED rather than merely "the old behaviour".
        //
        // Field NAMES only in the log, never values: extracted field values are document PII and must
        // not reach logs/Sentry (CLAUDE.md § frontend error monitoring applies the same rule to the
        // backend's structured logs).
        var unreadableFields = DocumentFieldReadability.UnreadableCanonicalFields(basis ?? doc);

        // Four independent signals route a document to ManualRequired, each catching a case the others
        // miss: low AVERAGE confidence; a low-confidence VERDICT-BEARING field the average hid (#401); the
        // model's own reprocess signal; and an unreadable canonical value (#383) — a confidently-read
        // date/amount in a shape we can't parse fires none of the other three yet leaves a
        // compliance-critical column silently null. The first three describe the READING and have no
        // mirror on the row; the fourth describes the ROW, which is why only it consults the basis.
        var distrusted = avgConf < ManualReviewConfidenceThreshold
            || hasLowConfidenceVerdictField
            || extraction.NeedsReprocessing
            || unreadableFields.Length > 0;

        // ONE boolean, TWO columns (#459 / ADR 0052). Pipeline POSITION and extraction TRUST used to be
        // the same column, so Reextract's re-arm to Pending erased the distrust and nothing restored it.
        // This read is what establishes the document's current basis, so it decides BOTH — and it is the
        // only writer that can restore trust without a human: a clean re-read of a previously-distrusted
        // document earns Trusted back here, which is what stops the flag being sticky the way
        // IsManuallyVerified was (ADR 0042 Amendment 2's recorded residue).
        //
        // Both writes sit BELOW the basis read, which is what lets them be decided from it. They fall
        // out of the basis's own overlay as a result — the basis carries the row's ExtractionStatus /
        // ExtractionTrust rather than this method's — and that is immaterial by inspection:
        // ComplianceCheckService reads neither column, so no verdict input moves. SetTrust still forces
        // its column, so the ordering costs the write nothing.
        doc.ExtractionStatus = distrusted ? ExtractionStatus.ManualRequired : ExtractionStatus.Completed;
        SetTrust(db, doc, distrusted ? ExtractionTrust.Distrusted : ExtractionTrust.Trusted);
        if (unreadableFields.Length > 0)
            logger.LogWarning(
                "Document {DocumentId} will carry {Count} canonical field(s) we could not parse ({Fields}); routed to manual review",
                doc.Id, unreadableFields.Length, string.Join(", ", unreadableFields));

        ForceVerdictWrite(db, doc);

        // ONE SaveChanges carries the extracted inputs, the DocumentField rows, the cleared + rewritten
        // ComplianceCheck rows and the verdict (#337 / ADR 0030). It must not THROW — see Clamp for what
        // that costs — and the clear is one of the ways it could: ApplyEvaluationAsync stages a per-row
        // DELETE of the checks it read, and a competing re-grade committing in the window between that read
        // and this line leaves them matching nothing. ComplianceCheckDeleteConcurrencyInterceptor makes
        // that a success rather than a DbUpdateConcurrencyException (#468); this line is deliberately left
        // bare of a try/catch, because a catch here would have nothing safe to do — the bookkeeping save in
        // ProcessDocumentAsync's catch runs on this same context and re-throws.
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Makes the two USER-VISIBLE copies of a canonical field — the <see cref="Document.ExtractionFields"/>
    /// JSON mirror and the <see cref="DocumentField"/> row — agree with the typed column the pending commit
    /// will actually LEAVE (#467 review, C1).
    /// <para/>
    /// The three copies do not move together. <c>ApplyToTypedColumn</c> ASSIGNS, so a value equal to the
    /// minutes-old snapshot leaves the property unmodified and EF omits the column — whatever a request
    /// committed inside the window survives. The mirror and the rows are rewritten from the response
    /// UNCONDITIONALLY. So the ticket's own interleave (a null column, a mid-run correction, a model answer
    /// nothing can parse) committed a row whose column held the user's valid date while both copies the
    /// field editor renders held <c>"12/31/2029 (per endorsement)"</c> — under a <c>Completed</c>/
    /// <c>Trusted</c> badge and a verdict graded from the surviving column, with <c>unreadableFields</c>
    /// EMPTY because <see cref="DocumentFieldReadability"/> short-circuits on the non-null column before it
    /// ever looks at the raw value the user is being shown. Fixing WHICH ROW the trust conclusion is about
    /// (Amendment 1) is what exposed this: the row is only "clean" if its copies say what its column says.
    /// <para/>
    /// The rule is one sentence: <b>a raw copy this persist writes may not contradict the column the commit
    /// will leave.</b> Where <see cref="CanonicalDocumentFields.SameTypedColumn"/> says the basis and the
    /// tracked entity disagree, the response's value is one the row will NOT be graded from, so the copies
    /// take the basis's rendering instead — and the response's own answer is preserved on the row as
    /// <see cref="DocumentField.OriginalValue"/>, which the detail page already renders as <i>was: …</i>.
    /// <see cref="Document.ExtractionRawJson"/> keeps the provider's verbatim answer either way, so the
    /// forensic trail is untouched.
    /// <para/>
    /// Scoped to canonical fields the response ACTUALLY carried, because the defect is a copy this persist
    /// WRITES. A field the model omitted has no mirror entry and no row here at all — nothing claims
    /// anything about it, so there is nothing to reconcile (and inventing an entry would be this writer
    /// asserting a value it never read).
    /// <para/>
    /// NOT ADR 0030 Amendment 2 Option G, which stays refuted: the typed columns are untouched, and this
    /// worker still emits exactly the columns it emitted before. It writes STRICTLY LESS of a request's
    /// value away than it did — the mirror and the rows were already being clobbered wholesale; one entry
    /// now echoes the committed column instead of overwriting it with a contradiction. Nothing is assigned
    /// onto a typed column, so no verdict input a REQUEST owns is re-asserted by the worker.
    /// <para/>
    /// Called from inside the caller's degrade-to-<c>Pending</c> try, so it inherits the placement rule
    /// every basis consumer has: nothing here may become a new way for <c>PersistSuccess</c> to throw,
    /// which is a re-paid Document AI + LLM run (see <see cref="Clamp"/>). It is pure in-memory work over
    /// a dictionary and a list.
    /// <para/>
    /// ONE CONSEQUENCE worth recording rather than rediscovering. Once this has run,
    /// <see cref="DocumentFieldReadability"/> gives the SAME answer for the tracked entity as for the
    /// basis, by construction: a reconciled field's raw copy is blank or a canonical rendering, so it is
    /// never unreadable on either, and an unreconciled one has equal typed columns on both. Amendment 1's
    /// <c>basis ?? doc</c> below therefore states the intent and owns the FALLBACK — no basis means no
    /// reconciliation either, i.e. the pre-#467 answer — while what the tests can still discriminate is
    /// the ORDER: move the walk back ABOVE this call and the ticket's bug returns intact.
    /// </summary>
    private static void ReconcileCanonicalCopiesWithTheRow(
        Document doc,
        Document basis,
        Dictionary<string, object?> fieldsDict,
        List<DocumentField> stagedFields)
    {
        var corrected = false;
        foreach (var fieldName in fieldsDict.Keys.ToArray())
        {
            if (!CanonicalDocumentFields.IsCanonical(fieldName)) continue;
            if (CanonicalDocumentFields.SameTypedColumn(doc, basis, fieldName)) continue;

            // The rendering the readability predicate itself reads the column through, so the copy we
            // leave behind parses back to exactly the value being committed — or is a JSON null when the
            // row carries none, which RawFieldValue reads as ABSENT rather than as unreadable (ADR 0040:
            // blank stays blank).
            var committed = DocumentFieldReadability.TypedColumnValue(basis, fieldName);
            fieldsDict[fieldName] = committed;
            corrected = true;

            foreach (var row in stagedFields)
            {
                if (!string.Equals(row.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)) continue;
                // The ONE owner of "this row now holds a corrected value" (#467 review round 2, S5):
                // demote the model's answer to OriginalValue, take the committed one, flag the row and
                // pin its confidence. The detail page prints "✎ Manually edited" and "was: …" and counts
                // these fields in the "Read again will discard N corrections" warning, all of which are
                // now true of it. OriginalValue inherits FieldValue's width, and FieldValue is already
                // clamped, so the demotion cannot 22001.
                //
                // The provenance capture is IDEMPOTENT there, which this loop needs (#467 review round 2,
                // C1): fieldsDict is ORDINAL while this match is OrdinalIgnoreCase, so a response
                // carrying `expiration_date` AND `Expiration_Date` reaches these same rows twice — and an
                // unguarded second demotion would set OriginalValue to the value the first pass just
                // wrote, making it equal FieldValue, which is exactly the state the page's `was: …` guard
                // renders as nothing. The model's own answer would be gone from the row.
                row.ApplyCorrection(committed);
            }
        }

        if (!corrected) return;
        doc.ExtractionFields = JsonDocument.Parse(JsonSerializer.Serialize(fieldsDict));
        // The basis is "the row this commit will leave", and the mirror is a property this writer MODIFIES
        // — so the overlay took the tracked value and has to keep taking it. Both of the conclusions drawn
        // below from this instance read it: the grade (through LookupValue's raw fallback) and the
        // readability walk.
        basis.ExtractionFields = doc.ExtractionFields;
    }

    /// <summary>
    /// Puts <see cref="Document.ComplianceStatus"/> into the persist's UPDATE unconditionally, covering BOTH
    /// arms above — the graded verdict and the degrade-to-<c>Pending</c> (#460 review round 2, C1).
    /// <para/>
    /// Grading the right row is only half of ADR 0030 Amendment 2; the verdict also has to be WRITTEN. The
    /// plain assignment is not enough, for exactly the reason spelled out on <see cref="SetTrust"/>: `doc`
    /// is the snapshot <see cref="ProcessDocumentAsync"/> loaded before OCR + the LLM, and EF Core emits
    /// only properties whose current value DIFFERS from it. So a freshly-computed verdict that happens to
    /// EQUAL what the row held at claim time produces no SET clause at all — while the
    /// <c>ComplianceCheck</c> rows are rewritten unconditionally and <c>UpdatedAt</c> guarantees the UPDATE
    /// still runs. A request that moved the stored verdict inside the window then keeps ITS verdict beside
    /// THIS read's inputs and THIS read's check rows: the torn pair ADR 0030 exists to prevent, in the
    /// false-affirmative direction. The reachable shape is one PATCH: a document on a strict checklist
    /// stored <c>NonCompliant</c>, reassigned mid-read to a lenient one (which re-grades it
    /// <c>Compliant</c>), extracted at a limit that fails even the lenient floor — the recomputed verdict
    /// is <c>NonCompliant</c>, equal to the pre-run snapshot, so the column is omitted and the row commits
    /// <c>Compliant</c> over the lowered limit and a FAILING check row. Nothing re-grades it (the sweep
    /// does date transitions only). The catch arm has the identical hole: a <c>Pending</c> snapshot
    /// swallows the degrade and leaves a competitor's confident verdict standing over inputs this persist
    /// just overwrote.
    /// <para/>
    /// This is NOT ADR 0030 Amendment 2 Option G, and the distinction is the whole reason the basis is
    /// read-only. Option G forces verdict INPUTS — <c>VendorId</c>, <c>DocumentType</c>, a typed column —
    /// which a REQUEST owns, so forcing them is the lost update Amendment 1 refutes. The verdict is this
    /// read's OWN conclusion about the basis it just graded, the same thing <see cref="SetTrust"/> forces
    /// one column over (ADR 0052 §2). Making the two agree in either direction is a defect.
    /// <para/>
    /// WORKER-ONLY on purpose. The request-path callers of <c>ApplyEvaluationAsync</c> run inside
    /// <c>DocumentWriteConcurrency</c>'s <c>REPEATABLE READ</c> guard, whose entire point is that a
    /// stale-basis UPDATE is REFUSED (40001 → re-run against a fresh read) rather than forced.
    /// </summary>
    private static void ForceVerdictWrite(SystemDbContext db, Document doc) =>
        db.Entry(doc).Property(d => d.ComplianceStatus).IsModified = true;
}
