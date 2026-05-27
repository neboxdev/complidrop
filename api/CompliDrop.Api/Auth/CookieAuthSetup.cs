using CompliDrop.Api.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CompliDrop.Api.Auth;

public static class CookieAuthSetup
{
    public const string SessionCookie = "cd_session";
    public const string RefreshCookie = "cd_refresh";
    public const string RefreshPath = "/api/auth";

    // Non-httpOnly companion cookie set/cleared alongside cd_session +
    // cd_refresh (#69). Its sole purpose: let the frontend read
    // `document.cookie` to detect "this browser has been authenticated at
    // some point" WITHOUT a network round-trip — so anonymous visitors to
    // the public landing page pay ZERO auth calls instead of the lone
    // `/api/auth/me` 401 that survived #30. The value is the literal "1";
    // the cookie carries NO credential and the real session/refresh tokens
    // remain httpOnly. See `BuildHintCookieOptions` for the rationale on
    // path, TTL, and the deliberate HttpOnly=false.
    public const string HintCookie = "cd_session_hint";
    public const string HintCookieValue = "1";

    public static void AddCookieJwtAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Cookies.TryGetValue(SessionCookie, out var token)
                            && !string.IsNullOrWhiteSpace(token))
                        {
                            ctx.Token = token;
                        }
                        return Task.CompletedTask;
                    }
                };

                var sp = services.BuildServiceProvider();
                var jwt = sp.GetRequiredService<IOptions<JwtSettings>>().Value;
                var tokenService = new TokenService(Options.Create(jwt));
                options.TokenValidationParameters = tokenService.BuildValidationParameters();
            });

        services.AddAuthorization();
    }

    public static CookieOptions BuildSessionCookieOptions(CookieSettings cfg, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = cfg.Secure,
        SameSite = ParseSameSite(cfg.SameSite),
        Path = "/",
        Domain = cfg.Domain,
        Expires = DateTimeOffset.UtcNow.Add(lifetime),
        IsEssential = true
    };

    public static CookieOptions BuildRefreshCookieOptions(CookieSettings cfg, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = cfg.Secure,
        SameSite = SameSiteMode.Strict,
        Path = RefreshPath,
        Domain = cfg.Domain,
        Expires = DateTimeOffset.UtcNow.Add(lifetime),
        IsEssential = true
    };

    public static CookieOptions BuildExpiredSessionCookieOptions(CookieSettings cfg) => new()
    {
        HttpOnly = true,
        Secure = cfg.Secure,
        SameSite = ParseSameSite(cfg.SameSite),
        Path = "/",
        Domain = cfg.Domain,
        Expires = DateTimeOffset.UnixEpoch
    };

    public static CookieOptions BuildExpiredRefreshCookieOptions(CookieSettings cfg) => new()
    {
        HttpOnly = true,
        Secure = cfg.Secure,
        SameSite = SameSiteMode.Strict,
        Path = RefreshPath,
        Domain = cfg.Domain,
        Expires = DateTimeOffset.UnixEpoch
    };

    // Hint-cookie options: deliberately !HttpOnly so the SPA can read it
    // via `document.cookie` (#69). Path "/" matches the landing page (the
    // exact surface that needs to short-circuit the probe). Secure +
    // SameSite mirror the SESSION cookie (Lax in prod) rather than the
    // refresh cookie's Strict — Strict would suppress the cookie's
    // send-back on cross-site link clicks (e.g. email → /), which doesn't
    // affect `document.cookie` READABILITY today but would matter if a
    // server-side hint reader is added later (#69 followup). Mirroring
    // the session cookie also means a future change to the session's
    // transport-security knob propagates here automatically.
    // TTL matches the refresh cookie: the hint is correlated with "this
    // browser still might resurrect a session via /api/auth/refresh", and
    // Refresh() in AuthEndpoints calls IssueCookies, which re-issues the
    // hint — so it slides forward in lock-step with the refresh window.
    public static CookieOptions BuildHintCookieOptions(CookieSettings cfg, TimeSpan lifetime) => new()
    {
        HttpOnly = false,
        Secure = cfg.Secure,
        SameSite = ParseSameSite(cfg.SameSite),
        Path = "/",
        Domain = cfg.Domain,
        Expires = DateTimeOffset.UtcNow.Add(lifetime),
        IsEssential = true
    };

    public static CookieOptions BuildExpiredHintCookieOptions(CookieSettings cfg) => new()
    {
        HttpOnly = false,
        Secure = cfg.Secure,
        SameSite = ParseSameSite(cfg.SameSite),
        Path = "/",
        Domain = cfg.Domain,
        Expires = DateTimeOffset.UnixEpoch
    };

    private static SameSiteMode ParseSameSite(string value) => value?.ToLowerInvariant() switch
    {
        "strict" => SameSiteMode.Strict,
        "none" => SameSiteMode.None,
        _ => SameSiteMode.Lax
    };
}
