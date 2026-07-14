/**
 * Pins the Sentry PII / secret scrubber (ADR 0037). CompliDrop handles COIs,
 * vendor/user data, emails, public portal tokens, and JWT auth cookies — none
 * of which may leave the browser/server in a Sentry event. These tests feed the
 * scrubber events carrying each of those vectors and assert every one is
 * removed or redacted before the event would be transmitted.
 */
import { describe, it, expect } from "vitest";
import type { Event, EventHint } from "@sentry/nextjs";
import {
  REDACTED,
  redactPiiText,
  redactString,
  sanitizeUrl,
  scrubEvent,
  tagCorrelationId,
} from "./scrub";

const SAMPLE_JWT = "eyJhbGciOiJI.eyJzdWIiOiIxMjM0.SflKxwRJSMeKKF2QT4";
const LONG_TOKEN = "PORTALtokenABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

describe("redactString (free-text net)", () => {
  it("redacts emails", () => {
    expect(redactString("ping owner@acme.com now")).toBe("ping [redacted-email] now");
  });

  it("redacts JWTs (cd_session / cd_refresh shape)", () => {
    expect(redactString(`token=${SAMPLE_JWT}`)).toBe("token=[redacted-jwt]");
  });

  it("redacts Bearer credentials", () => {
    expect(redactString("Authorization: Bearer abc123.def-456")).toBe(
      "Authorization: Bearer [redacted]",
    );
  });

  it("redacts opaque high-entropy tokens (portal token / API key)", () => {
    expect(redactString(`key ${LONG_TOKEN} end`)).toBe("key [redacted-token] end");
  });

  it("redacts US SSNs", () => {
    expect(redactString("ssn 123-45-6789 on file")).toBe("ssn [redacted-ssn] on file");
  });

  it("caps very long strings so the regex pass stays bounded (ReDoS guard)", () => {
    // A 200k @-less run would backtrack quadratically against an unbounded email
    // regex; the length cap + bounded quantifiers keep it fast, and the overflow
    // tail is dropped (not transmitted).
    const huge = "a".repeat(200_000);
    const start = performance.now();
    const out = redactString(huge);
    const elapsedMs = performance.now() - start;
    expect(out.length).toBeLessThan(9000);
    expect(out).toContain("[truncated]");
    expect(elapsedMs).toBeLessThan(1000);
  });

  it("leaves ordinary text and dashed GUIDs intact", () => {
    expect(redactString("upload failed at step 3")).toBe("upload failed at step 3");
    // A document id (dashed GUID) is split by word boundaries into sub-32 chunks
    // and must survive — it is not a credential.
    expect(redactString("doc 550e8400-e29b-41d4-a716-446655440000")).toBe(
      "doc 550e8400-e29b-41d4-a716-446655440000",
    );
  });
});

describe("redactPiiText (metadata net — preserves identifiers)", () => {
  it("redacts emails and JWTs", () => {
    expect(redactPiiText("a@b.com")).toBe("[redacted-email]");
    expect(redactPiiText(SAMPLE_JWT)).toBe("[redacted-jwt]");
  });

  it("does NOT shred high-entropy IDs (trace_id / event_id stay intact)", () => {
    const traceId = "0123456789abcdef0123456789abcdef"; // 32 hex
    expect(redactPiiText(traceId)).toBe(traceId);
  });
});

describe("sanitizeUrl", () => {
  it("redacts the vendor-portal token path segment", () => {
    const out = sanitizeUrl("https://www.complidrop.com/portal/SECRET_PORTAL_TOKEN_xyz");
    expect(out).toContain("/portal/[redacted]");
    expect(out).not.toContain("SECRET_PORTAL_TOKEN_xyz");
  });

  it("redacts the /api/portal/{token} path but keeps non-sensitive query params", () => {
    const out = sanitizeUrl("https://api.complidrop.com/api/portal/TOKVALUE?status=Expired");
    expect(out).toContain("/api/portal/[redacted]");
    expect(out).not.toContain("TOKVALUE");
    expect(out).toContain("status=Expired");
  });

  it("redacts token / email / signature query values, keeps the rest", () => {
    const out = sanitizeUrl("https://x/reset?token=abcd1234secret&email=v@x.com&page=2");
    expect(out).toContain("token=[redacted]");
    expect(out).not.toContain("abcd1234secret");
    expect(out).not.toContain("v@x.com");
    expect(out).toContain("page=2");
  });

  it("leaves a plain in-app URL unchanged", () => {
    expect(sanitizeUrl("https://www.complidrop.com/dashboard")).toBe(
      "https://www.complidrop.com/dashboard",
    );
  });
});

