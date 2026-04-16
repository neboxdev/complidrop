using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CompliDrop.Api.Tests.TestHelpers;

public static class DbContextFactory
{
    public static string GetConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var config = new ConfigurationBuilder()
            .AddUserSecrets<Marker>()
            .AddEnvironmentVariables()
            .Build();

        var conn = config.GetConnectionString("Database");
        if (string.IsNullOrWhiteSpace(conn))
            throw new InvalidOperationException(
                "Test DB connection missing. Set ConnectionStrings__Database env var or add user-secret ConnectionStrings:Database.");
        return conn;
    }

    public static AppDbContext CreateApp(ICurrentUser currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(GetConnectionString())
            .AddInterceptors(new AuditSaveChangesInterceptor(() => currentUser))
            .Options;
        return new AppDbContext(options, currentUser);
    }

    public static SystemDbContext CreateSystem(ICurrentUser? currentUser = null)
    {
        var options = new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql(GetConnectionString())
            .AddInterceptors(new AuditSaveChangesInterceptor(() => currentUser))
            .Options;
        return new SystemDbContext(options);
    }

    private class Marker { }
}
