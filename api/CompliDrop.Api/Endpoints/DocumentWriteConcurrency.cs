using System.Data;
using CompliDrop.Api.Data;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

/// <summary>
/// Runs one document write — load → mutate → evaluate → <c>SaveChanges</c> — as a serializable-enough
/// unit of work, retrying it against FRESH state when a concurrent commit lands inside the window.
/// The #366 half of ADR 0030 (Amendment 1).
/// <para/>
/// ADR 0030 made each writer commit its inputs and the verdict they imply in ONE transaction, which
/// removed the torn pair between a WHOLE-TUPLE writer (<c>ExtractionWorker.PersistSuccess</c>) and
/// anything else. It does not help two PARTIAL writers: <c>UpdateFields</c> and <c>UpdateDocument</c>
/// each rebuild the entire <c>ExtractionFields</c> JSON mirror from their own read snapshot, but EF's
/// modified-property tracking writes only the typed columns / FK THEY touched. So two overlapping edits
/// could commit a row whose typed <c>GeneralLiabilityLimit</c> was writer A's, whose JSON mirror said
/// writer B's (older) value, and whose <c>ComplianceStatus</c> matched neither — or a verdict graded
/// against the OLD vendor's checklist on a row that kept the NEW vendor.
/// <para/>
/// The fix is scoped to those two writers, on purpose. <c>REPEATABLE READ</c> makes Postgres refuse an
/// UPDATE that would land on a row version committed after this transaction's snapshot (<c>40001</c>),
/// which is precisely "someone else wrote this document while I was deciding what to write". Every
/// OTHER document writer — the extraction worker, <c>MarkVerified</c>, the delete, the re-grade
/// fan-outs, the nightly sweep — keeps its <c>READ COMMITTED</c> last-writer-wins semantics untouched,
/// which is what keeps this clear of ADR 0030 § Option A: an entity-level <c>xmin</c> token would have
/// made EVERY document write path optimistic, and the worker's persist path treats an exception out of
/// its <c>SaveChanges</c> as a re-payable extraction (see <c>ExtractionWorker.Clamp</c>'s remarks).
/// <para/>
/// A retry RELOADS and RECOMPUTES: the caller's callback re-reads the document inside the new
/// transaction and re-applies the request to it, so the winner's committed change is an INPUT to the
/// retried verdict rather than something the loser overwrites from a stale snapshot.
/// </summary>
internal static class DocumentWriteConcurrency
{
    /// <summary>
    /// Error code for the exhausted case. A distinct code (not the generic 500) so the client can tell
    /// "your change did not apply, try again" apart from "something broke".
    /// </summary>
    internal const string ConflictCode = "document.concurrent_update";

    internal const string ConflictMessage =
        "Someone else changed this document while you were editing it. Reload the page and make your change again.";

    /// <summary>
    /// Executes <paramref name="write"/> inside a <c>REPEATABLE READ</c> transaction, retrying the WHOLE
    /// callback on a concurrent-commit conflict up to <see cref="DocumentConcurrency.MaxAttempts"/>
    /// times. The callback owns the load, the mutation and the <c>SaveChanges</c>; this owns the
    /// transaction, the change-tracker reset between attempts, and the answer when the retries run out.
    /// <para/>
    /// Exhaustion commits NOTHING and answers <c>409</c>. That is deliberately stronger than ADR 0030's
    /// degrade-to-<c>Pending</c> rule rather than a departure from it: that rule exists for a RECOMPUTE
    /// failure, where the user's inputs are already going to commit and the only question is which
    /// verdict rides along, so a non-committal <c>Pending</c> beats a confident verdict from stale
    /// inputs. Here the whole unit of work is still abandonable — rolling back leaves the last
    /// successful writer's consistent <c>(inputs, verdict)</c> tuple exactly as it was, so no verdict is
    /// persisted that contradicts anything. Committing the edit with <c>Pending</c> instead would BE a
    /// half-applied write of the shape this ticket exists to remove.
    /// </summary>
    internal static async Task<IResult> RunAsync(
        AppDbContext db,
        ILoggerFactory loggerFactory,
        Guid documentId,
        Func<CancellationToken, Task<IResult>> write,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(DocumentWriteConcurrency));

        for (var attempt = 1; attempt <= DocumentConcurrency.MaxAttempts; attempt++)
        {
            // A fresh transaction per attempt: REPEATABLE READ takes its snapshot at the first statement,
            // so a retry inside the SAME transaction would keep reading the stale snapshot and conflict
            // forever. The snapshot must be re-taken, which means a new transaction.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            try
            {
                var result = await write(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (DocumentConcurrency.IsConcurrentUpdateConflict(ex))
            {
                // 40001 aborts the transaction, so nothing but ROLLBACK is legal on it now. Clearing the
                // change tracker afterwards is what makes the next attempt a genuine re-read: without it
                // the retry would re-attach this attempt's stale entities (including the AuditLog rows
                // the interceptor staged) and re-apply exactly the snapshot that just lost.
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                logger.LogWarning(ex,
                    "Concurrent write conflict on document {DocumentId} (attempt {Attempt} of {MaxAttempts}); reloading and recomputing",
                    documentId, attempt, DocumentConcurrency.MaxAttempts);
            }
        }

        logger.LogWarning(
            "Gave up on document {DocumentId} after {MaxAttempts} concurrent write conflicts; nothing was committed",
            documentId, DocumentConcurrency.MaxAttempts);
        return Conflict();
    }

    /// <summary>The one 409 envelope for an exhausted retry, in the <c>IdempotencyResults</c> shape.</summary>
    internal static IResult Conflict() =>
        Results.Json(
            new { data = (object?)null, error = new { code = ConflictCode, message = ConflictMessage } },
            statusCode: StatusCodes.Status409Conflict);
}
