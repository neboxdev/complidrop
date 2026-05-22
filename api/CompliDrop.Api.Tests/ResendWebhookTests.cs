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

    private static string DeliveredPayload(string messageId) => JsonSerializer.Serialize(new
    {
        type = "email.delivered",
        data = new { email_id = messageId }
    });

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
}
