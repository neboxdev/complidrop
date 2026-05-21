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

    public Task InitializeAsync() => Fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A client that stores/sends cookies, so auth survives across requests.</summary>
    protected HttpClient CreateClient() =>
        Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    protected SystemDbContext CreateSystemDb() =>
        new(new DbContextOptionsBuilder<SystemDbContext>().UseNpgsql(Fixture.ConnectionString).Options);

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

    protected sealed record AuthenticatedClient(HttpClient Client, Guid UserId, Guid OrgId, string Email);
}
