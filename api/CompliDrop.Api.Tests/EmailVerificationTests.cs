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
}
