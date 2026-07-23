using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

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

    /// <summary>
    /// Returns the value of a Set-Cookie attribute by name (e.g. "path",
    /// "domain", "samesite"). Returns null when the attribute is absent.
    /// Used instead of a `Contains("path=/")` substring check because
    /// substring matching is ambiguous between `path=/` and a regressed
    /// `path=/api/auth` (both contain the literal `path=/`). Parsing the
    /// attribute and comparing its value EXACTLY catches the regression
    /// the assertion docstring names (review #69 followup).
    /// </summary>
    private static string? CookieAttribute(string setCookieLine, string name)
    {
        foreach (var part in setCookieLine.Split(';'))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue; // boolean attribute like `secure` / `httponly`
            var key = trimmed[..eq];
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return trimmed[(eq + 1)..];
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
    /// <summary>
    /// Case-insensitive "contains httponly" check on a Set-Cookie line.
    /// ASP.NET Core's SetCookieHeaderValue emits the flag as lowercase
    /// `httponly` today, so a case-sensitive `Should().NotContain("httponly")`
    /// would catch a `HttpOnly = true` regression today — but FluentAssertions'
    /// string `NotContain` overload IS case-sensitive (the second arg is a
    /// 'because' reason, not a StringComparison). The existing positive
    /// assertions for cd_session / cd_refresh on lines 49–50 already
    /// defensively pass `OrdinalIgnoreCase`. Routing every hint-cookie
    /// assertion through this helper makes the framework's emission casing
    /// a non-load-bearing detail and brings the new hint tests in line
    /// with the pre-existing case-insensitive convention (review #69
    /// followup — pattern symmetry, not a fix for a silently-passing bug).
    /// </summary>
    private static bool HasHttpOnlyFlag(string setCookieLine) =>
        setCookieLine.Contains("httponly", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asserts the four load-bearing properties of an ACTIVE
    /// `cd_session_hint` Set-Cookie (issued by Register / Login /
    /// Refresh): the cookie exists, value is the literal "1", is NOT
    /// HttpOnly, and is scoped to `path=/`. Symmetric coverage across
    /// every auth-issuing endpoint catches a refactor that flips one
    /// property in one path only (review #69 — Refresh and Login tests
    /// previously skipped the path-pin that Register had).
    /// </summary>
    private static void AssertActiveHintCookie(IEnumerable<string> setCookies)
    {
        var cookies = setCookies.ToList();
        var hint = cookies.FirstOrDefault(c => c.StartsWith("cd_session_hint="));
        hint.Should().NotBeNull("the SPA needs cd_session_hint to skip the landing-page probe (#69)");
        // CRITICAL invariant: NO HttpOnly flag. The SPA reads this cookie
        // via document.cookie; an HttpOnly cookie cannot be read from JS,
        // making the gate always-false and the optimization invisible.
        HasHttpOnlyFlag(hint!).Should().BeFalse(
            "cd_session_hint MUST be readable from document.cookie — adding HttpOnly silently breaks the #69 optimization");
        // Path EXACTLY "/" so the landing page (and every other public
        // page) can read the hint regardless of where the user lands
        // first. Parsing the attribute and comparing for equality —
        // rather than `Contains("path=/")` substring matching — catches
        // a regression to `path=/api/auth` (mirroring cd_refresh by
        // accident), which the substring form happily accepts because
        // `/` is a prefix of `/api/auth`.
        CookieAttribute(hint!, "path").Should().Be(
            "/",
            "a hint cookie at any path other than `/` is invisible to document.cookie on the landing page");
        // The value is the literal "1" — no PII, no credential, no
        // identifier. The hint signals presence-of-session-at-some-point
        // only; the real session/refresh tokens remain httpOnly.
        CookieValue(cookies, "cd_session_hint").Should().Be("1");
    }

    [Fact]
    public async Task Register_sets_non_httponly_session_hint_cookie_at_root_path()
    {
        var resp = await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration($"h-{Guid.NewGuid():N}@x.com"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertActiveHintCookie(SetCookies(resp));
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
        AssertActiveHintCookie(SetCookies(resp));
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
        AssertActiveHintCookie(SetCookies(resp));
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
        // The cleared cookie's Path MUST match the live cookie's Path
        // EXACTLY (RFC 6265 §5.3) or the browser will not actually
        // overwrite — a future drift to `path=/api/auth` (mirroring
        // the refresh cookie by accident) would emit a Set-Cookie that
        // the browser happily ignores, leaving the live `path=/` hint
        // in place and re-opening the gate after sign-out (#69 AC #4
        // regression). Equality check on the parsed attribute, not a
        // `Contains("path=/")` substring check that would silently
        // accept the regressed `/api/auth` value.
        CookieAttribute(hint, "path").Should().Be(
            "/",
            "the cleared cookie's Path must match the live cookie's Path or the browser won't delete it (RFC 6265 §5.3)");
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
    public async Task Me_reports_the_corrected_checklists_feature_flag()
    {
        // The additive features object (#416, ADR 0036 Amendment 3) is how the SPA learns whether
        // the gated rules-page surfaces (the liquor add-menu option, the additional-insured nudge)
        // may render. The shared test host pins TemplateCorrections:Enabled=true (see
        // CustomWebApplicationFactory), so the flag surfaces TRUE here; the flag-OFF value is
        // pinned end-to-end by TemplateCorrectionsFlagTests against an ISOLATED database — booting
        // a flag-off host on the shared fixture DB would converge its system templates back to the
        // legacy set and corrupt later seed-dependent tests.
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("features").GetProperty("correctedChecklists").GetBoolean()
            .Should().BeTrue("features.correctedChecklists must mirror TemplateCorrections:Enabled (true on the test host)");
    }

    [Fact]
    public async Task Me_reports_the_corrected_additional_insured_wording_feature_flag_off_by_default()
    {
        // #396 (CLM-1, ADR 0042): the additive feature the SPA reads to pick the additional-insured
        // claim copy. The shared test host does NOT set ComplianceClaims:CorrectedAdditionalInsuredWording,
        // so it surfaces its prod DEFAULT — OFF (legacy copy) — here. The flag-ON value is pinned by
        // ComplianceClaimsFlagTests against an isolated host, mirroring TemplateCorrectionsFlagTests.
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("features").GetProperty("correctedAdditionalInsuredWording").GetBoolean()
            .Should().BeFalse("ComplianceClaims:CorrectedAdditionalInsuredWording defaults OFF and the shared host doesn't set it");
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

    [Fact]
    public async Task Refresh_is_not_throttled_by_the_5_per_minute_auth_strict_limit()
    {
        // Regression pin for the prod logout / "Too many requests" storm.
        // POST /api/auth/refresh moved OFF the 5/min IP-partitioned
        // `auth-strict` bucket onto the generous, cookie-partitioned
        // `auth-refresh` policy (60/min per session). Pre-fix, the 6th
        // refresh in a minute returned 429 — which failed the SPA's silent
        // refresh and logged the user out. With rate limiting ENABLED, six
        // consecutive refreshes carrying a valid cd_refresh cookie must all
        // succeed, proving keepalive is no longer on the brute-force bucket.
        await using var factory = new CustomWebApplicationFactory(
            Fixture.ConnectionString,
            new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var reg = await client.PostAsJsonAsync(
            "/api/auth/register", NewRegistration($"refresh-{Guid.NewGuid():N}@x.com"));
        reg.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshCookie = CookieValue(SetCookies(reg), "cd_refresh");
        refreshCookie.Should().NotBeNullOrEmpty();

        for (var i = 0; i < 6; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
            req.Headers.TryAddWithoutValidation("Cookie", $"cd_refresh={refreshCookie}");
            var resp = await client.SendAsync(req);
            resp.StatusCode.Should().Be(
                HttpStatusCode.OK,
                $"refresh attempt {i + 1}/6 must succeed — refresh is no longer on the 5/min auth-strict bucket");
        }
    }

    [Fact]
    public async Task Unauthenticated_request_to_a_protected_endpoint_returns_the_auth_envelope()
    {
        // The JwtBearer challenge used to write an EMPTY 401 body, which the
        // SPA could not parse — it fell back to the generic "Something went
        // wrong" card on every expired-session request. OnChallenge now emits
        // the standard envelope with a stable `auth.unauthorized` code so the
        // client distinguishes "session expired → redirect to /login" from a
        // real server error.
        var resp = await RawClient().GetAsync("/api/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("auth.unauthorized");
        body.GetProperty("error").GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_refresh_token_minted_before_a_password_change_can_no_longer_refresh()
    {
        // #202: the Refresh path validates the refresh token MANUALLY (it bypasses
        // the OnTokenValidated middleware), so its own stamp check must reject a
        // refresh token issued before a credential change — this guards the 30-day
        // cd_refresh, the longest-lived credential the ticket targets.
        var email = $"stamp-refresh-{Guid.NewGuid():N}@x.com";
        var reg = await RawClient().PostAsJsonAsync("/api/auth/register", NewRegistration(email));
        var cookies = SetCookies(reg);
        var session = CookieValue(cookies, "cd_session");
        var oldRefresh = CookieValue(cookies, "cd_refresh");
        session.Should().NotBeNullOrEmpty();
        oldRefresh.Should().NotBeNullOrEmpty();

        // Change the password (rotates the security stamp) using the session cookie.
        var change = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
        {
            Content = JsonContent.Create(new { currentPassword = "Password1234", newPassword = "RotatedPass456" })
        };
        change.Headers.TryAddWithoutValidation("Cookie", $"cd_session={session}");
        (await RawClient().SendAsync(change)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The refresh token captured BEFORE the change carries the old stamp → 401.
        var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refresh.Headers.TryAddWithoutValidation("Cookie", $"cd_refresh={oldRefresh}");
        (await RawClient().SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_token_without_a_stamp_claim_is_grandfathered()
    {
        // Deploy-safety (#202/ADR 0014): a token minted before security stamps
        // existed (no `stamp` claim) must still authenticate against a live user —
        // liveness applies, the stamp check is skipped — so deploying #202 doesn't
        // mass-logout everyone holding a pre-existing token.
        var auth = await RegisterAndLoginAsync();
        var stampless = MintStamplessSessionToken(auth.UserId, auth.OrgId);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        req.Headers.TryAddWithoutValidation("Cookie", $"cd_session={stampless}");
        (await RawClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Mints a valid session JWT with NO `stamp` claim, signed with the test host's
    /// Jwt secret/issuer/audience (see CustomWebApplicationFactory) — simulates a pre-#202 token.</summary>
    private static string MintStamplessSessionToken(Guid userId, Guid orgId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("integration-test-signing-secret-key-0123456789"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "complidrop-api-test",
            audience: "complidrop-frontend-test",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("org_id", orgId.ToString()),
                new Claim("plan", "free"),
                new Claim("typ", "session"),
                // deliberately NO "stamp" claim
            },
            notBefore: now,
            expires: now.AddMinutes(15),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
