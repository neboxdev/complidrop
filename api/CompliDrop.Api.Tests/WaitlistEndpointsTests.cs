using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the public, unauthenticated waitlist endpoint
/// (<see cref="CompliDrop.Api.Endpoints.WaitlistEndpoints"/>). The handler
/// itself is small (validate email → insert/dedupe → return); the test
/// surface that matters for #45's followup envelope-shape matrix is the
/// 429 path on the <c>waitlist</c> rate-limit policy (10/hr per IP).
///
/// The shared fixture boots the host with <c>RateLimiting:Enabled=false</c>,
/// so this test spins up a one-off host with the limiter enabled (same
/// pattern as <see cref="VendorPortalEndpointsTests"/> +
/// <see cref="AuthEndpointsTests"/>'s rate-limit tests). A one-off host
/// keeps the limiter's per-partition counter state isolated from
/// neighboring tests in the same fixture.
/// </summary>
public sealed class WaitlistEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static object NewWaitlistRequest(string? email = null) => new
    {
        email = email ?? $"signup-{Guid.NewGuid():N}@example.com",
        companyName = (string?)null,
        industry = (string?)null,
        source = (string?)null,
    };

    [Fact]
    public async Task Exceeding_the_waitlist_rate_limit_returns_the_rate_limit_envelope()
    {
        // #45 followup — the OnRejected hook in Program.cs fires GLOBALLY
        // for every rate-limit policy (portal-token, portal-ip,
        // auth-strict, waitlist, default-authed). The portal limiters
        // (VendorPortalEndpointsTests) and auth-strict (AuthEndpointsTests)
        // already pin the envelope shape; waitlist (10/hr per IP on
        // POST /api/waitlist) was the remaining uncovered slot. This test
        // closes that gap so a future refactor that per-policy-overrode
        // the hook for waitlist (e.g. emitted a different `error.code`
        // or leaked the policy name) fails here.
        //
        // The limiter partitions on Connection.RemoteIpAddress, falling
        // back to "unknown" under TestServer (TestServer surfaces no real
        // client IP). All 11 requests share that single "unknown"
        // partition. Each successful POST returns 200 and burns one
        // permit; the 11th trips the limiter and surfaces
        // `rate_limit.exceeded` from the OnRejected hook.
        await using var factory = new CustomWebApplicationFactory(
            Fixture.ConnectionString,
            new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "true" });
        var client = factory.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/waitlist", NewWaitlistRequest());
            ok.StatusCode.Should().Be(
                HttpStatusCode.OK,
                $"waitlist signup {i + 1}/10 must succeed before the limiter trips");
        }

        var throttled = await client.PostAsJsonAsync("/api/waitlist", NewWaitlistRequest());

        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await throttled.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString()
            .Should().Be(
                "rate_limit.exceeded",
                "the OnRejected hook in Program.cs is global to every policy; waitlist " +
                "must surface the same envelope code as the portal + auth-strict limiters " +
                "so clients have ONE retry rule across every limited endpoint.");

        // The envelope must NOT leak the internal policy name — mirrors
        // the same negative assertion in VendorPortalEndpointsTests's
        // full-shape test and AuthEndpointsTests's auth-strict test. The
        // partition name (`waitlist` here, `portal-token` / `portal-ip` /
        // `auth-strict` / `default-authed` elsewhere) is implementation
        // detail and irrelevant to the client; surfacing it would defeat
        // the universal-code contract.
        var raw = await throttled.Content.ReadAsStringAsync();
        raw.Should().NotContain("waitlist");
    }
}
