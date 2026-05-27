using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CompliDrop.Api.Tests;

/// <summary>Integration tests for the auth lifecycle (register / me / logout / refresh) on the harness.</summary>
public sealed class AuthEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private HttpClient RawClient() =>
        Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static object NewRegistration(string email) => new
    {
        email,
        password = "Password1234",
        fullName = "T",
        companyName = "C",
        industry = (string?)null,
        companySize = (string?)null,
        timeZone = "America/New_York"
    };

    private static List<string> SetCookies(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.ToList() : [];

    private static string? CookieValue(IEnumerable<string> setCookies, string name)
    {
        foreach (var c in setCookies)
        {
            if (!c.StartsWith(name + "=")) continue;
            var afterEq = c[(name.Length + 1)..];
            var semi = afterEq.IndexOf(';');
            return semi >= 0 ? afterEq[..semi] : afterEq;
        }
        return null;
    }

    [Fact]
    public async Task Register_sets_httponly_session_and_refresh_cookies()
    {
        var resp = await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration($"a-{Guid.NewGuid():N}@x.com"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookies = SetCookies(resp);
        cookies.Should().Contain(c => c.StartsWith("cd_session=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        cookies.Should().Contain(c => c.StartsWith("cd_refresh=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    // ── #69: the non-httpOnly hint cookie. Every successful auth handshake
    // (register / login / refresh) MUST set `cd_session_hint=1; Path=/`
    // WITHOUT the HttpOnly flag — that's the whole point of the cookie:
    // the SPA reads it via `document.cookie` to gate the landing-page
    // probe behind `useQuery({ enabled })`. If a future refactor copied
    // the session-cookie options (with HttpOnly=true) onto the hint by
    // accident, anonymous visitors would silently pay the auth round-trip
    // again. The tests below pin the shape so that regression is loud.
    [Fact]
    public async Task Register_sets_non_httponly_session_hint_cookie_at_root_path()
    {
        var resp = await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration($"h-{Guid.NewGuid():N}@x.com"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookies = SetCookies(resp);
        var hint = cookies.FirstOrDefault(c => c.StartsWith("cd_session_hint="));
        hint.Should().NotBeNull("the SPA needs cd_session_hint to skip the landing-page probe (#69)");
        // CRITICAL invariant: NO HttpOnly flag. The SPA reads this cookie
        // via document.cookie; an HttpOnly cookie cannot be read from JS,
        // making the gate always-false and the optimization invisible.
        hint!.Should().NotContain("httponly", "cd_session_hint MUST be readable from document.cookie — adding HttpOnly silently breaks the #69 optimization");
        // Path=/ so the landing page (and every other public page) can
        // read the hint regardless of where the user lands first.
        hint.Should().Contain("path=/", "the hint must be readable on the landing page surface");
        // The value is the literal "1" — no PII, no credential, no
        // identifier. The hint signals presence-of-session-at-some-point
        // only; the real session/refresh tokens remain httpOnly.
        CookieValue(cookies, "cd_session_hint").Should().Be("1");
    }

    [Fact]
    public async Task Login_sets_non_httponly_session_hint_cookie()
    {
        var email = $"login-hint-{Guid.NewGuid():N}@x.com";
        // Register first to create the account, then login (login is the
        // surface the ticket calls out explicitly).
        (await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration(email))).EnsureSuccessStatusCode();

        var resp = await RawClient().PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = "Password1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookies = SetCookies(resp);
        var hint = cookies.FirstOrDefault(c => c.StartsWith("cd_session_hint="));
        hint.Should().NotBeNull();
        hint!.Should().NotContain("httponly");
        CookieValue(cookies, "cd_session_hint").Should().Be("1");
    }

    [Fact]
    public async Task Refresh_re_issues_the_session_hint_cookie()
    {
        // The hint TTL tracks the refresh window: as long as the browser
        // can still resurrect a session via /api/auth/refresh, the hint
        // must keep sliding forward. Refresh() calls IssueCookies, which
        // writes all three; this test pins that the hint comes back with
        // each refresh so the cookie doesn't expire mid-session-lifecycle.
        var reg = await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration($"refresh-hint-{Guid.NewGuid():N}@x.com"));
        var refreshToken = CookieValue(SetCookies(reg), "cd_refresh");
        refreshToken.Should().NotBeNullOrEmpty();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.TryAddWithoutValidation("Cookie", $"cd_refresh={refreshToken}");
        var resp = await RawClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookies = SetCookies(resp);
        cookies.Should().Contain(c => c.StartsWith("cd_session_hint=1") && !c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logout_clears_the_session_hint_cookie_alongside_session_and_refresh()
    {
        // AC #4 from the ticket: logout MUST clear cd_session_hint so a
        // logged-out user landing back on `/` doesn't fire a stale-hint
        // probe (re-opening the round-trip #69 was meant to close).
        // Same shape as cd_session/cd_refresh clearing: Set-Cookie with
        // empty value + Expires=epoch.
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync("/api/auth/logout", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookies = SetCookies(resp);
        var hint = cookies.FirstOrDefault(c => c.StartsWith("cd_session_hint="));
        hint.Should().NotBeNull("logout must overwrite cd_session_hint or the SPA gate stays open after sign-out (#69 AC #4)");
        // Cleared cookie shape: empty value + Expires header in the past.
        // The exact format is `expires=Thu, 01 Jan 1970 00:00:00 GMT` in
        // .NET, but matching just the year keeps the assertion robust to
        // any future framework-level reformatting while still proving the
        // expiry is in the deep past.
        CookieValue(cookies, "cd_session_hint").Should().BeEmpty();
        hint!.Should().Contain("expires=", "the logout Set-Cookie must carry an explicit past expiry");
        hint.Should().Contain("1970");
    }

    [Fact]
    public async Task Me_returns_the_authenticated_user()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("email").GetString().Should().Be(auth.Email);
    }

    [Fact]
    public async Task Logout_clears_cookies_so_me_is_then_unauthorized()
    {
        var auth = await RegisterAndLoginAsync();
        (await auth.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await auth.Client.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await auth.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_a_valid_refresh_cookie_issues_a_new_session()
    {
        // cd_refresh is scoped to Path=/api/auth; send it explicitly because the test cookie
        // handler doesn't path-scope-replay it the way a real browser does (prod is unaffected).
        var reg = await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration($"r-{Guid.NewGuid():N}@x.com"));
        var refreshToken = CookieValue(SetCookies(reg), "cd_refresh");
        refreshToken.Should().NotBeNullOrEmpty();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.TryAddWithoutValidation("Cookie", $"cd_refresh={refreshToken}");
        var resp = await RawClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        SetCookies(resp).Should().Contain(c => c.StartsWith("cd_session="));
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_unauthorized()
    {
        (await RawClient().PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_token_presented_as_the_refresh_cookie_is_rejected()
    {
        // Capture the session token from registration, then send it where a refresh token is expected.
        var reg = await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration($"b-{Guid.NewGuid():N}@x.com"));
        var sessionToken = CookieValue(SetCookies(reg), "cd_session");
        sessionToken.Should().NotBeNullOrEmpty();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.TryAddWithoutValidation("Cookie", $"cd_refresh={sessionToken}");

        (await RawClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exceeding_the_auth_strict_rate_limit_returns_the_rate_limit_envelope()
    {
        // #45 followup — the OnRejected hook fires GLOBALLY for every
        // rate-limiter policy (portal-token, portal-ip, auth-strict,
        // waitlist, default-authed). Pre-followup, only the portal
        // paths had tests pinning the envelope shape — `auth-strict`'s
        // 429 behavior was silently changed from empty-body to
        // enveloped without any test coverage. This test closes that
        // gap: the auth-strict 5/min limiter on POST /api/auth/login
        // (and /register, /refresh) MUST emit the same
        // `rate_limit.exceeded` envelope so a future refactor that
        // skipped or per-policy-overrode the hook fails here.
        //
        // The limiter partitions on Connection.RemoteIpAddress, falling
        // back to "unknown" under TestServer. All 6 requests share that
        // single "unknown" partition. Each login attempt against a
        // fresh random email returns 401 (auth.invalid_credentials) and
        // burns one permit; the 6th trips the limiter and surfaces
        // `rate_limit.exceeded` from the OnRejected hook.
        await using var factory = new CustomWebApplicationFactory(
            Fixture.ConnectionString,
            new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        for (var i = 0; i < 5; i++)
        {
            var bogus = await client.PostAsJsonAsync(
                "/api/auth/login",
                new { email = $"missing-{Guid.NewGuid():N}@x.com", password = "Password1234" });
            bogus.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                $"login attempt {i + 1}/5 with no matching user must return 401 before the limiter trips");
        }

        var throttled = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = $"missing-{Guid.NewGuid():N}@x.com", password = "Password1234" });

        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await throttled.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString()
            .Should().Be(
                "rate_limit.exceeded",
                "the OnRejected hook in Program.cs is global to every policy; auth-strict " +
                "must surface the same envelope code as the portal limiters so clients have " +
                "ONE retry rule across every limited endpoint.");

        // The envelope must NOT leak the internal policy name —
        // mirrors the same negative assertion in
        // VendorPortalEndpointsTests's full-shape test.
        var raw = await throttled.Content.ReadAsStringAsync();
        raw.Should().NotContain("auth-strict");
    }
}
