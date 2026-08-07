/**
 * The vendor-portal token must not reach PostHog (ADR 0037 Amendment 1, #404).
 *
 * `/portal/{token}` is a BEARER credential in a URL path — whoever holds the
 * string can upload to that link — and PostHog is initialised with
 * `capture_pageview` / `capture_pageleave` on, so every portal visit assembles
 * `window.location` into a family of properties and posts them to a third-party
 * analytics store. Sentry has redacted exactly this string since #356; this
 * suite is the same guarantee on the other vendor, and the fix reuses that same
 * `sanitizeUrl` rather than keeping a second copy of the rule.
 *
 * WHY IT DRIVES THE REAL SDK. The defect was never in our code — it is in what
 * posthog-js adds on its own — so the fix is only as good as the set of
 * properties it covers. A test that fed a hand-written property bag to the hook
 * would pin our list against itself: green while the SDK ships a property
 * nobody wrote down. This runs the actual `initAnalytics()`, intercepts the real
 * ingest requests with MSW, decompresses them and asserts on the bytes that
 * would have left the browser. `$session_entry_url` / `$session_entry_pathname`
 * / `$session_entry_referrer` were all found that way, in no document we
 * started from.
 *
 * KNOWN, RECORDED RESIDUE (ADR 0037 Amendment 1 § What stays open): the
 * `/flags` request builds `person_properties` straight from persistence and
 * does NOT pass through `before_send`, so it can still carry
 * `$initial_current_url`. It fires only after `identify()`, which the portal
 * never calls (there is no session there) — the exposed value is an identified
 * CUSTOMER's own initial URL, not a stranger's credential. These assertions are
 * therefore scoped to the ingest endpoint deliberately and visibly, rather than
 * passing quietly over a channel this fix does not close.
 */
import { gunzipSync } from "node:zlib";
import { describe, it, expect, beforeAll, afterAll, vi } from "vitest";
import { http, HttpResponse } from "msw";
import { server } from "@/test/server";

const POSTHOG_HOST = "https://us.i.posthog.com";
/** Shaped like a real one: `PortalLink.GenerateToken` emits 24 base64url bytes. */
const PORTAL_TOKEN = "Yk3nQ2p-vRs7TzA_wLx9BdEf";
const PORTAL_REFERRER = `https://mail.example.com/portal/${PORTAL_TOKEN}?utm_source=reminder`;
/** A dashed GUID — an identifier, not a secret, and `sanitizeUrl` keeps it (#356). */
const DOCUMENT_ID = "7f3a1c2e-11b4-4f5a-9c0d-2b8e6a4d5f31";

interface PostHogEvent {
  readonly event: string;
  readonly properties: Record<string, unknown>;
  readonly $set_once?: Record<string, unknown>;
}

const ingestBodies: string[] = [];
const ingestEvents: PostHogEvent[] = [];

/**
 * Decode a PostHog request body. The SDK picks its own encoding per endpoint
 * (`compression=gzip-js` on `/e/`, `compression=base64` on `/flags/`), so a
 * raw-text read would "pass" against unreadable bytes — the failure mode that
 * makes a leak test worthless.
 */
function decodeBody(buf: Buffer, url: string): string {
  let body: string;
  try {
    body = gunzipSync(buf).toString("utf8");
  } catch {
    body = buf.toString("utf8");
  }
  if (url.includes("compression=base64")) {
    try {
      body = Buffer.from(decodeURIComponent(body).replace(/^data=/, ""), "base64").toString(
        "utf8",
      );
    } catch {
      /* keep the undecodable form — an assertion on it still fails loudly */
    }
  }
  return body;
}

