/**
 * The vendor-portal token must not reach PostHog (ADR 0037 Amendment 1, #404).
 *
 * `/portal/{token}` is a BEARER credential in a URL path — whoever holds the
 * string can upload to that link — and PostHog is initialised with
 * `capture_pageview` / `capture_pageleave` on, so every page it measures
 * assembles `window.location` into a family of properties and posts them to a
 * third-party analytics store. Sentry has redacted exactly this string since
 * #356; this suite is the same guarantee on the other vendor, and the fix
 * reuses that same `sanitizeUrl` rather than keeping a second copy of the rule.
 *
 * WHY THIS SUITE STILL EXISTS NOW THAT THE PORTAL ROUTE ITSELF NEVER
 * INITIALISES POSTHOG (ADR 0037 Amendments 2-3, pinned by `providers.test.tsx`).
 * Not initialising is the invariant that holds against channels nobody has
 * enumerated yet, but the token does not only appear in `location.href` — a
 * reminder mail can put a portal URL in any route's `$referrer`, and free text
 * can quote one anywhere. Redaction is the layer that covers that, so it is
 * driven and asserted here on its own.
 *
 * The example this docstring used to lead with — "a vendor who follows the
 * notice's Privacy Policy link reaches `/privacy`, a route that DOES measure,
 * carrying the tokenized portal URL in `document.referrer`" — lost its second
 * half in Amendment 3 (#404 round 3): that link is now a plain
 * `<a … rel="noreferrer">`, so the REFERRER is not sent at all. The first half
 * still holds, and this docstring's earlier correction over-reached (fixed
 * #398): `/privacy` is reached by a FULL DOCUMENT LOAD, i.e. a new JS context
 * in which the sticky gate starts false, so that visit IS measured — just
 * credential-free. The redaction stays exactly as it is either way: it protects
 * every OTHER route, and narrowing a redactor because one of its callers went
 * away is how the next channel gets missed.
 *
 * WHY IT DRIVES THE REAL SDK. The defect was never in our code — it is in what
 * posthog-js adds on its own — so the fix is only as good as the set of
 * properties AND CHANNELS it covers. A test that fed a hand-written property bag
 * to the hook would pin our list against itself: green while the SDK ships a
 * property nobody wrote down. This runs the actual `initAnalytics()`, intercepts
 * the real requests with MSW, decompresses them and asserts on the bytes that
 * would have left the browser. `$session_entry_url` / `$session_entry_pathname`
 * / `$session_entry_referrer` were all found that way, in no document we
 * started from.
 *
 * EVERY REQUEST IS RECORDED, NOT ONLY `/e/`. Round 1 scoped these assertions to
 * the ingest endpoint on a premise that was FALSE: it recorded that the
 * un-hooked `/flags` request "fires only after `identify()`", so it could only
 * ever carry an identified customer's own URL. In posthog-js 1.369.2
 * `/flags/?v=2` is issued at INIT — `_loaded()` → `RemoteConfigLoader.load()` →
 * `_onRemoteConfig()` → `featureFlags.ensureFlagsLoaded()` — and again from
 * `RemoteConfigLoader.refresh()` every 5 minutes, with no `identify()` anywhere
 * in either chain. Its `person_properties` bag is built from
 * `persistence.get_initial_props()`, i.e. the `$initial_current_url` /
 * `$initial_pathname` / `$initial_referrer` that the very first `capture()`
 * wrote via `set_initial_person_info()` — raw, because that bag never passes
 * through `before_send`. Run against the code before the fix, the flags body
 * below read:
 *
 *   "person_properties":{"$initial_referrer":"https://mail.example.com/portal/{TOKEN}",
 *    "$initial_current_url":"http://localhost:3000/portal/{TOKEN}",
 *    "$initial_pathname":"/portal/{TOKEN}", …}
 *
 * i.e. an ANONYMOUS visitor's bearer credential, which is precisely the
 * population #404 is about. Scoping the assertions away from that channel is
 * what hid it, so the handler now records EVERY PostHog request and the
 * assertions run over all of them.
 */
import { gunzipSync } from "node:zlib";
import { describe, it, expect, beforeAll, beforeEach, afterAll, vi } from "vitest";
import { http, HttpResponse } from "msw";
import type posthogJs from "posthog-js";
import { server } from "@/test/server";

