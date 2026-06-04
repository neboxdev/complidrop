using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the first-run onboarding flag (#191): a brand-new account
/// starts un-onboarded, <c>POST /api/auth/complete-onboarding</c> flips the
/// server-persisted flag (exposed on <c>/api/auth/me</c>), and the flip is
/// idempotent + auth-gated.
/// </summary>
public sealed class OnboardingEndpointTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static async Task<bool> HasCompletedOnboarding(HttpClient client)
    {
        var resp = await client.GetAsync("/api/auth/me");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("hasCompletedOnboarding").GetBoolean();
    }

    [Fact]
    public async Task New_account_starts_not_onboarded()
    {
        var auth = await RegisterAndLoginAsync();
        (await HasCompletedOnboarding(auth.Client)).Should().BeFalse(
            "a freshly-registered user must see the first-run welcome");
    }

    [Fact]
    public async Task Complete_onboarding_flips_the_flag_and_returns_updated_me()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsync("/api/auth/complete-onboarding", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("hasCompletedOnboarding").GetBoolean()
            .Should().BeTrue("the response carries the refreshed session so the client can cache it");

        // And it persists for the next /me read.
        (await HasCompletedOnboarding(auth.Client)).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_onboarding_is_idempotent()
    {
        var auth = await RegisterAndLoginAsync();

        (await auth.Client.PostAsync("/api/auth/complete-onboarding", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await auth.Client.PostAsync("/api/auth/complete-onboarding", null);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("hasCompletedOnboarding").GetBoolean().Should().BeTrue();
        (await HasCompletedOnboarding(auth.Client)).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_onboarding_requires_authentication()
    {
        var anon = CreateClient();

        var resp = await anon.PostAsync("/api/auth/complete-onboarding", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Onboarding_flag_is_per_user_not_shared()
    {
        // Completing onboarding for one org must not flip it for another.
        var a = await RegisterAndLoginAsync();
        var b = await RegisterAndLoginAsync();

        (await a.Client.PostAsync("/api/auth/complete-onboarding", null)).EnsureSuccessStatusCode();

        (await HasCompletedOnboarding(a.Client)).Should().BeTrue();
        (await HasCompletedOnboarding(b.Client)).Should().BeFalse("org B never completed onboarding");
    }
}
