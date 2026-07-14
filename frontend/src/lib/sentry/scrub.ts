import type { Breadcrumb, Event, EventHint } from "@sentry/nextjs";

/**
 * PII / secret scrubber for Sentry events (ADR 0037).
 *
 * CompliDrop handles certificates of insurance, vendor and user data, email
 * addresses, public vendor-portal tokens, and httpOnly auth cookies
 * (`cd_session` / `cd_refresh`, both JWTs). NONE of that may ever leave the
 * browser/server inside a Sentry event. `Sentry.init` runs with
 * `sendDefaultPii: false`; this module is the second, explicit line of defence:
 * `beforeSend` / `beforeSendTransaction` (wired in `./options`) pass every event
 * through {@link scrubEvent} before it is transmitted.
 *
 * The functions here are PURE and exported so `scrub.test.ts` can pin the
 * redaction contract directly — feeding events that carry a cookie, an auth /
 * portal token, an email, and document field text, and asserting every one is
 * removed or redacted.
 *
 * Boundary (see ADR 0037 "Negative"): we control what our own code attaches to
 * Sentry and never hand it raw document field values. The vectors this scrubber
 * closes are the ones the SDK can populate automatically — request bodies,
 * fetch/xhr breadcrumbs (URLs + bodies), headers, cookies, the error message —
 * plus a regex net for emails / JWTs / opaque high-entropy tokens that catches
 * a credential embedded in otherwise-free text.
 */

export const REDACTED = "[redacted]";

// Substrings (matched case-insensitively, anywhere in a key name) that mark an
// object key whose VALUE must be dropped wholesale regardless of its content —
// e.g. a `password` too short to trip any regex, or an `x-portal-token` header.
const SENSITIVE_KEY_PARTS = [
  "cookie",
  "set-cookie",
  "authorization",
  "token",
  "secret",
  "password",
  "passwd",
  "pwd",
  "apikey",
  "api_key",
  "api-key",
  "jwt",
  "credential",
  "bearer",
  "email",
  "portal",
  "csrf",
  "ssn",
] as const;

// Body-bearing data keys (fetch/xhr request+response bodies, console args).
// These can carry document field text or credentials verbatim, so the whole
// value is dropped rather than regex-scanned.
const BODY_KEYS: readonly string[] = [
  "request_body",
  "response_body",
  "body",
  "payload",
  "arguments",
];

// Bounded quantifiers (RFC-ish caps) keep this LINEAR. An unbounded local-part
// `+` backtracks quadratically over a long @-less run of class chars (e.g. a big
// JSON blob embedded in an error message), freezing the main thread inside
// beforeSend — a self-inflicted ReDoS. Bounding every segment removes that shape.
const EMAIL_RE = /[a-zA-Z0-9._%+-]{1,64}@[a-zA-Z0-9.-]{1,255}\.[a-zA-Z]{2,24}/g;
// JWTs (the cd_session / cd_refresh cookies are JWTs): three base64url segments.
const JWT_RE = /\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g;
const BEARER_RE = /\bBearer\s+[A-Za-z0-9._~+/=-]+/gi;
// US SSN — COIs and HR-adjacent uploads can carry one. Anchored + fixed-width,
// so it's linear and applied even in the mild metadata net (it's unambiguous PII).
const SSN_RE = /\b\d{3}-\d{2}-\d{4}\b/g;
// Opaque high-entropy strings (Stripe keys, base64 tokens, hex secrets): 32+
// contiguous alphanumerics/underscore. `-` is deliberately EXCLUDED from the
// class so a dashed GUID (a document / vendor / org id — an identifier, not a
// secret) is split into sub-32 segments and survives, keeping errors triageable.
// Portal tokens in URLs are handled deterministically by sanitizeUrl's path
// redaction, not this net. Applied ONLY to free text (see redactString).
const LONG_TOKEN_RE = /\b[A-Za-z0-9_]{32,}\b/g;

const MAX_DEPTH = 12;

