using System.Runtime.CompilerServices;
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
/// On ONE writer that exception is expensive rather than merely noisy, which is why this exists.
/// <c>ExtractionWorker.PersistSuccess</c> throws it out of the persist, and <c>ProcessDocumentAsync</c>'s
/// catch — which since #375 item 1 abandons the dirty context unsaved and records the failure in a fresh
/// scope against a guarded fresh
/// read — counts it and requeues: one counted failure and a re-paid Document AI + LLM run per landing,
/// bounded by <c>ExtractionWorker.MaxAttempts</c> and spaced by the #375 retry backoff, before a terminal
/// <c>Failed</c> (<c>ExtractionWorker.Clamp</c>'s remarks). Pre-#375 it was worse — the bookkeeping save
/// re-threw on the same context, <see cref="Document.FailedAttempts"/> never incremented, and the document
/// was zombie-reclaimed every five minutes — but bounded-or-not, paying real money per interleave for a
/// row-count technicality is the cost ADR 0030 Option A and Amendment 1 are refuted over, reached here
/// without any concurrency token at all (ADR 0030's 2026-08-08 note records the shrunk landing; the
/// decision below is unchanged by it).
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
    /// <summary>The per-<see cref="DbContext"/> accumulation behind the ONE summary line per
    /// <c>SaveChanges</c> — see <see cref="FlushTally"/> for why it is not one line per suppressed row.</summary>
    private sealed class Tally
    {
        public int Rows;
        public HashSet<Guid>? Documents;
    }

    // Keyed on the CONTEXT EF hands every hook rather than held as instance fields, so the reset/flush
    // pair is correct under any registration lifetime instead of under a documented one. The natural
    // wiring (DbContextOptions is SCOPED, so AddInterceptors gets one instance per scope per context type)
    // already gives one interceptor per context, but "already" is not "by construction": a SINGLETON
    // registration, or one instance passed to both contexts' option builders, would merge two units of
    // work into one tally and make the aggregate this warning claims to carry a lie. A weak table keys the
    // state where it actually belongs and lets the entry die with the context. Nothing here is on a hot
    // path — it is touched only by a save that suppressed something.
    private readonly ConditionalWeakTable<DbContext, Tally> _tallies = [];

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

    // The tally's lifetime is one SaveChanges: cleared as it starts, emitted when it ends — and EF has
    // THREE ways for one to end, so all three flush. Leaving any of them out would not merely delay the
    // line, it would lose it: the tally dies with the context, and the scope-per-save callers (the worker
    // disposes its scope the moment ProcessDocumentAsync returns) never reach a "next save" to drop it.
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ResetTally(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ResetTally(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushTally(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        FlushTally(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    // A suppression followed by an UNRELATED failure elsewhere in the same batch still gets its line: the
    // save did not commit, but "these check rows were already gone" is exactly the context that failure
    // needs to be read against.
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        FlushTally(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        FlushTally(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // A CANCELED save flushes too, and this pair is not symmetry for its own sake (#468 review S2). The
    // reachable cancellation is the extraction worker's: PersistSuccess saves on the request's own
    // CancellationToken, so a shutdown or a per-attempt timeout lands here — on the one writer whose
    // suppressed deletes are why this class exists. Npgsql can also observe the cancellation AFTER the
    // batch reached the server, so "canceled" does not imply "committed nothing"; dropping the tally would
    // leave a possibly-committed suppression with no trace at all, which is the very thing the logging is
    // for. Emitting it costs an extra Warning on a save that may have rolled back — cheap, and the wording
    // is about the deletes rather than about the commit.
    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        FlushTally(eventData.Context);
        base.SaveChangesCanceled(eventData);
    }

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        FlushTally(eventData.Context);
        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// True when the whole mismatch is check rows this writer wanted gone and something else got to them
    /// first — and it records the fact, because a suppressed exception is otherwise an event with no trace
    /// at all, and the interleave it reports is exactly the one that leaves the document holding two
    /// writers' check rows.
    /// <para/>
    /// It TALLIES rather than logging a warning, because this runs once per ORPHANED ROW and not once per
    /// save: EF/Npgsql attributes a rows-affected mismatch to the single modification command that produced
    /// it (which is also why the <see cref="IsCheckRowDelete"/> guard survives the worker's mixed batch of
    /// a <see cref="Document"/> UPDATE + <see cref="DocumentField"/> writes + check-row DELETEs), so
    /// <c>eventData.Entries</c> holds one entry every time. On a single-document writer that is a handful
    /// of invocations; on <c>ComplianceCheckService.ReevaluateWhereAsync</c>'s batched fan-out one page is
    /// up to <c>DefaultReevaluationPageSize</c> documents' worth of check rows in ONE SaveChanges, so a
    /// warning here would put a page-sized burst of lines — and their eager formatting — on the fan-out
    /// thread. The per-row detail stays available at Debug; <see cref="FlushTally"/> emits the aggregate.
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

        // EF supplies the saving context on every one of these events, so the null arm is belt-and-braces —
        // and it keeps the TRACE rather than the tally: an unattributable suppression is reported on the
        // spot instead of being silently dropped, since being unable to AGGREGATE an event is not a reason
        // to stop recording it.
        var tally = eventData.Context is { } context ? _tallies.GetOrCreateValue(context) : null;
        if (tally is not null) tally.Rows += eventData.Entries.Count;

        var detailed = logger.IsEnabled(LogLevel.Debug);
        HashSet<Guid> documents = tally is null ? [] : tally.Documents ??= [];
        foreach (var entry in eventData.Entries)
        {
            if (entry.Property(nameof(ComplianceCheck.DocumentId)).CurrentValue is not Guid documentId) continue;
            documents.Add(documentId);
            if (detailed)
            {
                logger.LogDebug(
                    "A ComplianceCheck row this write staged for removal was already gone (document "
                    + "{DocumentId}); a concurrent re-grade deleted it first.",
                    documentId);
            }
        }

        if (tally is null) Warn(eventData.Entries.Count, documents.Count);
        return true;
    }

    /// <summary>
    /// ONE warning per SaveChanges that suppressed anything, naming the aggregate the message actually
    /// carries (rows, and how many documents they spanned) — the per-document ids are the Debug lines
    /// above. Silent when nothing was suppressed, which is every save in ordinary operation.
    /// </summary>
    private void FlushTally(DbContext? context)
    {
        if (context is null || !_tallies.TryGetValue(context, out var tally) || tally.Rows == 0) return;

        Warn(tally.Rows, tally.Documents?.Count ?? 0);
        Reset(tally);
    }

    private void Warn(int rows, int documentCount) =>
        logger.LogWarning(
            "A concurrent re-grade had already deleted {Count} ComplianceCheck row(s) that this unit of "
            + "work staged for removal, across {DocumentCount} document(s); treating those deletes as done "
            + "rather than failing the unit of work. Each affected document may transiently hold both "
            + "writers' check rows until its next evaluation.",
            rows,
            documentCount);

    private void ResetTally(DbContext? context)
    {
        if (context is not null && _tallies.TryGetValue(context, out var tally)) Reset(tally);
    }

    private static void Reset(Tally tally)
    {
        tally.Rows = 0;
        tally.Documents?.Clear();
    }

    private static bool IsCheckRowDelete(EntityEntry entry) =>
        entry.State == EntityState.Deleted
        && entry.Metadata.ClrType == typeof(ComplianceCheck);
}
