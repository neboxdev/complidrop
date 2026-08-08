using System.Data.Common;
using CompliDrop.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Test-only <see cref="IDbCommandInterceptor"/> on <see cref="SystemDbContext"/> that fails ONE command
/// matching a test-supplied predicate, before it reaches Postgres.
/// <para/>
/// It exists for #460 review round 2, C2. <c>ExtractionWorker.PersistSuccess</c> reads its grading basis
/// INSIDE the best-effort <c>try</c> that degrades the verdict to <c>Pending</c>, deliberately: a throw out
/// of that method is the most expensive failure in this codebase — since #375 item 1 the catch abandons the
/// dirty context unsaved and records a counted failure in a fresh scope against a guarded reload, so the
/// throw costs up to <c>MaxAttempts</c> re-paid Document AI + LLM runs spaced by the retry backoff before a
/// terminal <c>Failed</c> (<c>ExtractionWorker.Clamp</c>'s remarks; pre-#375 it was worse — the bookkeeping
/// save re-threw on the same dirty context and the document was zombie-reclaimed every five minutes). Nothing
/// pinned that placement: the mid-run-delete test's soft delete yields a NON-null basis and never makes the
/// read fail, so hoisting the read above the <c>try</c> left the whole suite green. Making the read itself
/// fail is the only way to say "a transient failure HERE must land on Pending, not back in the queue" — and
/// a hard delete is not a substitute, because the persist's own UPDATE would then throw for an unrelated
/// reason.
/// <para/>
/// The fault is raised from <see cref="DbCommandInterceptor.ReaderExecutingAsync"/> — or, for the set-based
/// writers that go through <c>ExecuteNonQuery</c>, <see cref="DbCommandInterceptor.NonQueryExecutingAsync"/>
/// (<c>CostTrackingService.RecordSpendAsync</c>'s <c>ExecuteUpdateAsync</c>, which the #375 review's
/// settled-row tests fault to land a throw AFTER a successful persist) — i.e. before the command is sent,
/// so nothing is left half-executed and the connection stays usable for the caller's next
/// <c>SaveChanges</c> — which is precisely the "transient failure" shape being simulated.
/// <para/>
/// Armed by CALLBACK rather than by a request header, the same way as
/// <see cref="ConcurrentSystemWriteInterceptor"/> and for the same reason: the writer under test is a
/// background worker, so there is no <c>HttpContext</c> to key on. It self-disarms on the first fire, so
/// "exactly once" is a property of the hook rather than of the test's predicate; <see cref="FaultCount"/>
/// lets a test assert the fault really happened instead of passing because the predicate never matched.
/// Containment is the suite being serial plus the fixture reset.
/// </summary>
public sealed class SystemCommandFaultInterceptor : DbCommandInterceptor
{
    /// <summary>
    /// The message of the injected failure, so a test can prove the outcome came from HERE. Generic on
    /// purpose (#375 review round 2, S3): the faulted command is whatever the predicate matched — the
    /// grading-basis SELECT this class was built for, but also a <c>DocumentFields</c> INSERT batch or
    /// <c>RecordSpendAsync</c>'s <c>UPDATE "Subscriptions"</c> — and this text lands in real columns
    /// (<c>Document.ProcessingError</c>), so a shape-specific sentence would misname the failure there.
    /// </summary>
    public const string FaultMessage = "Simulated transient command failure.";

    /// <summary>
    /// Receives each intercepted command's SQL while armed — reader batches (EF queries and
    /// <c>SaveChanges</c>) AND the set-based non-query writers (<c>ExecuteUpdateAsync</c> /
    /// <c>ExecuteDeleteAsync</c>) — return <c>true</c> to fail that one instead of executing it. Null
    /// means inert, and the hook nulls it itself the moment it fires.
    /// </summary>
    public Func<string, bool>? ShouldFault { get; set; }

    /// <summary>How many commands this hook has faulted since the last reset.</summary>
    public int FaultCount { get; private set; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFault(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    // SaveChanges batches and EF queries execute as readers, but ExecuteUpdateAsync/ExecuteDeleteAsync
    // run through ExecuteNonQuery — without this override a predicate aimed at one of those (e.g.
    // RecordSpendAsync's UPDATE "Subscriptions") would silently never fire and the test would assert
    // the ordinary success path. Async only, like the reader half: every production caller is async.
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFault(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void MaybeFault(DbCommand command)
    {
        if (ShouldFault is { } predicate && predicate(command.CommandText))
        {
            ShouldFault = null; // fire exactly once — the retry/repeat shapes are not what this pins
            FaultCount++;
            throw new InvalidOperationException(FaultMessage);
        }
    }

    public void Reset()
    {
        ShouldFault = null;
        FaultCount = 0;
    }
}

/// <inheritdoc cref="SystemCommandFaultInterceptor"/>
public sealed class SystemCommandFaultOptionsConfiguration(SystemCommandFaultInterceptor interceptor)
    : IDbContextOptionsConfiguration<SystemDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(interceptor);
}
