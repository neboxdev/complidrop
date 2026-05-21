using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Proves the integration-test harness itself works: the host boots against the container,
/// migrations are applied, auth flows end-to-end with cookies, and Respawn resets state
/// (including automatic reset between tests).
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

    // The two tests below share a fixed email and each assert a clean database at the START of
    // the test. They both pass only if IntegrationTestBase.InitializeAsync resets between tests —
    // i.e. they make the per-test auto-reset (the harness's core promise that every downstream
    // ticket depends on) load-bearing, and would fail if it regressed. Without the auto-reset,
    // whichever runs second would see the first test's user (count != 0, and a duplicate-email 409).
    [Fact]
    public async Task Auto_reset_gives_each_test_a_clean_database_1() => await AssertCleanStartThenRegister();

    [Fact]
    public async Task Auto_reset_gives_each_test_a_clean_database_2() => await AssertCleanStartThenRegister();

    private async Task AssertCleanStartThenRegister()
    {
        await using (var db = CreateSystemDb())
        {
            (await db.Users.CountAsync())
                .Should().Be(0, "the per-test reset must wipe data created by other tests in the collection");
        }

        await RegisterAndLoginAsync(email: "iso@example.com");
    }
}
