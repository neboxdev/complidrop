@AGENTS.md

# CompliDrop frontend — conventions

Next.js 16 App Router + React 19 + Tailwind 4 + shadcn/ui; TanStack Query, React Hook Form + Zod, sonner toasts. These rules are repo law for everything under `frontend/`; the backend/domain invariants live in the root [CLAUDE.md](../CLAUDE.md).

## Error-message policy — never HTTP jargon

User-facing error toasts and error-card copy come from the server's `error.message` field when present, otherwise the `GENERIC_FALLBACK_MESSAGE` exported from [src/lib/api.ts](src/lib/api.ts) ("Something went wrong. Try again."). NEVER surface raw HTTP `res.statusText` ("Bad Gateway"), interpolated status codes (`Export failed (502)`), or browser TypeErrors ("Failed to fetch") — HTTP jargon is hostile to SMB users.

- The `api.*` client enforces this for every envelope-returning request via `fetchOrFriendlyThrow` (#77).
- Binary endpoints (file streams, blob downloads) go through `api.getBlob` (#254) — same cookie transport, coalesced silent 401-refresh, and friendly-error mapping. Do NOT hand-roll a bare `fetch` for downloads.
- Any residual bare-fetch site must IMPORT `GENERIC_FALLBACK_MESSAGE` and emit it on every error path.
- Tests assert `not.toHaveTextContent(/bad gateway/i)` / `not.toMatch(/typeerror/i)` style invariants to catch leaks (see `src/lib/api.test.ts`, `src/app/(dashboard)/documents/page.test.tsx`).

## Component + label rules (lint-enforced, CI-blocking)

- Never declare a React component inside another component's render body — hoist to module scope. `react-hooks/static-components` blocks CI; inline components reset state every parent render. Canonical fix: the `SkeletonRow` pattern in [register-form.tsx](src/app/(auth)/register/register-form.tsx) (#73).
- Every form `<label>` associates with its control (`htmlFor` → matching `id`, or nesting). `jsx-a11y/label-has-associated-control` (configured in [eslint.config.mjs](eslint.config.mjs)) blocks CI on the static shape; [src/test/forms.test.tsx](src/test/forms.test.tsx) pins the runtime wire-up (`getByLabelText` resolves) per enumerated form — **add an entry there when introducing a new form**. If a shadcn-style `<Label>` wrapper is ever adopted, extend the rule's `labelComponents` option (#76, #131).

## Testid policy (#92)

Prefer accessible-text selectors (`getByText` / `getByRole` / `getByLabelText`). Reach for `data-testid` only when text selectors are ambiguous-by-design (e.g. a status badge whose label collides with section copy). Placement rules:

1. **Leaf, not wrapper.** Put the testid on the element whose state is asserted (badge / input / row), never on a wrapper — wrapper testids force `within()` traversal and re-introduce the brittleness.
2. **Compound elements: tag the asserted substring.** When a badge renders status plus incidental data (`Pending · 87%`), wrap a stable nested `<span>` around the asserted substring — asserting on the outer badge fails on the suffix.
3. **One-of-its-kind per page.** Flat `{noun}-status` names are for detail pages where one of each exists. For lists/repeated rows use `getByRole('row', …)` + `within(row)`, or row-scoped ids (`extraction-status-{rowId}`). Never let `getByTestId` resolve to N elements.

Canonical detail-page shape: `extraction-status` / `compliance-status` on [documents/[id]/page.tsx](src/app/(dashboard)/documents/[id]/page.tsx). The documents LIST page deliberately stays on accessible-text selectors per rule 3.

## URL filter state ([ADR 0039](../docs/adr/0039-documents-url-source-of-truth-overlay.md), #370)

The documents list derives filters from the URL — no second filter-state copy. Which URL copy is current depends on how the URL moved (History-API writes and router navigations update `window.location` and `useSearchParams()` in OPPOSITE orders), so `useSearchParams()` is the base truth with the page's own writes overlaid as a QUEUE until the router echoes them back. Preferring `window.location` renders the previous route's query on deep links; preferring the raw hook makes filter picks flash backwards. [src/test/navigation.ts](src/test/navigation.ts) models both orderings deliberately — both shipped green once under a wrong model. Do not "simplify" the overlay or the harness.

## Telemetry PII ([ADR 0037](../docs/adr/0037-frontend-sentry-pii-scrubbing-and-gating.md), #356 / #404)

No-op unless `NEXT_PUBLIC_SENTRY_DSN` is set AND `NODE_ENV=production` (dev stays telemetry-silent). `sendDefaultPii: false` + the unit-tested scrubber ([src/lib/sentry/scrub.ts](src/lib/sentry/scrub.ts)) strips cookies (`cd_session`/`cd_refresh`), auth/portal tokens, emails, and request/response bodies; `/portal/{token}` paths are redacted. **Never hand raw document field values or user content to `captureException`/`setExtra`/`setContext`** — the scrubber catches the SDK's automatic capture, not prose we attach (a new such call is a review finding). Errors tie to the backend via the `correlation_id` tag (`ApiError.correlationId`). Session Replay OFF; `tracesSampleRate` defaults 0. Source-map upload gated on `SENTRY_AUTH_TOKEN` (build succeeds without it).

**PostHog takes the same URL rule** (Amendment 1, #404): `/portal/{token}` is a bearer credential in a path, and `capture_pageview` put it in `$current_url` / `$pathname` / `$referrer` / `$session_entry_*` / the `$set_once` `$initial_*` trio. `initAnalytics`'s `before_send` **imports** `sanitizeUrl` from [src/lib/sentry/scrub.ts](src/lib/sentry/scrub.ts) — never a second copy of the regex; two vendors disagreeing about what a secret is is the bug. Keys match by SUBSTRING (`url`/`path`/`referrer`/`href`, recursive) and object KEYS are sanitized too (`sanitizeUrlKey` — heatmaps buffers by `location.href`, so the URL arrives as a key); the set was established by driving the real SDK ([src/lib/analytics.test.ts](src/lib/analytics.test.ts) intercepts and gunzips EVERY request, not only `/e/`), not from docs. `before_send` not `sanitize_properties` — the latter is deprecated and logs an error per event (it DOES also receive `$set_once`; the old "never sees `$set_once`" claim here was false).

**The portal route initialises no analytics at all** (Amendment 2, #404 round 2): `Providers` gates `initAnalytics()` on the pathname, because `before_send` only wraps `capture()` and the SDK opens channels it cannot reach — `/flags` was shipping an ANONYMOUS visitor's raw `$initial_current_url` at init, no `identify()` involved, which is what Amendment 1 wrongly recorded as an identified-customer-only residue. `advanced_disable_flags: true` + `capture_heatmaps: false` close those two everywhere; the route gate is the invariant that covers the ones nobody has enumerated. Redaction is still load-bearing for every OTHER route (a vendor reaching `/privacy` from the portal carries the tokenized URL in `document.referrer`). The portal notice + `/privacy` say the page sets no cookies and is not measured (ADR 0054 Amendment 1); both claims are pinned, so reword neither without the code.

**The gate is per BROWSING CONTEXT, not per pathname** (Amendment 3, #404 round 3). posthog-js is a module-global singleton and nothing de-initialises or opts it out, so a pathname-only gate lapses the moment the SDK goes live anywhere in the same JS context — and the flow that does it is the notice's OWN policy link: `<Link>` → `/privacy` → `initAnalytics()` runs → Back, and the live SDK keeps running on `/portal/{token}` — `$pageleave` at tab close (bound to `pagehide` at init), autocapture on its clicks (`advanced_disable_flags: true` SKIPS `Autocapture.isEnabled`'s wait-for-the-server guard, so it runs on client config alone), and a `localStorage+cookie` writer — i.e. exactly what the shipped sentence denies, for the vendor who followed the link. BOTH halves ship, the same belt-and-braces shape as the heatmaps channel: [portal/[token]/page.tsx](src/app/portal/[token]/page.tsx)'s `PrivacyPolicyLink` is a plain `<a href="/privacy" rel="noreferrer">` (a FULL DOCUMENT LOAD, so the policy is read in a new JS context — do not "fix" it back to `next/link`, and `@next/next/no-html-link-for-pages` is reconciled at the call site, not silenced), and [providers.tsx](src/lib/providers.tsx) keeps a module-scope `contextHeldCredentialInUrl`. **The cost is recorded, not accidental:** a tab that has held the credential is unmeasured for the rest of its session and the vendor's `/privacy` visit with it — the earlier "measured from that click on" is FALSE. Pinned by [src/lib/providers.test.tsx](src/lib/providers.test.tsx): no PostHog request whatsoever from that route, each case in its own browsing context (`vi.resetModules()` + dynamic import), the round trip and the sticky half both asserted, and the dashboard case LAST because posthog-js's own singleton outlives `resetModules()`.

## Mirrors of backend contracts

- [src/lib/contact-email.ts](src/lib/contact-email.ts) ↔ `Services/ContactEmail.cs` ([ADR 0038](../docs/adr/0038-vendor-contact-email-mirrored-validation.md)): add cases to the shared corpus `api/CompliDrop.Api.Tests/SharedFixtures/contact-email-cases.json`, never to one suite. Keep the explicit `\uXXXX` blank ranges and the linear edge scan — `\s` and regex trims are load-bearing rejections, not style.
- [src/lib/document-types.ts](src/lib/document-types.ts) is the KNOWN-UNPINNED mirror of `Services/CanonicalDocumentTypes.cs` ([ADR 0045](../docs/adr/0045-canonical-document-type-vocabulary.md)) — no test can reach across, so sync it by hand whenever the vocabulary changes.

## Before pushing

Run `npm test`, `npm run build` and `npm run knip`. The CI `build` job runs knip (unused exports) as a merge gate that vitest/tsc/lint miss — a newly-exported-but-internal-only helper reddens CI (#396 lesson).
