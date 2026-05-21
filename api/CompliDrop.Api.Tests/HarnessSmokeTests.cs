using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Proves the integration-test harness itself works: the host boots against the container,
/// migrations are applied, auth flows end-to-end with cookies, and Respawn resets state.
/// </summary>
public sealed class HarnessSmokeTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Health_live_returns_ok()
    {
        var resp = await CreateClient().GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ready_confirms_migrated_database_is_reachable()
    {
        var resp = await CreateClient().GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_sets_auth_cookies_and_me_returns_the_user()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("userId").GetGuid().Should().Be(auth.UserId);
    }

    [Fact]
    public async Task Reset_clears_tenant_data_but_keeps_system_templates()
    {
        await RegisterAndLoginAsync(email: "persisted@example.com");
        await using (var db = CreateSystemDb())
        {
            (await db.Users.CountAsync(u => u.Email == "persisted@example.com")).Should().Be(1);
        }

        await Fixture.ResetAsync();

        await using (var db = CreateSystemDb())
        {
            (await db.Users.CountAsync()).Should().Be(0);
            (await db.ComplianceTemplates.CountAsync(t => t.IsSystemTemplate)).Should().BeGreaterThan(0);
        }
    }
}