// Hard cap on the string length the regexes scan. Emails / JWTs / Bearer tokens /
// SSNs are all short; nothing legitimate needs more. The SDK applies no default
// `maxValueLength` before beforeSend, so an arbitrarily large `error.message` /
// `extra` value can reach the scrubber — this bounds the worst case AND drops
// (rather than transmits) the unscanned overflow tail.
const MAX_SCRUB_STRING = 8192;

function keyIsSensitive(key: string): boolean {
  const k = key.toLowerCase();
  return SENSITIVE_KEY_PARTS.some((part) => k.includes(part));
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Redact unambiguous PII / credentials (emails, JWTs, `Bearer …`) WITHOUT the
 * opaque-token net — safe for structured SDK metadata (`contexts`, `tags`,
 * span data) where load-bearing high-entropy IDs (Sentry `event_id`,
 * `trace_id`, `span_id`) live and must NOT be shredded.
 */
export function redactPiiText(value: string): string {
  const capped =
    value.length > MAX_SCRUB_STRING
      ? `${value.slice(0, MAX_SCRUB_STRING)}...[truncated]`
      : value;
  return capped
    .replace(JWT_RE, "[redacted-jwt]")
    .replace(BEARER_RE, "Bearer [redacted]")
    .replace(EMAIL_RE, "[redacted-email]")
    .replace(SSN_RE, "[redacted-ssn]");
}

/**
 * Aggressive redaction for FREE-TEXT fields (error messages, `event.message`,
 * the app-controlled `extra` bag, and URLs): {@link redactPiiText} plus the
 * opaque-high-entropy-token net. Not used on SDK metadata.
 */
export function redactString(value: string): string {
  return redactPiiText(value).replace(LONG_TOKEN_RE, "[redacted-token]");
}

/**
 * Strip a vendor-portal token and any token/email-bearing query parameter from
 * a URL, then apply the free-text net. The `/portal/{token}` and
 * `/api/portal/{token}` path redaction is deterministic (independent of the
 * token's charset/length), so it does not rely on the regex backstop.
 */
export function sanitizeUrl(url: string): string {
  let out = url.replace(/(\/(?:api\/)?portal\/)[^/?#]+/gi, `$1${REDACTED}`);
  // Redact values of query params whose NAME implies a secret (token, email,
  // key, signature/sig — the last covers Azure blob SAS `sig=`, reset/verify
  // `?token=`, and similar).
  out = out.replace(
    /([?&][^=&#]*(?:token|email|secret|key|signature|sig|password|portal)[^=&#]*=)[^&#]*/gi,
    `$1${REDACTED}`,
  );
  return redactString(out);
}

function redactDeep(
  value: unknown,
  strFn: (s: string) => string,
  depth: number,
  seen: WeakSet<object>,
): unknown {
  if (depth > MAX_DEPTH) return REDACTED; // bound pathological / cyclic nesting
  if (typeof value === "string") return strFn(value);
  if (Array.isArray(value)) {
    return value.map((item) => redactDeep(item, strFn, depth + 1, seen));
  }
  if (isPlainRecord(value)) {
    if (seen.has(value)) return "[circular]";
    seen.add(value);
    const out: Record<string, unknown> = {};
    for (const [key, val] of Object.entries(value)) {
      out[key] = keyIsSensitive(key)
        ? REDACTED
        : redactDeep(val, strFn, depth + 1, seen);
    }
    return out;
  }
  // numbers / booleans / null / undefined pass through unchanged
  return value;
}

function isUrlKey(lowerKey: string): boolean {
  // `from` / `to` are the navigation-breadcrumb path fields the browser SDK emits
  // ({ category: "navigation", data: { from, to } }); a SPA route to
  // /portal/{token} would otherwise leak the token there. Running a non-URL value
  // (e.g. an email "from") through sanitizeUrl is harmless — it just redacts it.
  return (
    lowerKey === "url" ||
    lowerKey.endsWith("url") ||
    lowerKey === "href" ||
    lowerKey === "from" ||
    lowerKey === "to"
  );
}

/**
 * Scrub a `data` record (Sentry breadcrumb `data` or transaction span `data`):
 * drop body-bearing + sensitive-named values wholesale, path-sanitize URL
 * values, and mild-redact everything else (so trace/span IDs survive).
 */
function scrubDataRecord(data: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, val] of Object.entries(data)) {
    const lower = key.toLowerCase();
    if (keyIsSensitive(key) || BODY_KEYS.includes(lower)) {
      out[key] = REDACTED;
    } else if (isUrlKey(lower) && typeof val === "string") {
      out[key] = sanitizeUrl(val);
    } else {
      out[key] = redactDeep(val, redactPiiText, 1, new WeakSet());
    }
  }
  return out;
}

function scrubBreadcrumb(breadcrumb: Breadcrumb): Breadcrumb {
  const out: Breadcrumb = { ...breadcrumb };
  if (typeof out.message === "string") out.message = redactString(out.message);
  if (isPlainRecord(out.data)) out.data = scrubDataRecord(out.data);
  return out;
}

/**
 * Scrub a Sentry event in place and return it. Generic over `ErrorEvent` /
 * `TransactionEvent` so the one function serves both `beforeSend` and
 * `beforeSendTransaction`.
 */
export function scrubEvent<T extends Event>(event: T): T {
  // --- request: cookies, body, sensitive headers, URL/query ----------------
  if (event.request) {
    const req = event.request;
    delete req.cookies; // cd_session / cd_refresh and any other cookie
    delete req.data; // request body — may carry document fields / credentials
    delete req.env; // server env injected into request context
    delete req.query_string; // can carry tokens/emails; low diagnostic value
    if (req.headers) {
      for (const name of Object.keys(req.headers)) {
        // Drop sensitive-named headers (cookie, authorization, x-portal-token);
        // URL-sanitize the survivors (sanitizeUrl chains the free-text net, so
        // credential redaction is preserved) so that neither a credential in a
        // benign-named header (e.g. a custom header echoing a JWT) nor a
        // portal-token URL in a URL-valued header (e.g. Referer:
        // …/portal/{token}) can slip through. Running a non-URL header value
        // through sanitizeUrl is harmless — it just redacts it. The server
        // runtime can attach ARRAY-valued headers despite the string-only SDK
        // type (Node repeats multi-value headers), so sanitize each element
        // rather than skipping non-strings wholesale.
        if (keyIsSensitive(name)) {
          delete req.headers[name];
        } else {
          const value: unknown = req.headers[name];
          if (typeof value === "string") {
            req.headers[name] = sanitizeUrl(value);
          } else if (Array.isArray(value)) {
            req.headers[name] = value.map((item) =>
              typeof item === "string" ? sanitizeUrl(item) : item,
            ) as unknown as string;
          }
        }
      }
    }
    if (typeof req.url === "string") req.url = sanitizeUrl(req.url);
  }

  // --- user: drop direct PII, keep a non-PII id ----------------------------
  if (event.user) {
    delete event.user.email;
    delete event.user.username;
    delete event.user.ip_address;
    delete event.user.geo;
    // Deep-redact the rest (sensitive-named keys + nested objects + string PII)
    // so a nested custom user object can't carry an email/token past the scrubber.
    event.user = redactDeep(event.user, redactPiiText, 0, new WeakSet()) as typeof event.user;
  }

  // --- free-text surfaces --------------------------------------------------
  if (typeof event.message === "string") {
    event.message = redactString(event.message);
  }
  // transaction: the event's transaction NAME. The App Router instrumentation
  // names it `parameterizedPathname ?? pathname` — falling back to the RAW
  // pathname whenever route parameterization fails — and the initial pageload
  // transaction is always named from raw window.location.pathname (only client
  // navigations are parameterized). Scope data then copies the name onto error
  // events unconditionally, so `/portal/{token}` can arrive here verbatim on
  // BOTH error and transaction events. sanitizeUrl's deterministic path
  // redaction removes the token regardless of its charset.
  if (typeof event.transaction === "string") {
    event.transaction = sanitizeUrl(event.transaction);
  }
  // logentry: populated by a parameterized captureMessage — its interpolated
  // params are exactly the dynamic data most likely to carry an email / id.
  if (event.logentry) {
    if (typeof event.logentry.message === "string") {
      event.logentry.message = redactString(event.logentry.message);
    }
    if (Array.isArray(event.logentry.params)) {
      event.logentry.params = event.logentry.params.map((p) =>
        redactDeep(p, redactString, 1, new WeakSet()),
      );
    }
  }
  if (event.exception?.values) {
    for (const ex of event.exception.values) {
      if (typeof ex.value === "string") ex.value = redactString(ex.value);
      // Scrub captured local variables and source context. On the Node runtime
      // the default localVariablesIntegration + contextLinesIntegration populate
      // frame.vars / context_line / pre_context / post_context, which CAN hold a
      // decoded JWT, an email, a portal token, or a document field value — the
      // onRequestError server path routes straight into these. Function names /
      // file paths (the frame itself) stay intact for symbolication.
      for (const frame of ex.stacktrace?.frames ?? []) {
        const f = frame as Record<string, unknown>;
        if (isPlainRecord(f.vars)) {
          f.vars = redactDeep(f.vars, redactString, 1, new WeakSet());
        }
        if (typeof f.context_line === "string") {
          f.context_line = redactString(f.context_line);
        }
        for (const ctxKey of ["pre_context", "post_context"] as const) {
          const lines = f[ctxKey];
          if (Array.isArray(lines)) {
            f[ctxKey] = lines.map((l) => (typeof l === "string" ? redactString(l) : l));
          }
        }
      }
    }
  }

  // --- breadcrumbs ---------------------------------------------------------
  if (event.breadcrumbs) {
    event.breadcrumbs = event.breadcrumbs.map(scrubBreadcrumb);
  }

  // --- structured bags -----------------------------------------------------
  // `extra` is the app-controlled free-form bag → aggressive net.
  if (event.extra) {
    event.extra = redactDeep(
      event.extra,
      redactString,
      0,
      new WeakSet(),
    ) as typeof event.extra;
  }
  // `contexts` / `tags` hold SDK metadata (trace_id, event_id, runtime, …) →
  // mild net so PII/JWTs are still removed but identifiers survive.
  if (event.contexts) {
    event.contexts = redactDeep(
      event.contexts,
      redactPiiText,
      0,
      new WeakSet(),
    ) as typeof event.contexts;
    // The trace context's `data` bag is root-span data (`url` / `http.url` …).
    // Give its URL-valued keys the same path sanitization breadcrumb/span data
    // gets: the mild net above is entropy-blind by design, so a
    // `/portal/{token}` URL would otherwise ride through it intact.
    const trace = event.contexts.trace;
    if (trace && isPlainRecord(trace.data)) {
      trace.data = scrubDataRecord(trace.data);
    }
  }
  if (event.tags) {
    event.tags = redactDeep(
      event.tags,
      redactPiiText,
      0,
      new WeakSet(),
    ) as typeof event.tags;
  }

  // --- transaction spans ---------------------------------------------------
  if (event.spans) {
    for (const span of event.spans as unknown as Array<Record<string, unknown>>) {
      if (typeof span.description === "string") {
        span.description = redactString(span.description);
      }
      if (isPlainRecord(span.data)) {
        span.data = scrubDataRecord(span.data);
      }
    }
  }

  return event;
}

/**
 * Cross-reference a frontend Sentry event with the backend request that caused
 * it: when the captured error is the api.ts `ApiError` (duck-typed — no import,
 * so this stays runtime-agnostic for the server/edge configs), copy its
 * server-minted `correlationId` onto an event tag. The correlation id is a
 * server-generated identifier, not PII, so it is safe to send.
 *
 * Called AFTER {@link scrubEvent} in `beforeSend` so the tag is never itself
 * passed through the redactor (a long correlation id could otherwise trip the
 * opaque-token net).
 */
export function tagCorrelationId(event: Event, hint?: EventHint): void {
  const original = hint?.originalException;
  if (!original || typeof original !== "object") return;
  const correlationId = (original as { correlationId?: unknown }).correlationId;
  if (typeof correlationId === "string" && correlationId.length > 0) {
    event.tags = { ...event.tags, correlation_id: correlationId };
  }
}
