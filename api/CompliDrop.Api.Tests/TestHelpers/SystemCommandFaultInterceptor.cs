using System.Data.Common;
using CompliDrop.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Test-only <see cref="IDbCommandInterceptor"/> on <see cref="SystemDbContext"/> that fails ONE reader
/// command matching a test-supplied predicate, before it reaches Postgres.
/// <para/>
/// It exists for #460 review round 2, C2. <c>ExtractionWorker.PersistSuccess</c> reads its grading basis
/// INSIDE the best-effort <c>try</c> that degrades the verdict to <c>Pending</c>, deliberately: a throw out
/// of that method is the most expensive failure in this codebase (the catch's bookkeeping save runs on the
/// same context, <c>FailedAttempts</c> never increments, and the document is zombie-reclaimed every five
/// minutes RE-PAYING Document AI + the LLM — <c>ExtractionWorker.Clamp</c>'s remarks). Nothing pinned that
/// placement: the mid-run-delete test's soft delete yields a NON-null basis and never makes the read fail,
/// so hoisting the read above the <c>try</c> left the whole suite green. Making the read itself fail is the
/// only way to say "a transient failure HERE must land on Pending, not back in the queue" — and a hard
/// delete is not a substitute, because the persist's own UPDATE would then throw for an unrelated reason.
/// <para/>
/// The fault is raised from <see cref="DbCommandInterceptor.ReaderExecutingAsync"/>, i.e. before the command
/// is sent, so nothing is left half-executed and the connection stays usable for the persist's own
/// <c>SaveChanges</c> — which is precisely the "transient read failure" shape being simulated.
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
    /// <summary>The message of the injected failure, so a test can prove the degrade came from HERE.</summary>
    public const string FaultMessage = "Simulated transient failure reading the grading basis.";

    /// <summary>
    /// Receives each reader command's SQL while armed; return <c>true</c> to fail that one instead of
    /// executing it. Null means inert, and the hook nulls it itself the moment it fires.
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
        if (ShouldFault is { } predicate && predicate(command.CommandText))
        {
            ShouldFault = null; // fire exactly once — the retry/repeat shapes are not what this pins
            FaultCount++;
            throw new InvalidOperationException(FaultMessage);
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
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
