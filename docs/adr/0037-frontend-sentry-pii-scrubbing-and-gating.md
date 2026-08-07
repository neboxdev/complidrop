# 0037. Frontend Sentry: PII scrubbing, sampling, and dev gating

- **Status:** accepted
- **Date:** 2026-06-25
- **Deciders:** Ruben G.

## Context

`@sentry/nextjs` has shipped in `frontend/package.json` for a while but was completely
**unwired** — no `instrumentation*.ts`, no Sentry config, an empty `next.config.ts`. So the SDK
captured nothing on the frontend. The privacy policy (`frontend/src/app/privacy/page.tsx`)
nonetheless advertises *"Sentry — error monitoring"*, a claim satisfied only by the **backend**
(`Sentry.AspNetCore`). [ADR 0035](0035-standing-cleanup-tooling-gates.md) recorded the SDK as
"kept-but-ignored" in `knip.jsonc` pending exactly this product/legal decision.

[#356](https://github.com/neboxdev/complidrop/issues/356) decides it: wire frontend error
monitoring PRE-launch so the privacy representation is fully accurate before the product has
paying customers. That raises four non-trivial decisions for a product that handles certificates
of insurance, vendor/user data, emails, public vendor-portal capability tokens, and JWT auth
cookies (`cd_session` / `cd_refresh`): **what may leave the browser**, **how much it costs**, **how
it stays off in dev**, and **how a build degrades without upload credentials**. This ADR records
them; the config files reference it.

## Decision

Wire client + server + edge error capture for the App Router via the SDK's current Next-16 file
conventions, with a privacy-first, cost-conscious, dev-isolated configuration.

### Wiring (Next-16 shapes, verified against the installed SDK)

- `src/instrumentation-client.ts` — browser `Sentry.init`; exports `onRouterTransitionStart`.
- `src/instrumentation.ts` — `register()` inits the Node + Edge runtimes; exports
  `onRequestError = Sentry.captureRequestError` (server components / route handlers / actions).
- `src/app/global-error.tsx` — App Router global boundary: reports to Sentry and renders a friendly
  fallback (see Error-copy below).
- `next.config.ts` wrapped with `withSentryConfig`.
- All three `Sentry.init` call sites share one option builder, `src/lib/sentry/options.ts`, so the
  scrubber, gating, and sample rates can never drift between runtimes.

### PII / secrets — `sendDefaultPii: false` + a `beforeSend` / `beforeSendTransaction` scrubber

`src/lib/sentry/scrub.ts` runs every event through a pure, unit-tested scrubber before transmit:

- **Removed wholesale:** request cookies, request body (`request.data`), request `env`, query
  string; request headers whose name implies a secret (`cookie`, `authorization`, `x-portal-token`,
  …); direct user PII (`email`, `ip_address`, `username`, `geo`); breadcrumb / span request &
  response **bodies** (the primary document-field-text vector); and any object value under a
  sensitive-named key (`*token*`, `*secret*`, `*password*`, `*email*`, `*portal*`, …) at any depth.
- **Redacted by pattern:** emails, JWTs (the auth cookies are JWTs), `Bearer …` credentials, US
  SSNs, and opaque high-entropy tokens (Stripe keys, base64 secrets) in free text.
- **Surviving request-header values** are pattern-redacted (a credential in a benign-named custom
  header can't slip through), and the **user bag is deep-redacted** (a nested custom user object
  can't carry an email/token past the scrubber).
- **Server stack frames + `logentry`:** the Node runtime's default `localVariablesIntegration` /
  `contextLinesIntegration` populate `frame.vars` / `context_line` / `pre_context` / `post_context`
  (which can hold a decoded JWT, email, portal token, or document value — the `onRequestError` path
  routes straight into these), and a parameterized `captureMessage` populates `logentry.message` /
  `logentry.params`. Both are scrubbed; function names / file paths stay intact for symbolication.
- **URLs** are path-sanitized: the vendor-portal capability token always appears as
  `/portal/{token}` / `/api/portal/{token}` (a 24-byte base64url token, `PortalLink.GenerateToken`),
  so a deterministic path replacement removes it regardless of charset — not reliant on the
  entropy regex. This also covers navigation-breadcrumb `from` / `to` path fields. Token/email/`sig`-named
  query params are redacted too (covers reset/verify links and Azure blob SAS `sig=`).
- **Two-net design:** a free-text net (emails + JWTs + Bearer + SSN + opaque-token) for messages,
  error values, the app-controlled `extra` bag, and URLs; a milder net (emails + JWTs + Bearer +
  SSN, **entropy-blind by design**) for SDK metadata (`contexts`, `tags`, span data) so load-bearing
  identifiers — Sentry `event_id`, `trace_id`, `span_id` — and dashed GUIDs (document / vendor / org
  ids) survive and errors stay triageable.
- **ReDoS / cost guard:** every regex uses bounded quantifiers (no quadratic backtracking on a long
  `@`-less blob), each string is length-capped before the regex passes, and `maxValueLength: 8192`
  is set at the SDK level — so an arbitrarily large `error.message` / `extra` value can't freeze the
  main thread inside `beforeSend`.

### Session Replay — OFF

Not enabled. A certificate of insurance on screen must never be recorded. If ever revisited it must
be privacy-first (`maskAllText` + `blockAllMedia`) and sampled very low — a separate, explicit
decision.

### Sampling — conservative + env-tunable

`tracesSampleRate` defaults to **0** (pure error monitoring; errors are captured regardless of the
trace rate) and is tunable via `NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE`, parsed defensively (unset /
blank / non-numeric / out-of-range → fallback) so a typo can't bill 100% of traces at $49/mo scale.

### Dev / no-DSN no-op

`enabled = Boolean(dsn) && NODE_ENV === "production"`, DSN from `NEXT_PUBLIC_SENTRY_DSN`. A
Development build, or a production build with no DSN, captures **nothing** (`enabled: false` and
`dsn: undefined` both enforce it). This mirrors the #271 dev-isolation posture — `Resend:ApiKey`
left unset → email-silent — so `NEXT_PUBLIC_SENTRY_DSN` is simply left unset in dev. (Stricter than
PostHog, which gates on key presence only; Sentry additionally requires production, per #356.)

### Source maps — graceful degradation

`withSentryConfig` uploads source maps only when `SENTRY_AUTH_TOKEN` is present
(`sourcemaps.disable: !SENTRY_AUTH_TOKEN`, `silent` likewise). Local builds and any CI job without
the secret (frontend-ci's build step sets only `NEXT_PUBLIC_API_URL`) skip upload and still succeed
— a missing token never fails the build. `telemetry: false` (no build telemetry from a compliance
product). (The deprecated `disableLogger` / webpack `treeshake.*` options are omitted: Next 16
builds with Turbopack, which doesn't support them, and the SDK debug logger is inert unless
`debug: true`, which we never set.)

### Backend cross-reference via `correlationId`

`api.ts`'s `ApiError` already carries the server `correlationId`. `beforeSend` duck-types the
captured exception (no import — keeps the helper runtime-agnostic for server/edge) and, **after**
scrubbing, copies that id onto a `correlation_id` tag, so a frontend error and the backend request
that caused it are cross-referenceable. The id is server-**resolved**, not always server-minted: the
API honors an inbound `X-Trace-Id` when it is a well-formed trace id and mints a fresh one
otherwise. What keeps the un-redacted tag safe is therefore its SHAPE, not its origin —
`CorrelationIdMiddleware.IsUsableTraceId` admits only ASCII alphanumerics, `-` and `_` up to 64
chars ([ADR 0044](0044-audit-client-input-clamped-at-the-boundary.md) /
[#372](https://github.com/neboxdev/complidrop/issues/372)), so an email address or any other free
text cannot be smuggled into this tag by a client.

### Error-copy policy (#77 / #254) preserved

`global-error.tsx` renders `GENERIC_FALLBACK_MESSAGE` (the single source of truth in `lib/api.ts`),
**never** `error.message` — a raw React render error or HTTP jargon must never reach the screen.
Sentry holds the technical detail; the UI stays human.

## Consequences

### Positive
- The privacy policy's frontend "Sentry — error monitoring" claim is now true; client, server, and
  edge errors plus unhandled React render crashes are captured in production.
- No cookie, auth/portal token, email, or document body can reach Sentry — proven by
  `scrub.test.ts`. `sendDefaultPii: false` plus the scrubber are independent layers.
- Off by default everywhere it should be: dev, missing DSN, missing build token.
- Conservative cost posture (traces off by default) suits a $49/mo product.

### Negative
- **Scrubber boundary.** The scrubber closes the vectors the SDK populates *automatically* (bodies,
  headers, cookies, URLs, breadcrumbs) and regex-redacts embedded credentials/emails. It does **not**
  strip arbitrary free *prose* we explicitly attach (e.g. a document sentence placed in `extra` with
  no email/token in it). The mitigation is a project rule, not code: **application code never hands
  raw document field values to Sentry** (`captureException` / `setExtra` / `setContext`). Reviewers
  should treat a new `Sentry.setExtra(...)` of user/document content as a finding.
- A second client-side processor now receives (scrubbed) error data — disclosed by the existing
  privacy-policy Sentry line; no new processor beyond what the policy already names.

- **The mild metadata net is entropy-blind by design.** A bare high-entropy secret with no
  recognisable shape (e.g. a 32-hex token) sitting under a *non-sensitive* key in `contexts` /
  `tags` survives — that's the deliberate trade to keep `trace_id` / `event_id` intact. Acceptable
  because the same "we never attach raw secrets" rule applies; if it ever stops holding, move
  metadata to the aggressive net.

### Neutral
- Bundle grows by the Sentry browser SDK (Replay excluded, logger tree-shaken). Acceptable for the
  observability gained; revisit only if bundle budgets tighten.
- `tracesSampleRate: 0` means `beforeSendTransaction` rarely fires by default; it is wired and
  tested so raising the env knob is safe. `browserTracingIntegration` is still bundled and
  instantiated at rate 0 (it just sends no transactions) — kept rather than tree-shaken so flipping
  the env knob stays an env change, not a code change.
- **Data region is chosen at provisioning, not in code.** The Sentry project should be created in
  the **US region** (DSN-selected) to match the backend Sentry and the privacy policy's "we process
  and store data primarily in the United States". An ops checklist item, not enforceable here.

## Alternatives considered

### Drop the SDK instead of wiring it
Rejected: the privacy policy advertises frontend error monitoring and the backend already pays for
Sentry; frontend visibility is genuinely useful pre-launch. Dropping it would mean editing the
policy to remove a capability we want.

### Enable Session Replay (masked)
Rejected for now: even fully masked, Replay on a screen showing COIs is a risk/cost we don't need to
take to get error monitoring. Left OFF; revisit on explicit request.

### Rely on `sendDefaultPii: false` alone (no scrubber)
Rejected: that flag governs the SDK's *default* attachments but not breadcrumb URLs containing a
portal token, an email inside an error message, or anything our code attaches. Defence in depth is
warranted for a compliance product.

### Gate only on DSN presence (PostHog-style)
Rejected: a leaked DSN in a non-prod build would start sending. Requiring `NODE_ENV === production`
*and* a DSN matches the #271 "isolated by default" posture.

## Amendment 1 (2026-08-07) — the other telemetry vendor gets the same URL rule, from the same function

**PostHog was never scrubbed.** `lib/analytics.ts` initialised it with `capture_pageview: true` /
`capture_pageleave: true` and no property hook at all, so every URL PostHog assembles from
`window.location` went out verbatim. The vendor-portal token IS a URL path segment
(`/portal/{token}`) and it is a **bearer credential** — whoever holds the string can upload to that
link. So the one page in the product a non-customer touches was shipping that credential to a
third-party analytics store on every event, while the decision above had been redacting the *exact
same string* for Sentry since #356 and `robots.ts` was disallowing `/portal/` to keep it out of
third-party indexes. Found while implementing [ADR 0054](0054-portal-gives-notice-at-collection.md)
/ [#404](https://github.com/neboxdev/complidrop/issues/404), which added the first out-link from
that page — so `document.referrer` on a middle-click made it a `$referrer` too.

### The rule is IMPORTED, not mirrored

`initAnalytics` passes a `before_send` hook that rewrites URL-valued properties through
**`sanitizeUrl` from `lib/sentry/scrub.ts`** — the same function, imported. A second copy of a
redaction regex is the drift this repo refuses elsewhere (the `ContactEmail` ↔ `contact-email.ts`
mirror exists with a shared corpus for exactly this reason, [ADR 0038](0038-vendor-contact-email-mirrored-validation.md)),
and it would be worse here: the two vendors would disagree about what a secret is. `scrub.ts`
imports `@sentry/nextjs` for **types only**, so nothing of the Sentry runtime is pulled into the
analytics bundle.

`before_send`, not `sanitize_properties`. The installed SDK marks `sanitize_properties`
`@deprecated`, logs an error on **every** event that uses it, and hands the hook `properties`
alone — while `$set_once`, a sibling of `properties` on the wire, is where the `$initial_*` family
rides. `before_send` receives the whole assembled `CaptureResult`.

### Which properties, and how they were established

By **observation, not documentation**: `analytics.test.ts` drives the real SDK in jsdom, intercepts
the ingest requests with MSW, gunzips them and asserts on the bytes that would have left the
browser. That found `$session_entry_url` / `$session_entry_pathname` / `$session_entry_referrer`,
which were in nothing we started from, alongside the expected `$current_url` / `$pathname` /
`$referrer` and the `$set_once` trio `$initial_current_url` / `$initial_pathname` /
`$initial_referrer`.

The match is therefore a **substring rule on the key** (`url` | `path` | `referrer` | `href`, applied
recursively so autocapture's nested `attr__href` is covered), not an enumeration: the SDK grows
these by family and a future `$…_url` is covered the day it ships. Over-matching is harmless —
`sanitizeUrl` leaves a non-portal URL alone, and dashed GUIDs (document / vendor / org ids) survive
its opaque-token net by design — while under-matching is the bug. `$host` / `$referring_domain`
carry no path and so cannot carry the token.

### What stays open (recorded, not overlooked)

The **`/flags` request** builds its `person_properties` bag straight from persistence and does not
pass through `before_send`, so it can still carry a raw `$initial_current_url`. Verified live, not
inferred. It is bounded and it is a different population: it fires only after `identify()`, which
the portal never calls (there is no session there), so the exposed value is an **identified
customer's own** initial URL — reachable when a customer follows their own portal link and then
signs in from the same browser — never a stranger's credential.

Closing it means `advanced_disable_flags: true`, which the SDK documents as also disabling remote
configuration (web vitals, surveys, and anything else configured in PostHog's own settings rather
than in code). Nothing in `frontend/` reads a feature flag today, but that is a product/analytics
decision with a visible blast radius and it is **not** what #404 reported, so it is recorded here
rather than taken silently. The test file states the same boundary where it scopes its assertions
to the ingest endpoint, so the scope is visible at the pin as well as in the record.

Unchanged: PostHog still gates on `NEXT_PUBLIC_POSTHOG_KEY` presence alone (this ADR's "Gate only
on DSN presence (PostHog-style)" alternative is about *Sentry's* stricter gate and is not
reopened), and this amendment discloses nothing new to a user — [ADR 0054](0054-portal-gives-notice-at-collection.md)'s
notice already says the page uses analytics cookies.

## References

- **Tickets:** [#356](https://github.com/neboxdev/complidrop/issues/356) (this feature),
  [#404](https://github.com/neboxdev/complidrop/issues/404) (Amendment 1 — the PostHog half,
  found while shipping the portal notice-at-collection).
- **Related ADRs:** [0035](0035-standing-cleanup-tooling-gates.md) (supersedes its
  `@sentry/nextjs` kept-but-ignored knip sub-decision),
  [0034](0034-dev-environment-isolation-and-boot-banner.md) /
  [#271](https://github.com/neboxdev/complidrop/issues/271) (dev isolation posture this mirrors).
- **Error-copy policy:** [#77](https://github.com/neboxdev/complidrop/issues/77),
  [#254](https://github.com/neboxdev/complidrop/issues/254).
- **Code:** `frontend/src/lib/sentry/{scrub,options,build}.ts`,
  `frontend/src/instrumentation.ts`, `frontend/src/instrumentation-client.ts`,
  `frontend/src/app/global-error.tsx`, `frontend/next.config.ts`; Amendment 1 —
  `frontend/src/lib/analytics.ts` (imports `sanitizeUrl`).
- **Tests:** `frontend/src/lib/sentry/scrub.test.ts`; Amendment 1 —
  `frontend/src/lib/analytics.test.ts` (drives the real SDK and asserts on the decompressed
  ingest bodies).
- **Env:** `NEXT_PUBLIC_SENTRY_DSN` (public DSN; absence ⇒ no-op), optional
  `NEXT_PUBLIC_SENTRY_ENVIRONMENT` (tag; defaults to `NODE_ENV`) and
  `NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE` (default `0`), plus the build-time
  source-map trio `SENTRY_AUTH_TOKEN` / `SENTRY_ORG` / `SENTRY_PROJECT` (server-only, never
  committed; absent ⇒ upload skipped, build still succeeds).
