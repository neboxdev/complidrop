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
    /// Upper bound (seconds) on the configurable per-attempt timeout. Sits below the 300s
    /// (5-minute) zombie-reclaim threshold baked into <see cref="ClaimSql"/>'s
    /// <c>interval '5 minutes'</c>, with a 60s margin so a timed-out attempt can cancel AND requeue
    /// before a second worker could reclaim the same row. The whole point of the clamp is to keep
    /// the timeout strictly under that threshold regardless of misconfiguration.
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
        // Clamped for the same reason the extracted fields are: `message` is an arbitrary exception
        // message (a provider body echoed back, an EF error listing parameters) and ProcessingError is
        // varchar(2000). A 22001 HERE is the worst place for one — this IS the failure-bookkeeping write.
        doc.ProcessingError = Clamp($"{code}: {message}", ProcessingErrorMaxLength);
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

        // Which fields did the model return NON-BLANK but unparseable (#383, ADR 0040)? Those clear
        // their typed column, which is indistinguishable downstream from "the certificate has no such
        // value" — so a real expiration the parser choked on ("12/31/2026 (per endorsement)") would
        // otherwise leave a high-confidence, no-reprocess-signal document that can never turn Expired
        // and never triggers a reminder.
        //
        // Asked of the DOCUMENT, through the same DocumentFieldReadability predicate the request path
        // uses (DocumentEndpoints.ResolveManualReview) and the same one the detail DTO reports. This
        // used to be a second, independent mechanism — a per-field TypedColumnResult accumulated in
        // this loop — with nothing pinning the two equal, so "is this document in the #383 state?" had
        // two answers that could drift apart (#383 review round 2, S5). Both inputs it needs are
        // already final at this point: doc.ExtractionFields was assigned from fieldsDict above and the
        // typed columns were just written. Last-value-wins now falls out structurally rather than
        // being something this loop has to remember — the JSON mirror and the typed columns are both
        // last-wins, and the predicate reads only those.
        //
        // Field NAMES only in the log, never values: extracted field values are document PII and must
        // not reach logs/Sentry (CLAUDE.md § frontend error monitoring applies the same rule to the
        // backend's structured logs).
        var unreadableFields = DocumentFieldReadability.UnreadableCanonicalFields(doc);

        db.DocumentFields.RemoveRange(db.DocumentFields.Where(df => df.DocumentId == doc.Id));
        foreach (var f in extraction.Fields)
        {
            // Clamped to the DocumentField column widths (#373). These three strings come VERBATIM from
            // the same untrusted provider response the documentType does, and land in varchar(200) /
            // varchar(2000) / varchar(50) — and `description_of_operations`, which the prompt asks for, is
            // routinely long on an ACORD 25 with an ACORD 101 continuation. See Clamp for why an overflow
            // here is not a graceful failure but ~15 re-paid OCR + LLM runs.
            db.DocumentFields.Add(new DocumentField
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                FieldName = Clamp(f.Name, FieldNameMaxLength)!,
                FieldValue = Clamp(f.Value, FieldValueMaxLength),
                FieldType = Clamp(f.Type, FieldTypeMaxLength),
                Confidence = f.Confidence,
                IsManuallyEdited = false,
                OriginalValue = null
            });
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
        // "Covered" (VendorEndpoints.ComputeCoverage drops a ManualRequired doc from in-force coverage).
        // An ABSENT field never trips this (only a present-but-low-confidence one): the model omits what
        // it can't find, and a missing required field is the rule engine's concern, not the gate's.
        var hasLowConfidenceVerdictField = extraction.Fields.Any(f =>
            VerdictBearingFields.Contains(f.Name) && f.Confidence < ManualReviewConfidenceThreshold);

        // Four independent signals route a document to ManualRequired, each catching a case the others
        // miss: low AVERAGE confidence; a low-confidence VERDICT-BEARING field the average hid (#401); the
        // model's own reprocess signal; and an unreadable canonical value (#383) — a confidently-read
        // date/amount in a shape we can't parse fires none of the other three yet leaves a
        // compliance-critical column silently null.
        doc.ExtractionStatus = avgConf < ManualReviewConfidenceThreshold
                || hasLowConfidenceVerdictField
                || extraction.NeedsReprocessing
                || unreadableFields.Length > 0
            ? ExtractionStatus.ManualRequired
            : ExtractionStatus.Completed;
        if (unreadableFields.Length > 0)
            logger.LogWarning(
                "Document {DocumentId} returned {Count} canonical field(s) we could not parse ({Fields}); routed to manual review",
                doc.Id, unreadableFields.Length, string.Join(", ", unreadableFields));
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
        try
        {
            await compliance.ApplyEvaluationAsync(db, doc, ct);
        }
        catch (Exception ex)
        {
            doc.ComplianceStatus = ComplianceStatus.Pending;
            logger.LogError(ex, "Compliance evaluation failed for {DocumentId} during extraction persist; verdict left Pending", doc.Id);
        }

        await db.SaveChangesAsync(ct);
    }
}
