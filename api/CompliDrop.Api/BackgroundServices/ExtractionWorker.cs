using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Services.Extraction;
using CompliDrop.Api.Services.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CompliDrop.Api.BackgroundServices;

public class ExtractionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ExtractionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExtractionWorker starting.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await TryProcessNextAsync(stoppingToken);
                if (!processed) await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "ExtractionWorker poll failed.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        logger.LogInformation("ExtractionWorker stopping.");
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Guid? claimedId = null;
        var claimSql = """
            UPDATE "Documents"
            SET "ExtractionStatus" = 'Processing',
                "ProcessingStartedAt" = now() at time zone 'utc',
                "ProcessingAttempts" = "ProcessingAttempts" + 1,
                "UpdatedAt" = now() at time zone 'utc'
            WHERE "Id" = (
              SELECT "Id" FROM "Documents"
              WHERE "DeletedAt" IS NULL
                AND (
                    "ExtractionStatus" = 'Pending'
                    OR ("ExtractionStatus" = 'Processing'
                        AND "ProcessingStartedAt" < now() at time zone 'utc' - interval '5 minutes')
                )
              ORDER BY "CreatedAt"
              FOR UPDATE SKIP LOCKED
              LIMIT 1
            )
            RETURNING "Id";
            """;

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = claimSql;
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                claimedId = reader.GetGuid(0);
            }
        }

        if (claimedId is null)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        await tx.CommitAsync(ct);

        await ProcessDocumentAsync(claimedId.Value, ct);
        return true;
    }

    private async Task ProcessDocumentAsync(Guid documentId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
        var ocrService = scope.ServiceProvider.GetRequiredService<IOcrService>();
        var extractionFactory = scope.ServiceProvider.GetRequiredService<IExtractionClientFactory>();
        var costTracker = scope.ServiceProvider.GetRequiredService<ICostTrackingService>();

        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return;

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

            await using var blob = await blobs.DownloadAsync(doc.BlobStoragePath, ct);
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

            logger.LogInformation("Extraction complete for {DocumentId} — {FieldCount} fields, avg conf {Conf:0.00}",
                doc.Id, extraction.Fields.Count, doc.ExtractionConfidence);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extraction failed for {DocumentId}", doc.Id);
            if (doc.ProcessingAttempts >= MaxAttempts)
            {
                await MarkFailed(db, doc, "extraction.failed", ex.Message, ct);
            }
            else
            {
                doc.ExtractionStatus = ExtractionStatus.Pending;
                doc.ProcessingStartedAt = null;
                doc.ProcessingError = ex.Message;
                doc.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
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

        foreach (var f in extraction.Fields)
        {
            if (string.Equals(f.Name, "effective_date", StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParse(f.Value, out var eff))
                doc.EffectiveDate = DateTime.SpecifyKind(eff, DateTimeKind.Utc);
            else if (string.Equals(f.Name, "expiration_date", StringComparison.OrdinalIgnoreCase)
                && DateTime.TryParse(f.Value, out var exp))
                doc.ExpirationDate = DateTime.SpecifyKind(exp, DateTimeKind.Utc);
            else if (string.Equals(f.Name, "general_liability_limit", StringComparison.OrdinalIgnoreCase)
                && decimal.TryParse(f.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var gll))
                doc.GeneralLiabilityLimit = gll;
        }

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

        await db.SaveChangesAsync(ct);
    }
}