describe("scrubEvent — full event payload", () => {
  function makeEvent(): Event {
    return {
      message: "Upload failed for owner@acme.com",
      request: {
        url: "https://www.complidrop.com/portal/PORTAL_SECRET_TOKEN_123456789?email=v@x.com",
        method: "POST",
        cookies: { cd_session: SAMPLE_JWT, cd_refresh: "refreshvalue" },
        headers: {
          cookie: `cd_session=${SAMPLE_JWT}`,
          authorization: "Bearer SUPERSECRETTOKEN",
          "x-portal-token": "PORTALTOKENVALUE",
          "content-type": "application/json",
        },
        data: {
          insuredName: "Acme Catering LLC",
          policyNumber: "GL-99",
          coverage: "lots of document text",
        },
        query_string: "token=abc&x=1",
        env: { SECRET_VALUE: "shh" },
      },
      user: {
        id: "org_42",
        email: "owner@acme.com",
        ip_address: "1.2.3.4",
        username: "owner",
      },
      exception: { values: [{ type: "Error", value: "boom for owner@acme.com" }] },
      breadcrumbs: [
        {
          category: "fetch",
          message: "GET /api/portal/TOK for owner@acme.com",
          data: {
            url: "https://api.complidrop.com/api/portal/PORTALTOK?email=v@x.com",
            method: "GET",
            status_code: 500,
            request_body: '{"insuredName":"Acme Catering LLC"}',
            response_body: '{"coverage":"document text"}',
          },
        },
        {
          category: "console",
          message: "rendered page for owner@acme.com",
          data: { authToken: "abc123", note: "ok" },
        },
      ],
      extra: {
        documentText: "Holder owner@acme.com policy",
        apiKey: "SECRETKEY",
        token: LONG_TOKEN,
      },
      contexts: {
        trace: {
          trace_id: "0123456789abcdef0123456789abcdef",
          span_id: "abcdef0123456789",
        },
        custom: { email: "owner@acme.com", note: "fine" },
      },
      tags: { feature: "upload", userEmail: "owner@acme.com" },
    };
  }

  it("removes auth cookies, the request body, env, and query string", () => {
    const e = scrubEvent(makeEvent());
    expect(e.request?.cookies).toBeUndefined();
    expect(e.request?.data).toBeUndefined();
    expect(e.request?.env).toBeUndefined();
    expect(e.request?.query_string).toBeUndefined();
  });

  it("strips sensitive request headers but keeps benign ones", () => {
    const e = scrubEvent(makeEvent());
    const headers = e.request?.headers ?? {};
    expect(headers.cookie).toBeUndefined();
    expect(headers.authorization).toBeUndefined();
    expect(headers["x-portal-token"]).toBeUndefined();
    expect(headers["content-type"]).toBe("application/json");
  });

  it("sanitizes the request URL (portal token + email query)", () => {
    const e = scrubEvent(makeEvent());
    expect(e.request?.url).toContain("/portal/[redacted]");
    expect(e.request?.url).not.toContain("PORTAL_SECRET_TOKEN_123456789");
    expect(e.request?.url).not.toContain("v@x.com");
  });

  it("strips direct user PII but keeps a non-PII id", () => {
    const e = scrubEvent(makeEvent());
    expect(e.user?.id).toBe("org_42");
    expect(e.user?.email).toBeUndefined();
    expect(e.user?.ip_address).toBeUndefined();
    expect(e.user?.username).toBeUndefined();
  });

  it("redacts emails in the message and exception value (keeps the error readable)", () => {
    const e = scrubEvent(makeEvent());
    expect(e.message).toBe("Upload failed for [redacted-email]");
    const value = e.exception?.values?.[0]?.value ?? "";
    expect(value).toContain("boom for [redacted-email]");
    expect(value).not.toContain("owner@acme.com");
  });

  it("drops document field text carried in breadcrumb request/response bodies", () => {
    const e = scrubEvent(makeEvent());
    const fetchData = e.breadcrumbs?.[0]?.data ?? {};
    expect(fetchData.request_body).toBe(REDACTED);
    expect(fetchData.response_body).toBe(REDACTED);
    expect(fetchData.status_code).toBe(500); // benign diagnostic kept
    // The breadcrumb URL is path-sanitized and email-stripped.
    expect(String(fetchData.url)).toContain("/api/portal/[redacted]");
    expect(String(fetchData.url)).not.toContain("PORTALTOK");
    expect(String(fetchData.url)).not.toContain("v@x.com");
  });

  it("redacts emails in breadcrumb messages and token-named breadcrumb data", () => {
    const e = scrubEvent(makeEvent());
    expect(e.breadcrumbs?.[0]?.message).not.toContain("owner@acme.com");
    expect(e.breadcrumbs?.[1]?.message).toBe("rendered page for [redacted-email]");
    expect(e.breadcrumbs?.[1]?.data?.authToken).toBe(REDACTED);
    expect(e.breadcrumbs?.[1]?.data?.note).toBe("ok");
  });

  it("redacts sensitive-named keys + emails in the extra bag", () => {
    const e = scrubEvent(makeEvent());
    expect(e.extra?.apiKey).toBe(REDACTED);
    expect(e.extra?.token).toBe(REDACTED);
    // Free prose stays, but an embedded email inside it is still stripped.
    expect(String(e.extra?.documentText)).not.toContain("owner@acme.com");
  });

  it("redacts PII in contexts/tags WITHOUT shredding the trace id", () => {
    const e = scrubEvent(makeEvent());
    const trace = (e.contexts?.trace ?? {}) as Record<string, unknown>;
    expect(trace.trace_id).toBe("0123456789abcdef0123456789abcdef");
    const custom = (e.contexts?.custom ?? {}) as Record<string, unknown>;
    expect(custom.email).toBe(REDACTED);
    expect(custom.note).toBe("fine");
    expect(e.tags?.feature).toBe("upload");
    expect(e.tags?.userEmail).toBe(REDACTED);
  });

  it("scrubs transaction span descriptions and data", () => {
    const txn = {
      type: "transaction",
      spans: [
        {
          description: "GET https://x/api?email=a@b.com",
          data: { "http.url": "https://x/api/portal/SECRET?email=a@b.com", "http.status_code": 200 },
        },
      ],
    } as unknown as Event;
    const e = scrubEvent(txn);
    const span = (e.spans?.[0] ?? {}) as Record<string, unknown>;
    expect(String(span.description)).not.toContain("a@b.com");
    const data = span.data as Record<string, unknown>;
    expect(String(data["http.url"])).toContain("/api/portal/[redacted]");
    expect(String(data["http.url"])).not.toContain("a@b.com");
    expect(data["http.status_code"]).toBe(200);
  });

  it("does not throw on circular structures", () => {
    const cyclic: Record<string, unknown> = { name: "x" };
    cyclic.self = cyclic;
    const e = { extra: { cyclic } } as unknown as Event;
    expect(() => scrubEvent(e)).not.toThrow();
  });
});

