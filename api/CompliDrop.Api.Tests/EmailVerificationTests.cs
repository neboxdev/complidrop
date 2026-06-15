using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for email verification (#184): register sends a tokenized link, the verify
/// endpoint redeems it (with idempotency + expiry + supersede semantics), resend issues a fresh
/// link and invalidates the old, and /me surfaces the verified flag.
/// </summary>
public sealed class EmailVerificationTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeEmailService Email =>
        (FakeEmailService)Fixture.Factory.Services.GetRequiredService<IEmailService>();

    /// <summary>Pulls the raw verification token out of a captured email's link. The raw token is
    /// base64url (all unreserved chars) so it survives Uri.EscapeDataString unchanged.</summary>
    private static string ExtractToken(string htmlBody)
    {
        var m = Regex.Match(htmlBody, @"token=([A-Za-z0-9\-_]+)");
        m.Success.Should().BeTrue("the verification email must contain a ?token= link");
        return m.Groups[1].Value;
    }

    private async Task<bool> MeEmailVerifiedAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("emailVerified").GetBoolean();
    }

    [Fact]
    public async Task Register_sends_a_verification_email_and_user_starts_unverified()
    {
        var auth = await RegisterAndLoginAsync();

        Email.Sends.Should().ContainSingle()
            .Which.ToEmail.Should().Be(auth.Email);
        Email.Sends.Single().Subject.Should().Contain("Confirm");
        Email.Sends.Single().HtmlBody.Should().Contain("/verify-email?token=");

        (await MeEmailVerifiedAsync(auth.Client)).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_email_with_a_valid_token_marks_the_user_verified()
    {
        var auth = await RegisterAndLoginAsync();
        var token = ExtractToken(Email.Sends.Single().HtmlBody);

        var resp = await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MeEmailVerifiedAsync(auth.Client)).Should().BeTrue();

        // The token row is now consumed.
        await using var db = CreateSystemDb();
        var stored = await db.EmailVerificationTokens.SingleAsync(t => t.UserId == auth.UserId);
        stored.ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Verify_email_with_an_unknown_token_is_rejected()
    {
        await RegisterAndLoginAsync(); // a real user exists, but we present a bogus token
        var client = CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = "not-a-real-token" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("auth.verification_invalid");
    }

    [Fact]
    public async Task Verify_email_with_an_empty_token_is_rejected()
    {
        var resp = await CreateClient().PostAsJsonAsync("/api/auth/verify-email", new { token = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("validation.token");
    }

    [Fact]
    public async Task Verify_email_with_an_expired_token_is_rejected()
    {
        var auth = await RegisterAndLoginAsync();

        // Plant an expired token for this user (the raw never leaves this test).
        var (raw, hash) = SecureToken.Generate();
        await using (var db = CreateSystemDb())
        {
            db.EmailVerificationTokens.Add(new EmailVerificationToken
            {
                Id = Guid.NewGuid(),
                UserId = auth.UserId,
                TokenHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt = DateTime.UtcNow.AddDays(-8),
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token = raw });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("auth.verification_expired");
        (await MeEmailVerifiedAsync(auth.Client)).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_email_is_idempotent_on_a_second_click()
    {
        var auth = await RegisterAndLoginAsync();
        var token = ExtractToken(Email.Sends.Single().HtmlBody);

        (await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Second click on the same (now-redeemed) link → idempotent 200, still verified.
        var second = await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MeEmailVerifiedAsync(auth.Client)).Should().BeTrue();
    }

    [Fact]
    public async Task Resend_sends_a_fresh_link_and_invalidates_the_original_token()
    {
        var auth = await RegisterAndLoginAsync();
        var originalToken = ExtractToken(Email.Sends.Single().HtmlBody);

        var resend = await auth.Client.PostAsync("/api/auth/resend-verification", null);
        resend.StatusCode.Should().Be(HttpStatusCode.OK);

        // A second email went out…
        Email.Sends.Should().HaveCount(2);
        var newToken = ExtractToken(Email.Sends.Last().HtmlBody);
        newToken.Should().NotBe(originalToken);

        // …and the ORIGINAL link is now dead (superseded, not "already confirmed").
        var stale = await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token = originalToken });
        stale.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await stale.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("auth.verification_invalid");
        (await MeEmailVerifiedAsync(auth.Client)).Should().BeFalse();

        // The NEW link still works.
        (await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token = newToken }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await MeEmailVerifiedAsync(auth.Client)).Should().BeTrue();
    }

    [Fact]
    public async Task Resend_when_already_verified_is_a_noop_and_sends_no_email()
    {
        var auth = await RegisterAndLoginAsync();
        var token = ExtractToken(Email.Sends.Single().HtmlBody);
        (await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var sendsBefore = Email.Sends.Count;
        var resend = await auth.Client.PostAsync("/api/auth/resend-verification", null);

        resend.StatusCode.Should().Be(HttpStatusCode.OK);
        Email.Sends.Count.Should().Be(sendsBefore, "an already-verified user should not trigger another email");
    }

    [Fact]
    public async Task Resend_requires_authentication()
    {
        (await CreateClient().PostAsync("/api/auth/resend-verification", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resend_with_email_unconfigured_returns_an_honest_error_not_a_false_success()
    {
        // #249: with the email subsystem off, resend must NOT claim success the user never receives.
        var auth = await RegisterAndLoginAsync();
        var sendsAfterRegister = Email.Sends.Count;
        Email.IsEnabled = false;

        var resend = await auth.Client.PostAsync("/api/auth/resend-verification", null);

        resend.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resend.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("email.not_configured");
        body.GetProperty("error").GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        Email.Sends.Count.Should().Be(sendsAfterRegister, "nothing should be sent (or claimed) when email is unconfigured");
    }

    [Fact]
    public async Task Resend_when_the_provider_rejects_the_send_returns_an_error_not_a_false_success()
    {
        // #249: a non-accepted send (Resend returns no message id) must surface, not toast "sent".
        var auth = await RegisterAndLoginAsync();
        Email.NextSendReturnsNull = true;

        var resend = await auth.Client.PostAsync("/api/auth/resend-verification", null);

        resend.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await resend.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("email.send_failed");
    }

    [Fact]
    public async Task Resend_when_the_send_throws_a_transport_error_returns_502_not_500()
    {
        // #249: the OTHER path to 502 — SendVerificationEmailAsync's catch-when(!ct.IsCancellationRequested)
        // swallowing an HttpClient transport throw (DNS/socket/TLS). The null-return branch is pinned
        // above; this pins the catch gate, the subtler half — a wrong filter would leak a 500 or, worse,
        // let a thrown send fall through and falsely report success.
        var auth = await RegisterAndLoginAsync();
        var sendsAfterRegister = Email.Sends.Count;
        Email.NextSendThrows = new HttpRequestException("simulated transport failure");

        var resend = await auth.Client.PostAsync("/api/auth/resend-verification", null);

        resend.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await resend.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("email.send_failed");
        Email.Sends.Count.Should().Be(sendsAfterRegister, "a thrown send records nothing");
    }

    [Fact]
    public async Task Resend_when_the_send_times_out_returns_502_not_500()
    {
        // #249: an HttpClient timeout surfaces as TaskCanceledException tied to the client's OWN token,
        // NOT our request ct. The catch gate (!ct.IsCancellationRequested) must still treat it as a send
        // failure (502), not mistake it for a caller abort and let it escape as an unhandled 500.
        var auth = await RegisterAndLoginAsync();
        Email.NextSendThrows = new TaskCanceledException("simulated 30s Resend timeout");

        var resend = await auth.Client.PostAsync("/api/auth/resend-verification", null);

        resend.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await resend.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("email.send_failed");
    }

    [Fact]
    public async Task ChangeEmail_with_email_unconfigured_returns_503_and_mints_no_token()
    {
        // #302: change-email must not claim "we sent the link to {newEmail}" when email is off — the
        // link goes to an address with no Resend affordance, so a false success strands the user. Fail
        // honestly BEFORE minting the change token (nothing claimed), mirroring resend-verification.
        var auth = await RegisterAndLoginAsync();
        Email.IsEnabled = false;

        var resp = await auth.Client.PostAsJsonAsync("/api/auth/change-email",
            new { newEmail = $"new-{Guid.NewGuid():N}@example.com", password = "Password1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("email.not_configured");

        await using var db = CreateSystemDb();
        (await db.EmailVerificationTokens.AnyAsync(t => t.UserId == auth.UserId && t.NewEmail != null))
            .Should().BeFalse("no email-change token should be minted when we can't send the link");
    }

    [Fact]
    public async Task ChangeEmail_when_the_provider_rejects_the_send_returns_502()
    {
        // #302: a non-accepted send (Resend returns no message id) must surface, not toast "sent".
        var auth = await RegisterAndLoginAsync();
        Email.NextSendReturnsNull = true;

        var resp = await auth.Client.PostAsJsonAsync("/api/auth/change-email",
            new { newEmail = $"new-{Guid.NewGuid():N}@example.com", password = "Password1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("email.send_failed");
    }

    [Fact]
    public async Task Register_succeeds_and_persists_a_token_even_when_email_delivery_is_disabled()
    {
        // Signup must NEVER block on email delivery: with Resend disabled the send
        // is a no-op, but the account is still created (logged in, unverified) and
        // the durable token row survives so a later resend can deliver a link.
        Email.IsEnabled = false;

        var auth = await RegisterAndLoginAsync();

        (await MeEmailVerifiedAsync(auth.Client)).Should().BeFalse();
        Email.Sends.Should().BeEmpty("delivery was disabled, yet registration must still succeed");
        await using (var db = CreateSystemDb())
        {
            (await db.EmailVerificationTokens.CountAsync(t => t.UserId == auth.UserId))
                .Should().Be(1, "the verification token must persist so a resend can deliver later");
        }

        // Re-enable delivery → resend issues a working link.
        Email.IsEnabled = true;
        (await auth.Client.PostAsync("/api/auth/resend-verification", null)).EnsureSuccessStatusCode();
        Email.Sends.Should().ContainSingle();
        var token = ExtractToken(Email.Sends.Single().HtmlBody);
        (await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await MeEmailVerifiedAsync(auth.Client)).Should().BeTrue();
    }

    [Fact]
    public async Task Verify_with_a_still_open_token_after_the_user_is_already_verified_is_idempotent()
    {
        // Pins the user-keyed idempotency branch (NOT ConsumedAt-keyed): a token
        // that is still open but whose user is already verified returns 200, not
        // verification_invalid. A refactor that keyed idempotency on ConsumedAt
        // would break this.
        var auth = await RegisterAndLoginAsync();
        var firstToken = ExtractToken(Email.Sends.Single().HtmlBody);

        // Plant a SECOND independent, still-open token for the same user.
        var (secondRaw, secondHash) = SecureToken.Generate();
        await using (var db = CreateSystemDb())
        {
            db.EmailVerificationTokens.Add(new EmailVerificationToken
            {
                Id = Guid.NewGuid(),
                UserId = auth.UserId,
                TokenHash = secondHash,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Verify via the FIRST token → user is now verified.
        (await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token = firstToken }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await MeEmailVerifiedAsync(auth.Client)).Should().BeTrue();

        // Present the SECOND (still-open, never-consumed) token → idempotent 200.
        (await auth.Client.PostAsJsonAsync("/api/auth/verify-email", new { token = secondRaw }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
