# 0004. Portal upload rate limits are applied via a chained global limiter, not two `.RequireRateLimiting(...)` calls

- **Status:** accepted
- **Date:** 2026-05-23
- **Deciders:** Ruben G.

## Context

The vendor portal upload endpoint (`POST /api/portal/{token}/upload`) is PUBLIC and unauthenticated — the highest-risk surface in CompliDrop. The architecture spec calls for two independent rate limits on it:

- **`portal-token`** — 10/hr per upload token. The control that stops someone hammering a single leaked/shared link.
- **`portal-ip`** — 30/hr per client IP. The coarse abuse cap across links.

Both are documented in CLAUDE.md ("portal-token (10/hr) + portal-ip (30/hr) rate limits"). The original wiring registered each as a named policy via `AddPolicy(...)` and attached them to the endpoint by chaining:

```csharp
group.MapPost("/{token}/upload", UploadViaPortal)
    .DisableAntiforgery()
    .RequireRateLimiting("portal-token")
    .RequireRateLimiting("portal-ip");
```

This looks correct, and reviews missed it. Ticket #10 (writing integration tests for the portal endpoints) surfaced that it isn't: 11 same-token uploads were not throttled at the 11th, while 31 distinct-token requests were throttled at the 31st. Only `portal-ip` (the *last* policy applied) was actually enforced.

Root cause is in `RateLimitingMiddleware`:

```csharp
var enableRateLimitingAttribute = endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>();
```

`GetMetadata<T>()` returns the *last* metadata entry of a type. `RequireRateLimiting(policy)` adds an `EnableRateLimitingAttribute`. Two calls add two attributes, the middleware reads one, and the first policy is silently dropped. **There is no compile-time, startup-time, or runtime warning** when this happens — chained `RequireRateLimiting` calls type-check, build, and run, just without applying all of the policies the author thought they were applying.

The per-token cap was therefore absent in production until ticket #10's test caught it.

## Decision

Apply both portal limits via a single chained `PartitionedRateLimiter<HttpContext>` registered as `RateLimiterOptions.GlobalLimiter`, gated to portal-upload requests; all other requests get `RateLimitPartition.GetNoLimiter`. Drop the `portal-token` and `portal-ip` named policies and the two `.RequireRateLimiting(...)` calls on the endpoint.

```csharp
opts.GlobalLimiter = PartitionedRateLimiter.CreateChained(
    PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        IsPortalUpload(ctx)
            ? RateLimitPartition.GetFixedWindowLimiter("portal-token:" + token(ctx), ...10/hr...)
            : RateLimitPartition.GetNoLimiter("non-portal")),
    PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        IsPortalUpload(ctx)
            ? RateLimitPartition.GetFixedWindowLimiter("portal-ip:" + ip(ctx), ...30/hr...)
            : RateLimitPartition.GetNoLimiter("non-portal")));
```

`IsPortalUpload` matches `POST` requests whose path starts with `/api/portal` and ends with `/upload`. The token's route value is read inside the partition factory (routing has run by the time the limiter is consulted because `UseRateLimiter()` sits after `UseRouting()`).

The global limiter runs additively alongside named policies on other endpoints — `auth-strict`, `waitlist`, and `default-authed` continue to apply where they already do, and non-portal requests pay only a single `GetNoLimiter` partition lookup.

## Consequences

### Positive
- Both portal limits are now actually enforced; AC2 of ticket #10 is satisfiable. The per-token cap is the one that protects against a single leaked link being hammered — it's also the one that was silently missing.
- The chained design is the framework-blessed way to apply N independent limits to one request. `PartitionedRateLimiter.CreateChained` is exactly what ASP.NET ships for this.
- Future portal endpoints that need the same caps get them automatically by virtue of matching `IsPortalUpload` (or trivially extended for new paths) — no per-endpoint wiring to forget.

### Negative
- The global limiter runs on every request, including ones that should not be limited at all (e.g. `/health/live`). The non-portal branch returns `GetNoLimiter`, which is a cheap dictionary lookup + no-op acquire, but it is not literally zero work. At MVP traffic this is invisible; at any meaningful scale it remains negligible compared to the rest of the pipeline.
- "Portal upload" is identified by path + method rather than by endpoint metadata. If a future endpoint accidentally matches that shape (e.g. `POST /api/portal/foo/upload` for an unrelated purpose) it will be silently rate-limited as a portal upload. Mitigation: keep the check tight to the existing route shape, and replace it with an endpoint-metadata marker (e.g. a custom attribute) the moment a second portal-shaped path appears.

