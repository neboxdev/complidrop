import type { Breadcrumb, Event, EventHint } from "@sentry/nextjs";

/**
 * PII / secret scrubber for Sentry events (ADR 0036).
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
 * Boundary (see ADR 0036 "Negative"): we control what our own code attaches to
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

const EMAIL_RE = /[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/g;
// JWTs (the cd_session / cd_refresh cookies are JWTs): three base64url segments.
const JWT_RE = /\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g;
const BEARER_RE = /\bBearer\s+[A-Za-z0-9._~+/=-]+/gi;
// Opaque high-entropy strings (Stripe keys, base64 tokens, hex secrets): 32+
// contiguous alphanumerics/underscore. `-` is deliberately EXCLUDED from the
// class so a dashed GUID (a document / vendor / org id — an identifier, not a
// secret) is split into sub-32 segments and survives, keeping errors triageable.
// Portal tokens in URLs are handled deterministically by sanitizeUrl's path
// redaction, not this net. Applied ONLY to free text (see redactString).
const LONG_TOKEN_RE = /\b[A-Za-z0-9_]{32,}\b/g;

const MAX_DEPTH = 12;

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
  return value
    .replace(JWT_RE, "[redacted-jwt]")
    .replace(BEARER_RE, "Bearer [redacted]")
    .replace(EMAIL_RE, "[redacted-email]");
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
  return lowerKey === "url" || lowerKey.endsWith("url") || lowerKey === "href";
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
        if (keyIsSensitive(name)) delete req.headers[name];
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
    for (const [key, val] of Object.entries(event.user)) {
      if (keyIsSensitive(key)) {
        (event.user as Record<string, unknown>)[key] = REDACTED;
      } else if (typeof val === "string") {
        (event.user as Record<string, unknown>)[key] = redactPiiText(val);
      }
    }
  }

  // --- free-text surfaces --------------------------------------------------
  if (typeof event.message === "string") {
    event.message = redactString(event.message);
  }
  if (event.exception?.values) {
    for (const ex of event.exception.values) {
      if (typeof ex.value === "string") ex.value = redactString(ex.value);
      // Stack frames (file paths / function names) are intentionally left
      // intact — they carry no user PII and are required for symbolication.
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
