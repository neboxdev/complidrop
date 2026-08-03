using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CompliDrop.Api.Data;

/// <summary>
/// Makes the DELETE of a <see cref="ComplianceCheck"/> row NOT row-count-critical: a delete that affects
/// zero rows because a concurrent re-grade already removed the row is a SUCCESS, not a conflict
/// (<see href="https://github.com/neboxdev/complidrop/issues/468">#468</see>, ADR 0030 § Consequences).
/// <para/>
/// <c>ComplianceCheckService.ApplyEvaluationCoreAsync</c> clears a document's existing check rows by
/// MATERIALIZING them and staging a <c>RemoveRange</c>, so EF emits one <c>DELETE … WHERE "Id" = …</c> per
/// row and requires each to affect exactly one. Any other writer's re-grade of the same document
/// (<c>UpdateFields</c>, <c>UpdateDocument</c>, "Check again", the batched fan-outs) clears the same rows,
/// so one committing between that read and this writer's <c>SaveChanges</c> leaves the DELETE matching
/// nothing and EF answers <see cref="DbUpdateConcurrencyException"/>.
/// <para/>
/// On ONE writer that exception is catastrophic rather than merely noisy, which is why this exists.
/// <c>ExtractionWorker.PersistSuccess</c> throws it out of the persist, and
/// <c>ProcessDocumentAsync</c>'s catch then runs its bookkeeping <c>SaveChanges</c> on the SAME context —
/// which still tracks the same staged deletes, so it throws again, <see cref="Document.FailedAttempts"/>
/// never increments, and the document is zombie-reclaimed every five minutes RE-PAYING Document AI + the
/// LLM on every doomed run (<c>ExtractionWorker.Clamp</c>'s remarks). That is the money-burning loop ADR
/// 0030 Option A and Amendment 1 are refuted over, reached here without any concurrency token at all.
/// <para/>
/// <b>Why suppression rather than a different delete.</b> A <see cref="ComplianceCheck"/> is a DERIVED
/// display row: it carries no concurrency token, it is never updated in place, and every writer that
/// clears it does so to replace it. "The row is already gone" is precisely the outcome the DELETE was
/// asking for, and nothing reads the affected count. The two shapes that also remove the throw were
/// rejected, both because they would move the unit of work:
/// <list type="bullet">
/// <item>A set-based <c>ExecuteDeleteAsync</c> clear runs OUTSIDE the change tracker and issues its own
/// statement immediately; it joins the caller's transaction only when one is explicitly open. Two callers
/// have none — <c>ExtractionWorker.PersistSuccess</c> and <c>EvaluateForSystemAsync</c> — so there the
/// clear would COMMIT on its own, separately from the inserts and the verdict, splitting exactly the unit
/// of work ADR 0030 exists to keep whole (#337).</item>
/// <item>Giving the worker an explicit transaction so the set-based clear could join it re-opens the same
/// loop from the other side: a DATABASE-level failure inside <c>PersistSuccess</c>'s best-effort grading
/// <c>try</c> would then abort that transaction, and the degrade-to-<c>Pending</c> <c>SaveChanges</c>
/// would answer <c>25P02 current transaction is aborted</c> — a throw out of the persist again. ADR 0030
/// Amendment 1 records that landing for the two <c>REPEATABLE READ</c> writers, where it is the correct
/// answer because a request can 500; on the worker it costs an extraction.</item>
/// </list>
/// Suppressing instead changes NOTHING about the unit of work: the deletes still ride in the caller's own
/// <c>SaveChanges</c> batch, on every caller, exactly as before.
/// <para/>
/// <b>The guard is the scope.</b> It suppresses only when EVERY entry the failure is attributed to is a
/// <see cref="ComplianceCheck"/> being DELETED. A row-count mismatch on anything else — a
/// <see cref="Document"/>, a <see cref="DocumentField"/>, an INSERT or an UPDATE — still throws, because
/// for those the count is genuine information. Widening this to "suppress concurrency exceptions" would
/// hide real lost updates, and narrowing the delete's shape is the rejected alternative above.
/// <para/>
/// <b>What it does NOT do.</b> The competing re-grade's OWN new check rows are already committed when this
/// writer's inserts land, so the document can transiently hold BOTH sets — the display desync ADR 0030
/// § Consequences records, now the only residue of this interleave rather than a thrown persist. It clears
/// on the document's next evaluation, which rewrites the whole set. Registered on BOTH contexts: the
/// request path writes checks through <see cref="AppDbContext"/> and the worker and the seed fan-out
/// through <see cref="SystemDbContext"/>, and the rule is a property of the ROW, not of the caller.
/// </summary>
public sealed class ComplianceCheckDeleteConcurrencyInterceptor(
    ILogger<ComplianceCheckDeleteConcurrencyInterceptor> logger) : SaveChangesInterceptor
{
    // BOTH overrides, because EF dispatches to whichever matches the SaveChanges the caller made and a
    // sync one would otherwise keep throwing. Every production writer here is async; the sync half is
    // covered by the same test as the async half so the two cannot drift.
    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result) =>
        IsCheckRowAlreadyGone(eventData) ? InterceptionResult.Suppress() : result;

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IsCheckRowAlreadyGone(eventData) ? InterceptionResult.Suppress() : result);

    /// <summary>
    /// True when the whole mismatch is check rows this writer wanted gone and something else got to them
    /// first — and it LOGS when it says so, because a suppressed exception is otherwise an event with no
    /// trace at all, and the interleave it reports is exactly the one that leaves the document holding two
    /// writers' check rows.
    /// <para/>
    /// Ids only, never the entities: <see cref="ComplianceCheck.ActualValue"/> and
    /// <see cref="ComplianceCheck.Notes"/> carry extracted document content, which must not reach logs or
    /// Sentry (the same rule the extraction worker's unreadable-field warning follows).
    /// <para/>
    /// The empty-entry case deliberately returns false: an attribution EF could not resolve to any entry is
    /// not one this can reason about, so it keeps the exception.
    /// </summary>
    private bool IsCheckRowAlreadyGone(ConcurrencyExceptionEventData eventData)
    {
        if (eventData.Entries.Count == 0 || !eventData.Entries.All(IsCheckRowDelete)) return false;

        logger.LogWarning(
            "A concurrent re-grade had already deleted {Count} ComplianceCheck row(s) this write staged for "
            + "removal (document {DocumentIds}); treating the delete as done rather than failing the unit of "
            + "work. The document may transiently hold both writers' check rows until its next evaluation.",
            eventData.Entries.Count,
            string.Join(", ", eventData.Entries
                .Select(e => e.Property(nameof(ComplianceCheck.DocumentId)).CurrentValue)
                .Distinct()));
        return true;
    }

    private static bool IsCheckRowDelete(EntityEntry entry) =>
        entry.State == EntityState.Deleted
        && entry.Metadata.ClrType == typeof(ComplianceCheck);
}
