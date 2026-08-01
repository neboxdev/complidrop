using System.Data.Common;
using CompliDrop.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Test-only <see cref="IDbTransactionInterceptor"/> that can fail an explicit transaction's COMMIT
/// with Postgres's <c>40001 serialization_failure</c>, and run a callback once the resulting ROLLBACK
/// has released the row locks.
/// <para/>
/// It exists because #366's guard must not depend on WHERE Postgres reports a conflict.
/// <c>REPEATABLE READ</c> reports first-updater-wins at the UPDATE statement, so today a conflicted
/// attempt of <c>DocumentEndpoints.UpdateFields</c> never reaches the line that records "this attempt
/// wrote a field edit". That is a property of the isolation level, not of the endpoint: SSI reports at
/// COMMIT under <c>SERIALIZABLE</c>, and so does any future writer that takes an explicit row lock.
/// <see cref="ConcurrentDocumentWriteInterceptor"/> cannot construct that shape — a competing write
/// driven from <c>SavingChangesAsync</c> makes the UPDATE itself fail, which is the case that already
/// works — so the fault has to be injected at the commit.
/// <para/>
/// The <see cref="OnRolledBack"/> half is what makes the constructed interleave reachable at all. A
/// competing write cannot run while the faulted attempt is inside its commit: that attempt already holds
/// the document row's UPDATE lock, so a second connection touching the row would block on a lock only
/// this callback's own return can release — a deadlock, not a test. Firing after the rollback is the one
/// point where the previous attempt has let go and the next has not yet read.
/// <para/>
/// Inert unless a test sets a callback, exactly like <see cref="ConcurrentDocumentWriteInterceptor"/>
/// (and for the same reason it is not header-armed: the callbacks drive in-process requests through the
/// same <c>TestServer</c>). Containment is the suite being serial plus the fixture / <c>DisposeAsync</c>
/// resets. The re-entrancy flags keep a callback's own transactions from re-entering the hook.
/// </summary>
public sealed class CommitFaultInterceptor : DbTransactionInterceptor
{
    private int _commits;
    private bool _insideCallback;

    /// <summary>
    /// Invoked with the 1-based ordinal of each commit on this context while armed; return <c>true</c>
    /// to fail that commit with 40001 instead of committing. Null means inert.
    /// </summary>
    public Func<int, Task<bool>>? OnCommitting { get; set; }

    /// <summary>Invoked after a rollback has completed — i.e. once the attempt's row locks are gone.</summary>
    public Func<Task>? OnRolledBack { get; set; }

    /// <summary>The message of the injected failure, so a test can prove the retry came from HERE.</summary>
    public const string FaultMessage = "Simulated serialization failure at COMMIT.";

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (OnCommitting is { } hook && !_insideCallback)
        {
            _insideCallback = true;
            try
            {
                if (await hook(Interlocked.Increment(ref _commits)))
                    throw new PostgresException(
                        FaultMessage, "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
            }
            finally
            {
                _insideCallback = false;
            }
        }

        return await base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    public override async Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);

        if (OnRolledBack is { } hook && !_insideCallback)
        {
            _insideCallback = true;
            try
            {
                await hook();
            }
            finally
            {
                _insideCallback = false;
            }
        }
    }

    public void Reset()
    {
        OnCommitting = null;
        OnRolledBack = null;
        _commits = 0;
        _insideCallback = false;
    }
}

/// <summary>
/// Attaches <see cref="CommitFaultInterceptor"/> to <see cref="AppDbContext"/> from the TEST host only,
/// through the same EF options-configuration hook — and for the same reasons — as
/// <see cref="PostCommitFaultOptionsConfiguration"/> and
/// <see cref="ConcurrentDocumentWriteOptionsConfiguration"/> (see the first one's remarks).
/// </summary>
public sealed class CommitFaultOptionsConfiguration(CommitFaultInterceptor interceptor)
    : IDbContextOptionsConfiguration<AppDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(interceptor);
}