describe("scrubEvent — review-hardening vectors (#356)", () => {
  it("scrubs captured stack-frame local vars + source context (Node localVariables/contextLines)", () => {
    const e = {
      exception: {
        values: [
          {
            type: "Error",
            value: "boom",
            stacktrace: {
              frames: [
                {
                  function: "loadDoc",
                  filename: "/app/route.ts",
                  vars: { token: "abc", contactEmail: "owner@acme.com", count: 3 },
                  context_line: "  const session = 'owner@acme.com';",
                  pre_context: ["before owner@acme.com"],
                  post_context: ["after 123-45-6789"],
                },
              ],
            },
          },
        ],
      },
    } as unknown as Event;
    const frame = (scrubEvent(e).exception?.values?.[0] as Record<string, unknown>)
      .stacktrace as { frames: Array<Record<string, unknown>> };
    const f = frame.frames[0];
    expect(f.function).toBe("loadDoc"); // symbolication data preserved
    const vars = f.vars as Record<string, unknown>;
    expect(vars.token).toBe(REDACTED); // sensitive-named key dropped
    expect(String(vars.contactEmail)).toBe(REDACTED); // "email" key dropped
    expect(vars.count).toBe(3);
    expect(String(f.context_line)).not.toContain("owner@acme.com");
    expect((f.pre_context as string[])[0]).not.toContain("owner@acme.com");
    expect((f.post_context as string[])[0]).toContain("[redacted-ssn]");
  });

  it("scrubs logentry message + interpolated params (parameterized captureMessage)", () => {
    const e = {
      logentry: {
        message: "User %s failed",
        params: ["owner@acme.com", "ssn 123-45-6789"],
      },
    } as unknown as Event;
    const out = scrubEvent(e);
    expect(out.logentry?.message).toBe("User %s failed");
    expect(out.logentry?.params?.[0]).toBe("[redacted-email]");
    expect(out.logentry?.params?.[1]).toBe("ssn [redacted-ssn]");
  });

  it("sanitizes navigation breadcrumb from/to URL paths (portal token leak)", () => {
    const e = {
      breadcrumbs: [
        {
          category: "navigation",
          data: { from: "/dashboard", to: "/portal/SECRETPORTALTOKEN123" },
        },
      ],
    } as unknown as Event;
    const data = scrubEvent(e).breadcrumbs?.[0]?.data ?? {};
    expect(String(data.to)).toContain("/portal/[redacted]");
    expect(String(data.to)).not.toContain("SECRETPORTALTOKEN123");
    expect(data.from).toBe("/dashboard");
  });

  it("value-redacts a credential in a benign-named request header", () => {
    const e = {
      request: {
        headers: {
          "x-custom-trace": `jwt ${SAMPLE_JWT}`,
          "x-contact": "owner@acme.com",
          "content-type": "application/json",
        },
      },
    } as unknown as Event;
    const headers = scrubEvent(e).request?.headers ?? {};
    expect(headers["x-custom-trace"]).toBe("jwt [redacted-jwt]");
    expect(headers["x-contact"]).toBe("[redacted-email]");
    expect(headers["content-type"]).toBe("application/json");
  });

  it("recurses nested objects under non-sensitive user keys", () => {
    const e = {
      user: { id: "org_1", segment: { contactEmail: "owner@acme.com", tier: "pro" } },
    } as unknown as Event;
    const user = scrubEvent(e).user as Record<string, unknown>;
    expect(user.id).toBe("org_1");
    const segment = user.segment as Record<string, unknown>;
    expect(segment.contactEmail).toBe(REDACTED);
    expect(segment.tier).toBe("pro");
  });
});

