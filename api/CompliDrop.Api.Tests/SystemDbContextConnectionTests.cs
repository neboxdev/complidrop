using CompliDrop.Api.Data;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins that the background-worker DbContext pins a server-side idle_in_transaction_session_timeout
/// on its connections (#259, problem 4). Without it, a worker connection left idle inside a
/// transaction would hold a row lock indefinitely and the extraction zombie-reclaim would skip the
/// locked row forever. Resolves the REAL host SystemDbContext (which carries the Options-augmented
/// connection string from Program.cs) rather than the harness's bare-connection-string helper.
/// </summary>
public sealed class SystemDbContextConnectionTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task System_db_context_connections_pin_idle_in_transaction_timeout()
    {
        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        await db.Database.OpenConnectionAsync();
        try
        {
            var conn = db.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SHOW idle_in_transaction_session_timeout";
            var value = (string?)await cmd.ExecuteScalarAsync();

            // 120000 ms; Postgres renders it in the largest round unit.
            value.Should().Be("2min",
                "Program.cs must pin idle_in_transaction_session_timeout on the worker's connection so "
                + "Postgres reaps an orphaned-locked, idle-in-transaction connection (#259, problem 4)");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
