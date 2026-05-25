using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the Resend inbound (delivery-status) webhook: Svix signature
/// verification (valid → accepted + status updated; missing / forged / replayed → 401 with no
/// state change) and the Development "no secret configured" escape hatch. Runs on the
/// Testcontainers harness.
/// </summary>
public sealed class ResendWebhookTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // The base64 body MUST match CustomWebApplicationFactory's Resend:WebhookSecret (after "whsec_").
    private const string SecretBase64 = "Y29tcGxpZHJvcC1yZXNlbmQtd2ViaG9vay10ZXN0LXNlY3JldC0wMTIzNDU2Nzg5";

    private const string WebhookPath = "/api/reminders/resend-webhook";

    private async Task<string> SeedReminderLogAsync(string status = "sent")
    {
        var orgId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        var messageId = $"resend_{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;

        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
        db.Reminders.Add(new Reminder { Id = reminderId, OrganizationId = orgId, DaysBefore = 30 });
        db.ReminderLogs.Add(new ReminderLog
        {
            Id = Guid.NewGuid(),
            ReminderId = reminderId,
            DocumentId = Guid.NewGuid(), // no FK constraint on DocumentId
            RecipientEmail = "vendor@example.com",
            SentAt = now,
            SendDate = DateOnly.FromDateTime(now),
            ResendMessageId = messageId,
            Status = status
        });
        await db.SaveChangesAsync();
        return messageId;
    }

    private static string EventPayload(string type, string emailId) => JsonSerializer.Serialize(new
    {
        type,
        data = new { email_id = emailId }
    });

    private static string DeliveredPayload(string messageId) => EventPayload("email.delivered", messageId);

    /// <summary>Builds the three Svix headers for a payload, signed with the harness secret.</summary>
    private static (string id, string timestamp, string signature) Sign(string payload, DateTimeOffset? when = null)
    {
        var id = $"msg_{Guid.NewGuid():N}";
        var ts = (when ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Convert.FromBase64String(SecretBase64));
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{ts}.{payload}")));
        return (id, ts, $"v1,{sig}");
    }

    private static HttpRequestMessage BuildRequest(
        string payload, (string id, string timestamp, string signature)? svix)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (svix is { } s)
        {
            req.Headers.TryAddWithoutValidation("svix-id", s.id);
            req.Headers.TryAddWithoutValidation("svix-timestamp", s.timestamp);
            req.Headers.TryAddWithoutValidation("svix-signature", s.signature);
        }
        return req;
    }

    private Task<HttpResponseMessage> PostWebhook(
        string payload, (string id, string timestamp, string signature)? svix) =>
        CreateClient().SendAsync(BuildRequest(payload, svix));

    private async Task<string> StatusOf(string messageId)
    {
        await using var db = CreateSystemDb();
        return (await db.ReminderLogs.FirstAsync(l => l.ResendMessageId == messageId)).Status;
    }

    [Fact]
    public async Task Valid_signature_is_accepted_and_updates_reminder_log_status()
    {
        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = DeliveredPayload(messageId);

        var resp = await PostWebhook(payload, Sign(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(messageId)).Should().Be("delivered");
    }

    [Fact]
    public async Task Missing_signature_is_rejected_401_with_no_state_change()
    {
        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = DeliveredPayload(messageId);

        var resp = await PostWebhook(payload, svix: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusOf(messageId)).Should().Be("sent");
    }

    [Fact]
    public async Task Forged_signature_is_rejected_401_with_no_state_change()
    {
        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = DeliveredPayload(messageId);

        // A correctly-formatted but wrong v1 signature (32 zero bytes) with a CURRENT timestamp —
        // isolates hash-mismatch rejection from the timestamp-tolerance window.
        var (id, ts, _) = Sign(payload);
        var bogus = "v1," + Convert.ToBase64String(new byte[32]);

        var resp = await PostWebhook(payload, (id, ts, bogus));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusOf(messageId)).Should().Be("sent");
    }

    [Fact]
    public async Task Stale_timestamp_is_rejected_401_replay_protection()
    {
        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = DeliveredPayload(messageId);

        // Validly signed, but the timestamp is 10 minutes old (beyond the 5-minute tolerance).
        var resp = await PostWebhook(payload, Sign(payload, DateTimeOffset.UtcNow.AddMinutes(-10)));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusOf(messageId)).Should().Be("sent");
    }

    [Theory]
    [InlineData("email.bounced", "bounced")]
    [InlineData("email.complained", "complained")]
    [InlineData("email.opened", "opened")]
    [InlineData("email.clicked", "clicked")]
    public async Task Valid_signature_maps_each_event_type_to_its_status(string eventType, string expectedStatus)
    {
        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = EventPayload(eventType, messageId);

        var resp = await PostWebhook(payload, Sign(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(messageId)).Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Duplicate_valid_delivery_is_idempotent()
    {
        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = DeliveredPayload(messageId);
        var svix = Sign(payload); // same id/timestamp/signature reused on both deliveries

        (await PostWebhook(payload, svix)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostWebhook(payload, svix)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusOf(messageId)).Should().Be("delivered");
    }

    [Fact]
    public async Task Signed_but_unparseable_body_returns_400()
    {
        var messageId = await SeedReminderLogAsync(status: "sent");
        const string body = "this is not json"; // signed verbatim so verification passes

        var resp = await PostWebhook(body, Sign(body));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOf(messageId)).Should().Be("sent");
    }

    [Fact]
    public async Task Valid_signature_for_unknown_message_id_is_a_noop()
    {
        var seededId = await SeedReminderLogAsync(status: "sent");
        // A correctly-signed event for an email_id that matches no ReminderLog.
        var payload = DeliveredPayload($"resend_{Guid.NewGuid():N}");

        var resp = await PostWebhook(payload, Sign(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);          // accepted (signature valid)
        (await StatusOf(seededId)).Should().Be("sent");          // but nothing was mutated
    }

    [Fact]
    public async Task When_secret_unset_in_Development_unsigned_request_is_allowed()
    {
        // Spin up a one-off host with Resend:WebhookSecret cleared. The factory runs as
        // "Development", so the signature check is skipped (the documented local escape hatch).
        await using var factory = new CustomWebApplicationFactory(
            Fixture.ConnectionString,
            new Dictionary<string, string?> { ["Resend:WebhookSecret"] = "" });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = DeliveredPayload(messageId);

        var resp = await client.SendAsync(BuildRequest(payload, svix: null));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(messageId)).Should().Be("delivered");
    }

    // -- Ordering-aware status updates (ticket #21). ----------------------------------------
    // Resend delivers via Svix, which does NOT guarantee event ordering and may redeliver older
    // events. These tests assert the lifecycle precedence rule end-to-end through the HTTP
    // handler; the pure precedence function itself is exhaustively unit-tested in
    // ReminderStatusPrecedenceTests.

    [Theory]
    [InlineData("bounced", "email.delivered", "bounced")]
    [InlineData("bounced", "email.opened", "bounced")]
    [InlineData("bounced", "email.clicked", "bounced")]
    [InlineData("complained", "email.delivered", "complained")]
    [InlineData("complained", "email.clicked", "complained")]
    public async Task Positive_event_after_negative_does_not_regress_status(
        string seeded, string positiveEventType, string expectedFinal)
    {
        var messageId = await SeedReminderLogAsync(status: seeded);
        var payload = EventPayload(positiveEventType, messageId);

        var resp = await PostWebhook(payload, Sign(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(messageId)).Should().Be(expectedFinal);
    }

    [Theory]
    [InlineData("delivered", "email.bounced", "bounced")]
    [InlineData("opened", "email.bounced", "bounced")]
    [InlineData("clicked", "email.bounced", "bounced")]
    [InlineData("delivered", "email.complained", "complained")]
    [InlineData("clicked", "email.complained", "complained")]
    public async Task Negative_event_after_positive_overrides_to_negative(
        string seeded, string negativeEventType, string expectedFinal)
    {
        var messageId = await SeedReminderLogAsync(status: seeded);
        var payload = EventPayload(negativeEventType, messageId);

        var resp = await PostWebhook(payload, Sign(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(messageId)).Should().Be(expectedFinal);
    }

    [Fact]
    public async Task Late_lower_rank_positive_does_not_regress_a_higher_rank_positive()
    {
        // Seed at clicked; a late email.delivered redelivery must NOT roll back to delivered.
        var messageId = await SeedReminderLogAsync(status: "clicked");
        var payload = DeliveredPayload(messageId);

        var resp = await PostWebhook(payload, Sign(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(messageId)).Should().Be("clicked");
    }

    [Fact]
    public async Task Sequential_positives_settle_on_highest_rank_reached()
    {
        // Send delivered → opened → clicked → late delivered → late opened. End state: clicked.
        var messageId = await SeedReminderLogAsync(status: "sent");

        foreach (var type in new[] { "email.delivered", "email.opened", "email.clicked", "email.delivered", "email.opened" })
        {
            var payload = EventPayload(type, messageId);
            (await PostWebhook(payload, Sign(payload))).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await StatusOf(messageId)).Should().Be("clicked");
    }

    [Fact]
    public async Task Failed_current_status_can_be_advanced_by_a_real_positive_event()
    {
        // "failed" logs carry no ResendMessageId in production (set by ReminderBackgroundService
        // only when Resend returns no id), but the precedence rule must still be defined for an
        // unknown current status. We seed a log with status="failed" AND a synthetic message id
        // to exercise the path where it matches.
        var messageId = await SeedReminderLogAsync(status: "failed");
        var payload = DeliveredPayload(messageId);

        var resp = await PostWebhook(payload, Sign(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(messageId)).Should().Be("delivered");
    }

    [Fact]
    public async Task Duplicate_negative_redelivery_is_idempotent()
    {
        // The existing Duplicate_valid_delivery_is_idempotent test covers positive-duplicate;
        // assert the same for a negative, which under the precedence rule is also a no-op write
        // when current == incoming.
        var messageId = await SeedReminderLogAsync(status: "sent");
        var payload = EventPayload("email.bounced", messageId);
        var svix = Sign(payload);

        (await PostWebhook(payload, svix)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostWebhook(payload, svix)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusOf(messageId)).Should().Be("bounced");
    }

    [Fact]
    public async Task Negative_after_different_negative_applies_per_rule()
    {
        // Per the ticket: "negative/terminal events always apply, regardless of current status".
        // This is the corner case (a bounced log getting a later complained event, or vice
        // versa) — pinned to document the chosen interpretation.
        var messageId = await SeedReminderLogAsync(status: "bounced");
        var payload = EventPayload("email.complained", messageId);

        (await PostWebhook(payload, Sign(payload))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusOf(messageId)).Should().Be("complained");
    }
}
