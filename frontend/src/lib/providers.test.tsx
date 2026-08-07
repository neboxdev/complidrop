/**
 * The vendor portal initialises no analytics at all (#404, ADR 0037 Amendments 2–3).
 *
 * `/portal/{token}` is the one surface in this product where the URL ITSELF is a
 * bearer credential: whoever holds the string can upload to that link. Redacting
 * it per channel — `before_send` for `capture()`, `advanced_disable_flags` for
 * `/flags`, `capture_heatmaps: false` for the buffer keyed by `location.href` —
 * is a promise renewed every time the SDK grows a channel, and round 2 of #404
 * found two more that a shipped, reviewed round 1 had missed (plus a third,
 * `/i/v1/logs`, reachable by remote config). Not initialising is ONE invariant
 * that holds whatever posthog-js does next.
 *
 * The pin is therefore "no PostHog request at all from that route", not "no
 * token in the requests": the second is what the enumeration keeps failing to
 * guarantee. The dashboard case at the bottom is what keeps it honest — without
 * it a broken key, a broken mock or a dead provider would pass this file.
 *
 * ## The credential outlives the pathname (round 3)
 *
 * posthog-js is a MODULE-GLOBAL singleton and nothing here de-initialises or
 * opts it out, so a gate keyed on the CURRENT pathname alone stops covering the
 * route the moment the SDK goes live anywhere in the same JS context. The flow
 * that does it is the one ADR 0054 itself recommends and anticipates: the notice
 * links the Privacy Policy, the vendor reads it, and comes back ("loses the
 * Received card on the way back"). Coming back, the effect returns early while
 * the live SDK keeps running against `/portal/{token}` — `$pageleave` at tab
 * close (`_handle_unload` bound to `pagehide` at init, `posthog-core.js`),
 * autocapture on portal clicks, and a `localStorage+cookie` persistence writer.
 * So the shipped sentence *"This page sets no cookies and doesn't measure how
 * it's used"*, quoted verbatim in the CLM-5 counsel register, would be false for
 * exactly the vendor who followed the notice's own link, while they are reading
 * it. The round-trip case below drives the FIRST of those three, because it is
 * the one an interceptor can observe deterministically.
 *
 * The fix is two-sided and both sides are pinned:
 *
 *   (a) the notice's link is a plain `<a href="/privacy" rel="noreferrer">`, so
 *       the policy opens in a FULL DOCUMENT LOAD — a new JS context — and the
 *       portal context never contains an initialised SDK at all. Pinned in
 *       `app/portal/[token]/page.test.tsx` ("FULL DOCUMENT LOAD").
 *   (b) the gate is STICKY per browsing context: once a tab has held the
 *       credential, `initAnalytics()` never runs in it again until a hard load.
 *       Pinned here, by the two middle cases.
 *
 * (b) has a cost, recorded rather than discovered: a customer who opens a portal
 * link in the same tab loses analytics for the rest of that tab's session. The
 * "vendor's /privacy visit included" case asserts the flag's reach directly, so
 * deleting the flag reddens this file instead of silently re-opening the window.
 *
 * READ THAT CASE FOR WHAT IT IS (corrected #398). It drives a SOFT navigation,
 * because jsdom never navigates — so it pins (b) against the counterfactual (b)
 * exists to survive, not against the shipped flow. In production (a) wins first:
 * `/privacy` arrives via a full document load, the module-scope flag starts
 * false there, and that visit IS measured — credential-free, since the referrer
 * is suppressed and `before_send` redacts anyway. Do NOT "fix" this case to
 * match the shipped flow; it would stop pinning the flag, which is the whole
 * reason it is here.
 */
