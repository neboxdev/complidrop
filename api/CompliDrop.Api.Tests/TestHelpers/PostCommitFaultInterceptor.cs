using CompliDrop.Api.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Test-only <see cref="ISaveChangesInterceptor"/> that throws from
/// <see cref="SaveChangesInterceptor.SavedChangesAsync"/> — i.e. AFTER the row has committed but
/// BEFORE <c>SaveChangesAsync</c> returns to the endpoint.
/// <para/>
/// That window is the one an upload endpoint cannot tell apart from a failed insert, and it is real:
/// a connection reset while reading the COMMIT acknowledgement, or a cancellation racing that round
/// trip, leaves Postgres holding the row while the caller sees an exception. The upload paths react to
/// "<c>SaveChangesAsync</c> did not return normally" by rolling their blob back, so without a
/// confirm-absence check they delete the file of a document that EXISTS (#389 re-review, ADR 0046 §5
/// Amendment 2). Nothing else in the app can produce this state on demand: the dashboard handler sets
/// its <c>documentPersisted</c> flag on the very next statement, so the fault has to come from inside
/// <c>SaveChangesAsync</c> itself.
/// <para/>
/// EF fires this hook from <c>DbContext.SaveChangesAsync</c> only after
/// <c>StateManager.SaveChangesAsync</c> (which owns the implicit transaction's commit) has returned —
/// so throwing here is a faithful simulation rather than an approximation.
/// <para/>
/// Inert unless the request carries <see cref="HeaderName"/>, and it fires at most ONCE per request, so
/// every other test — and any second save the faulting request makes while unwinding — is unaffected.
/// </summary>
public sealed class PostCommitFaultInterceptor(IHttpContextAccessor http) : SaveChangesInterceptor
{
    /// <summary>Opt-in header. Its presence, not its value, arms the fault.</summary>
    public const string HeaderName = "X-Test-Fault-After-Commit";

    private const string FiredKey = "PostCommitFaultInterceptor.Fired";

    /// <summary>Message of the thrown exception, so a test can prove the 500 came from HERE.</summary>
    public const string FaultMessage = "Simulated fault after the commit landed.";

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken)
    {
        var context = http.HttpContext;
        if (context is not null
            && context.Request.Headers.ContainsKey(HeaderName)
            && !context.Items.ContainsKey(FiredKey))
        {
            // HttpContext.Items rather than a field: the interceptor is a singleton shared by every
            // concurrent request, and "fire once" must mean once per REQUEST.
            context.Items[FiredKey] = true;
            throw new InvalidOperationException(FaultMessage);
        }

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// Attaches <see cref="PostCommitFaultInterceptor"/> to <see cref="AppDbContext"/> from the TEST host
/// only.
/// <para/>
/// <c>Program.cs</c> registers the context with <c>AddDbContext&lt;AppDbContext&gt;((sp, options) =&gt;
/// …)</c>, and <c>AddCoreServices</c> registers <c>DbContextOptions&lt;AppDbContext&gt;</c> with
/// <c>TryAdd</c> — so a second <c>AddDbContext</c> in <c>ConfigureTestServices</c> is a silent no-op,
/// and replacing the registration outright would mean copying the production connection string and
/// audit interceptor into the harness (two copies that drift). EF's own extension point avoids both:
/// every <c>IDbContextOptionsConfiguration&lt;TContext&gt;</c> in the application service provider is
/// applied to the options builder AFTER the <c>AddDbContext</c> lambda has run.
/// <para/>
/// A plain <c>services.AddSingleton&lt;IInterceptor, …&gt;()</c> does NOT work here — EF does not
/// resolve interceptors from the application service provider on its own.
/// </summary>
public sealed class PostCommitFaultOptionsConfiguration(PostCommitFaultInterceptor interceptor)
    : IDbContextOptionsConfiguration<AppDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(interceptor);
}
