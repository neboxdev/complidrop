using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the authenticated reminder settings endpoints
/// (<see cref="CompliDrop.Api.Endpoints.ReminderEndpoints"/>). Registration seeds 4 default
/// reminders (60/30/14/7 days), so each test operates on those rows.
/// </summary>
public sealed class ReminderEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    [Fact]
    public async Task List_reminders_does_not_expose_emailBodyTemplate()
    {
        // #264 / FP-095: EmailBodyTemplate was stored and round-tripped but never read by the
        // send path (BuildBody) — a stored-but-ignored lie on the API surface. The field is
        // off the contract until #241 decides whether reminder emails honor it.
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/reminders");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await Data(resp);
        data.GetArrayLength().Should().Be(4, "registration seeds 60/30/14/7-day reminders");
        foreach (var row in data.EnumerateArray())
        {
            row.TryGetProperty("emailBodyTemplate", out _).Should().BeFalse(
                "the dead field must not round-trip to clients");
            row.TryGetProperty("emailSubjectTemplate", out _).Should().BeTrue(
                "the subject template IS honored by the send path and stays on the surface");
        }
    }

    [Fact]
    public async Task Update_reminder_persists_toggles_and_leaves_stored_body_template_untouched()
    {
        var auth = await RegisterAndLoginAsync();

        // Seed a body template directly in the DB (no API accepts it anymore) to prove the
        // PUT no longer clobbers the dormant column.
        Guid reminderId;
        await using (var db = CreateSystemDb())
        {
            var reminder = await db.Reminders.FirstAsync(r => r.OrganizationId == auth.OrgId && r.DaysBefore == 30);
            reminder.EmailBodyTemplate = "<p>legacy template</p>";
            await db.SaveChangesAsync();
            reminderId = reminder.Id;
        }

        var resp = await auth.Client.PutAsJsonAsync($"/api/reminders/{reminderId}", new
        {
            notifyInternalUser = false,
            notifyVendor = true,
            isActive = false,
            emailSubjectTemplate = "Custom subject",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var db = CreateSystemDb())
        {
            var r = await db.Reminders.SingleAsync(r => r.Id == reminderId);
            r.NotifyInternalUser.Should().BeFalse();
            r.NotifyVendor.Should().BeTrue();
            r.IsActive.Should().BeFalse();
            r.EmailSubjectTemplate.Should().Be("Custom subject");
            r.EmailBodyTemplate.Should().Be("<p>legacy template</p>",
                "the dormant column must survive PUTs that no longer carry the field");
        }
    }

    [Fact]
    public async Task Update_reminder_is_tenant_scoped()
    {
        var orgA = await RegisterAndLoginAsync();
        var orgB = await RegisterAndLoginAsync();

        Guid orgAReminderId;
        await using (var db = CreateSystemDb())
            orgAReminderId = (await db.Reminders.FirstAsync(r => r.OrganizationId == orgA.OrgId)).Id;

        var resp = await orgB.Client.PutAsJsonAsync($"/api/reminders/{orgAReminderId}", new
        {
            notifyInternalUser = false,
            notifyVendor = false,
            isActive = false,
            emailSubjectTemplate = (string?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "org B cannot see org A's reminders through the tenant filter");
        await using (var db2 = CreateSystemDb())
            (await db2.Reminders.SingleAsync(r => r.Id == orgAReminderId)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task History_returns_only_callers_logs_ordered_most_recent_first()
    {
        // #309: history scopes by ReminderLog's denormalized OrganizationId (via the AppDbContext
        // tenant filter) and orders SentAt DESC. The other org's row carries the LATEST SentAt, so
        // a broken filter would surface it FIRST — making this sensitive to both the scoping and
        // the ordering. Replaces the pre-#309 EXISTS-join scoping; the cross-tenant guarantee is
        // also pinned by TenantIsolationTests.Reminder_history_returns_only_the_callers_org_logs.
        var a = await RegisterAndLoginAsync();
        var other = await RegisterAndLoginAsync();

        var baseTime = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = CreateSystemDb())
        {
            var aReminder = await db.Reminders.FirstAsync(r => r.OrganizationId == a.OrgId);
            var otherReminder = await db.Reminders.FirstAsync(r => r.OrganizationId == other.OrgId);
            db.ReminderLogs.AddRange(
                HistoryLog(a.OrgId, aReminder.Id, "oldest@a.example", baseTime),
                HistoryLog(a.OrgId, aReminder.Id, "middle@a.example", baseTime.AddHours(1)),
                HistoryLog(a.OrgId, aReminder.Id, "newest@a.example", baseTime.AddHours(2)),
                // Other org's row is the most recent of all — it must NOT leak in despite sorting first.
                HistoryLog(other.OrgId, otherReminder.Id, "secret@other.example", baseTime.AddHours(3)));
            await db.SaveChangesAsync();
        }

        var resp = await a.Client.GetAsync("/api/reminders/history");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipients = (await Data(resp)).EnumerateArray()
            .Select(r => r.GetProperty("recipient").GetString())
            .ToArray();
        recipients.Should().Equal("newest@a.example", "middle@a.example", "oldest@a.example");
        recipients.Should().NotContain("secret@other.example", "org A must never see another org's reminder logs");
    }

    [Fact]
    public async Task History_names_the_document_vendor_and_rung_and_nulls_them_when_removed()
    {
        // FP-090: history must name WHICH document/vendor/rung — the correlated subqueries resolve
        // those from the related rows, and fall back to null when the document is gone (soft-deleted).
        var auth = await RegisterAndLoginAsync();
        var vendorId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        Guid reminderId;
        int daysBefore;
        await using (var db = CreateSystemDb())
        {
            var reminder = await db.Reminders.FirstAsync(r => r.OrganizationId == auth.OrgId);
            reminderId = reminder.Id;
            daysBefore = reminder.DaysBefore;
            db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = auth.OrgId, Name = "Acme Catering", CreatedAt = now, UpdatedAt = now });
            db.Documents.Add(new Document
            {
                Id = docId, OrganizationId = auth.OrgId, VendorId = vendorId,
                OriginalFileName = "acme-coi.pdf", BlobStorageUrl = "blob://x", FileSizeBytes = 1,
                ContentType = "application/pdf", CreatedAt = now, UpdatedAt = now,
            });
            db.ReminderLogs.Add(new ReminderLog
            {
                Id = Guid.NewGuid(), OrganizationId = auth.OrgId, ReminderId = reminderId, DocumentId = docId,
                RecipientEmail = "ops@acme.test", SentAt = now, SendDate = DateOnly.FromDateTime(now),
                Status = ReminderLogStatus.Sent,
            });
            await db.SaveChangesAsync();
        }

        var row = (await Data(await auth.Client.GetAsync("/api/reminders/history"))).EnumerateArray().First();
        row.GetProperty("documentName").GetString().Should().Be("acme-coi.pdf");
        row.GetProperty("vendorName").GetString().Should().Be("Acme Catering");
        row.GetProperty("daysBefore").GetInt32().Should().Be(daysBefore);

        // Soft-delete the document → the name fields fall back to null, but the log row remains.
        await using (var db = CreateSystemDb())
        {
            var doc = await db.Documents.SingleAsync(d => d.Id == docId);
            doc.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var afterDelete = (await Data(await auth.Client.GetAsync("/api/reminders/history"))).EnumerateArray().First();
        afterDelete.GetProperty("documentName").ValueKind.Should().Be(JsonValueKind.Null);
        afterDelete.GetProperty("vendorName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static ReminderLog HistoryLog(Guid orgId, Guid reminderId, string recipient, DateTime sentAt) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = orgId,
        ReminderId = reminderId,
        DocumentId = Guid.NewGuid(),
        RecipientEmail = recipient,
        SentAt = sentAt,
        SendDate = DateOnly.FromDateTime(sentAt),
        Status = ReminderLogStatus.Sent,
    };
}