import { describe, it, expect, beforeAll, beforeEach, afterAll, vi } from "vitest";
import { act, render, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { server } from "@/test/server";
import { setNavigationState } from "@/test/navigation";

const POSTHOG_HOST = "https://us.i.posthog.com";
const POSTHOG_ASSETS_HOST = "https://us-assets.i.posthog.com";
const PORTAL_TOKEN = "Yk3nQ2p-vRs7TzA_wLx9BdEf";
const PORTAL_PATH = `/portal/${PORTAL_TOKEN}`;

const posthogRequests: string[] = [];

function registerPostHogHandlers() {
  server.use(
    http.all(`${POSTHOG_HOST}/*`, ({ request }) => {
      posthogRequests.push(request.url);
      return HttpResponse.json({ featureFlags: {}, config: {} });
    }),
    http.all(`${POSTHOG_ASSETS_HOST}/*`, ({ request }) => {
      posthogRequests.push(request.url);
      return HttpResponse.text("");
    }),
  );
}

/**
 * Give the SDK room to speak. `capture_pageview` fires its first event from a
 * `setTimeout(…, 1)` and the remote-config chain is asynchronous after that, so
 * an assertion taken on the next tick would pass against a leak that simply had
 * not left yet.
 */
async function settle(ms = 600) {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * One browsing context per call — the unit the gate is scoped to.
 *
 * `vi.resetModules()` drops the transformed-module registry, so the next
 * `import("./providers")` re-evaluates `providers.tsx` (its sticky flag) and
 * `analytics.ts` (its `initialized` flag) from source, which is what a hard
 * document load does to both. Every case below needs a context that has not
 * already been decided by the previous one: since round 3 the gate is STICKY,
 * so without this the first portal render would suppress analytics for the rest
 * of the file and the dashboard case — the one that keeps the others honest —
 * could never observe a clean start.
 *
 * Imported DYNAMICALLY, never at module scope, for a second reason as well:
 * `Providers` pulls in `./analytics` and therefore posthog-js, whose
 * `utils/globals.js` binds `exports.fetch = global.fetch` at MODULE-EVAL time —
 * and vitest evaluates a test file's static imports during collection, before
 * `vitest.setup.ts`'s `beforeAll` runs `server.listen()`. A static import hands
 * the SDK the un-intercepted fetch, every request escapes MSW, and every case
 * below records nothing: the portal ones pass for the wrong reason and the
 * dashboard one, which exists to catch exactly that, is the only reason it was
 * noticed.
 *
 * What `resetModules` does NOT reset is posthog-js itself — vitest externalises
 * node_modules, so the SDK's own module singleton survives, and a second
 * `posthog.init` in the same file is a logged no-op. That is why the dashboard
 * case runs LAST: it is the only case that may initialise the SDK at all.
 */
async function freshTab(): Promise<typeof import("./providers").Providers> {
  vi.resetModules();
  return (await import("./providers")).Providers;
}

/** A soft navigation: the App Router keeps the JS context, `Providers` stays mounted. */
async function softNavigateTo(pathname: string) {
  await act(async () => {
    setNavigationState({ pathname });
  });
}

beforeAll(() => {
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_KEY", "phc_providers_test");
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_HOST", POSTHOG_HOST);
});

beforeEach(() => {
  posthogRequests.length = 0;
  registerPostHogHandlers();
});

afterAll(() => {
  vi.unstubAllEnvs();
});

describe("Providers analytics gate (#404 / ADR 0037 Amendments 2–3)", () => {
  it("issues no PostHog request whatsoever from /portal/{token}", async () => {
    const Providers = await freshTab();
    setNavigationState({ pathname: PORTAL_PATH, params: { token: PORTAL_TOKEN } });
    render(<Providers>portal</Providers>);
    await settle();
    expect(
      posthogRequests,
      "the portal route reached PostHog — the credential is in the URL of every one of these",
    ).toEqual([]);
  });

  it("never initialises again in a tab that has held the credential — the vendor's /privacy visit included", async () => {
    const Providers = await freshTab();
    setNavigationState({ pathname: PORTAL_PATH, params: { token: PORTAL_TOKEN } });
    render(<Providers>portal</Providers>);
    await settle();
    expect(posthogRequests, "the portal route reached PostHog before any navigation").toEqual([]);

    // The vendor follows the notice's Privacy Policy link. Modelled as a SOFT
    // navigation on purpose: that is what `<Link>` did, and it is the flow
    // remedy (b) has to survive on its own — (a) removes the soft navigation,
    // (b) makes its absence unnecessary.
    await softNavigateTo("/privacy");
    await settle();

    expect(
      posthogRequests,
      "PostHog initialised in a JS context that has held the portal token — the sticky flag is gone",
    ).toEqual([]);
  });

  it("issues no PostHog request from /portal/{token} after a policy round trip in one context", async () => {
    const Providers = await freshTab();
    setNavigationState({ pathname: PORTAL_PATH, params: { token: PORTAL_TOKEN } });
    render(<Providers>portal</Providers>);
    await settle();

    await softNavigateTo("/privacy");
    await settle();

    // …and Back, which the App Router serves without a document load.
    await softNavigateTo(PORTAL_PATH);
    const backOnPortal = posthogRequests.length;
    await settle();
    expect(
      posthogRequests.slice(backOnPortal),
      "PostHog spoke on arriving back at /portal/{token} — the credential is in the URL of every one of these",
    ).toEqual([]);

    // The vendor closes the tab while the portal URL is on screen. posthog-js
    // binds `_handle_unload` to `pagehide` at init (`'onpagehide' in self ?
    // 'pagehide' : 'unload'`, `posthog-core.js`), and with
    // `capture_pageleave: true` that captures `$pageleave` from
    // `window.location` and unloads the request queue.
    act(() => {
      window.dispatchEvent(new Event("pagehide"));
      window.dispatchEvent(new Event("unload"));
    });
    await settle();

    expect(
      posthogRequests.slice(backOnPortal),
      "$pageleave left from /portal/{token} at tab close — a live SDK the pathname gate can no longer reach",
    ).toEqual([]);
  });

  // LAST, and load-bearing that it is last: this is the only case that lets the
  // SDK initialise, and posthog-js's own module singleton outlives
  // `vi.resetModules()` (see `freshTab`). It is also the case that keeps every
  // assertion above honest — a broken key, a broken mock or a dead provider
  // would satisfy them all.
  it("still measures an ordinary in-app route in a tab that never held the credential", async () => {
    const Providers = await freshTab();
    setNavigationState({ pathname: "/documents", params: {} });
    render(<Providers>dashboard</Providers>);
    await waitFor(() => expect(posthogRequests.length).toBeGreaterThan(0), { timeout: 5000 });
  });
});