### Neutral
- The `portal-token` and `portal-ip` named policies are gone. Any future code that calls `.RequireRateLimiting("portal-token")` will fail at startup with an "unknown policy" error — loud, immediate, the desired failure mode. Note this guard catches typo'd or removed *policy names*, not the original chaining anti-pattern itself: if `.RequireRateLimiting("a").RequireRateLimiting("b")` reappears on another endpoint with two existing policies, the silent-drop bug recurs there. If/when that happens, the mitigation is a startup check that walks `EndpointDataSource.Endpoints` and throws when any endpoint carries more than one `EnableRateLimitingAttribute`. Not added now (zero call sites today), but the option is on the table.
- `RemoteIpAddress` being `null` (which it is under the test server, and can be in some proxied deployments without `UseForwardedHeaders`) falls back to a literal `"unknown"` partition key. Same behavior as before this change. `app.UseForwardedHeaders()` runs ahead of the limiter, so behind a configured proxy the real client IP is used.

## Alternatives considered

### Option A — keep two `.RequireRateLimiting(...)` calls, hope the framework grows multi-policy support
Rejected: the framework's last-wins behavior is the documented semantics of `GetMetadata<T>()`, not a bug that will be fixed. Waiting it out leaves the per-token cap unenforced indefinitely.

### Option B — collapse to a single named policy with a composite partition key (`token + ip`)
Rejected: this changes the meaning of the limit. A composite key gives one combined limit, not two independent ones. An attacker with one token can no longer exceed *either* cap alone, but the *meaning* of "10/hr per token regardless of IP" is lost — and that's the meaning that matters for shared-link abuse from a botnet (many IPs, one token).

### Option C — custom endpoint filter that manually acquires from both limiters
Rejected: more code, duplicates what `PartitionedRateLimiter.CreateChained` does for free, and the filter would still have to be remembered on every new portal-shaped endpoint (the failure mode the chained global limiter avoids).

### Option D — keep named policies and apply them via `[EnableRateLimiting(...)]` attributes
Rejected: the attribute mechanism has the same last-wins metadata semantics. Multiple attributes on the same endpoint produce the same silent drop.

## Rejection envelope (addendum, [#45](https://github.com/neboxdev/complidrop/issues/45))

A `RateLimiterOptions.OnRejected` hook in [`Program.cs`](../../api/CompliDrop.Api/Program.cs) writes an explicit error envelope on every rate-limit rejection — global limiter (portal-token + portal-ip), `auth-strict`, `waitlist`, and `default-authed`:

```json
{ "data": null, "error": { "code": "rate_limit.exceeded",
  "message": "Too many requests. Please try again later.", "correlationId": "..." } }
```

Shape matches [`ExceptionHandlingMiddleware`](../../api/CompliDrop.Api/Middleware/ExceptionHandlingMiddleware.cs)'s 500-path envelope byte-for-byte: same `data: null`, same `error.{code, message, correlationId}`, same camelCase `PropertyNamingPolicy`. The serializer is invoked against the strongly-typed anonymous payload (not a hand-rolled JSON literal) so a future field addition to the canonical envelope propagates automatically.

The code `rate_limit.exceeded` is universal across every policy by design — the policy distinction (which partition rejected) is internal accounting and not actionable for the client. The envelope's correlationId (from `HttpContext.Items["CorrelationId"]`) is the field a client uses to correlate the 429 with server logs. The envelope deliberately does NOT leak the policy name in any field — pinned by the negative assertions in [`Rate_limit_envelope_carries_the_full_documented_shape`](../../api/CompliDrop.Api.Tests/VendorPortalEndpointsTests.cs) and [`Exceeding_the_auth_strict_rate_limit_returns_the_rate_limit_envelope`](../../api/CompliDrop.Api.Tests/AuthEndpointsTests.cs).