describe("scrubEvent — transaction name, trace data, header URLs (#356 re-review)", () => {
  // 17 chars: deliberately NOT caught by the opaque-token net (32+), nor by the
  // email/JWT/Bearer/SSN patterns — ONLY sanitizeUrl's deterministic
  // /portal/{token} path redaction removes it, so these tests discriminate the
  // sanitizeUrl call sites from the pre-existing redactPiiText/redactString ones.
  const PORTAL_TOKEN = "PORTALTOKEN123abc";

  it("sanitizes the portal token out of an error event's transaction name", () => {
    // The App Router instrumentation names the transaction
    // `parameterizedPathname ?? pathname` (raw pathname when parameterization
    // fails) and scope data copies it onto error events unconditionally.
    const e = {
      transaction: `/portal/${PORTAL_TOKEN}`,
      exception: { values: [{ type: "Error", value: "boom" }] },
    } as unknown as Event;
    const out = scrubEvent(e);
    expect(out.transaction).toBe(`/portal/${REDACTED}`);
    expect(out.transaction).not.toContain(PORTAL_TOKEN);
  });

  it("sanitizes the portal token out of a pageload transaction event's name", () => {
    // The INITIAL pageload transaction is named from raw
    // window.location.pathname (only client navigations are parameterized), so
    // with a non-zero traces rate the raw /portal/{token} would ship as the name.
    const e = {
      type: "transaction",
      transaction: `/portal/${PORTAL_TOKEN}`,
    } as unknown as Event;
    const out = scrubEvent(e);
    expect(out.transaction).toBe(`/portal/${REDACTED}`);
    expect(out.transaction).not.toContain(PORTAL_TOKEN);
  });

  it("leaves a parameterized transaction name intact", () => {
    const e = { transaction: "/documents/[id]" } as unknown as Event;
    expect(scrubEvent(e).transaction).toBe("/documents/[id]");
  });

  it("path-sanitizes URL-valued keys in contexts.trace.data (mild net alone is entropy-blind)", () => {
    const e = {
      type: "transaction",
      contexts: {
        trace: {
          trace_id: "0123456789abcdef0123456789abcdef",
          span_id: "abcdef0123456789",
          data: {
            url: `https://www.complidrop.com/portal/${PORTAL_TOKEN}?email=v@x.com`,
            "http.status_code": 200,
          },
        },
      },
    } as unknown as Event;
    const trace = (scrubEvent(e).contexts?.trace ?? {}) as Record<string, unknown>;
    const data = trace.data as Record<string, unknown>;
    expect(String(data.url)).toContain(`/portal/${REDACTED}`);
    expect(String(data.url)).not.toContain(PORTAL_TOKEN);
    expect(String(data.url)).not.toContain("v@x.com");
    expect(data["http.status_code"]).toBe(200); // benign diagnostic kept
    // Trace identifiers survive — the reason trace data gets the URL-key
    // treatment instead of the aggressive free-text net.
    expect(trace.trace_id).toBe("0123456789abcdef0123456789abcdef");
  });

  it("sanitizes a portal-token URL in a benign-named header value (Referer)", () => {
    const e = {
      request: {
        headers: {
          referer: `https://www.complidrop.com/portal/${PORTAL_TOKEN}`,
          "content-type": "application/json",
        },
      },
    } as unknown as Event;
    const headers = scrubEvent(e).request?.headers ?? {};
    expect(String(headers.referer)).toContain(`/portal/${REDACTED}`);
    expect(String(headers.referer)).not.toContain(PORTAL_TOKEN);
    expect(headers["content-type"]).toBe("application/json");
  });

  it("sanitizes each element of an array-valued header (not skipped as non-string)", () => {
    const e = {
      request: {
        headers: {
          // Node repeats multi-value headers as arrays despite the SDK's
          // string-only header type.
          "x-original-referer": [
            `https://www.complidrop.com/portal/${PORTAL_TOKEN}`,
            "owner@acme.com",
          ],
        },
      },
    } as unknown as Event;
    const headers = scrubEvent(e).request?.headers ?? {};
    const values = headers["x-original-referer"] as unknown as string[];
    expect(values[0]).toContain(`/portal/${REDACTED}`);
    expect(values[0]).not.toContain(PORTAL_TOKEN);
    expect(values[1]).toBe("[redacted-email]");
  });
});

