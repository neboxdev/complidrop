using CompliDrop.Api.Data;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

/// <summary>
/// The shared best-effort rollback for a blob that was uploaded BEFORE its <c>Document</c> row was
/// inserted — the dashboard upload, the PUBLIC portal upload and the sample seed (#389, ADR 0046 §5).
/// All three store the file first and commit the row second, so any failure in between leaves a
/// paid-for blob nothing points at.
/// <para/>
/// <b>It CONFIRMS ABSENCE before it deletes.</b> This is the whole point of the type, and it is why the
/// three sites share one implementation instead of three <c>finally</c> bodies. "The insert failed" and
/// "<c>SaveChangesAsync</c> did not return normally" are NOT the same set: a connection reset while
/// reading the COMMIT acknowledgement, or a cancellation racing that round trip, throws at the caller
/// while Postgres has already committed. Deleting on that signal alone destroys a REAL customer
/// document's file — unviewable, undownloadable, un-extractable (<c>DownloadAsync</c> returns null) and
/// still listed on the audit export. That is strictly worse than the orphan this cleanup exists to
/// prevent, so the row is re-queried first and the delete is SKIPPED whenever it is there.
/// <para/>
/// Narrowing the trigger back to a provably-rolled-back signal (an <c>IsKeyConflict</c>-only catch)
/// would be the other, older bug: it re-opens the orphan on every failure class nobody enumerated.
/// The confirm-absence check is what lets the trigger stay broad AND stay safe.
/// <para/>
/// The read runs on a FRESH scope's <see cref="SystemDbContext"/> — a different connection than the one
/// whose request just faulted, which may be mid-unwind, holding a rolled-back transaction, or bound to
/// a cancelled token. If that read cannot be completed we have not proved absence, so we do NOT delete:
/// an orphan costs storage and is named in the log, a wrongly-deleted blob costs the customer their
/// document.
/// </summary>
internal static class OrphanBlobCleanup
{
    /// <summary>
    /// Ceiling on the WHOLE cleanup — the confirm read plus the delete. The caller is still awaiting
    /// this inside its <c>finally</c>, on request threads that include the PUBLIC portal route, and
    /// <c>BlobStorageService</c> is configured with retries + a 30s network timeout, so an unbounded
    /// token (the <c>CancellationToken.None</c> this replaced) could hold a failed request open for
    /// ~90s of back-off. Short-lived, but NEVER the request's own token: it is already cancelled on the
    /// client-abort path — the likeliest orphan-maker of all — and
    /// <c>BlobClient.DeleteIfExistsAsync</c> throws on an already-cancelled token before it issues the
    /// DELETE, so the one failure mode that most needs the cleanup would be the one whose cleanup
    /// cannot run.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Deletes <paramref name="blobName"/> if — and only if — no row with <paramref name="documentId"/>
    /// exists. Swallows every failure of its own (logged loud enough for an operator to find the
    /// orphan), which is what lets it sit in a <c>finally</c> without masking the real exception on the
    /// way out.
    /// </summary>
    /// <param name="scopes">Supplies the fresh DI scope the confirming read runs in.</param>
    /// <param name="blobs">Blob store the file was written to.</param>
    /// <param name="documentId">Id of the row this blob belongs to — the request's own new Guid, so a
    /// concurrent same-key loser can only ever confirm and delete its OWN upload.</param>
    /// <param name="blobName">Name of the blob to roll back.</param>
    /// <param name="loggerFactory">Used for the operator-facing log lines.</param>
    /// <param name="operation">Short description of the upload path, for those log lines.</param>
    internal static async Task RunAsync(
        IServiceScopeFactory scopes,
        IBlobStorageService blobs,
        Guid documentId,
        string blobName,
        ILoggerFactory loggerFactory,
        string operation)
    {
        var logger = loggerFactory.CreateLogger(nameof(OrphanBlobCleanup));
        using var cleanup = new CancellationTokenSource(Budget);

        bool documentExists;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
            // IgnoreQueryFilters, in a SYSTEM context (CLAUDE.md permits it exactly here): the question
            // is "does ANY row own this blob?", and a soft-deleted row owns its blob too — ADR 0013
            // deliberately RETAINS the blob of a deleted document. Answering it through the soft-delete
            // filter would report "absent" for a row that is merely hidden and delete a file the audit
            // trail still points at.
            documentExists = await db.Documents.IgnoreQueryFilters()
                .AnyAsync(d => d.Id == documentId, cleanup.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not confirm whether document {DocumentId} committed after a failed {Operation}; "
                    + "KEEPING blob {BlobName} rather than risk deleting a live document's file. If the "
                    + "document does not exist, this blob is an orphan and can be removed by hand",
                documentId, operation, blobName);
            return;
        }

        if (documentExists)
        {
            logger.LogWarning(
                "{Operation} faulted AFTER its document {DocumentId} committed, so blob {BlobName} is "
                    + "kept — the row owns it. The caller saw an error for a document that exists",
                operation, documentId, blobName);
            return;
        }

        try
        {
            await blobs.DeleteAsync(blobName, cleanup.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to roll back orphaned blob {BlobName} after a failed {Operation}; no document "
                    + "row owns it",
                blobName, operation);
        }
    }
}
