using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for PUT /api/auth/organization (#185): the org owner edits the org name +
/// IANA time zone, the change is persisted + reflected in /me, invalid input is rejected, and the
/// update is strictly tenant-scoped.
/// </summary>
public sealed class OrganizationUpdateTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static async Task<(string Name, string TimeZone)> MeOrgAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return (data.GetProperty("organizationName").GetString()!, data.GetProperty("timeZone").GetString()!);
    }

    [Fact]
    public async Task Update_changes_name_and_timezone_and_is_reflected_in_me_and_the_db()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PutAsJsonAsync(
            "/api/auth/organization",
            new { name = "Lone Star Event Venues", timeZone = "America/Chicago" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("organizationName").GetString().Should().Be("Lone Star Event Venues");
        data.GetProperty("timeZone").GetString().Should().Be("America/Chicago");

        (await MeOrgAsync(auth.Client)).Should().Be(("Lone Star Event Venues", "America/Chicago"));

        // The persisted zone is what the reminder worker reads to compute the
        // local-08:00 send window, so the change flows through to reminders.
        await using var db = CreateSystemDb();
        var org = await db.Organizations.SingleAsync(o => o.Id == auth.OrgId);
        org.TimeZone.Should().Be("America/Chicago");
        org.Name.Should().Be("Lone Star Event Venues");
    }

    [Fact]
    public async Task Update_trims_the_name()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PutAsJsonAsync(
            "/api/auth/organization",
            new { name = "  Trimmed Co  ", timeZone = "UTC" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MeOrgAsync(auth.Client)).Name.Should().Be("Trimmed Co");
    }

    [Fact]
    public async Task Update_rejects_an_invalid_timezone_and_leaves_the_org_unchanged()
    {
        var auth = await RegisterAndLoginAsync();
        var (nameBefore, tzBefore) = await MeOrgAsync(auth.Client);

        var resp = await auth.Client.PutAsJsonAsync(
            "/api/auth/organization",
            new { name = "New Name", timeZone = "Not/AZone" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("validation.timezone");
        // A rejected request must not partially apply the name either.
        (await MeOrgAsync(auth.Client)).Should().Be((nameBefore, tzBefore));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_rejects_an_empty_name(string name)
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PutAsJsonAsync(
            "/api/auth/organization",
            new { name, timeZone = "UTC" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString().Should().Be("validation.required");
    }

    [Fact]
    public async Task Update_requires_authentication()
    {
        var resp = await CreateClient().PutAsJsonAsync(
            "/api/auth/organization",
            new { name = "Anon Co", timeZone = "UTC" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_only_affects_the_callers_own_org()
    {
        var orgA = await RegisterAndLoginAsync($"a-{Guid.NewGuid():N}@x.com");
        var orgB = await RegisterAndLoginAsync($"b-{Guid.NewGuid():N}@x.com");
        var (bNameBefore, bTzBefore) = await MeOrgAsync(orgB.Client);

        (await orgA.Client.PutAsJsonAsync(
            "/api/auth/organization",
            new { name = "Org A Renamed", timeZone = "Asia/Tokyo" }))
            .EnsureSuccessStatusCode();

        // Org A changed…
        (await MeOrgAsync(orgA.Client)).Should().Be(("Org A Renamed", "Asia/Tokyo"));
        // …Org B is untouched (the tenant filter scoped the update to A's org).
        (await MeOrgAsync(orgB.Client)).Should().Be((bNameBefore, bTzBefore));
    }
}