describe("scrubEvent — dashed portal token quoted in free text (#356 re-review)", () => {
  // Portal capability tokens are base64url, so they CAN contain `-` — and
  // redactString's opaque-token net deliberately excludes `-` (GUID
  // preservation), splitting this token into sub-32 segments that all survive.
  // Only sanitizeUrl's deterministic /portal/{token} path redaction removes it,
  // so these tests discriminate the sanitizeUrl routing of the free-text
  // surfaces (breadcrumb.message / event.message / exception.value) from the
  // previous bare-redactString scrubbing.
  const DASHED_TOKEN = "AbCd-EfGh-IjKl-MnOp-QrSt-Uv12";
  const PORTAL_URL = `https://www.complidrop.com/portal/${DASHED_TOKEN}`;

  it("survives bare redactString (why these surfaces must route via sanitizeUrl)", () => {
    expect(redactString(`GET ${PORTAL_URL} failed`)).toContain(DASHED_TOKEN);
  });

  it("is redacted from a console breadcrumb message", () => {
    const e = {
      breadcrumbs: [{ category: "console", message: `GET ${PORTAL_URL} 500` }],
    } as unknown as Event;
    const message = String(scrubEvent(e).breadcrumbs?.[0]?.message);
    expect(message).toContain(`/portal/${REDACTED}`);
    expect(message).not.toContain(DASHED_TOKEN);
  });

  it("is redacted from an exception value", () => {
    const e = {
      exception: {
        values: [{ type: "Error", value: `fetch to ${PORTAL_URL} failed` }],
      },
    } as unknown as Event;
    const value = String(scrubEvent(e).exception?.values?.[0]?.value);
    expect(value).toContain(`/portal/${REDACTED}`);
    expect(value).not.toContain(DASHED_TOKEN);
  });

  it("is redacted from event.message", () => {
    const e = { message: `Upload via ${PORTAL_URL} failed` } as unknown as Event;
    const message = String(scrubEvent(e).message);
    expect(message).toContain(`/portal/${REDACTED}`);
    expect(message).not.toContain(DASHED_TOKEN);
  });
});

describe("tagCorrelationId", () => {
  it("copies the ApiError correlationId onto an event tag", () => {
    const event: Event = {};
    const hint = { originalException: { correlationId: "trace-abc-123" } } as EventHint;
    tagCorrelationId(event, hint);
    expect(event.tags?.correlation_id).toBe("trace-abc-123");
  });

  it("no-ops when the exception carries no correlationId", () => {
    const event: Event = {};
    tagCorrelationId(event, { originalException: new Error("boom") } as EventHint);
    expect(event.tags?.correlation_id).toBeUndefined();
  });

  it("no-ops for a missing / non-object exception", () => {
    const event: Event = {};
    tagCorrelationId(event, undefined);
    tagCorrelationId(event, { originalException: "a string" } as EventHint);
    expect(event.tags).toBeUndefined();
  });
});
