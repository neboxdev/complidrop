# 0053. Backend error events reach Sentry via a Serilog sink, scrubbed, and never from Development

- **Status:** accepted
- **Date:** 2026-08-06
- **Deciders:** Ruben G.

## Context

`Sentry:Dsn` has been configured in production for a long time, the dashboard showed performance
transactions (10% sample), and the privacy policy names Sentry as a processor. All of that made
backend error monitoring *look* live. It was not: **not one unhandled 500, extraction failure,
reminder-tick failure or sweep failure had ever produced a Sentry event.**
[#386](https://github.com/neboxdev/complidrop/issues/386) traced it to two independent breaks, either
of which was sufficient on its own.

**1. Request-path exceptions never propagate to Sentry.** `Sentry.AspNetCore` captures exceptions that
reach its middleware, installed outermost via a startup filter. `ExceptionHandlingMiddleware` catches
everything, logs it, writes the `server.error` envelope and does **not** rethrow (except the rare
`Response.HasStarted` path), and does not set `IExceptionHandlerFeature`. The exception is consumed
before Sentry's middleware can see it.

**2. The `ILogger` fallback is severed by Serilog.** `builder.Host.UseSerilog(...)` replaces the MEL
provider pipeline — `writeToProviders` defaults `false` — so `SentryLoggerProvider`, which would
otherwise turn every `LogError` into an event, is bypassed. No `Sentry.Serilog` package was
referenced, so no bridge existed. Every background-worker failure line (`"Reminder tick failed"`,
`"Compliance sweep failed"`, `"ExtractionWorker process failed"`) died in a console-only path.

Net capture was performance transactions plus the near-unreachable `HasStarted` rethrow. A Neon
outage or a Gemini key expiry was invisible until a customer emailed. This ADR records the fix and,
more importantly, the three decisions the fix forced — because turning every `LogError` into an event
changes what leaves the building.

## Decision

### The bridge: one Serilog sink, `Error` and above

`Sentry.Serilog` is added at the same version as `Sentry.AspNetCore` (they ship as one SDK; a version
split would load two Sentry assemblies), and `BackendSentry.AddSentryErrorEvents` registers the sink
inside the `UseSerilog` lambda with `InitializeSdk = false` — `UseSentry` already initialised the SDK
from the same DSN, so the sink piggybacks on that hub and one process has one Sentry client.

This single change closes **both** breaks, because `ExceptionHandlingMiddleware` and every worker tick
already call `logger.LogError(ex, …)`. Rethrowing from the middleware was rejected (see Alternatives):
the swallow is what produces the `server.error` envelope the frontend contract depends on.

### The gate: a DSN **and** not Development

`BackendSentry.IsEnabled(configuration, environment)` is the one gate, asked identically by the SDK
and by the sink, so the two can never disagree about whether a process reports. It requires **both** a
non-blank DSN (whitespace reads as absent) **and** a non-Development environment.

The Development half is not decoration. Presence of a DSN alone was *assumed* to keep dev silent
because `appsettings.json` ships `"Dsn": ""` — but configuration layers above that file, and #386
found the **real production DSN sitting in the local `user-secrets` store**, where it also reaches the
integration-test host (same `UserSecretsId`, Development environment). Harmless while the backend
captured nothing but traces; not harmless once every `LogError` is an event, because the dev database
is a **clone of prod data** ([docs/dev-environment.md](../dev-environment.md)) — an exception naming a
real vendor would have been exported to the production Sentry project by nothing more than running
the test suite.

It is spelled **not-Development** rather than **is-Production** because the failure directions are not
symmetric: an unset `ASPNETCORE_ENVIRONMENT` already means Production and a Staging box should report,
so going dark over an environment-name spelling is the exact failure this ticket exists to end —
whereas a production box literally naming itself `Development` would already be serving OpenAPI and
skipping HTTPS redirection. This is ADR 0037's frontend rule (`dsn && NODE_ENV === "production"`) with
the asymmetry corrected for the end that must never go quiet.

`CustomWebApplicationFactory` additionally pins `Sentry:Dsn = ""`, the same explicit lock it already
applies to `Stripe:SecretKey` for the same user-secret-leakage reason.

### PII: a `BeforeSend` scrubber, and no breadcrumbs

`SendDefaultPii = false` and `MaxRequestBodySize = RequestSize.None` are set **explicitly** even
though both match the SDK default — they are the load-bearing half of the posture, and a default is
not a decision anyone can see in a diff. On top of them, `SentryScrub.Scrub` runs as `BeforeSend` over
every event: message (template, formatted, params), exception values, `extra` values, tag values, and
`Request.Url` / `QueryString` / header values. Four bounded nets, applied after a hard 8192-character
cap (the cap runs **first**, so an unbounded third-party error body cannot turn `BeforeSend` into a
stall — same reasoning as ADR 0037's `maxValueLength`):

- **Vendor-portal capability token**, replaced deterministically by path shape
  (`/api/portal/{token}` → `/api/portal/[redacted]`, `/portal/…` likewise, case-insensitive, stopping
  at the next separator). Not an entropy heuristic — ADR 0037 rejected that route for this same value.
  It matters because `UseSerilogRequestLogging` logs a completed request at **Error** when the
  response is a 500, and the Sentry ASP.NET integration attaches `Request.Url` regardless of
  `SendDefaultPii`; both spell the real path, and that path *is* the bearer credential for the link.
- **Email addresses**, deliberately laxer than `Services/ContactEmail.cs` — a redactor must
  over-match, where a validator must not.
- **JWTs** (`eyJ…`, the shape of `cd_session` / `cd_refresh`).
- **Credential query parameters** (`token`, `email`, `sig`, `key`, `secret`, `password`, `code`),
  anchored on start-of-string as well as `?`/`&` because `SentryRequest.QueryString` is bare.

Document / vendor / org GUIDs and route shapes deliberately survive — the same triage-preserving trade
ADR 0037 makes with its entropy-blind metadata net.

**Breadcrumbs are off** (`MinimumBreadcrumbLevel = Fatal`, the highest level, at which this codebase
logs nothing). A breadcrumb would ship the whole `Information`/`Warning` log stream to a third party,
and that stream is exactly what [#378](https://github.com/neboxdev/complidrop/issues/378) (open) says
still embeds end-user email addresses — `"Resend not configured — skipping email to {To}"` is one line
of many, and the Development-only `"DEV email suppressed — would send to {To} … Body: {HtmlBody}"` is
worse. Decisively, `SentryEvent.Breadcrumbs` is **read-only**, so the scrubber could not reach them
the way it reaches everything else. Revisit when #378 closes.

### The correlation join

`SentryScrub` promotes the request's correlation id from the `CorrelationId` structured log property
— which `Enrich.FromLogContext` puts on every log event in a request and the Serilog sink forwards as
an extra — onto a `correlation_id` **tag**, after scrubbing, exactly as ADR 0037's `tagCorrelationId`
does on the frontend. Same key, same id, so a browser error and the backend 500 behind it are one
search apart.

The tag is deliberately **not** redacted, because a correlation id is an opaque identifier and
redacting it would defeat the join it exists for. What makes that safe is its SHAPE, decided in one
place: `CorrelationIdMiddleware.IsUsableTraceId`, the ADR 0044 charset guard (`[A-Za-z0-9_-]`, ≤64)
that REPLACES an unusable inbound `X-Trace-Id` rather than truncating it. The promotion **re-asks**
that predicate rather than trusting the log property, so client free text cannot land on the tag even
if some future path put an unvetted value into the log scope. An existing `correlation_id` tag is
never overwritten.

## Consequences

### Positive
- Every unhandled 500, extraction failure, reminder-tick failure and sweep failure is now a Sentry
  event. The founder's belief that error alerting works becomes true.
- One change covers both breaks; `ExceptionHandlingMiddleware` keeps its response contract.
- Dev and the test suite are silent **by construction**, not by everyone keeping a secret store tidy.
- No portal token, email, JWT or credential parameter can reach Sentry, proven against the SERIALIZED
  envelope (`CapturingSentryTransport`) rather than against an event object — the claim is about what
  leaves the process, so the assertion is too.
- Frontend and backend events join on `correlation_id`.

### Negative
- **A 500 now produces two events.** `ExceptionHandlingMiddleware` logs the exception, and
  `UseSerilogRequestLogging` independently logs the completed request at `Error`. Kept rather than
  filtered: the request-completion line is the only signal for a 500 that never threw, Sentry groups
  by fingerprint, and both pass the scrubber. Revisit only if quota becomes a real cost.
- **The scrubber is a shape net, not a prose filter.** ADR 0037's project rule carries over verbatim
  to the backend: *application code never hands raw document field values to Sentry*, and by extension
  never to an `Error`-level log line. The #386 audit walked all 23 `Error`-level sites (no `Fatal`
  sites exist); every template carries ids, counts, codes and fixed labels only. Four hand a
  third-party response body to a structured property — `EmailService` (`Resend send failed {Status}
  {Body}`, whose 422 body names the rejected recipient) and the Gemini / Anthropic / Document AI
  clients. Those bodies are the only diagnostic a failed call leaves, so the lines stand and the
  redaction happens at the sink; the console sink still records them whole. The broadest residue is
  `ExceptionHandlingMiddleware`'s `ex` itself: an exception raised anywhere can embed user content in
  its `Message`, and only the four nets catch it.
- Development can no longer smoke-test Sentry by setting a DSN; it needs a non-Development
  `ASPNETCORE_ENVIRONMENT`. Deliberate, and documented in `docs/dev-environment.md`.

### Neutral
- `appsettings.Development.json`'s `Sentry:TracesSampleRate: 0.0` is now moot (Development never
  initialises the SDK). Left in place — it costs nothing and it is not wrong.
- A pre-existing local `Sentry:Dsn` in someone's user-secrets is now inert rather than dangerous.
  Removing it is still the tidy thing to do; the gate means nobody has to.

## Alternatives considered

### Rethrow from `ExceptionHandlingMiddleware` so Sentry's middleware sees the exception
Rejected. The catch is what writes the `{ data, error: { code, message, correlationId } }` envelope
the frontend's `ApiError` contract parses; rethrowing yields a bare 500 with no body and no
correlation id. It would also have fixed only break 1, leaving every worker-tick failure dark.

### `SentrySdk.CaptureException(ex)` inside the middleware (the ticket's optional suggestion)
Rejected as redundant *and* worse: the middleware already calls `LogError`, so with the sink in place
this would double-report every 500 — and it would still leave breaks in the workers. The tag it was
suggested to carry is instead promoted for **every** event raised during a request, in one place.

### `writeToProviders: true` on `UseSerilog`
Rejected. It re-enables the whole MEL provider pipeline to get one bridge, doubling every log write
and re-introducing `SentryLoggerProvider`'s Information-level breadcrumbs — the exact #378 exposure
this ADR closes.

### Gate on DSN presence only, and just tell people not to set one in dev
Rejected: that is the assumption #386 falsified. It had already failed silently on the founder's own
machine, and the cost of the failure is prod-cloned data leaving the building.

### Gate on `IsProduction()`, mirroring ADR 0037 exactly
Rejected: it makes a Staging deploy silent and, worse, makes prod silent if `ASPNETCORE_ENVIRONMENT`
is ever spelled differently. See the Decision for the asymmetry.

### Scrub breadcrumbs instead of suppressing them
Not available: `SentryEvent.Breadcrumbs` is read-only, so `BeforeSend` cannot rewrite them. The only
lever is the level at which they are created.

## References

- **Tickets:** [#386](https://github.com/neboxdev/complidrop/issues/386) (this fix),
  [#378](https://github.com/neboxdev/complidrop/issues/378) (PII in sub-`Error` log lines — the reason
  breadcrumbs are off).
- **Related ADRs:** [0037](0037-frontend-sentry-pii-scrubbing-and-gating.md) (the frontend half — the
  scrubber shape, the `correlation_id` tag, the never-attach-user-content rule),
  [0044](0044-audit-client-input-clamped-at-the-boundary.md) (`IsUsableTraceId`, the charset that keeps
  the un-redacted tag safe), [0034](0034-dev-environment-isolation-and-boot-banner.md) /
  [#271](https://github.com/neboxdev/complidrop/issues/271) (the isolated-by-default dev posture).
- **Code:** `api/CompliDrop.Api/BackendSentry.cs`, `api/CompliDrop.Api/SentryScrub.cs`,
  `api/CompliDrop.Api/Program.cs`, `api/CompliDrop.Api/Middleware/CorrelationIdMiddleware.cs`.
- **Env:** `Sentry:Dsn` (backend DSN, **distinct** from `NEXT_PUBLIC_SENTRY_DSN`; absent or
  Development ⇒ no events at all), `Sentry:TracesSampleRate` (default `0.1`).
