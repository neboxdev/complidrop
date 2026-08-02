using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CompliDrop.Api.Services;

/// <summary>
/// Recognizes the one database signal that says "another transaction committed a change to a row we
/// had already read" — the detection half of the #366 guard on the two PARTIAL document writers
/// (<c>DocumentEndpoints.UpdateFields</c> / <c>UpdateDocument</c>, ADR 0030 Amendment 1).
/// <para/>
/// Both writers rebuild the whole <c>ExtractionFields</c> JSON mirror from their own read snapshot while
/// EF writes only the columns THEY modified, so an overlapping edit could commit a row whose typed
/// column, JSON mirror and <c>ComplianceStatus</c> all disagreed. They now run their load → evaluate →
/// save under a <c>REPEATABLE READ</c> transaction: Postgres refuses to let such an UPDATE apply on top
/// of a row version that changed after the snapshot and raises <c>40001</c>
/// (<c>serialization_failure</c>) instead, which <see cref="Endpoints.DocumentWriteConcurrency"/> turns
/// into a reload-and-recompute retry.
/// <para/>
/// The predicate lives in <c>Services/</c> — the layer both an endpoint and a service may depend on —
/// for the reason <see cref="SampleData.IsDocumentUniqueViolation"/> and
/// <see cref="WaitlistSignup.IsDuplicateEmail"/> do, and it has the same three-case unit test (right
/// SqlState true, other SqlState false, non-Postgres false).
/// </summary>
public static class DocumentConcurrency
{
    /// <summary>
    /// How many times a conflicted write is re-run before the caller gives up. Three because the
    /// conflict is a genuine concurrent commit on ONE document, not lock contention on a hot row: the
    /// realistic populations are two people editing the same certificate and an edit landing on top of
    /// a re-extraction, so a second attempt almost always wins and a third is the safety margin. An
    /// unbounded loop would spin a request against a writer that keeps winning; the exhausted case is
    /// answered, not retried forever.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// True when <paramref name="ex"/> carries Postgres's <c>40001 serialization_failure</c> — under
    /// <c>REPEATABLE READ</c>, "you tried to update a row another transaction changed and committed
    /// after your snapshot".
    /// <para/>
    /// Walks the whole inner-exception chain rather than checking one level like the sibling 23505
    /// predicates (<see cref="SampleData.IsDocumentUniqueViolation"/>,
    /// <see cref="IdempotencyService.IsKeyConflict"/>), and that difference is load-bearing, not
    /// stylistic: Npgsql reports 40001 as a TRANSIENT error, and EF — with no retrying execution
    /// strategy configured, which is this app's setup — re-wraps a transient
    /// <see cref="DbUpdateException"/> in an <see cref="InvalidOperationException"/> ("An exception has
    /// been raised that is likely due to a transient failure"). So the real cause sits TWO levels down
    /// out of a <c>SaveChanges</c>, and a one-level check would silently never match, turning the whole
    /// guard into a 500. A unique violation is not transient and is never re-wrapped, which is why the
    /// siblings can look exactly one level in.
    /// <para/>
    /// Deliberately NOT widened to <c>40P01 deadlock_detected</c>, even though Postgres documents both
    /// as retryable. The guard changes no lock ACQUISITION order — it adds no <c>SELECT … FOR UPDATE</c>
    /// and issues the same statements in the same order the un-guarded code did — so a deadlock here
    /// would mean a NEW lock-order inversion somewhere, which must surface loudly rather than be
    /// absorbed by a retry that hides it.
    /// </summary>
    public static bool IsConcurrentUpdateConflict(Exception? ex)
    {
        for (var candidate = ex; candidate is not null; candidate = candidate.InnerException)
            if (candidate is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
                return true;
        return false;
    }
}
