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

    private static SameSiteMode ParseSameSite(string value) => value?.ToLowerInvariant() switch
    {
        "strict" => SameSiteMode.Strict,
        "none" => SameSiteMode.None,
        _ => SameSiteMode.Lax
    };
}
