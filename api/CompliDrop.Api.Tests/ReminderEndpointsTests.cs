using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
}