const POSTHOG_HOST = "https://us.i.posthog.com";
const POSTHOG_ASSETS_HOST = "https://us-assets.i.posthog.com";
/** Shaped like a real one: `PortalLink.GenerateToken` emits 24 base64url bytes. */
const PORTAL_TOKEN = "Yk3nQ2p-vRs7TzA_wLx9BdEf";
const PORTAL_URL = `https://app.example.com/portal/${PORTAL_TOKEN}`;
const REDACTED_PORTAL_URL = "https://app.example.com/portal/[redacted]";
const PORTAL_REFERRER = `https://mail.example.com/portal/${PORTAL_TOKEN}?utm_source=reminder`;
/** A dashed GUID — an identifier, not a secret, and `sanitizeUrl` keeps it (#356). */
const DOCUMENT_ID = "7f3a1c2e-11b4-4f5a-9c0d-2b8e6a4d5f31";

interface PostHogEvent {
  readonly event: string;
  readonly properties: Record<string, unknown>;
  readonly $set?: Record<string, unknown>;
  readonly $set_once?: Record<string, unknown>;
}

interface PostHogRequest {
  readonly url: string;
  readonly body: string;
  /**
   * True for everything the SDK sent before `identify()` was ever called — the
   * anonymous-vendor population #404 is about. The round-1 record claimed the
   * un-hooked channels could only ever carry an IDENTIFIED customer's own URL;
   * this flag is what makes that claim testable instead of merely asserted.
   */
  readonly anonymous: boolean;
}

const posthogRequests: PostHogRequest[] = [];
const ingestEvents: PostHogEvent[] = [];
let identified = false;
/**
 * Imported DYNAMICALLY inside `beforeAll`, never at module scope.
 * `posthog-js/lib/src/utils/globals.js` binds `exports.fetch = global.fetch` at
 * MODULE-EVAL time, and vitest evaluates a test file's static imports during
 * collection — before `vitest.setup.ts`'s `beforeAll` runs `server.listen()`
 * and patches the global. A static import therefore hands the SDK the
 * un-intercepted fetch and every request escapes MSW silently, leaving this
 * whole suite asserting over an empty array.
 */
let posthog: typeof posthogJs;

/** Bodies of the ingest (`/e/`) requests — the events, as opposed to every channel. */
function ingestBodies(): string[] {
  return posthogRequests.filter((r) => r.url.includes("/e/")).map((r) => r.body);
}

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

/**
 * Registered from BOTH `beforeAll` (the one-time SDK drive needs them) and
 * `beforeEach` — `vitest.setup.ts`'s global `afterEach` calls
 * `server.resetHandlers()`, so from the second test onward this file would
 * otherwise run with no PostHog handler at all and the next capture-driving
 * test added here would fail on `onUnhandledRequest: "error"` for a reason that
 * has nothing to do with redaction.
 */