The discriminator-pair invariant — `rate_limit.exceeded` (transient, retry-next-hour) ≠ `vendor.portal_quota_exceeded` (permanent, never-retry) — is pinned by [`Rate_limit_and_quota_429_codes_are_distinguishable_to_clients`](../../api/CompliDrop.Api.Tests/VendorPortalEndpointsTests.cs). A future refactor that aliased the two codes (e.g. via namespace-collapse) would fail there.

The hook checks `Response.HasStarted` before writing — defensive parity with `ExceptionHandlingMiddleware`. A pipeline change that started the response earlier than the limiter trips would now no-op rather than throw `InvalidOperationException` inside the limiter middleware.

## Addendum ([#242](https://github.com/neboxdev/complidrop/issues/242) / [#308](https://github.com/neboxdev/complidrop/issues/308)) — the limiter now covers the portal READ routes too

The #242 tenant-isolation audit found that the global limiter gated **only** `POST …/upload` (the
`IsPortalUpload` check), so the two PUBLIC, unauthenticated portal GET routes — info
(`GET /api/portal/{token}`) and upload status (`GET /api/portal/{token}/status/{uploadId}`) — fell
through to the no-op partition and were **entirely unthrottled** (no per-token AND no per-IP cap), an
unbounded DB-load surface on routes that take untrusted input. Excluding the GETs from the *strict*
10/hr token budget was deliberate (a vendor polling status must not burn the upload budget — see
`Portal_GET_endpoints_allow_generous_polling`), but "no limit at all" was the unintended consequence.

The gate generalised from the inline `IsPortalUpload` boolean to a unit-tested classifier,
[`PortalRateLimit.Classify`](../../api/CompliDrop.Api/PortalRateLimit.cs) → `Upload` / `Read` / `None`
(this is the "endpoint-metadata marker the moment a second portal-shaped path appears" the original
Negative consequence #2 anticipated). The limits are now:

- **Upload** — per token 10/hr (`portal-token:`) + per IP 30/hr (`portal-ip-upload:`). Unchanged caps.
- **Read** — **uncapped per token** (polling is not throttled) + per IP **240/hr** (`portal-ip-read:`),
  a generous single-IP backstop far above any legitimate polling burst. A distributed flood is an
  edge/infra concern, not the in-app limiter's job.
- **None** (non-portal) — no-op partition, as before. Named policies still apply additively.

Implementation note: the per-IP Upload and Read partitions use **distinct key prefixes**
(`portal-ip-upload:` vs `portal-ip-read:`). A shared `portal-ip:`+IP key would collapse the two into
one cached bucket whose limit is set by whichever request type opened the window first — silently
lifting the upload cap to 240 or capping polling at 30. Pinned by
`Portal_read_and_upload_per_ip_limits_are_independent`. CLAUDE.md's portal description was corrected
to match.

## References

- Tickets: [#10](https://github.com/neboxdev/complidrop/issues/10) (surfaced during AC2 test writing), [#45](https://github.com/neboxdev/complidrop/issues/45) (envelope-disambiguation followup), [#242](https://github.com/neboxdev/complidrop/issues/242) / [#308](https://github.com/neboxdev/complidrop/issues/308) (read-route coverage, this addendum)
- Endpoint: `api/CompliDrop.Api/Endpoints/VendorPortalEndpoints.cs`
- Composition: `api/CompliDrop.Api/Program.cs` (rate-limiting section + `OnRejected` hook)
- Tests: `api/CompliDrop.Api.Tests/VendorPortalEndpointsTests.cs` (`Exceeding_the_portal_token_rate_limit_returns_429`, `Exceeding_the_portal_ip_rate_limit_returns_429`, `Rate_limit_and_quota_429_codes_are_distinguishable_to_clients`, `Rate_limit_envelope_carries_the_full_documented_shape`), `api/CompliDrop.Api.Tests/AuthEndpointsTests.cs` (`Exceeding_the_auth_strict_rate_limit_returns_the_rate_limit_envelope`)
- Framework: [`RateLimitingMiddleware`](https://github.com/dotnet/aspnetcore/blob/main/src/Middleware/RateLimiting/src/RateLimitingMiddleware.cs), [`PartitionedRateLimiter.CreateChained`](https://learn.microsoft.com/dotnet/api/system.threading.ratelimiting.partitionedratelimiter.createchained)
