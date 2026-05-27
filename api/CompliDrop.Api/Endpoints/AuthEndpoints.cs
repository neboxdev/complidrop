using System.Security.Claims;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Auth;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/register", Register).RequireRateLimiting("auth-strict");
        group.MapPost("/login", Login).RequireRateLimiting("auth-strict");
        group.MapPost("/logout", Logout);
        group.MapPost("/refresh", Refresh).RequireRateLimiting("auth-strict");
        group.MapGet("/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Register(
        RegisterRequest req,
        SystemDbContext db,
        IPasswordHasher hasher,
        ITokenService tokens,
        IOptions<CookieSettings> cookieOpts,
        IOptions<JwtSettings> jwtOpts,
        IAuditLogger audit,
        HttpContext http)
    {
        if (!IsValidEmail(req.Email))
            return Error(400, "validation.email", "Enter a valid email.");
        if (!IsStrongPassword(req.Password))
            return Error(400, "validation.password", "Password must be at least 12 characters and include a letter and a digit.");
        if (string.IsNullOrWhiteSpace(req.FullName) || string.IsNullOrWhiteSpace(req.CompanyName))
            return Error(400, "validation.required", "Full name and company name are required.");

        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email))
            return Error(409, "auth.email_taken", "An account with that email already exists.");

        var now = DateTime.UtcNow;
        var org = new Organization
        {
            Id = Guid.NewGuid(),
            Name = req.CompanyName.Trim(),
            Industry = req.Industry,
            CompanySize = req.CompanySize,
            TimeZone = NormalizeTimeZone(req.TimeZone),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Organizations.Add(org);

        var user = new User
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Email = email,
            PasswordHash = hasher.Hash(req.Password),
            FullName = req.FullName.Trim(),
            Role = "admin",
            CreatedAt = now,
            LastLoginAt = now
        };
        db.Users.Add(user);

        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            StripeCustomerId = null,
            Plan = "free",
            Status = "active",
            DocumentLimit = 5,
            HasVendorPortal = false,
            CreatedAt = now,
            UpdatedAt = now
        });

        foreach (var days in new[] { 60, 30, 14, 7 })
        {
            db.Reminders.Add(new Reminder
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                DaysBefore = days,
                NotifyInternalUser = true,
                NotifyVendor = days <= 30,
                IsActive = true
            });
        }

        await db.SaveChangesAsync();

        IssueCookies(http, user, "free", tokens, cookieOpts.Value, jwtOpts.Value);

        await audit.LogAsync(
            "user.registered",
            nameof(User),
            user.Id,
            after: new { user.Id, user.Email, user.OrganizationId },
            organizationIdOverride: org.Id,
            userIdOverride: user.Id);

        return Results.Ok(new
        {
            data = ToMeResponse(user, org, "free"),
            error = (object?)null
        });
    }

    private static async Task<IResult> Login(
        LoginRequest req,
        SystemDbContext db,
        IPasswordHasher hasher,
        ITokenService tokens,
        IOptions<CookieSettings> cookieOpts,
        IOptions<JwtSettings> jwtOpts,
        IAuditLogger audit,
        HttpContext http)
    {
        var email = req.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
            return Error(400, "validation.email", "Email is required.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
            return Error(401, "auth.invalid_credentials", "Invalid email or password.");

        if (user.LockedUntil is { } locked && locked > DateTime.UtcNow)
            return Error(423, "auth.locked", "Account temporarily locked. Try again later.");

        if (!hasher.Verify(req.Password ?? string.Empty, user.PasswordHash))
        {
            user.FailedLoginAttempts += 1;
            if (AuthLockout.ComputeLockoutDuration(user.FailedLoginAttempts) is { } lockFor)
                user.LockedUntil = DateTime.UtcNow.Add(lockFor);
            await db.SaveChangesAsync();
            await audit.LogAsync(
                "user.login_failed",
                nameof(User),
                user.Id,
                organizationIdOverride: user.OrganizationId,
                userIdOverride: user.Id);
            return Error(401, "auth.invalid_credentials", "Invalid email or password.");
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == user.OrganizationId);
        var plan = sub?.Plan ?? "free";
        IssueCookies(http, user, plan, tokens, cookieOpts.Value, jwtOpts.Value);

        var org = await db.Organizations.FirstAsync(o => o.Id == user.OrganizationId);

        await audit.LogAsync(
            "user.logged_in",
            nameof(User),
            user.Id,
            organizationIdOverride: user.OrganizationId,
            userIdOverride: user.Id);

        return Results.Ok(new { data = ToMeResponse(user, org, plan), error = (object?)null });
    }

    private static IResult Logout(
        HttpContext http,
        IOptions<CookieSettings> cookieOpts)
    {
        http.Response.Cookies.Append(
            CookieAuthSetup.SessionCookie, string.Empty,
            CookieAuthSetup.BuildExpiredSessionCookieOptions(cookieOpts.Value));
        http.Response.Cookies.Append(
            CookieAuthSetup.RefreshCookie, string.Empty,
            CookieAuthSetup.BuildExpiredRefreshCookieOptions(cookieOpts.Value));
        // Clear cd_session_hint too (#69) — otherwise a logged-out user
        // landing back on `/` would still fire the `useMe` probe because
        // the frontend's `enabled: hasSessionHint()` gate would see the
        // stale hint and re-open the round-trip we just eliminated.
        http.Response.Cookies.Append(
            CookieAuthSetup.HintCookie, string.Empty,
            CookieAuthSetup.BuildExpiredHintCookieOptions(cookieOpts.Value));
        return Results.Ok(new { data = new { message = "Logged out." }, error = (object?)null });
    }

    private static async Task<IResult> Refresh(
        SystemDbContext db,
        ITokenService tokens,
        IOptions<CookieSettings> cookieOpts,
        IOptions<JwtSettings> jwtOpts,
        HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieAuthSetup.RefreshCookie, out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
            return Error(401, "auth.token_expired", "Session expired. Please log in again.");

        var principal = tokens.ValidateToken(refreshToken, isRefresh: true);
        if (principal is null) return Error(401, "auth.token_expired", "Session expired. Please log in again.");

        // JwtSecurityTokenHandler maps "sub" to ClaimTypes.NameIdentifier on validation, so the
        // user id must be read from there — the raw "sub" claim no longer exists post-validation.
        var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Error(401, "auth.token_expired", "Session expired.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.token_expired", "Session expired.");

        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == user.OrganizationId);
        var plan = sub?.Plan ?? "free";
        IssueCookies(http, user, plan, tokens, cookieOpts.Value, jwtOpts.Value);

        return Results.Ok(new { data = new { message = "Refreshed." }, error = (object?)null });
    }

    private static async Task<IResult> Me(
        HttpContext http,
        SystemDbContext db)
    {
        var userIdStr = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Error(401, "auth.unauthorized", "Not authenticated.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.unauthorized", "Not authenticated.");

        var org = await db.Organizations.FirstAsync(o => o.Id == user.OrganizationId);
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == user.OrganizationId);
        return Results.Ok(new { data = ToMeResponse(user, org, sub?.Plan ?? "free"), error = (object?)null });
    }

    private static void IssueCookies(
        HttpContext http,
        User user,
        string plan,
        ITokenService tokens,
        CookieSettings cookieCfg,
        JwtSettings jwt)
    {
        var session = tokens.IssueSessionToken(user, plan);
        var refresh = tokens.IssueRefreshToken(user);

        http.Response.Cookies.Append(
            CookieAuthSetup.SessionCookie, session,
            CookieAuthSetup.BuildSessionCookieOptions(cookieCfg, TimeSpan.FromMinutes(jwt.SessionExpiryMinutes)));
        http.Response.Cookies.Append(
            CookieAuthSetup.RefreshCookie, refresh,
            CookieAuthSetup.BuildRefreshCookieOptions(cookieCfg, TimeSpan.FromDays(jwt.RefreshExpiryDays)));
        // Non-httpOnly hint cookie (#69). TTL tracks the refresh window:
        // as long as the browser could still resurrect a session via
        // /api/auth/refresh, the landing page may legitimately fire the
        // probe; once the refresh expires the hint should expire with it
        // so anonymous visits return to zero-cost. Refresh() also calls
        // IssueCookies, so the hint slides forward whenever the session
        // does.
        http.Response.Cookies.Append(
            CookieAuthSetup.HintCookie, CookieAuthSetup.HintCookieValue,
            CookieAuthSetup.BuildHintCookieOptions(cookieCfg, TimeSpan.FromDays(jwt.RefreshExpiryDays)));
    }

    private static AuthMeResponse ToMeResponse(User user, Organization org, string plan) =>
        new(user.Id, user.OrganizationId, user.Email, user.FullName, user.Role, plan, org.Name, org.TimeZone);

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Contains('@')
        && email.Length <= 256;

    private static bool IsStrongPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12) return false;
        var hasLetter = password.Any(char.IsLetter);
        var hasDigit = password.Any(char.IsDigit);
        return hasLetter && hasDigit;
    }

    private static string NormalizeTimeZone(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return "America/New_York";
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); return tz; }
        catch { return "America/New_York"; }
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
