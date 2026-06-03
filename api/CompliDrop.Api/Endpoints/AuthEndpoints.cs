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
        // Email verification (#184). verify-email is anonymous (clicked from an
        // email link, no session); resend requires auth (a logged-in user asking
        // for a fresh link). Both ride the 5/min auth-strict IP bucket — resend
        // only ever targets the caller's OWN inbox, so the abuse surface is a
        // user spamming themselves, which 5/min already bounds.
        group.MapPost("/verify-email", VerifyEmail).RequireRateLimiting("auth-strict");
        group.MapPost("/resend-verification", ResendVerification)
            .RequireAuthorization()
            .RequireRateLimiting("auth-strict");
        // Refresh uses its own generous, cookie-partitioned limiter (not the
        // 5/min IP-based auth-strict) — see the "auth-refresh" policy in
        // Program.cs for why lumping keepalive with login/register brute-force
        // throttling logged users out behind Railway's proxy.
        group.MapPost("/refresh", Refresh).RequireRateLimiting("auth-refresh");
        group.MapGet("/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Register(
        RegisterRequest req,
        SystemDbContext db,
        IPasswordHasher hasher,
        ITokenService tokens,
        IOptions<CookieSettings> cookieOpts,
        IOptions<JwtSettings> jwtOpts,
        IOptions<FrontendSettings> frontendOpts,
        IEmailService emailService,
        ILogger<EmailVerificationToken> logger,
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

        // Tokenized email verification (#184). Persist the HASH now (inside the
        // registration transaction); the RAW token only lives in the emailed
        // link below. If Resend is unconfigured the send is a logged no-op and
        // the user can resend later from the dashboard banner — registration
        // still succeeds (we soft-gate, never block signup on email delivery).
        var (verificationToken, rawVerificationToken) = CreateVerificationToken(user.Id, now);
        db.EmailVerificationTokens.Add(verificationToken);

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

        // Best-effort send AFTER commit — the token row is durable, so a failed
        // send (Resend down / unconfigured) just means "resend later", never a
        // dangling link. The await deliberately couples register latency to the
        // Resend round-trip (bounded, acceptable at MVP and consistent with the
        // reminder worker's inline send); SendVerificationEmailAsync swallows +
        // logs any send exception so it can never fail the committed signup.
        await SendVerificationEmailAsync(emailService, frontendOpts.Value, user.Email, rawVerificationToken, logger, http.RequestAborted);

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

    /// <summary>
    /// Anonymous (#184). Redeems a verification token from the emailed link. The
    /// raw token is the bearer secret; we look it up by SHA-256 hash. Idempotent
    /// on a re-click of an already-redeemed link (returns 200) so a user who taps
    /// twice isn't shown a scary error.
    /// </summary>
    private static async Task<IResult> VerifyEmail(
        VerifyEmailRequest req,
        SystemDbContext db,
        IAuditLogger audit)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return Error(400, "validation.token", "This verification link is invalid.");

        var hash = SecureToken.Hash(req.Token.Trim());
        var token = await db.EmailVerificationTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        // Unknown token: never existed, or the user was deleted (cascade). One
        // generic message — we don't distinguish, to avoid leaking which links
        // were ever valid.
        if (token is null || token.User is null)
            return Error(400, "auth.verification_invalid", "This verification link is invalid or has expired.");

        // Idempotent success keyed on the USER, not the token: a double-click on
        // the same link (first click already verified them) returns success. We
        // deliberately do NOT key this on ConsumedAt — a token can be consumed by
        // INVALIDATION on resend without the user being verified, and that link
        // must NOT report success.
        if (token.User.EmailVerifiedAt is not null)
            return Results.Ok(new { data = new { message = "Your email is already confirmed." }, error = (object?)null });

        // Consumed but the user is still unverified ⇒ this link was superseded by
        // a newer resend. Send them to the most recent link instead of redeeming
        // a stale one.
        if (token.ConsumedAt is not null)
            return Error(400, "auth.verification_invalid", "This verification link is no longer valid. Use the most recent email, or resend a new link from your dashboard.");

        if (token.ExpiresAt <= DateTime.UtcNow)
            return Error(400, "auth.verification_expired", "This verification link has expired. Request a new one from your dashboard.");

        var now = DateTime.UtcNow;
        token.ConsumedAt = now;
        token.User.EmailVerifiedAt = now;
        await db.SaveChangesAsync();

        await audit.LogAsync(
            "user.email_verified",
            nameof(User),
            token.UserId,
            organizationIdOverride: token.User.OrganizationId,
            userIdOverride: token.UserId);

        return Results.Ok(new { data = new { message = "Email confirmed. Thanks!" }, error = (object?)null });
    }

    /// <summary>
    /// Authenticated (#184). Issues a fresh verification link to the caller's own
    /// email. No-op-success when already verified. Invalidates any prior
    /// outstanding tokens so only the newest link works.
    /// </summary>
    private static async Task<IResult> ResendVerification(
        HttpContext http,
        SystemDbContext db,
        IOptions<FrontendSettings> frontendOpts,
        IEmailService email,
        ILogger<EmailVerificationToken> logger)
    {
        var userIdStr = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Error(401, "auth.unauthorized", "Not authenticated.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.unauthorized", "Not authenticated.");

        if (user.EmailVerifiedAt is not null)
            return Results.Ok(new { data = new { message = "Your email is already confirmed." }, error = (object?)null });

        var now = DateTime.UtcNow;
        // Consume previously-emailed links so a stale one can't be redeemed after
        // a resend. (Two near-simultaneous resends could each leave a valid link;
        // that's benign — both belong to the same user verifying their own email,
        // and the 5/min auth-strict limit makes the interleave rare — so we don't
        // serialize.)
        var outstanding = await db.EmailVerificationTokens
            .Where(t => t.UserId == userId && t.ConsumedAt == null)
            .ToListAsync();
        foreach (var t in outstanding) t.ConsumedAt = now;

        var (token, rawToken) = CreateVerificationToken(user.Id, now);
        db.EmailVerificationTokens.Add(token);
        await db.SaveChangesAsync();

        await SendVerificationEmailAsync(email, frontendOpts.Value, user.Email, rawToken, logger, http.RequestAborted);

        return Results.Ok(new { data = new { message = "Verification email sent." }, error = (object?)null });
    }

    // Email verification links stay valid for a week — users routinely confirm
    // late, and (unlike a password reset) the token grants no account access, so
    // a longer TTL trades little risk for far fewer "link expired" dead-ends.
    private const int VerificationTokenTtlDays = 7;

    private static (EmailVerificationToken Token, string RawToken) CreateVerificationToken(Guid userId, DateTime now)
    {
        var (raw, hash) = SecureToken.Generate();
        var token = new EmailVerificationToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = hash,
            ExpiresAt = now.AddDays(VerificationTokenTtlDays),
            CreatedAt = now,
        };
        return (token, raw);
    }

    private static async Task SendVerificationEmailAsync(
        IEmailService email,
        FrontendSettings frontend,
        string toEmail,
        string rawToken,
        ILogger logger,
        CancellationToken ct)
    {
        var link = $"{frontend.BaseUrl.TrimEnd('/')}/verify-email?token={Uri.EscapeDataString(rawToken)}";
        const string subject = "Confirm your email for CompliDrop";
        var body = $"""
            <div style="font-family: system-ui, sans-serif; color: #0c4a6e;">
              <h2 style="color: #0284c7;">Confirm your email</h2>
              <p>Welcome to CompliDrop! Please confirm this is your email address so your compliance reminders reach you.</p>
              <p><a href="{link}" style="display:inline-block;background:#0284c7;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;">Confirm my email</a></p>
              <p style="color: #64748b; font-size: 12px;">Or paste this link into your browser:<br>{link}</p>
              <p style="color: #64748b; font-size: 12px;">This link expires in {VerificationTokenTtlDays} days. If you didn't create a CompliDrop account, you can ignore this email.</p>
            </div>
            """;
        // The send MUST NOT be able to fail the calling request. SendAsync
        // swallows non-2xx Resend responses, but a transient transport failure
        // (DNS/socket/TLS → HttpRequestException) or a client abort
        // (http.RequestAborted → OperationCanceledException) would otherwise
        // throw AFTER the caller has already committed the user + token row —
        // surfacing a 500 to a user whose account actually exists. The durable
        // token guarantees they can always resend later, so we log and continue.
        try
        {
            await email.SendAsync(toEmail, subject, body, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Verification email send failed for {Email}; the durable token lets the user resend later.",
                toEmail);
        }
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
        new(user.Id, user.OrganizationId, user.Email, user.FullName, user.Role, plan, org.Name, org.TimeZone,
            EmailVerified: user.EmailVerifiedAt is not null);

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
