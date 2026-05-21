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
}
