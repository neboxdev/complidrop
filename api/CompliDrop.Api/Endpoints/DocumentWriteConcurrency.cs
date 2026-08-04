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
/// removed the torn pair between a whole-tuple writer (<c>ExtractionWorker.PersistSuccess</c>, for every
/// input it EXTRACTS) and anything else. That qualifier is load-bearing, and this guard does not close
/// what it leaves out: the worker grades from a tracked snapshot read minutes earlier and EF writes back
/// only what it MODIFIED, so every canonical verdict input the worker leaves unmodified in that snapshot
/// keeps a request's committed value beside a verdict computed from the pre-run one — <c>VendorId</c>
/// always, <c>DocumentType</c> whenever <c>CanonicalDocumentTypes.NormalizeExtracted</c> returns the
/// stored value, any typed column whose field the model omitted (ADR 0030 Amendment 1 residual 2, #460).
/// That half is closed by ADR 0030 Amendment 2 — NOT by widening this guard, which is refuted for the
/// worker (a throw out of its <c>SaveChanges</c> re-pays Document AI + the LLM) — but by
/// <c>Services/DocumentGradingBasis</c>: the worker grades a read-only prediction of the row its own
/// commit will leave and still writes only the columns it extracted.
/// <para/>
/// It does not help two PARTIAL writers either: <c>UpdateFields</c> and <c>UpdateDocument</c>
/// each rebuild the entire <c>ExtractionFields</c> JSON mirror from their own read snapshot, but EF's
/// modified-property tracking writes only the typed columns / FK THEY touched. So two overlapping edits
/// could commit a row whose typed <c>GeneralLiabilityLimit</c> was writer A's, whose JSON mirror said
/// writer B's (older) value, and whose <c>ComplianceStatus</c> matched neither — or a verdict graded
/// against the OLD vendor's checklist on a row that kept the NEW vendor.
/// <para/>
/// The THIRD caller writes no inputs at all and needs the same signal for a different reason (#461, ADR
/// 0030 Amendment 3). <c>ComplianceEndpoints.RunCheck</c> — the "Check again" button — is a pure
/// RE-GRADE: read the document, compute the verdict, write only <c>ComplianceStatus</c> and the
/// <c>ComplianceCheck</c> rows. It cannot LOSE an update, and that was the only comfort: an
/// <c>UpdateFields</c> that LOWERS a limit inside its window commits its own consistent tuple and the
/// re-grade's UPDATE then lands on top, leaving the lowered limit beside a stored <c>Compliant</c> and
/// passing check rows citing a value the row no longer holds. Nothing heals it — the nightly sweep only
/// does date transitions.
/// <para/>
/// The fix is scoped to those three CALL SITES, on purpose. <c>REPEATABLE READ</c> makes Postgres refuse
/// an UPDATE that would land on a row version committed after this transaction's snapshot
/// (<c>40001</c>), which is precisely "someone else wrote this document while I was deciding what to
/// write". Every OTHER document writer — the extraction worker, <c>MarkVerified</c>, the delete, the
/// BATCHED re-grade fan-outs (<c>ComplianceCheckService.ReevaluateWhereAsync</c>), the nightly sweep —
/// keeps its <c>READ COMMITTED</c> last-writer-wins semantics untouched, which is what keeps this clear
/// of ADR 0030 § Option A: an entity-level <c>xmin</c> token would have made EVERY document write path
/// optimistic, and the worker's persist path treats an exception out of its <c>SaveChanges</c> as a
/// re-payable extraction (see <c>ExtractionWorker.Clamp</c>'s remarks).
/// <para/>
/// <c>MarkVerified</c> stays on that list after #465 (ADR 0052 Amendment 2) and the distinction is worth
/// stating, because it now has a conflict guard of its own. That guard is NOT this one: it commits under
/// <c>READ COMMITTED</c> and re-runs only when the BASIS its decision is a pure function of moved — the
/// <c>ExtractionStatus</c> it read, and whether the row still carries an unreadable canonical value —
/// which it detects by re-reading the row after its own write, while it holds the row lock. That is also
/// why <c>ExtractionStatus</c> is the one column it keeps OUT of that write and applies afterwards: a
/// re-read can only see a competitor on a column the same transaction did not write, so a status inside
/// the UPDATE would make the check answer with this request's own value. An unrelated concurrent commit
/// still wins last without conflicting there, exactly as this paragraph promises and as
/// <c>An_unrelated_document_writer_still_wins_last_without_conflicting</c> pins. It shares this class's
/// 409 envelope and retry bound; it does not share its isolation level.
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
    /// The exhausted-retry copy for a pure RE-GRADE (<c>ComplianceEndpoints.RunCheck</c>). Same
    /// <see cref="ConflictCode"/> and same envelope — a client cannot act differently on the two, and the
    /// frontend surfaces whichever message arrives, as written. Separate WORDING because the edit copy
    /// names a change this caller never submitted: "Check again" asks for a fresh verdict, so there is
    /// nothing of the user's to lose and nothing for them to re-make. Telling them to anyway would be the
    /// kind of inaccurate copy the frontend error-copy policy exists to keep out.
    /// </summary>
    internal const string RegradeConflictMessage =
        "Someone else changed this document while we were re-checking it. Reload the page and try again.";

    /// <summary>
    /// The exhausted-retry copy for the human CONFIRMATION (<c>DocumentEndpoints.MarkVerified</c>, #465 /
    /// ADR 0052 Amendment 2). Same <see cref="ConflictCode"/> and the same envelope as its two siblings —
    /// one code, because a client cannot act differently on any of them.
    /// <para/>
    /// Separate WORDING for the reason <see cref="RegradeConflictMessage"/> is separate: the edit copy
    /// names a change this caller never submitted (<c>PUT /verify</c> has no request body at all), and the
    /// re-grade copy names a re-check nobody asked for. What this caller lost is the confirmation itself.
    /// <para/>
    /// The message lives HERE rather than in the endpoint even though <c>MarkVerified</c> deliberately does
    /// NOT take <see cref="RunAsync"/> (its guard is a post-write pair check under <c>READ COMMITTED</c>,
    /// not this class's <c>REPEATABLE READ</c> — see the endpoint's own remarks): all three copies a
    /// document-write conflict can answer with belong beside each other, so a fourth is written knowing
    /// what the other three already say.
    /// </summary>
    internal const string VerifyConflictMessage =
        "Someone else changed this document while you were confirming it. Reload the page and try again.";

    /// <summary>
    /// Executes <paramref name="write"/> inside a <c>REPEATABLE READ</c> transaction, retrying the WHOLE
    /// callback on a concurrent-commit conflict up to <see cref="DocumentConcurrency.MaxAttempts"/>
    /// times. The callback owns the load, the mutation and the <c>SaveChanges</c>; this owns the
    /// transaction, discarding an abandoned attempt, and the answer when the retries run out.
    /// <para/>
    /// <paramref name="onAttemptAbandoned"/> is how a caller discards state an abandoned attempt
    /// produced OUTSIDE the change tracker — see its own remarks. Required rather than optional so every
    /// call site answers the question; <c>null</c> is the legible "this caller keeps none".
    /// <para/>
    /// Exhaustion commits NOTHING and answers <c>409</c>. That is deliberately stronger than ADR 0030's
    /// degrade-to-<c>Pending</c> rule rather than a departure from it: that rule exists for a RECOMPUTE
    /// failure, where the user's inputs are already going to commit and the only question is which
    /// verdict rides along, so a non-committal <c>Pending</c> beats a confident verdict from stale
    /// inputs. Here the whole unit of work is still abandonable — rolling back leaves the last
    /// successful writer's consistent <c>(inputs, verdict)</c> tuple exactly as it was, so no verdict is
    /// persisted that contradicts anything. Committing the edit with <c>Pending</c> instead would BE a
    /// half-applied write of the shape this ticket exists to remove.
    /// <para/>
    /// The re-grade caller lands on the same answer by a shorter route (#461): it writes no inputs, so
    /// rolling back leaves the row exactly as the winning writer left it — and every writer that can win
    /// a conflict here commits its OWN <c>(inputs, verdict)</c> pair, so "leave the previous verdict
    /// alone" IS the consistent state. Degrading it to <c>Pending</c> would be strictly worse: it would
    /// overwrite a correct verdict with a non-committal one, through a write that is itself what kept
    /// conflicting. Answering <c>200</c> would be worse still — the product would claim it re-checked a
    /// document it did not.
    /// </summary>
    /// <param name="onAttemptAbandoned">
    /// Invoked once per attempt that is rolled back, alongside the change-tracker clear that discards
    /// the same attempt's ENTITY state — the caller-side twin of it. It fires for the terminal attempt
    /// too, which is the whole point: a conflict can surface at the <c>CommitAsync</c> below rather than
    /// at the UPDATE (SSI under <c>SERIALIZABLE</c>, or any future writer taking an explicit row lock),
    /// so an attempt can run to completion and still be abandoned with no next attempt to tidy up after
    /// it. A caller that instead re-set such state at the START of each attempt would leave the last
    /// one's behind, and would be relying on WHERE Postgres happens to report the conflict.
    /// </param>
    /// <param name="conflictMessage">
    /// The copy the exhausted <c>409</c> carries — <see cref="ConflictMessage"/> for a writer submitting
    /// the user's change, <see cref="RegradeConflictMessage"/> for a pure re-grade. Required rather than
    /// defaulted for the same reason <paramref name="onAttemptAbandoned"/> is: the two say different
    /// things about what the user lost, and a default would silently give a new caller the wrong one.
    /// </param>
    internal static async Task<IResult> RunAsync(
        AppDbContext db,
        ILoggerFactory loggerFactory,
        Guid documentId,
        string conflictMessage,
        Func<CancellationToken, Task<IResult>> write,
        Action? onAttemptAbandoned,
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
                // onAttemptAbandoned is the same discard for whatever the CALLER kept outside the
                // tracker, so one abandonment throws away both halves of the attempt and they cannot
                // disagree about whether it happened.
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                onAttemptAbandoned?.Invoke();
                logger.LogWarning(ex,
                    "Concurrent write conflict on document {DocumentId} (attempt {Attempt} of {MaxAttempts}); reloading and recomputing",
                    documentId, attempt, DocumentConcurrency.MaxAttempts);
            }
        }

        logger.LogWarning(
            "Gave up on document {DocumentId} after {MaxAttempts} concurrent write conflicts; nothing was committed",
            documentId, DocumentConcurrency.MaxAttempts);
        return Conflict(conflictMessage);
    }

    /// <summary>The one 409 envelope for an exhausted retry, in the <c>IdempotencyResults</c> shape.</summary>
    internal static IResult Conflict(string message) =>
        Results.Json(
            new { data = (object?)null, error = new { code = ConflictCode, message } },
            statusCode: StatusCodes.Status409Conflict);
}
