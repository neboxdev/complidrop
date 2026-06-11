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

    private FakeStripeService FakeStripe =>
        (FakeStripeService)Fixture.Factory.Services.GetRequiredService<IStripeService>();

    /// <summary>Flips the registered org's subscription to a live paid plan, as the
    /// checkout.session.completed webhook would have left it. Returns the Stripe sub id.</summary>
    private async Task<string> MakeSubscriptionPaidAsync(Guid orgId, string status = "active")
    {
        var subId = $"sub_{Guid.NewGuid():N}";
        await using var db = CreateSystemDb();
        var sub = await db.Subscriptions.FirstAsync(s => s.OrganizationId == orgId);
        sub.StripeCustomerId = $"cus_{Guid.NewGuid():N}";
        sub.StripeSubscriptionId = subId;
        sub.Plan = "pro";
        sub.Status = status;
        sub.DocumentLimit = null;
        sub.HasVendorPortal = true;
        await db.SaveChangesAsync();
        return subId;
    }

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
    public async Task Change_password_keeps_the_current_session_and_evicts_others()
    {
        // #202: changing your password evicts OTHER sessions but must NOT log you
        // out of the tab you're using (ChangePassword re-issues the caller's
        // cookies with the new stamp).
        var a = await RegisterAndLoginAsync();       // session A (the active tab)
        var b = await LoginAsync(a.Email);           // session B (another tab, same user)
        (await b.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await a.Client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Password1234", newPassword = "ChangedPass5678" }))
            .EnsureSuccessStatusCode();

        // A was re-issued (new stamp) → still in. B's old token (old stamp) → 401.
        (await a.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await b.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deleted_account_is_rejected_on_an_org_claim_tenant_endpoint()
    {
        // #202 / ADR 0014: per-request principal re-validation rejects a deleted
        // account's still-valid session on EVERY authed endpoint — including the
        // tenant endpoints that authorize on the org_id claim alone (which the
        // #183 soft-delete-on-user-lookup did NOT cover).
        var a = await RegisterAndLoginAsync();
        var b = await LoginAsync(a.Email);
        (await b.Client.GetAsync("/api/documents/")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await a.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .EnsureSuccessStatusCode();

        (await b.Client.GetAsync("/api/documents/")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

        // #202: change-email does NOT rotate the security stamp (the password is
        // unchanged), so the original session that initiated it stays valid.
        (await auth.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
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
    public async Task Resend_verification_does_not_cancel_a_pending_change_email()
    {
        // #180 re-review: an unverified user starts a change-email, then taps the
        // banner's one-tap Resend. Resend must invalidate only SIGNUP tokens, NOT
        // the pending change-email — otherwise the change is silently lost and the
        // new-inbox link reports "no longer valid".
        var auth = await RegisterAndLoginAsync();
        var newEmail = $"new-{Guid.NewGuid():N}@x.com";
        Email.Reset();
        (await auth.Client.PostAsJsonAsync("/api/auth/change-email", new { password = "Password1234", newEmail }))
            .EnsureSuccessStatusCode();
        var changeToken = ExtractVerifyToken(Email.Sends.Single(s => s.ToEmail == newEmail).HtmlBody);

        // Tap Resend (a fresh signup-verification to the CURRENT address).
        (await auth.Client.PostAsync("/api/auth/resend-verification", null)).EnsureSuccessStatusCode();

        // The pending change-email link still works → the swap completes.
        (await CreateClient().PostAsJsonAsync("/api/auth/verify-email", new { token = changeToken }))
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
    public async Task Delete_account_revokes_a_concurrent_live_session()
    {
        var auth = await RegisterAndLoginAsync();
        // A second, independent live session for the same user (separate cookie
        // jar) — simulates another open tab. It never sees the delete response's
        // cookie-clear, so its cd_session JWT stays present and valid for the full
        // TTL; revocation must come from the soft-delete filter on the user lookup,
        // not from cookie clearing.
        var other = await LoginAsync(auth.Email);
        (await other.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .EnsureSuccessStatusCode();

        // The concurrent session is revoked on the endpoints that resolve the user
        // (Me / account-export): the soft-deleted user lookup returns null → 401,
        // even though the JWT is still cryptographically valid. (Broader tenant
        // endpoints that authorize on the org_id claim alone are hardened
        // separately in #202's per-request principal re-validation.)
        (await other.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await other.Client.GetAsync("/api/auth/account/export")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

    // ───────── delete account × Stripe cancel (#255) ─────────

    [Fact]
    public async Task Delete_account_with_a_live_paid_subscription_cancels_it_on_stripe()
    {
        var auth = await RegisterAndLoginAsync();
        var stripeSubId = await MakeSubscriptionPaidAsync(auth.OrgId);

        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        FakeStripe.CanceledSubscriptions.Should().ContainSingle().Which.Should().Be(stripeSubId);
        await using var db = CreateSystemDb();
        (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == auth.UserId)).DeletedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("past_due")]
    [InlineData("trialing")]
    public async Task Delete_account_cancels_non_terminal_paid_statuses_too(string status)
    {
        var auth = await RegisterAndLoginAsync();
        var stripeSubId = await MakeSubscriptionPaidAsync(auth.OrgId, status);

        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        FakeStripe.CanceledSubscriptions.Should().ContainSingle().Which.Should().Be(stripeSubId);
    }

    [Fact]
    public async Task Delete_account_on_the_free_plan_makes_no_stripe_call()
    {
        var auth = await RegisterAndLoginAsync();

        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        FakeStripe.CanceledSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_account_with_an_already_canceled_subscription_skips_the_stripe_call()
    {
        var auth = await RegisterAndLoginAsync();
        await MakeSubscriptionPaidAsync(auth.OrgId, status: "canceled");

        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        FakeStripe.CanceledSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_account_aborts_with_a_friendly_error_when_the_stripe_cancel_fails()
    {
        // #255: if the cancel can't be confirmed, deletion must NOT proceed — destroying
        // the login destroys the only path to the billing portal while the card keeps
        // billing. The error is retryable and the retry completes both halves.
        var auth = await RegisterAndLoginAsync();
        var stripeSubId = await MakeSubscriptionPaidAsync(auth.OrgId);

        FakeStripe.FailNextCancelSubscriptionWith = new Stripe.StripeException("Simulated transient Stripe cancel failure.");
        var failed = await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" });

        failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await failed.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("billing.cancel_failed");
        body.GetProperty("error").GetProperty("message").GetString().Should().NotContainEquivalentOf("stripe",
            "error copy stays jargon-free for SMB users");

        // Nothing was deleted, no false audit trail, still signed in.
        (await auth.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var db = CreateSystemDb())
        {
            (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == auth.UserId)).DeletedAt.Should().BeNull();
            (await db.AuditLogs.CountAsync(a => a.Action == "user.account_deleted" && a.UserId == auth.UserId))
                .Should().Be(0, "the deletion audit event must not be recorded for an aborted deletion");
        }

        // Retry (transient failure cleared) completes the cancel AND the deletion.
        (await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        FakeStripe.CanceledSubscriptions.Should().ContainSingle().Which.Should().Be(stripeSubId);
    }

    [Fact]
    public async Task Delete_account_maps_a_stripe_sdk_timeout_to_the_retryable_502()
    {
        // The Stripe SDK surfaces its own HTTP timeout as a TaskCanceledException while the
        // request is NOT client-aborted. The catch filter must map that to the designed 502
        // billing.cancel_failed (retryable, friendly copy) — not let it escape as a 500.
        var auth = await RegisterAndLoginAsync();
        await MakeSubscriptionPaidAsync(auth.OrgId);

        FakeStripe.FailNextCancelSubscriptionWith = new TaskCanceledException("Stripe SDK request timeout.");
        var failed = await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" });

        failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await failed.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("billing.cancel_failed");
        (await auth.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_account_aborts_with_503_when_stripe_is_not_configured_but_a_live_sub_exists()
    {
        // Proceeding would recreate the exact #255 harm (card keeps billing, login gone),
        // so an unconfigured Stripe must abort the deletion with the retryable 503.
        var auth = await RegisterAndLoginAsync();
        await MakeSubscriptionPaidAsync(auth.OrgId);

        FakeStripe.IsEnabled = false;
        var resp = await auth.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("billing.unavailable");
        body.GetProperty("error").GetProperty("message").GetString().Should().NotContainEquivalentOf("stripe");

        FakeStripe.CanceledSubscriptions.Should().BeEmpty();
        (await auth.Client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == auth.UserId)).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Delete_account_never_touches_another_orgs_subscription()
    {
        // Both new subscription lookups run on SystemDbContext (no tenant filter) — the
        // manual OrganizationId predicate is the only isolation. Two-org pin: deleting
        // free-org B must not cancel paid-org A's Stripe subscription.
        var paidOrg = await RegisterAndLoginAsync();
        var paidSubId = await MakeSubscriptionPaidAsync(paidOrg.OrgId);
        var freeOrg = await RegisterAndLoginAsync();

        (await freeOrg.Client.PostAsJsonAsync("/api/auth/account/delete", new { password = "Password1234" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        FakeStripe.CanceledSubscriptions.Should().BeEmpty();
        await using var db = CreateSystemDb();
        var paidSub = await db.Subscriptions.FirstAsync(s => s.OrganizationId == paidOrg.OrgId);
        paidSub.StripeSubscriptionId.Should().Be(paidSubId);
        paidSub.Status.Should().Be("active");
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
        // Pull Before/After (jsonb→string) and concat in MEMORY — concatenating
        // jsonb columns in SQL would coerce the `?? ""` to invalid json.
        await using var db = CreateSystemDb();
        var rows = await db.AuditLogs
            .Where(a => a.OrganizationId == auth.OrgId)
            .Select(a => new { a.BeforeJson, a.AfterJson })
            .ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(
            r => !((r.BeforeJson ?? "") + (r.AfterJson ?? "")).Contains("$2"),
            "no BCrypt hash may land in AuditLog JSON");
    }

    [Fact]
    public async Task Export_requires_authentication()
    {
        (await CreateClient().GetAsync("/api/auth/account/export"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
