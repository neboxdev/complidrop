using CompliDrop.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Test-only <see cref="ISaveChangesInterceptor"/> that runs a supplied callback from
/// <see cref="SaveChangesInterceptor.SavingChangesAsync"/> — i.e. AFTER the handler has read the
/// document and staged its changes, but BEFORE EF issues the UPDATE.
/// <para/>
/// That is the exact window #366 is about, and this is what makes the interleave DETERMINISTIC. Racing
/// two real HTTP requests from two threads and hoping they overlap gives a test that passes for the
/// wrong reason on most runs; holding the first request open at this hook and driving the competing
/// writer to completion inside it makes "the other writer committed between my snapshot and my UPDATE"
/// a certainty on every run, on every machine.
/// <para/>
/// The callback is what commits the competing change — the tests drive a REAL second HTTP request
/// through the same host (its own scope, its own connection, its own transaction), so both sides of the
/// interleave are genuine endpoint invocations rather than hand-written SQL that only resembles one.
/// <para/>
/// Inert unless a test has set <see cref="OnSavingChanges"/>, which is also what scopes it: tests set it
/// immediately before the request under test and clear it immediately after (the fixture reset clears it
/// too, so a forgotten one cannot leak into the next test). Arming on the callback rather than on a
/// request header — the <see cref="PostCommitFaultInterceptor"/> shape — is deliberate: the competing
/// writer is itself an in-process request through the same <c>TestServer</c>, so
/// <c>IHttpContextAccessor</c> is exactly the thing whose value across that nested call this test must
/// not depend on.
/// <para/>
/// Re-entrancy is handled by a plain instance flag held across the awaited callback, so the competing
/// write's OWN <c>SaveChanges</c> sees the hook disarmed and cannot trigger a competing write of its own
/// (without it, the retry-exhaustion shape — where the callback never stops firing — recurses until the
/// request dies). It has to be an instance field rather than an <see cref="AsyncLocal{T}"/>:
/// <c>TestServer</c> defaults <c>PreserveExecutionContext</c> to false and SUPPRESSES execution-context
/// flow into the request pipeline, so an AsyncLocal set out here is invisible inside the nested request.
/// A shared field is safe precisely because this fixture's tests are a serial collection — there is no
/// second concurrent request to disarm by accident.
/// <para/>
/// It fires on EVERY <c>SaveChanges</c> while armed, retries included — deliberately: a test that wants
/// exactly one conflict lets its own callback stop after the first call, and the retry-exhaustion test
/// wants every attempt to conflict. A fire-once rule in here would make the second shape untestable.
/// </summary>
public class ConcurrentDocumentWriteInterceptor : SaveChangesInterceptor
{
    private bool _insideCompetingWrite;

    /// <summary>
    /// The competing write, invoked (and awaited) inside the armed request's <c>SaveChanges</c>. Null
    /// means inert; <see cref="Reset"/> restores that.
    /// </summary>
    public Func<Task>? OnSavingChanges { get; set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    {
        if (OnSavingChanges is { } competingWrite && !_insideCompetingWrite)
        {
            _insideCompetingWrite = true;
            try
            {
                await competingWrite();
            }
            finally
            {
                _insideCompetingWrite = false;
            }
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public void Reset()
    {
        OnSavingChanges = null;
        _insideCompetingWrite = false;
    }
}

/// <summary>
/// Attaches <see cref="ConcurrentDocumentWriteInterceptor"/> to <see cref="AppDbContext"/> from the TEST
/// host only — the same EF options-configuration hook, and for the same reasons, as
/// <see cref="PostCommitFaultOptionsConfiguration"/> (see its remarks: a second <c>AddDbContext</c> is a
/// silent no-op, and a plain <c>AddSingleton&lt;IInterceptor&gt;</c> is not resolved by EF). Every
/// registered <c>IDbContextOptionsConfiguration&lt;AppDbContext&gt;</c> is applied, so this coexists
/// with the post-commit fault one rather than replacing it.
/// </summary>
public sealed class ConcurrentDocumentWriteOptionsConfiguration(ConcurrentDocumentWriteInterceptor interceptor)
    : IDbContextOptionsConfiguration<AppDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(interceptor);
}

/// <summary>
/// The same hook on the OTHER context — <see cref="SystemDbContext"/>, which is what the background
/// workers save through. A SEPARATE type (and therefore a separate singleton with its own callback)
/// rather than the AppDbContext instance wired twice, deliberately: <c>IAuditLogger</c> writes on its own
/// <see cref="SystemDbContext"/> during ordinary requests, so sharing one armed callback across both
/// contexts would make every existing #366 test fire its competing write from a place it never chose.
/// <para/>
/// It exists for #460 (ADR 0030 Amendment 2). <c>ExtractionWorker.PersistSuccess</c> reads its grading
/// basis and THEN saves, so the only window that can distinguish "grade the fresh row, write nothing back"
/// from "re-read the input and ASSIGN it" is the one BETWEEN those two — and
/// <see cref="FakeExtractionClient.DuringExtract"/> cannot reach it (it fires before the basis read, where
/// an assign-back writes the value the row already holds and every behavioural assertion still passes).
/// This one fires from inside the worker's own <c>SavingChanges</c>, i.e. after the basis read and before
/// the UPDATE, which is where a lost update becomes visible.
/// <para/>
/// A successful <c>ProcessDocumentAsync</c> raises <c>SavingChangesAsync</c> on this context exactly ONCE —
/// the persist. <c>CostTrackingService.RecordSpendAsync</c>, which runs right after it, does NOT: it is an
/// <c>ExecuteUpdateAsync</c>, which goes through <see cref="IDbCommandInterceptor"/> and never through the
/// change tracker. So a test's self-disarm on first fire is belt-and-braces beside the base class's
/// <c>_insideCompetingWrite</c> re-entrancy flag rather than a guard against an expected second save — the
/// same rule as the AppDbContext hook, kept for the same reason (a callback that drives a real request can
/// reach this context again through <c>IAuditLogger</c>).
/// </summary>
public sealed class ConcurrentSystemWriteInterceptor : ConcurrentDocumentWriteInterceptor;

/// <inheritdoc cref="ConcurrentSystemWriteInterceptor"/>
public sealed class ConcurrentSystemWriteOptionsConfiguration(ConcurrentSystemWriteInterceptor interceptor)
    : IDbContextOptionsConfiguration<SystemDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(interceptor);
}
