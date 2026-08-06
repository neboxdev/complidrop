using System.Data.Common;
using CompliDrop.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Test-only <see cref="IDbConnectionInterceptor"/> on <see cref="SystemDbContext"/> that makes every
/// attempt to OPEN a database connection fail, without a database that is actually down.
/// <para/>
/// It exists for the two halves of #390's health-probe question, and both need the same thing — a host
/// whose database is unreachable to the request path while still being reachable to the test's own
/// assertions and to the boot that had to succeed to get here:
/// <list type="bullet">
/// <item>the READINESS probe must answer 503 (and say why in the log, not in the body);</item>
/// <item>the two LIVENESS probes must answer 200 while it is armed — <see cref="FaultCount"/> staying
/// at zero is what proves they never asked the database at all, which is the property that keeps a
/// transient Neon blip from restarting a healthy container (ADR 0053).</item>
/// </list>
/// Pointing a second host at a dead connection string cannot serve either: <c>Program.cs</c> migrates on
/// startup, so that host never boots (ADR 0016).
/// <para/>
/// Armed by a flag rather than a request header, like <see cref="SystemCommandFaultInterceptor"/> and for
/// the same reason (there is no <c>HttpContext</c> at the connection layer). It does NOT self-disarm —
/// the liveness pin is an assertion about TWO requests — so callers arm it in a <c>try</c> and
/// <see cref="Reset"/> in the matching <c>finally</c>. Containment is the suite being serial
/// (<c>AssemblyInfo</c>'s <c>CollectionBehavior(DisableTestParallelization = true)</c>).
/// </summary>
public sealed class SystemConnectionFaultInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// The message of the injected failure. Also the string a leak assertion looks for: a real
    /// connection failure's message names the host, database and user, which is exactly what must not
    /// reach a public 503 body.
    /// </summary>
    public const string FaultMessage =
        "Simulated failure opening the connection to host=db.example.internal user=complidrop.";

    /// <summary>While true, every connection open on <see cref="SystemDbContext"/> throws.</summary>
    public bool Armed { get; set; }

    /// <summary>How many opens this hook has faulted since the last reset.</summary>
    public int FaultCount { get; private set; }

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        FaultIfArmed();
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        FaultIfArmed();
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    public void Reset()
    {
        Armed = false;
        FaultCount = 0;
    }

    private void FaultIfArmed()
    {
        if (!Armed) return;
        FaultCount++;
        throw new InvalidOperationException(FaultMessage);
    }
}

/// <inheritdoc cref="SystemConnectionFaultInterceptor"/>
public sealed class SystemConnectionFaultOptionsConfiguration(SystemConnectionFaultInterceptor interceptor)
    : IDbContextOptionsConfiguration<SystemDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(interceptor);
}
