using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Base class for HTTP-level integration tests. Resets the database before each test for
/// isolation and exposes helpers for cookie-authenticated clients and direct DB access.
/// Lives in the "integration" collection so all such tests share one container and run serially.
/// </summary>
[Collection("integration")]
public abstract class IntegrationTestBase(IntegrationTestFixture fixture) : IAsyncLifetime
{
    protected IntegrationTestFixture Fixture { get; } = fixture;

    /// <summary>
    /// Resets the DB before every test. Subclasses that need to seed fixture data should override
    /// and <c>await base.InitializeAsync()</c> first so the reset happens before their seed.
    /// </summary>
    public virtual Task InitializeAsync() => Fixture.ResetAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A client that stores/sends cookies, so auth survives across requests.</summary>
    protected HttpClient CreateClient() =>
        Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    /// <summary>
    /// Constructs a <see cref="SystemDbContext"/> against the test container with the
    /// <see cref="AuditSaveChangesInterceptor"/> wired so seeds/reads via the harness see the
    /// same behavior as production code paths (UpdatedAt auto-set, soft-delete translation,
    /// audit-log emission when a user is supplied). Passing <c>null</c> for the user skips
    /// audit-log emission but still applies UpdatedAt — the right semantic for test fixture
    /// setup.
    /// <para/>
    /// Note: because the interceptor is wired, <c>db.Remove(entity)</c> on a soft-deletable
    /// entity here is translated to a soft delete (UPDATE DeletedAt=now). If a test needs a
    /// genuine hard delete, run it directly against the EF Core context without going through
    /// this helper, or use <c>ExecuteDeleteAsync</c>.
    /// </summary>
    protected SystemDbContext CreateSystemDb(ICurrentUser? user = null) =>
        new(new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(() => user))
            .Options);

    protected AppDbContext CreateAppDb(ICurrentUser user) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(() => user))
            .Options, user);

    /// <summary>Registers a fresh org + admin user and returns a cookie-authenticated client.</summary>
    protected async Task<AuthenticatedClient> RegisterAndLoginAsync(
        string? email = null, string password = "Password1234")
    {
        var client = CreateClient();
        email ??= $"user-{Guid.NewGuid():N}@example.com";

        var resp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            fullName = "Test User",
            companyName = "Test Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        return new AuthenticatedClient(
            client,
            data.GetProperty("userId").GetGuid(),
            data.GetProperty("organizationId").GetGuid(),
            email);
    }

    /// <summary>
    /// Posts <c>/api/auth/login</c> for an existing user and returns a cookie-authenticated
    /// client. Caller is responsible for having registered <paramref name="email"/> first —
    /// typically via <see cref="RegisterAndLoginAsync"/> in arrangement, then discarding that
    /// client and logging back in with <c>LoginAsync</c> when the test scenario needs an
    /// independent login (e.g. a second session for the same user, or proving the login path
    /// itself works given the user already exists).
    /// </summary>
    protected async Task<AuthenticatedClient> LoginAsync(
        string email, string password = "Password1234")
    {
        var client = CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        return new AuthenticatedClient(
            client,
            data.GetProperty("userId").GetGuid(),
            data.GetProperty("organizationId").GetGuid(),
            email);
    }

    protected sealed record AuthenticatedClient(HttpClient Client, Guid UserId, Guid OrgId, string Email);
}
