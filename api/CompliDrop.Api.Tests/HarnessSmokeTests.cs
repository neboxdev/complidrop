using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data.Seed;
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
    // Tightened from "> 0" to exact equality so a partial reseed regression fails loud. Reading
    // ComplianceTemplateSeed.TemplateCount directly (exposed internal via InternalsVisibleTo) keeps
    // the count in one place — adding a sixth template doesn't break this test, but a seeder
    // regression that fails to insert all templates does.
    private static int ExpectedSystemTemplateCount => ComplianceTemplateSeed.TemplateCount;

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

    /// <summary>
    /// Pins the cookie contract that everything else in the harness — and the frontend —
    /// implicitly depends on: registering issues both <c>cd_session</c> and <c>cd_refresh</c>
    /// as <c>HttpOnly</c> cookies. A regression that flipped HttpOnly off would expose tokens
    /// to client-side script, and a regression that dropped one cookie would silently break
    /// every cookie-authenticated test downstream.
    /// </summary>
    [Fact]
    public async Task Register_response_sets_session_and_refresh_cookies_as_HttpOnly()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"cookie-test-{Guid.NewGuid():N}@example.com",
            password = "Password1234",
            fullName = "Cookie Test",
            companyName = "Cookie Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        resp.EnsureSuccessStatusCode();

        AssertAuthCookiesPresent(resp);
    }

    /// <summary>Mirror of the register assertion for the login path.</summary>
    [Fact]
    public async Task Login_response_sets_session_and_refresh_cookies_as_HttpOnly()
    {
        var email = $"login-cookie-test-{Guid.NewGuid():N}@example.com";
        await RegisterAndLoginAsync(email: email); // arrange: user exists

        // Use a fresh client so the response Set-Cookies under test aren't conflated with the
        // ones from register (the cookie container would otherwise hold both sets).
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Password1234" });
        resp.EnsureSuccessStatusCode();

        AssertAuthCookiesPresent(resp);
    }

    /// <summary>
    /// Smoke test for the <see cref="IntegrationTestBase.LoginAsync"/> helper added in #13.
    /// Register a user, then exercise the login path independently and confirm the returned
    /// client carries credentials that <c>/api/auth/me</c> recognises.
    /// </summary>
    [Fact]
    public async Task LoginAsync_helper_returns_cookie_authed_client_for_existing_user()
    {
        var email = $"login-helper-{Guid.NewGuid():N}@example.com";
        var registered = await RegisterAndLoginAsync(email: email);

        var auth = await LoginAsync(email);

        auth.UserId.Should().Be(registered.UserId);
        auth.OrgId.Should().Be(registered.OrgId);
        auth.Email.Should().Be(email);

        var resp = await auth.Client.GetAsync("/api/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("userId").GetGuid().Should().Be(registered.UserId);
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
            (await db.ComplianceTemplates.CountAsync(t => t.IsSystemTemplate))
                .Should().Be(ExpectedSystemTemplateCount);
        }
    }

    /// <summary>
    /// Inspects the response's <c>Set-Cookie</c> headers (independent of the cookie container)
    /// so the assertions don't lean on the same code path that stores them.
    /// <list type="bullet">
    /// <item>Both <c>cd_session</c> and <c>cd_refresh</c> must be present (exactly once).</item>
    /// <item>Both must carry <c>HttpOnly</c> so the tokens aren't readable by client-side script.</item>
    /// <item>The refresh cookie must be <c>Path</c>-scoped to <c>/api/auth</c> — it's long-lived
    /// and shouldn't be sent on every request, only to the refresh endpoint.</item>
    /// </list>
    /// Each cookie is parsed by splitting on <c>;</c> and trimming, so the attribute check looks
    /// at attribute segments rather than substring-matching the whole header (a JWT value could
    /// in principle contain a literal substring; the segment-based check can't false-positive).
    /// </summary>
    private static void AssertAuthCookiesPresent(HttpResponseMessage resp)
    {
        resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
            .Should().BeTrue("auth endpoints must return Set-Cookie headers");
        var cookies = setCookies!.ToList();

        var sessionMatches = cookies
            .Where(c => c.StartsWith($"{CookieAuthSetup.SessionCookie}=", StringComparison.Ordinal))
            .ToList();
        sessionMatches.Should().ContainSingle("session cookie 'cd_session' must be issued exactly once");
        var sessionAttrs = ParseAttributes(sessionMatches[0]);
        sessionAttrs.Should().Contain("httponly",
            "the session token must not be readable by client-side script");

        var refreshMatches = cookies
            .Where(c => c.StartsWith($"{CookieAuthSetup.RefreshCookie}=", StringComparison.Ordinal))
            .ToList();
        refreshMatches.Should().ContainSingle("refresh cookie 'cd_refresh' must be issued exactly once");
        var refreshAttrs = ParseAttributes(refreshMatches[0]);
        refreshAttrs.Should().Contain("httponly",
            "the refresh token must not be readable by client-side script");
        refreshAttrs.Should().Contain("path=/api/auth",
            "the long-lived refresh cookie must be scoped to /api/auth, not the whole site");
    }

    /// <summary>
    /// Parses a Set-Cookie header into a list of lowercase attribute strings (excluding the
    /// name=value pair itself), so callers can assert membership by attribute name or
    /// name=value without false-positive matches inside the cookie value.
    /// </summary>
    private static List<string> ParseAttributes(string setCookieHeader) =>
        setCookieHeader
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1) // first segment is name=value
            .Select(s => s.ToLowerInvariant())
            .ToList();

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
