using CompliDrop.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// The predicate half of #366 (ADR 0030 Amendment 1). Same three-case shape as
/// <c>SampleData.IsDocumentUniqueViolation</c> / <c>IdempotencyService.IsKeyConflict</c>: the right
/// SqlState is true, another SqlState is false, and a non-Postgres failure is false.
/// </summary>
public class DocumentConcurrencyTests
{
    private static PostgresException SerializationFailure() => new(
        "could not serialize access due to concurrent update", "ERROR", "ERROR",
        PostgresErrorCodes.SerializationFailure);

    [Fact]
    public void A_serialization_failure_is_recognized_however_deeply_EF_wrapped_it()
    {
        // THE shape production actually produces, and the reason this predicate walks the chain instead
        // of looking exactly one level in like its 23505 siblings: Npgsql reports 40001 as transient, and
        // EF (no retrying execution strategy configured here) re-wraps a transient DbUpdateException in
        // an InvalidOperationException — so the cause sits TWO levels down out of SaveChangesAsync. A
        // one-level check compiles, reads correctly, and never matches: every conflict would 500.
        DocumentConcurrency.IsConcurrentUpdateConflict(new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new DbUpdateException("conflict", SerializationFailure()))).Should().BeTrue();

        // One level in, and bare (how a raw command would surface it). All three assert the same fact, so
        // a caller must not have to know which wrapping its write produced.
        DocumentConcurrency.IsConcurrentUpdateConflict(new DbUpdateException("conflict", SerializationFailure()))
            .Should().BeTrue();
        DocumentConcurrency.IsConcurrentUpdateConflict(SerializationFailure()).Should().BeTrue();
    }

    [Fact]
    public void Another_postgres_failure_is_not_a_concurrency_conflict()
    {
        // A broadened predicate would retry a write that is failing for a reason retrying cannot fix —
        // and would report the 409 "someone else changed this" over, say, a 22001 that is really a bug.
        DocumentConcurrency.IsConcurrentUpdateConflict(new DbUpdateException("too long", new PostgresException(
            "value too long", "ERROR", "ERROR", PostgresErrorCodes.StringDataRightTruncation)))
            .Should().BeFalse();

        // 40P01 deadlock_detected is deliberately excluded even though Postgres calls it retryable: the
        // guard reorders no lock acquisition, so a deadlock here would be a NEW inversion that must
        // surface rather than be absorbed. See DocumentConcurrency's remarks.
        DocumentConcurrency.IsConcurrentUpdateConflict(new DbUpdateException("deadlock", new PostgresException(
            "deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_non_postgres_failure_is_not_a_concurrency_conflict()
    {
        DocumentConcurrency.IsConcurrentUpdateConflict(
            new DbUpdateException("x", new InvalidOperationException("not postgres"))).Should().BeFalse();
        DocumentConcurrency.IsConcurrentUpdateConflict(new InvalidOperationException("not postgres"))
            .Should().BeFalse();
        DocumentConcurrency.IsConcurrentUpdateConflict(null).Should().BeFalse();
    }

    [Fact]
    public void The_retry_budget_is_bounded_and_leaves_room_for_a_second_try()
    {
        // Pins the shape of the bound rather than the number for its own sake: one attempt is no retry at
        // all (the ticket's whole point is that the loser must re-run against fresh state), and an
        // unbounded loop would let a request spin against a writer that keeps winning.
        DocumentConcurrency.MaxAttempts.Should().BeGreaterThan(1).And.BeLessThan(10);
    }
}
