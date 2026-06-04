using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the forgot/reset-password flow + the lockout-with-unlock-time
/// message (#183). Careful-review: no user enumeration, single-use + expiring tokens, lockout
/// cleared on reset.
/// </summary>
public sealed class PasswordResetTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeEmailService Email =>
        (FakeEmailService)Fixture.Factory.Services.GetRequiredService<IEmailService>();

    private static string ExtractResetToken(string htmlBody)
    {
        var m = Regex.Match(htmlBody, @"reset-password\?token=([A-Za-z0-9\-_]+)");
        m.Success.Should().BeTrue("the reset email must contain a /reset-password?token= link");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// forgot-password does ALL its work (user lookup, token write, audit, send)
    /// on a DETACHED background scope so response time can't leak account existence
    /// (#183 + #180 re-review). The captured send therefore lands after the 200 —
    /// poll for it. Deterministic (the fake email completes synchronously); the
    /// generous bound covers the background scope + DB round-trips on CI.
    /// </summary>
    private async Task WaitForSendsAsync(int count)
    {
        for (var i = 0; i < 500 && Email.Sends.Count < count; i++)
            await Task.Delay(20);
        Email.Sends.Count.Should().BeGreaterThanOrEqualTo(count, "the reset email(s) should have been sent");
    }

    [Fact]
    public async Task Forgot_password_for_a_real_user_sends_a_reset_email_and_returns_200()
    {
        var auth = await RegisterAndLoginAsync();
        Email.Reset(); // drop the signup verification email

        var resp = await CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = auth.Email });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitForSendsAsync(1);
        Email.Sends.Should().ContainSingle()
            .Which.ToEmail.Should().Be(auth.Email);
        Email.Sends.Single().HtmlBody.Should().Contain("/reset-password?token=");
    }

    [Fact]
    public async Task Forgot_password_for_an_unknown_email_returns_200_and_sends_nothing_no_enumeration()
    {
        var resp = await CreateClient().PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = $"nobody-{Guid.NewGuid():N}@x.com" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        Email.Sends.Should().BeEmpty("an unknown email must not reveal its absence by behaving differently");
    }

    [Fact]
    public async Task Reset_password_with_a_valid_token_changes_the_password()
    {
        var auth = await RegisterAndLoginAsync();
        Email.Reset();
        (await CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = auth.Email })).EnsureSuccessStatusCode();
        await WaitForSendsAsync(1);
        var token = ExtractResetToken(Email.Sends.Single().HtmlBody);

        var resp = await CreateClient().PostAsJsonAsync(
            "/api/auth/reset-password",
            new { token, newPassword = "BrandNewPass123" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // Old password no longer works; new one does.
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "BrandNewPass123" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_password_is_single_use_and_invalidates_sibling_tokens()
    {
        var auth = await RegisterAndLoginAsync();
        Email.Reset();
        // Two reset requests → the FIRST token is invalidated by the second.
        (await CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = auth.Email })).EnsureSuccessStatusCode();
        await WaitForSendsAsync(1);
        var firstToken = ExtractResetToken(Email.Sends.Last().HtmlBody);
        (await CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = auth.Email })).EnsureSuccessStatusCode();
        await WaitForSendsAsync(2);
        var secondToken = ExtractResetToken(Email.Sends.Last().HtmlBody);

        // The superseded first token is dead.
        (await CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token = firstToken, newPassword = "FirstTokenPass9" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The newest token works once…
        (await CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token = secondToken, newPassword = "SecondTokenPass9" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // …and not twice.
        (await CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token = secondToken, newPassword = "ThirdTokenPass99" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reset_password_rejects_a_weak_password()
    {
        var auth = await RegisterAndLoginAsync();
        Email.Reset();
        (await CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = auth.Email })).EnsureSuccessStatusCode();
        await WaitForSendsAsync(1);
        var token = ExtractResetToken(Email.Sends.Single().HtmlBody);

        var resp = await CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "short" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("validation.password");
    }

    [Fact]
    public async Task Reset_password_with_an_unknown_token_is_rejected()
    {
        (await CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token = "nope", newPassword = "ValidPass12345" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reset_password_with_an_expired_token_is_rejected_and_leaves_the_password_unchanged()
    {
        var auth = await RegisterAndLoginAsync();

        // Plant an expired reset token (the raw never leaves this test).
        var (raw, hash) = CompliDrop.Api.Auth.SecureToken.Generate();
        await using (var db = CreateSystemDb())
        {
            db.PasswordResetTokens.Add(new CompliDrop.Api.Entities.PasswordResetToken
            {
                Id = Guid.NewGuid(),
                UserId = auth.UserId,
                TokenHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt = DateTime.UtcNow.AddHours(-1),
            });
            await db.SaveChangesAsync();
        }

        var resp = await CreateClient().PostAsJsonAsync(
            "/api/auth/reset-password",
            new { token = raw, newPassword = "ShouldNotApply9" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("auth.reset_invalid");
        // The expired link must NOT have changed the password.
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resetting_password_clears_an_active_lockout()
    {
        // Lock the account (10 wrong-password attempts), then prove a reset unlocks it.
        var auth = await RegisterAndLoginAsync();
        for (var i = 0; i < 10; i++)
            await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "wrong-password-x" });

        // Confirm it's locked even with the CORRECT password.
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.Locked);

        Email.Reset();
        (await CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = auth.Email })).EnsureSuccessStatusCode();
        await WaitForSendsAsync(1);
        var token = ExtractResetToken(Email.Sends.Single().HtmlBody);
        (await CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "RecoveredPass123" }))
            .EnsureSuccessStatusCode();

        // The lockout is cleared — the new password signs in immediately.
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "RecoveredPass123" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lockout_response_shows_an_unlock_time()
    {
        var auth = await RegisterAndLoginAsync();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 10; i++)
            last = await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "wrong-password-x" });

        // The 10th attempt crosses the threshold and returns the lockout message.
        last!.StatusCode.Should().Be(HttpStatusCode.Locked);
        var body = await last.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("auth.locked");
        var message = body.GetProperty("error").GetProperty("message").GetString()!;
        // Relative duration ("about N more minutes") — conveys when access returns
        // WITHOUT leaking the org's time zone to an unauthenticated caller (#180 re-review).
        message.Should().MatchRegex(@"locked for about \d+ more minute", "the lockout message must show when access returns (#183)");
        message.Should().Contain("Reset your password", "the lockout message must point to the recovery path");
        // Must NOT leak org-internal config (an IANA zone like 'America/...') pre-auth.
        message.Should().NotContain("/", "the unauthenticated lockout message must not embed an IANA time zone");
    }
}
