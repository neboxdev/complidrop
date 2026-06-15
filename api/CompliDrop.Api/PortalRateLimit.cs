using Microsoft.AspNetCore.Http;

namespace CompliDrop.Api;

/// <summary>
/// Classifies an inbound request for the PUBLIC vendor-portal rate limiter. A composition-root
/// helper extracted from <c>Program.cs</c>'s global limiter so the classification — which decides
/// whether a portal route is throttled at all — is unit-testable.
/// </summary>
/// <remarks>
/// Fixes #242: the limiter previously gated ONLY on <c>POST …/upload</c>, so the two public,
/// unauthenticated GET routes — portal info (<c>GET /api/portal/{token}</c>) and upload status
/// (<c>GET /api/portal/{token}/status/{uploadId}</c>) — fell through to the no-op partition and were
/// entirely unthrottled. That left a per-IP-unbounded token-validity oracle plus a DB-load DoS (each
/// portal read is a multi-join query) on routes that take untrusted input. Uploads keep the stricter
/// documented limits; reads get a looser cap that still bounds abuse without blocking a vendor
/// legitimately polling upload status.
/// </remarks>
public static class PortalRateLimit
{
    public enum Kind { None, Upload, Read }

    /// <summary>
    /// Classifies a request by method + path. <see cref="Kind.Upload"/> = <c>POST /api/portal/…/upload</c>
    /// (the <c>/upload</c> suffix is matched case-insensitively — ASP.NET routing matches the route
    /// template case-insensitively, so an ordinal compare would let <c>/Upload</c> skip the limiter).
    /// <see cref="Kind.Read"/> = any other <c>/api/portal/*</c> request (the public GET info + status
    /// routes). <see cref="Kind.None"/> = everything else (gets the no-op partition; other named
    /// policies still apply).
    /// </summary>
    public static Kind Classify(string method, PathString path)
    {
        if (!path.StartsWithSegments("/api/portal") || path.Value is not { } p)
            return Kind.None;
        if (HttpMethods.IsPost(method) && p.EndsWith("/upload", StringComparison.OrdinalIgnoreCase))
            return Kind.Upload;
        return Kind.Read;
    }
}
