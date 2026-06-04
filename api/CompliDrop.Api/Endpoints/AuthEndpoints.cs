using System.Security.Claims;
using System.Text.Json;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Auth;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        // Org self-service (#185): the owner edits their org name + IANA time
        // zone (the zone silently drives reminder send time, so it must be
        // fixable). Tenant-scoped via AppDbContext; default-authed limiter.
        group.MapPut("/organization", UpdateOrganization)
            .RequireAuthorization()
            .RequireRateLimiting("default-authed");

        // Account & access management (#183). The password flows + email send +
        // deletion all ride the 5/min auth-strict bucket; export is a read so it
        // gets the generous default-authed limiter. forgot/reset-password are
        // anonymous (clicked while logged out); the rest require auth.
        group.MapPost("/forgot-password", ForgotPassword).RequireRateLimiting("auth-strict");
        group.MapPost("/reset-password", ResetPassword).RequireRateLimiting("auth-strict");
        group.MapPost("/change-password", ChangePassword)
            .RequireAuthorization().RequireRateLimiting("auth-strict");
        group.MapPost("/change-email", ChangeEmail)
            .RequireAuthorization().RequireRateLimiting("auth-strict");
        group.MapPost("/account/delete", DeleteAccount)
            .RequireAuthorization().RequireRateLimiting("auth-strict");
        group.MapGet("/account/export", ExportAccount)
            .RequireAuthorization().RequireRateLimiting("default-authed");
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
            return Error(423, "auth.locked", BuildLockoutMessage(locked));

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
            // If THIS attempt just locked the account, tell the user the unlock
            // time + reset path now (#183) rather than a bare "invalid" that
            // leaves them hammering a locked account.
            if (user.LockedUntil is { } justLocked && justLocked > DateTime.UtcNow)
                return Error(423, "auth.locked", BuildLockoutMessage(justLocked));
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

        var isChangeEmail = token.NewEmail is not null;

        // Idempotent success — keyed on the END STATE, not on ConsumedAt (a token
        // can be consumed by INVALIDATION on resend without the goal being met):
        //   - signup verification (#184): the user is already verified.
        //   - change-email (#183): the user's email already equals the pending
        //     new address, i.e. the swap already happened (double-click).
        if (!isChangeEmail && token.User.EmailVerifiedAt is not null)
            return Results.Ok(new { data = new { message = "Your email is already confirmed." }, error = (object?)null });
        if (isChangeEmail && string.Equals(token.User.Email, token.NewEmail, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(new { data = new { message = "Your new email is confirmed." }, error = (object?)null });

        // Consumed but the goal isn't met ⇒ this link was superseded by a newer
        // request. Send them to the most recent link instead of redeeming a stale
        // one.
        if (token.ConsumedAt is not null)
            return Error(400, "auth.verification_invalid", "This verification link is no longer valid. Use the most recent email, or resend a new link from your dashboard.");

        if (token.ExpiresAt <= DateTime.UtcNow)
            return Error(400, "auth.verification_expired", "This verification link has expired. Request a new one from your dashboard.");

        // Change-email flow (#183): the token confirms a PENDING new address.
        // Re-check uniqueness at redeem time (someone else may have taken it in
        // the interim) before swapping it in.
        if (token.NewEmail is { } pendingEmail)
        {
            if (await db.Users.AnyAsync(u => u.Email == pendingEmail && u.Id != token.UserId))
                return Error(409, "auth.email_taken", "That email address is already in use by another account.");
            token.User.Email = pendingEmail;
        }

        var now = DateTime.UtcNow;
        token.ConsumedAt = now;
        token.User.EmailVerifiedAt = now;
        await db.SaveChangesAsync();

        await audit.LogAsync(
            token.NewEmail is null ? "user.email_verified" : "user.email_changed",
            nameof(User),
            token.UserId,
            organizationIdOverride: token.User.OrganizationId,
            userIdOverride: token.UserId);

        return Results.Ok(new
        {
            data = new { message = token.NewEmail is null ? "Email confirmed. Thanks!" : "Your new email is confirmed." },
            error = (object?)null
        });
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
        // Consume previously-emailed SIGNUP-verification links (NewEmail == null)
        // so a stale one can't be redeemed after a resend. Deliberately leave any
        // pending CHANGE-EMAIL token (NewEmail != null) intact: resend targets the
        // user's CURRENT address, and a one-tap banner resend must NOT silently
        // cancel an in-flight email change (a cross-flow data-loss seam surfaced
        // in the #180 re-review). (Two near-simultaneous resends could each leave
        // a valid signup link; that's benign — same user, same address, 5/min
        // limited — so we don't serialize.)
        var outstanding = await db.EmailVerificationTokens
            .Where(t => t.UserId == userId && t.ConsumedAt == null && t.NewEmail == null)
            .ToListAsync();
        foreach (var t in outstanding) t.ConsumedAt = now;

        var (token, rawToken) = CreateVerificationToken(user.Id, now);
        db.EmailVerificationTokens.Add(token);
        await db.SaveChangesAsync();

        await SendVerificationEmailAsync(email, frontendOpts.Value, user.Email, rawToken, logger, http.RequestAborted);

        return Results.Ok(new { data = new { message = "Verification email sent." }, error = (object?)null });
    }

    /// <summary>
    /// Authenticated (#185). Updates the caller's organization name + IANA time
    /// zone. Tenant-scoped via AppDbContext (the global query filter guarantees
    /// the caller can only ever touch their own org). The time zone strictly
    /// drives reminder send time, so an invalid zone is REJECTED (400) rather
    /// than silently normalized to a default — the user must end up with the
    /// zone they intended. Returns the refreshed Me so the SPA updates its cache.
    /// </summary>
    private static async Task<IResult> UpdateOrganization(
        UpdateOrganizationRequest req,
        HttpContext http,
        AppDbContext db)
    {
        var userIdStr = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Error(401, "auth.unauthorized", "Not authenticated.");

        var name = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Error(400, "validation.required", "Organization name is required.");
        if (name.Length > 200)
            return Error(400, "validation.required", "Organization name must be 200 characters or fewer.");

        if (string.IsNullOrWhiteSpace(req.TimeZone) || !IsValidTimeZone(req.TimeZone))
            return Error(400, "validation.timezone", "Choose a valid time zone.");

        // AppDbContext's tenant filter scopes Organizations to CurrentOrgId, so
        // this resolves the caller's own org and nothing else.
        var org = await db.Organizations.FirstOrDefaultAsync();
        if (org is null) return Error(404, "org.not_found", "Organization not found.");

        org.Name = name;
        org.TimeZone = req.TimeZone;
        // The AuditSaveChangesInterceptor records the Organization Before/After
        // (old zone → new zone), so no explicit audit call is needed.
        await db.SaveChangesAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.unauthorized", "Not authenticated.");
        var sub = await db.Subscriptions.FirstOrDefaultAsync();
        return Results.Ok(new { data = ToMeResponse(user, org, sub?.Plan ?? "free"), error = (object?)null });
    }

    private static bool IsValidTimeZone(string tz)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); return true; }
        catch { return false; }
    }

    // ───────────────────────── Account & access management (#183) ─────────────────────────

    /// <summary>
    /// Anonymous (#183). Sends a password-reset link. ALWAYS returns the same 200
    /// body, and now does ALL account-existence-dependent work (the user lookup,
    /// token write, audit, and email send) on a DETACHED background scope — so the
    /// response time is identical whether or not the email is registered. An
    /// earlier version only detached the email send, but the exists-path still did
    /// extra synchronous DB round-trips (token query/insert + audit), leaving a
    /// measurable timing oracle (#180 re-review). Moving everything off the
    /// request path closes it: both the registered and unregistered cases return
    /// after zero DB work.
    /// </summary>
    private static IResult ForgotPassword(
        ForgotPasswordRequest req,
        IServiceScopeFactory scopeFactory,
        IOptions<FrontendSettings> frontendOpts,
        ILogger<PasswordResetToken> logger)
    {
        var generic = Results.Ok(new
        {
            data = new { message = "If that email is registered, we've sent a password reset link." },
            error = (object?)null
        });

        var normalized = req.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return generic;

        var frontend = frontendOpts.Value;
        // Detached: a fresh DI scope (the request scope is gone once we return).
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<SystemDbContext>();
                var email = sp.GetRequiredService<IEmailService>();
                var audit = sp.GetRequiredService<IAuditLogger>();

                var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized);
                if (user is null) return; // unknown email — no-op (no enumeration signal)

                var now = DateTime.UtcNow;
                // Only the newest reset link works: consume prior outstanding ones.
                var outstanding = await db.PasswordResetTokens
                    .Where(t => t.UserId == user.Id && t.ConsumedAt == null)
                    .ToListAsync();
                foreach (var t in outstanding) t.ConsumedAt = now;

                var (raw, hash) = SecureToken.Generate();
                db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    TokenHash = hash,
                    ExpiresAt = now.AddMinutes(PasswordResetTokenTtlMinutes),
                    CreatedAt = now,
                });
                await db.SaveChangesAsync();
                await audit.LogAsync(
                    "user.password_reset_requested",
                    nameof(User), user.Id,
                    organizationIdOverride: user.OrganizationId, userIdOverride: user.Id);

                // Save BEFORE send so the emailed link always resolves to a row.
                await SendPasswordResetEmailAsync(email, frontend, user.Email, raw, logger, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background forgot-password processing failed.");
            }
        });

        return generic;
    }

    /// <summary>
    /// Anonymous (#183). Redeems a reset token and sets a new password. On success
    /// it CLEARS the account lockout (a locked-out user resets to regain access)
    /// and invalidates every other outstanding reset token for the user.
    /// </summary>
    private static async Task<IResult> ResetPassword(
        ResetPasswordRequest req,
        SystemDbContext db,
        IPasswordHasher hasher,
        IAuditLogger audit)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return Error(400, "validation.token", "This reset link is invalid.");
        if (!IsStrongPassword(req.NewPassword))
            return Error(400, "validation.password", "Password must be at least 12 characters and include a letter and a digit.");

        var hash = SecureToken.Hash(req.Token.Trim());
        var token = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        // One generic rejection for unknown / consumed / expired — the link is the
        // bearer secret, so we don't distinguish the failure modes.
        if (token is null || token.User is null || token.ConsumedAt is not null || token.ExpiresAt <= DateTime.UtcNow)
            return Error(400, "auth.reset_invalid", "This reset link is invalid or has expired. Request a new one.");

        var now = DateTime.UtcNow;
        token.User.PasswordHash = hasher.Hash(req.NewPassword);
        // A successful reset is also the lockout escape hatch (#183).
        token.User.FailedLoginAttempts = 0;
        token.User.LockedUntil = null;
        token.ConsumedAt = now;

        var others = await db.PasswordResetTokens
            .Where(t => t.UserId == token.UserId && t.ConsumedAt == null && t.Id != token.Id)
            .ToListAsync();
        foreach (var t in others) t.ConsumedAt = now;

        await db.SaveChangesAsync();

        await audit.LogAsync(
            "user.password_reset",
            nameof(User), token.UserId,
            organizationIdOverride: token.User.OrganizationId, userIdOverride: token.UserId);

        return Results.Ok(new
        {
            data = new { message = "Your password has been reset. You can sign in with your new password now." },
            error = (object?)null
        });
    }

    /// <summary>Authenticated (#183). Changes the password after verifying the current one.</summary>
    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest req,
        HttpContext http,
        SystemDbContext db,
        IPasswordHasher hasher,
        IAuditLogger audit)
    {
        if (GetUserId(http) is not { } userId)
            return Error(401, "auth.unauthorized", "Not authenticated.");
        if (!IsStrongPassword(req.NewPassword))
            return Error(400, "validation.password", "Password must be at least 12 characters and include a letter and a digit.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.unauthorized", "Not authenticated.");

        if (!hasher.Verify(req.CurrentPassword ?? string.Empty, user.PasswordHash))
            return Error(400, "auth.invalid_password", "Your current password is incorrect.");

        user.PasswordHash = hasher.Hash(req.NewPassword);
        await db.SaveChangesAsync();

        await audit.LogAsync(
            "user.password_changed",
            nameof(User), user.Id,
            organizationIdOverride: user.OrganizationId, userIdOverride: user.Id);

        return Results.Ok(new { data = new { message = "Your password has been updated." }, error = (object?)null });
    }

    /// <summary>
    /// Authenticated (#183). Starts a change-email flow: verifies the password,
    /// then emails a confirmation link to the NEW address. The email only swaps
    /// once that link is redeemed (VerifyEmail), so a typo'd new address can't
    /// lock the user out of their account.
    /// </summary>
    private static async Task<IResult> ChangeEmail(
        ChangeEmailRequest req,
        HttpContext http,
        SystemDbContext db,
        IPasswordHasher hasher,
        IOptions<FrontendSettings> frontendOpts,
        IEmailService email,
        ILogger<EmailVerificationToken> logger,
        IAuditLogger audit)
    {
        if (GetUserId(http) is not { } userId)
            return Error(401, "auth.unauthorized", "Not authenticated.");
        if (!IsValidEmail(req.NewEmail))
            return Error(400, "validation.email", "Enter a valid email.");

        var newEmail = req.NewEmail.Trim().ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.unauthorized", "Not authenticated.");

        if (!hasher.Verify(req.Password ?? string.Empty, user.PasswordHash))
            return Error(400, "auth.invalid_password", "Your password is incorrect.");
        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            return Error(400, "validation.email", "That's already your email address.");
        // Uniqueness MUST be checked across ALL tenants (the Email unique index is
        // global), which is exactly why every authed handler here uses
        // SystemDbContext, NOT AppDbContext: AppDbContext's tenant filter
        // (OrganizationId == CurrentOrgId) would hide a clash owned by another org
        // and let a duplicate slip past into a DB unique-constraint violation. This
        // is the intentional reason #183's authed writes diverge from #185's
        // UpdateOrganization (which uses AppDbContext) — do NOT "unify" them onto
        // AppDbContext.
        if (await db.Users.AnyAsync(u => u.Email == newEmail))
            return Error(409, "auth.email_taken", "That email address is already in use.");

        var now = DateTime.UtcNow;
        // Invalidate any prior outstanding verification tokens (a pending signup
        // confirmation or an earlier change request) so only the newest applies.
        var outstanding = await db.EmailVerificationTokens
            .Where(t => t.UserId == userId && t.ConsumedAt == null)
            .ToListAsync();
        foreach (var t in outstanding) t.ConsumedAt = now;

        var (token, raw) = CreateVerificationToken(user.Id, now, newEmail);
        db.EmailVerificationTokens.Add(token);
        await db.SaveChangesAsync();

        // The confirmation link goes to the NEW address (proving the user owns it).
        await SendVerificationEmailAsync(email, frontendOpts.Value, newEmail, raw, logger, http.RequestAborted);
        await audit.LogAsync(
            "user.email_change_requested",
            nameof(User), user.Id,
            organizationIdOverride: user.OrganizationId, userIdOverride: user.Id);

        return Results.Ok(new
        {
            data = new { message = $"We've sent a confirmation link to {newEmail}. Click it to finish changing your email." },
            error = (object?)null
        });
    }

    /// <summary>
    /// Authenticated (#183). Deletes the account after a password re-check
    /// (GDPR/CCPA erasure): scrubs the user's PII (email + name), soft-deletes the
    /// user + organization (revoking all access and hiding tenant data via the
    /// query filters), and clears the auth cookies. Scrubbing the email also frees
    /// it for a future re-registration.
    /// </summary>
    private static async Task<IResult> DeleteAccount(
        DeleteAccountRequest req,
        HttpContext http,
        SystemDbContext db,
        IPasswordHasher hasher,
        IOptions<CookieSettings> cookieOpts,
        IAuditLogger audit)
    {
        if (GetUserId(http) is not { } userId)
            return Error(401, "auth.unauthorized", "Not authenticated.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.unauthorized", "Not authenticated.");

        if (!hasher.Verify(req.Password ?? string.Empty, user.PasswordHash))
            return Error(400, "auth.invalid_password", "Your password is incorrect.");

        var orgId = user.OrganizationId;
        // Audit FIRST so the deletion event is recorded with the still-intact
        // identity (the explicit log is excluded from the scrub below).
        await audit.LogAsync(
            "user.account_deleted",
            nameof(User), user.Id,
            before: new { user.Email, user.OrganizationId },
            organizationIdOverride: orgId, userIdOverride: user.Id);

        var now = DateTime.UtcNow;
        // Scrub PII + soft-delete the user (manual DeletedAt, not Remove(), so the
        // scrubbed email/name land in the same UPDATE). The scrubbed email is
        // unique per user id, freeing the original address for re-registration.
        user.Email = $"deleted+{user.Id:N}@deleted.invalid";
        user.FullName = "Deleted account";
        user.DeletedAt = now;

        // Soft-delete the org → its query filter hides it and (transitively) the
        // tenant's data; login/me then resolve nothing for this account.
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId);
        if (org is not null) org.DeletedAt = now;

        await db.SaveChangesAsync();

        ClearAuthCookies(http, cookieOpts.Value);
        return Results.Ok(new { data = new { message = "Your account has been deleted." }, error = (object?)null });
    }

    /// <summary>
    /// Authenticated (#183). Returns the account's data as a downloadable JSON file
    /// (GDPR/CCPA data portability): the user, organization, vendors, documents
    /// (metadata), and reminders.
    /// </summary>
    private static async Task<IResult> ExportAccount(
        HttpContext http,
        SystemDbContext db)
    {
        if (GetUserId(http) is not { } userId)
            return Error(401, "auth.unauthorized", "Not authenticated.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Error(401, "auth.unauthorized", "Not authenticated.");

        var orgId = user.OrganizationId;
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId);
        var vendors = await db.Vendors.AsNoTracking()
            .Where(v => v.OrganizationId == orgId)
            .Select(v => new { v.Name, v.ContactEmail, v.ContactPhone, v.Category, v.CreatedAt })
            .ToListAsync();
        var documents = await db.Documents.AsNoTracking()
            .Where(d => d.OrganizationId == orgId)
            .Select(d => new { d.OriginalFileName, d.DocumentType, d.ExpirationDate, d.ComplianceStatus, d.CreatedAt })
            .ToListAsync();
        var reminders = await db.Reminders.AsNoTracking()
            .Where(r => r.OrganizationId == orgId)
            .Select(r => new { r.DaysBefore, r.NotifyInternalUser, r.NotifyVendor, r.IsActive })
            .ToListAsync();

        var export = new
        {
            exportedAt = DateTime.UtcNow,
            account = new { user.Email, user.FullName, user.Role, EmailVerified = user.EmailVerifiedAt is not null, user.CreatedAt },
            organization = org is null ? null : new { org.Name, org.Industry, org.CompanySize, org.TimeZone, org.CreatedAt },
            vendors,
            documents,
            reminders,
        };

        // camelCase to match the rest of the API's JSON convention.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        return Results.File(bytes, "application/json", $"complidrop-account-export-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    private static Guid? GetUserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static void ClearAuthCookies(HttpContext http, CookieSettings cookieCfg)
    {
        http.Response.Cookies.Append(
            CookieAuthSetup.SessionCookie, string.Empty,
            CookieAuthSetup.BuildExpiredSessionCookieOptions(cookieCfg));
        http.Response.Cookies.Append(
            CookieAuthSetup.RefreshCookie, string.Empty,
            CookieAuthSetup.BuildExpiredRefreshCookieOptions(cookieCfg));
        http.Response.Cookies.Append(
            CookieAuthSetup.HintCookie, string.Empty,
            CookieAuthSetup.BuildExpiredHintCookieOptions(cookieCfg));
    }

    // Password-reset tokens are short-lived: they grant the ability to set a new
    // password (far more sensitive than email verification), so a 45-minute TTL
    // bounds the window in which a leaked link is usable.
    private const int PasswordResetTokenTtlMinutes = 45;

    private static async Task SendPasswordResetEmailAsync(
        IEmailService email,
        FrontendSettings frontend,
        string toEmail,
        string rawToken,
        ILogger logger,
        CancellationToken ct)
    {
        var link = $"{frontend.BaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(rawToken)}";
        const string subject = "Reset your CompliDrop password";
        var body = $"""
            <div style="font-family: system-ui, sans-serif; color: #0c4a6e;">
              <h2 style="color: #0284c7;">Reset your password</h2>
              <p>We received a request to reset your CompliDrop password. Click below to choose a new one:</p>
              <p><a href="{link}" style="display:inline-block;background:#0284c7;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;">Reset my password</a></p>
              <p style="color: #64748b; font-size: 12px;">Or paste this link into your browser:<br>{link}</p>
              <p style="color: #64748b; font-size: 12px;">This link expires in {PasswordResetTokenTtlMinutes} minutes. If you didn't request this, you can ignore this email — your password won't change.</p>
            </div>
            """;
        try
        {
            await email.SendAsync(toEmail, subject, body, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Password-reset email send failed for {Email}; the user can request another link.", toEmail);
        }
    }

    /// <summary>
    /// Builds the lockout message (#183) as a RELATIVE duration + the reset path.
    /// Deliberately NOT org-local wall-clock: the login 423 is reachable by an
    /// unauthenticated caller (before any password check), so rendering the org's
    /// IANA time zone here would leak org-internal config to an anonymous party.
    /// A relative "about N more minutes" conveys the unlock time, leaks nothing,
    /// and needs no DB read.
    /// </summary>
    private static string BuildLockoutMessage(DateTime lockedUntilUtc)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntilUtc - DateTime.UtcNow).TotalMinutes));
        var unit = minutes == 1 ? "minute" : "minutes";
        return $"Too many sign-in attempts — your account is locked for about {minutes} more {unit}. Reset your password to regain access now.";
    }

    // Email verification links stay valid for a week — users routinely confirm
    // late, and (unlike a password reset) the token grants no account access, so
    // a longer TTL trades little risk for far fewer "link expired" dead-ends.
    private const int VerificationTokenTtlDays = 7;

    private static (EmailVerificationToken Token, string RawToken) CreateVerificationToken(
        Guid userId, DateTime now, string? newEmail = null)
    {
        var (raw, hash) = SecureToken.Generate();
        var token = new EmailVerificationToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = hash,
            NewEmail = newEmail,
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

    // Register normalizes a best-effort optional zone to a default; UpdateOrganization
    // (#185) REJECTS an invalid zone instead. Both share the single validity check
    // (IsValidTimeZone) so the two policies can never drift on what "valid" means.
    private static string NormalizeTimeZone(string? tz) =>
        !string.IsNullOrWhiteSpace(tz) && IsValidTimeZone(tz) ? tz : "America/New_York";

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
