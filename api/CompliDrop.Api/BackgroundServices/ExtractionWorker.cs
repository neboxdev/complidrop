using System.Text.Json;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Services.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    /// Upper bound (seconds) on the configurable per-attempt timeout. Sits below the 300s
    /// (5-minute) zombie-reclaim threshold baked into <see cref="ClaimSql"/>'s
    /// <c>interval '5 minutes'</c>, with a 60s margin so a timed-out attempt can cancel AND requeue
    /// before a second worker could reclaim the same row. The whole point of the clamp is to keep
    /// the timeout strictly under that threshold regardless of misconfiguration.
    /// </summary>
    internal const int AttemptTimeoutCeilingSeconds = 240; // = 300s zombie threshold − 60s margin

    private const int AttemptTimeoutFloorSeconds = 60;

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
    /// </summary>
    internal const string ClaimSql = """
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
                    AND "ProcessingStartedAt" < now() - interval '5 minutes')
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
            var extraction = await extractor.ExtractAsync(
                ocr,
                imageStream,
                doc.ContentType,
                doc.DocumentType == "other" ? null : doc.DocumentType,
                ct);

            await PersistSuccess(db, doc, ocr, extraction, ct);
            var totalCost = ocr.EstimatedCostUsd + (extraction.Usage?.EstimatedCostUsd ?? 0m);
            if (totalCost > 0) await costTracker.RecordSpendAsync(doc.OrganizationId, totalCost, ct);

            try
            {
                var compliance = scope.ServiceProvider.GetRequiredService<IComplianceCheckService>();
                await compliance.EvaluateForSystemAsync(doc.Id, ct);
            }
            catch (Exception compEx)
            {
                logger.LogError(compEx, "Compliance evaluation failed for {DocumentId}", doc.Id);
            }

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
            RecordFailedAttempt(doc, "extraction.failed", ex.Message);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Records one genuine failure against the retry budget: increments <see cref="Document.FailedAttempts"/>
    /// and either marks the document <c>Failed</c> (budget spent) or returns it to <c>Pending</c> for
    /// another attempt. Does not save — the caller owns the unit of work.
    /// </summary>
    private static void RecordFailedAttempt(Document doc, string code, string message)
    {
        doc.FailedAttempts += 1;
        doc.ProcessingError = $"{code}: {message}";
        doc.UpdatedAt = DateTime.UtcNow;
        if (doc.FailedAttempts >= MaxAttempts)
        {
            doc.ExtractionStatus = ExtractionStatus.Failed;
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
        RecordFailedAttempt(doc, code, message);
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
        doc.ProcessingError = $"{code}: {message}";
        doc.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task PersistSuccess(
        SystemDbContext db,
        Document doc,
        OcrResult ocr,
        ExtractionResult extraction,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        doc.DocumentType = extraction.DocumentType;
        doc.DocumentSubType = extraction.DocumentSubType;

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
        foreach (var f in extraction.Fields)
            CanonicalDocumentFields.ApplyToTypedColumn(doc, f.Name, f.Value);

        db.DocumentFields.RemoveRange(db.DocumentFields.Where(df => df.DocumentId == doc.Id));
        foreach (var f in extraction.Fields)
        {
            db.DocumentFields.Add(new DocumentField
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                FieldName = f.Name,
                FieldValue = f.Value,
                FieldType = f.Type,
                Confidence = f.Confidence,
                IsManuallyEdited = false,
                OriginalValue = null
            });
        }

        var avgConf = extraction.Fields.Count > 0
            ? extraction.Fields.Average(f => f.Confidence)
            : 0;
        doc.ExtractionConfidence = avgConf;
        doc.ExtractionStatus = avgConf < 0.7 || extraction.NeedsReprocessing
            ? ExtractionStatus.ManualRequired
            : ExtractionStatus.Completed;
        doc.ExtractionCompletedAt = now;
        doc.ProcessingError = null;
        doc.ComplianceStatus = ComplianceStatus.Pending;
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

        await db.SaveChangesAsync(ct);
    }
}
