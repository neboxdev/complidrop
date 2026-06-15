using CompliDrop.Api;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Unit tests for <see cref="PortalRateLimit.Classify"/> — the portal rate-limiter route
/// classification extracted from Program.cs (#242). The integration harness disables rate limiting
/// (no client IP to partition on in TestServer), so this is where the "which portal routes are
/// throttled" contract is pinned. The bug this guards: the public GET info + status routes used to
/// classify as unthrottled.
/// </summary>
public sealed class PortalRateLimitTests
{
    [Theory]
    [InlineData("POST", "/api/portal/abc/upload", PortalRateLimit.Kind.Upload)]
    [InlineData("POST", "/api/portal/abc/Upload", PortalRateLimit.Kind.Upload)]   // case-insensitive suffix
    [InlineData("GET", "/api/portal/abc", PortalRateLimit.Kind.Read)]             // portal info
    [InlineData("GET", "/api/portal/abc/status/def", PortalRateLimit.Kind.Read)]  // upload status (was unthrottled)
    [InlineData("POST", "/api/portal/abc", PortalRateLimit.Kind.Read)]            // any non-upload portal request
    [InlineData("GET", "/api/documents", PortalRateLimit.Kind.None)]
    [InlineData("POST", "/api/documents/upload", PortalRateLimit.Kind.None)]      // non-portal /upload path
    [InlineData("GET", "/api/portalish/abc", PortalRateLimit.Kind.None)]          // segment-boundary: not /api/portal
    public void Classify_routes_to_the_right_limiter_bucket(string method, string path, PortalRateLimit.Kind expected)
    {
        PortalRateLimit.Classify(method, new PathString(path)).Should().Be(expected);
    }

    [Fact]
    public void Public_portal_GET_routes_are_rate_limited_not_unthrottled()
    {
        // The #242 regression guard: the public GET info + upload-status routes MUST classify as
        // Read (i.e. get a limiter partition), never None (the no-op partition that left them an
        // unbounded token-validity oracle + DB-load DoS).
        PortalRateLimit.Classify("GET", new PathString("/api/portal/tok")).Should().Be(PortalRateLimit.Kind.Read);
        PortalRateLimit.Classify("GET", new PathString("/api/portal/tok/status/up")).Should().Be(PortalRateLimit.Kind.Read);
    }
}
