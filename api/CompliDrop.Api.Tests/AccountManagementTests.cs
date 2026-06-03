using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for change-password, change-email, account deletion, and data export (#183).
/// Careful-review: each mutation re-checks the password; deletion scrubs PII + revokes access.
/// </summary>
public sealed class AccountManagementTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeEmailService Email =>
        (FakeEmailService)Fixture.Factory.Services.GetRequiredService<IEmailService>();

    private static string ExtractVerifyToken(string htmlBody)
    {
        var m = Regex.Match(htmlBody, @"verify-email\?token=([A-Za-z0-9\-_]+)");
        m.Success.Should().BeTrue("the change-email confirmation must contain a /verify-email?token= link");
        return m.Groups[1].Value;
    }

    // ───────── change password ─────────

    [Fact]
    public async Task Change_password_with_the_correct_current_password_succeeds_and_takes_effect()
    {
        var auth = await RegisterAndLoginAsync();

        (await auth.Client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Password1234", newPassword = "ChangedPass5678" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "ChangedPass5678" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Change_password_with_a_wrong_current_password_is_rejected()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "not-my-password", newPassword = "ChangedPass5678" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("auth.invalid_password");
        // The original password still works — nothing changed.
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Change_password_rejects_a_weak_new_password()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Password1234", newPassword = "weak" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("validation.password");
    }

    [Fact]
    public async Task Change_password_requires_authentication()
    {
        (await CreateClient().PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "x", newPassword = "ValidPass12345" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ───────── change email ─────────

    [Fact]
    public async Task Change_email_sends_a_confirmation_to_the_new_address_and_swaps_only_after_verify()
    {
        var auth = await RegisterAndLoginAsync();
        var newEmail = $"new-{Guid.NewGuid():N}@x.com";
        Email.Reset();

        (await auth.Client.PostAsJsonAsync("/api/auth/change-email",
            new { password = "Password1234", newEmail }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The confirmation goes to the NEW address.
        Email.Sends.Should().ContainSingle().Which.ToEmail.Should().Be(newEmail);
        // Not swapped yet — the OLD email still signs in.
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Redeem the link → email swaps.
        var token = ExtractVerifyToken(Email.Sends.Single().HtmlBody);
        (await CreateClient().PostAsJsonAsync("/api/auth/verify-email", new { token }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = newEmail, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Change_email_confirmation_link_is_idempotent_on_a_second_click()
    {
        var auth = await RegisterAndLoginAsync();
        var newEmail = $"new-{Guid.NewGuid():N}@x.com";
        Email.Reset();
        (await auth.Client.PostAsJsonAsync("/api/auth/change-email",
            new { password = "Password1234", newEmail })).EnsureSuccessStatusCode();
        var token = ExtractVerifyToken(Email.Sends.Single().HtmlBody);

        // First click swaps the email…
        (await CreateClient().PostAsJsonAsync("/api/auth/verify-email", new { token }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // …second click on the SAME link is idempotent success (user.Email already
        // equals NewEmail), not an error.
        (await CreateClient().PostAsJsonAsync("/api/auth/verify-email", new { token }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = newEmail, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Change_email_with_a_wrong_password_is_rejected()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsJsonAsync("/api/auth/change-email",
            new { password = "wrong", newEmail = $"x-{Guid.NewGuid():N}@x.com" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("auth.invalid_password");
    }

    [Fact]
    public async Task Change_email_to_an_already_taken_address_is_rejected()
    {
        var taken = await RegisterAndLoginAsync();
        var mover = await RegisterAndLoginAsync();

        var resp = await mover.Client.PostAsJsonAsync("/api/auth/change-email",
            new { password = "Password1234", newEmail = taken.Email });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("auth.email_taken");
    }

    [Fact]
    public async Task Change_email_requires_authentication()
    {
        (await CreateClient().PostAsJsonAsync("/api/auth/change-email",
            new { password = "x", newEmail = "x@x.com" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ───────── delete account ─────────

    [Fact]
    public async Task Delete_account_revokes_access_and_frees_the_email_for_reregistration()
    {
        var auth = await RegisterAndLoginAsync();

        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The deleted account can no longer sign in.
        (await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.Email, password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The user row is tombstoned AND its PII scrubbed (GDPR erasure contract).
        await using (var db = CreateSystemDb())
        {
            var scrubbed = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == auth.UserId);
            scrubbed.DeletedAt.Should().NotBeNull();
            scrubbed.Email.Should().StartWith("deleted+").And.NotBe(auth.Email);
            scrubbed.FullName.Should().Be("Deleted account");
        }

        // The freed email can register anew.
        var reReg = await CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            email = auth.Email,
            password = "Password1234",
            fullName = "Returning User",
            companyName = "New Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        reReg.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_account_with_a_wrong_password_is_rejected_and_keeps_access()
    {
        var auth = await RegisterAndLoginAsync();

        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "wrong" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Still signed in / account intact.
        (await auth.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_account_requires_authentication()
    {
        (await CreateClient().PostAsJsonAsync("/api/auth/account/delete", new { password = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ───────── data export ─────────

    [Fact]
    public async Task Export_returns_a_json_file_with_the_account_data_and_no_secrets()
    {
        var auth = await RegisterAndLoginAsync();
        // Seed a vendor so the vendor projection is actually exercised.
        await using (var db = CreateSystemDb())
        {
            db.Vendors.Add(new CompliDrop.Api.Entities.Vendor
            {
                Id = Guid.NewGuid(),
                OrganizationId = auth.OrgId,
                Name = "Acme Electric",
                ContactEmail = "ops@acme-electric.test",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.GetAsync("/api/auth/account/export");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var raw = await resp.Content.ReadAsStringAsync();

        var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("account").GetProperty("email").GetString().Should().Be(auth.Email);
        doc.RootElement.TryGetProperty("organization", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("documents", out _).Should().BeTrue();
        doc.RootElement.GetProperty("vendors")[0].GetProperty("name").GetString().Should().Be("Acme Electric");

        // GDPR export must NEVER leak credentials/token material — a future
        // refactor to `account = user` (whole entity) would regress this.
        raw.Should().NotContain("PasswordHash").And.NotContain("passwordHash");
        raw.Should().NotContain("TokenHash");
        raw.Should().NotContain("$2", "no BCrypt hash prefix may appear in the export");
    }

    [Fact]
    public async Task Change_password_does_not_write_the_password_hash_into_the_audit_log()
    {
        var auth = await RegisterAndLoginAsync();

        (await auth.Client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Password1234", newPassword = "ChangedPass5678" }))
            .EnsureSuccessStatusCode();

        // The interceptor audits the User update, but PasswordHash must be redacted.
        await using var db = CreateSystemDb();
        var rows = await db.AuditLogs
            .Where(a => a.OrganizationId == auth.OrgId)
            .Select(a => (a.BeforeJson ?? "") + (a.AfterJson ?? ""))
            .ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(json => !json.Contains("$2"), "no BCrypt hash may land in AuditLog JSON");
    }

    [Fact]
    public async Task Export_requires_authentication()
    {
        (await CreateClient().GetAsync("/api/auth/account/export"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