async function waitForEvent(name: string): Promise<PostHogEvent> {
  const deadline = Date.now() + 10000;
  for (;;) {
    const found = ingestEvents.find((e) => e.event === name);
    if (found) return found;
    if (Date.now() > deadline) {
      throw new Error(
        `PostHog never sent a "${name}" event (saw: ${ingestEvents.map((e) => e.event).join(", ") || "nothing"}). ` +
          "Without it every leak assertion below would be vacuous.",
      );
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

/**
 * One SDK lifetime for the whole file: posthog-js keeps a module-global
 * instance that `vi.resetModules()` does not reach ("You have already
 * initialized PostHog! Re-initializing is a no-op"), so a per-test init would
 * silently measure the FIRST test's configuration. The two scenarios are
 * therefore two navigations within one session, which is also how a real
 * visitor reaches them.
 */
beforeAll(async () => {
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_KEY", "phc_analytics_test");
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_HOST", POSTHOG_HOST);
  server.use(
    http.all(`${POSTHOG_HOST}/*`, async ({ request }) => {
      const body = decodeBody(Buffer.from(await request.arrayBuffer()), request.url);
      if (request.url.includes("/e/")) {
        ingestBodies.push(body);
        try {
          const parsed: unknown = JSON.parse(body);
          for (const event of Array.isArray(parsed) ? parsed : [parsed]) {
            ingestEvents.push(event as PostHogEvent);
          }
        } catch {
          throw new Error(`unparseable PostHog ingest body: ${body.slice(0, 200)}`);
        }
      }
      return HttpResponse.json({ featureFlags: {}, config: {} });
    }),
    // The SDK may pull remote extensions; answer rather than let MSW's
    // `onUnhandledRequest: "error"` turn a background fetch into a failure.
    http.all("https://us-assets.i.posthog.com/*", () => HttpResponse.text("")),
  );

  // 1. The vendor lands on the tokenized portal URL, from a tokenized referrer.
  window.history.replaceState({}, "", `/portal/${PORTAL_TOKEN}`);
  Object.defineProperty(document, "referrer", {
    configurable: true,
    value: PORTAL_REFERRER,
  });

  const { initAnalytics, identify, track } = await import("./analytics");
  initAnalytics(); // → $pageview
  // `$set_once` (the `$initial_*` family) only rides along once a person exists.
  identify("user_00000000-0000-4000-8000-000000000001"); // → $identify
  await waitForEvent("$identify");

  // 2. …then an ordinary in-app route, in the same session.
  window.history.replaceState({}, "", `/documents/${DOCUMENT_ID}`);
  track("document_viewed"); // → document_viewed
  await waitForEvent("document_viewed");
}, 30000);

afterAll(() => {
  vi.unstubAllEnvs();
});

describe("PostHog URL redaction (#404 / ADR 0037 Amendment 1)", () => {
  it("never sends the portal token, in any property, on any event", () => {
    // The whole-body assertion is the actual guarantee: it does not depend on
    // this test knowing which properties the SDK decided to populate.
    for (const body of ingestBodies) {
      expect(body, "a PostHog ingest request carried the portal token").not.toContain(
        PORTAL_TOKEN,
      );
    }
    expect(ingestBodies.length).toBeGreaterThan(0);
  });

  it("redacts the token in every URL-bearing property the SDK populates", async () => {
    const pageview = await waitForEvent("$pageview");
    const identifyEvent = await waitForEvent("$identify");

    // Named one by one so a regression says WHERE, not just that something
    // leaked. Every one of these was observed live against the real SDK.
    const eventProperties = [
      "$current_url",
      "$pathname",
      "$referrer",
      "$session_entry_url",
      "$session_entry_pathname",
      "$session_entry_referrer",
    ];
    for (const property of eventProperties) {
      const value = pageview.properties[property];
      expect(typeof value, `PostHog stopped emitting ${property} on $pageview`).toBe("string");
      expect(value as string, `${property} leaked the portal token`).toContain(
        "/portal/[redacted]",
      );
    }

    // `$set_once` is a sibling of `properties` on the wire, so a hook that only
    // rewrote `properties` would leave these three intact.
    const setOnce = identifyEvent.$set_once ?? {};
    for (const property of ["$initial_current_url", "$initial_pathname", "$initial_referrer"]) {
      const value = setOnce[property];
      expect(typeof value, `PostHog stopped emitting ${property} in $set_once`).toBe("string");
      expect(value as string, `${property} leaked the portal token`).toContain(
        "/portal/[redacted]",
      );
    }
  });

  it("leaves an ordinary in-app URL intact — this redacts a credential, not analytics", async () => {
    const viewed = await waitForEvent("document_viewed");
    expect(viewed.properties.$current_url).toContain(`/documents/${DOCUMENT_ID}`);
    expect(viewed.properties.$current_url).not.toContain("[redacted]");
    expect(viewed.properties.$pathname).toBe(`/documents/${DOCUMENT_ID}`);
  });
});