function registerPostHogHandlers() {
  server.use(
    http.all(`${POSTHOG_HOST}/*`, async ({ request }) => {
      const body = decodeBody(Buffer.from(await request.arrayBuffer()), request.url);
      posthogRequests.push({ url: request.url, body, anonymous: !identified });
      if (request.url.includes("/e/")) {
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
    http.all(`${POSTHOG_ASSETS_HOST}/*`, () => HttpResponse.text("")),
  );
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
 * Wait until the SDK has been quiet for `quietMs`. The `/flags` call is
 * debounced 5 ms and answered asynchronously, so the phase below has to end on
 * silence rather than on a specific request: asserting "no `/flags` request"
 * cannot wait for one to arrive. Bounded by `ceilingMs` so a closed channel
 * costs one quiet window rather than a hang.
 */
async function waitForQuiet(quietMs = 400, ceilingMs = 8000): Promise<void> {
  const deadline = Date.now() + ceilingMs;
  let seen = -1;
  let quietSince = Date.now();
  for (;;) {
    if (posthogRequests.length !== seen) {
      seen = posthogRequests.length;
      quietSince = Date.now();
    }
    if (Date.now() - quietSince >= quietMs || Date.now() > deadline) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

function isRecordValue(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** `nest("x", 3)` → `{ n: { n: { n: "x" } } }` — a value buried past the walker's depth cap. */
function nest(leaf: string, levels: number): unknown {
  let value: unknown = leaf;
  for (let i = 0; i < levels; i++) value = { n: value };
  return value;
}

/**
 * One SDK lifetime for the whole file: posthog-js keeps a module-global
 * instance that `vi.resetModules()` does not reach ("You have already
 * initialized PostHog! Re-initializing is a no-op"), so a per-test init would
 * silently measure the FIRST test's configuration. The scenarios are therefore
 * navigations within one session, which is also how a real visitor reaches them.
 */
beforeAll(async () => {
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_KEY", "phc_analytics_test");
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_HOST", POSTHOG_HOST);
  registerPostHogHandlers();

  // 1. A tokenized URL, reached from a tokenized referrer, with NOBODY
  //    identified — the anonymous vendor population.
  window.history.replaceState({}, "", `/portal/${PORTAL_TOKEN}`);
  Object.defineProperty(document, "referrer", {
    configurable: true,
    value: PORTAL_REFERRER,
  });

  const { initAnalytics, identify, track } = await import("./analytics");
  posthog = (await import("posthog-js")).default;
  initAnalytics(); // → $pageview
  await waitForEvent("$pageview");

  // 2. Ask the SDK to load feature flags, still anonymous. This is the same
  //    entry point both un-identified triggers reach —
  //    `RemoteConfigLoader._onRemoteConfig()` → `ensureFlagsLoaded()` at init and
  //    `RemoteConfigLoader.refresh()` on its 5-minute interval both call
  //    `reloadFeatureFlags()`. The init-time trigger cannot be awaited in jsdom:
  //    it runs behind `_loadRemoteConfigJs`, which injects a <script> jsdom will
  //    not execute, so the chain stalls before `_onRemoteConfig` and the channel
  //    would look closed here for a reason that has nothing to do with our
  //    config. Driving `reloadFeatureFlags()` reaches `_callFlagsEndpoint()` —
  //    where `advanced_disable_flags` is actually honoured — with no identify.
  posthog.reloadFeatureFlags();
  await waitForQuiet();

  // 3. …only now does a person exist. `$set_once` (the `$initial_*` family)
  //    rides along from here.
  identified = true;
  identify("user_00000000-0000-4000-8000-000000000001", { profileUrl: PORTAL_URL }); // → $identify
  await waitForEvent("$identify");

  // 4. The heatmaps extension buffers clicks in a map KEYED BY
  //    `window.location.href` and flushes it through `capture()`
  //    (`node_modules/posthog-js/lib/src/heatmaps.js` — `_capture` builds
  //    `this._buffer[url]`, `_flush` sends `capture('$$heatmap', { $heatmap_data })`).
  //    The URL is therefore an object KEY, and `$heatmap_data` matches nothing in
  //    `URL_BEARING_KEY`, so a value-only walker copies the token straight
  //    through. Driven with the extension's own event name and payload shape.
  track("$$heatmap", {
    $heatmap_data: {
      [PORTAL_URL]: [{ x: 12, y: 340, target_fixed: false, type: "click" }],
    },
  });
  await waitForEvent("$$heatmap");

  // 5. Autocapture nests `attr__href` inside `$elements` — the reason the walker
  //    recurses at all. Nothing exercised that branch before, so a shallow
  //    reimplementation that mapped over top-level keys only would have kept
  //    every assertion green while re-opening the autocapture path.
  track("link_clicked", {
    $elements: [{ tag_name: "a", attr__href: PORTAL_URL, nth_child: 2 }],
  });
  await waitForEvent("link_clicked");

  // 6. Something URL-bearing nested past the walker's depth cap. The cap used
  //    to return the value untouched, which made it an escape hatch rather than
  //    a bound; it now fails closed in the same direction as `sentry/scrub.ts`.
  track("deep_probe", { deep_url_probe: nest(PORTAL_URL, 14) });
  await waitForEvent("deep_probe");

  // 7. …then an ordinary in-app route, in the same session.
  window.history.replaceState({}, "", `/documents/${DOCUMENT_ID}`);
  track("document_viewed"); // → document_viewed
  await waitForEvent("document_viewed");
}, 30000);

beforeEach(() => {
  registerPostHogHandlers();
});

afterAll(() => {
  vi.unstubAllEnvs();
});

describe("PostHog URL redaction (#404 / ADR 0037 Amendment 1)", () => {
  it("never sends the portal token, on any channel, on any event", () => {
    // The whole-body assertion is the actual guarantee: it does not depend on
    // this test knowing which properties — or which endpoints — the SDK decided
    // to populate.
    for (const { url, body } of posthogRequests) {
      expect(
        body,
        `a PostHog request to ${new URL(url).pathname} carried the portal token`,
      ).not.toContain(PORTAL_TOKEN);
    }
    expect(posthogRequests.length).toBeGreaterThan(0);
  });

  it("sends nothing carrying the token while the visitor is anonymous — the population #404 is about", () => {
    const anonymous = posthogRequests.filter((r) => r.anonymous);
    expect(
      anonymous.length,
      "the SDK sent nothing before identify() — every assertion here would be vacuous",
    ).toBeGreaterThan(0);
    for (const { url, body } of anonymous) {
      expect(
        body,
        `${new URL(url).pathname} carried the portal token for an ANONYMOUS visitor`,
      ).not.toContain(PORTAL_TOKEN);
    }
  });

  it("issues no /flags request at all — that channel is closed at init, not redacted", () => {
    // `/flags` never passes through `before_send`: it builds `person_properties`
    // straight from persistence, so redaction cannot reach it. The pin is
    // therefore on the request not existing, which is what
    // `advanced_disable_flags: true` buys.
    expect(
      posthogRequests.filter((r) => r.url.includes("/flags")).map((r) => r.url),
      "posthog-js issued a /flags request — `advanced_disable_flags` is no longer in effect",
    ).toEqual([]);
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

    // `$set` is the third sibling — an identify() trait, ours rather than the
    // SDK's, and unreached by a hook that walked only `properties`/`$set_once`.
    expect(
      (identifyEvent.$set ?? {}).profileUrl,
      "an identify() trait leaked the portal token",
    ).toBe(REDACTED_PORTAL_URL);
  });

  it("redacts a URL used as an object KEY — the heatmaps channel", async () => {
    const heatmap = await waitForEvent("$$heatmap");
    const data = heatmap.properties.$heatmap_data as Record<string, unknown>;
    const keys = Object.keys(data);
    expect(keys, "the heatmap buffer lost its bucket").toHaveLength(1);
    expect(keys[0], "the buffer key carried the portal token").toBe(REDACTED_PORTAL_URL);
    // The bucket's contents are click coordinates and must survive untouched —
    // this redacts a credential, not analytics.
    expect(data[keys[0]]).toEqual([{ x: 12, y: 340, target_fixed: false, type: "click" }]);
  });

  it("pins the heatmaps extension off at init, so a project toggle cannot re-open the key channel", () => {
    // Belt and braces, deliberately: the key rule above is the correct general
    // fix, and this is what survives someone rewriting the walker. With neither
    // `capture_heatmaps` nor `enable_heatmaps` set, `Heatmaps.isEnabled` falls
    // through to `_enabledServerSide` — the PostHog project's own dashboard
    // setting, which nothing in this repo controls.
    expect(posthog.config.capture_heatmaps).toBe(false);
  });

  it("redacts a URL nested under autocapture's $elements", async () => {
    const clicked = await waitForEvent("link_clicked");
    const elements = clicked.properties.$elements as Record<string, unknown>[];
    expect(elements[0].attr__href).toBe(REDACTED_PORTAL_URL);
    // Everything else in the element survives — including the numeric field, so
    // the walker is not stringifying what it walks.
    expect(elements[0].tag_name).toBe("a");
    expect(elements[0].nth_child).toBe(2);
  });

  it("fails CLOSED past the depth cap — a bound, not an escape hatch", async () => {
    const probe = await waitForEvent("deep_probe");
    let node = probe.properties.deep_url_probe;
    // Walk down to the leaf: everything above the cap is copied structurally.
    for (let i = 0; i < 14 && isRecordValue(node); i++) node = node.n;
    expect(node, "the deep leaf under a URL-bearing key survived unredacted").toBe("[redacted]");
  });

  it("leaves an ordinary in-app URL intact — this redacts a credential, not analytics", async () => {
    const viewed = await waitForEvent("document_viewed");
    expect(viewed.properties.$current_url).toContain(`/documents/${DOCUMENT_ID}`);
    expect(viewed.properties.$current_url).not.toContain("[redacted]");
    expect(viewed.properties.$pathname).toBe(`/documents/${DOCUMENT_ID}`);
    expect(ingestBodies().length, "no ingest request was recorded at all").toBeGreaterThan(0);
  });
});
