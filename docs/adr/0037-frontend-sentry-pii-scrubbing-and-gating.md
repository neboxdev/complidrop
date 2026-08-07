# 0037. Frontend Sentry: PII scrubbing, sampling, and dev gating

- **Status:** accepted (amended 2026-08-07 — see [Amendment 1](#amendment-1-2026-08-07--the-other-telemetry-vendor-gets-the-same-url-rule-from-the-same-function); amended 2026-08-07 — see [Amendment 2](#amendment-2-2026-08-07--the-portal-route-initialises-no-analytics-at-all), which **retracts Amendment 1 § What stays open**)
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

> **This subsection is WRONG and is retracted by [Amendment 2](#amendment-2-2026-08-07--the-portal-route-initialises-no-analytics-at-all).**
> It is kept verbatim because the mistake — accepting a residue on an unverified premise, and then
> recording that premise in four places where it told the next reviewer not to look — is the thing
> worth remembering. `/flags` fires at **init**, with no `identify()`, and the URL it exposes is an
> **anonymous vendor's bearer credential**. Do not cite anything below.

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

## Amendment 2 (2026-08-07) — the portal route initialises no analytics at all

### The retraction first

Amendment 1 accepted the `/flags` residue on this sentence:

> *"It is bounded and it is a different population: it fires only after `identify()`, which the
> portal never calls (there is no session there), so the exposed value is an **identified customer's
> own** initial URL … never a stranger's credential."*

**That is false in posthog-js 1.369.2, and it was false when it was written.** `_loaded()` ends with
`new RemoteConfigLoader(this).load()`; `load()` → `_onRemoteConfig()` calls
`featureFlags.ensureFlagsLoaded()` whenever the remote config does not say `hasFeatureFlags: false`
(including when the fetch fails, since `undefined !== false`), and `RemoteConfigLoader.refresh()`
re-issues it every 5 minutes. `identify()` appears in neither chain. The bag it posts is
`person_properties: { ...persistence.get_initial_props(), … }` — the `$initial_current_url` /
`$initial_pathname` / `$initial_referrer` written by `set_initial_person_info()` inside the FIRST
`capture()`, i.e. the anonymous portal `$pageview`, raw, because nothing there passes through
`before_send`. Driven in-repo before the fix, the body read
`"$initial_current_url":"http://localhost:3000/portal/{TOKEN}"` with no identify anywhere.

So the accepted residue was **every anonymous vendor's bearer credential**, on every visit and again
on every later one (it is persisted in `localStorage+cookie`) — the exact vector #404 exists to
close, not the bounded identified-customer case the decision was taken on. The same false boundary
had been copied into `.claude/reviewers.md`'s do-NOT-flag list, `frontend/CLAUDE.md`, the root
`CLAUDE.md` index line and the test docstring, where it instructed the next reviewer not to report a
live leak. **A wrong fact in a do-NOT-flag list is worse than no fact.** All five copies are
corrected; the subsection above is marked rather than deleted, because the lesson is the process one:
"verified live, not inferred" was written about a claim nobody had verified.

Amendment 1's closing line — *"this amendment discloses nothing new to a user — ADR 0054's notice
already says the page uses analytics cookies"* — is stale for the same reason, in the other
direction: the notice no longer says that, because the page no longer does it. See
[ADR 0054 Amendment 1](0054-portal-gives-notice-at-collection.md#amendment-1-2026-08-07--the-cookie-sentence-becomes-a-disclaimer-because-the-route-stopped-being-measured).

### What changed in code

Three layers, smallest to largest:

1. **`advanced_disable_flags: true`.** `_callFlagsEndpoint()` early-returns on `_shouldDisableFlags()`,
   so the channel is closed rather than redacted — redaction cannot reach a bag built straight from
   persistence. The cost is real and accepted: PostHog **remote configuration** goes with it (web
   vitals, surveys, and anything else configured in PostHog's settings rather than in code). Nothing
   in `frontend/` reads a feature flag today, verified by search rather than assumed.
2. **`capture_heatmaps: false`, plus keys sanitized in the walker.** The heatmaps extension ships in
   the default bundle and, with neither `capture_heatmaps` nor `enable_heatmaps` set, enables itself
   from the PostHog project's own dashboard toggle. It buffers by `window.location.href` and sends the
   map as `$heatmap_data` — the URL is an object **KEY**, and the walker rewrote values only, so the
   token left intact. `sanitizeUrlKey` is the correct general rule (only URL-shaped keys, so the
   opaque-token net cannot rename an ordinary property); the init flag is what survives someone
   rewriting the walker. Both, deliberately.
3. **`Providers` does not call `initAnalytics()` under `/portal/`.**

### Why (3), when (1) and (2) close what is known

Because the enumeration keeps growing. Round 1 shipped a reviewed fix; round 2 found two more
channels and named a third (`/i/v1/logs` — the console-log extension puts `location.href` in
`context.currentUrl`, remote-gated). Each is a promise renewed at the vendor's release cadence, and
`/portal/{token}` is the one route in this product where **the URL itself is the credential**:
everywhere else a leaked URL exposes an identifier, here it exposes upload authority. One invariant
that holds whatever the SDK does next beats N promises that each hold until it changes.

Gated on the **current pathname**, not on "has ever been on the portal": a vendor who follows the
notice's Privacy Policy link is on an ordinary route from that click on. Every other route is
unaffected.

**The cost, stated plainly: the founder loses PostHog analytics on the vendor-portal page.** Traffic,
conversion and drop-off on the upload surface become unmeasurable. That is a deliberate trade, not an
oversight.

**Rejected alternative — keep measuring the portal and redact each channel as it is discovered.** It
is what round 1 chose and what round 2 falsified: two live leaks shipped through a full review under
that model, and the record it produced actively discouraged the next reviewer from looking. It also
scales the wrong way — every posthog-js release is a re-audit of a third party's transport surface
against a credential.

### Layer (1)+(2) are not redundant

Redaction still matters, and the analytics suite still drives it. The token is not only in
`location.href`: the notice's own out-link means a vendor reaching `/privacy` carries the tokenized
URL in `document.referrer`, and a reminder mail can put one in any route's `$referrer`. (3) covers the
portal route; (1) and (2) cover the token wherever else it turns up.

### Pins

`frontend/src/lib/providers.test.tsx` asserts **no PostHog request at all** from `/portal/{token}`,
with a dashboard case beside it so the pin cannot pass on a broken key or a dead provider — it did,
until the SDK was imported dynamically (`posthog-js/lib/src/utils/globals.js` binds
`exports.fetch = global.fetch` at module-eval time, before MSW patches it, so a static import in a
test file sends every request past the interceptor). `analytics.test.ts` now records **every** PostHog
request rather than only `/e/` — scoping assertions to one endpoint is what hid this — and adds the
`$$heatmap` key case, the `$elements` / `$set` recursion cases, and a depth-cap probe.

### Two smaller corrections carried here

- **`sanitize_properties` is NOT handed `properties` alone.** `_calculate_set_once_properties` calls
  it a second time with the `$set_once` bag. The `before_send` choice is unchanged and still right —
  the hook is `@deprecated` and logs an error on every event that uses it — but the reason recorded in
  four places was half false, and this repo treats a false reason on file as a durable defect
  ([ADR 0054](0054-portal-gives-notice-at-collection.md) Option E's own rationale).
- **The walker's depth cap failed OPEN** while its comment claimed to mirror `sentry/scrub.ts`, which
  fails closed. Past the cap a value under a URL-bearing key is now `REDACTED`, and the limit matches
  the neighbour's 12. It is deliberately not the neighbour's blanket `return REDACTED`: outside a
  URL-bearing key this walker redacts nothing anyway, so blanking those would delete ordinary deep
  analytics for no privacy gain.

### What stays open (Amendment 2)

- **Everything PostHog measures on the OTHER routes still relies on redaction**, i.e. on the property
  set being complete. That set is established by driving the real SDK, not from docs, which is what
  found `$session_entry_*`; it is a smaller claim than round 1's because the credential-bearing route
  is out of scope entirely, but it is not zero.
- **Server-side.** This is a browser decision. `/api/portal/{token}` still receives the token (it must
  — it is the credential) and it appears in ordinary request logs; that is first-party and out of
  this ADR's scope.
- **Sentry is untouched** and still runs on the portal route. It sets no cookie, has Session Replay
  off, and `sanitizeUrl` has redacted `/portal/{token}` there since #356.

## References

- **Tickets:** [#356](https://github.com/neboxdev/complidrop/issues/356) (this feature),
  [#404](https://github.com/neboxdev/complidrop/issues/404) (Amendment 1 — the PostHog half,
  found while shipping the portal notice-at-collection; Amendment 2 — its round-2 review, which
  falsified Amendment 1's `/flags` boundary and took the route out of analytics entirely).
- **Related ADRs:** [0035](0035-standing-cleanup-tooling-gates.md) (supersedes its
  `@sentry/nextjs` kept-but-ignored knip sub-decision),
  [0034](0034-dev-environment-isolation-and-boot-banner.md) /
  [#271](https://github.com/neboxdev/complidrop/issues/271) (dev isolation posture this mirrors),
  [0054](0054-portal-gives-notice-at-collection.md) Amendment 1 (the portal copy that moved with
  Amendment 2 — the notice's cookie sentence became false in the other direction).
- **Error-copy policy:** [#77](https://github.com/neboxdev/complidrop/issues/77),
  [#254](https://github.com/neboxdev/complidrop/issues/254).
- **Code:** `frontend/src/lib/sentry/{scrub,options,build}.ts`,
  `frontend/src/instrumentation.ts`, `frontend/src/instrumentation-client.ts`,
  `frontend/src/app/global-error.tsx`, `frontend/next.config.ts`; Amendment 1 —
  `frontend/src/lib/analytics.ts` (imports `sanitizeUrl`); Amendment 2 —
  `frontend/src/lib/providers.tsx` (the pathname gate) and `analytics.ts`'s
  `advanced_disable_flags` / `capture_heatmaps` / `sanitizeUrlKey`.
- **Tests:** `frontend/src/lib/sentry/scrub.test.ts`; Amendment 1 —
  `frontend/src/lib/analytics.test.ts` (drives the real SDK and asserts on the decompressed
  ingest bodies, now on EVERY channel); Amendment 2 — `frontend/src/lib/providers.test.tsx`
  (no PostHog request whatsoever from `/portal/{token}`).
- **Env:** `NEXT_PUBLIC_SENTRY_DSN` (public DSN; absence ⇒ no-op), optional
  `NEXT_PUBLIC_SENTRY_ENVIRONMENT` (tag; defaults to `NODE_ENV`) and
  `NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE` (default `0`), plus the build-time
  source-map trio `SENTRY_AUTH_TOKEN` / `SENTRY_ORG` / `SENTRY_PROJECT` (server-only, never
  committed; absent ⇒ upload skipped, build still succeeds).
