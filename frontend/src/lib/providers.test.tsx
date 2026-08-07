/**
 * The vendor portal initialises no analytics at all (#404, ADR 0037 Amendment 2).
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
 * guarantee. The dashboard case below is what keeps it honest — without it a
 * broken key, a broken mock or a dead provider would pass this file.
 */
import { describe, it, expect, beforeAll, beforeEach, afterAll, vi } from "vitest";
import { render, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { server } from "@/test/server";
import { setNavigationState } from "@/test/navigation";

/**
 * Imported DYNAMICALLY in `beforeAll`, never at module scope. `Providers` pulls
 * in `./analytics` and therefore posthog-js, whose `utils/globals.js` binds
 * `exports.fetch = global.fetch` at MODULE-EVAL time — and vitest evaluates a
 * test file's static imports during collection, before `vitest.setup.ts`'s
 * `beforeAll` runs `server.listen()`. A static import hands the SDK the
 * un-intercepted fetch, every request escapes MSW, and BOTH cases below record
 * nothing: the portal one passes for the wrong reason and the dashboard one,
 * which exists to catch exactly that, is the only reason it was noticed.
 */
let Providers: typeof import("./providers").Providers;

const POSTHOG_HOST = "https://us.i.posthog.com";
const POSTHOG_ASSETS_HOST = "https://us-assets.i.posthog.com";
const PORTAL_TOKEN = "Yk3nQ2p-vRs7TzA_wLx9BdEf";

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

beforeAll(async () => {
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_KEY", "phc_providers_test");
  vi.stubEnv("NEXT_PUBLIC_POSTHOG_HOST", POSTHOG_HOST);
  ({ Providers } = await import("./providers"));
});

beforeEach(() => {
  registerPostHogHandlers();
});

afterAll(() => {
  vi.unstubAllEnvs();
});

describe("Providers analytics gate (#404 / ADR 0037 Amendment 2)", () => {
  // Order matters and is load-bearing: posthog-js keeps a module-global
  // instance and `initAnalytics` a module-global `initialized` flag, so once
  // the second test has initialised the SDK the first could never observe a
  // clean start again.
  it("issues no PostHog request whatsoever from /portal/{token}", async () => {
    setNavigationState({ pathname: `/portal/${PORTAL_TOKEN}`, params: { token: PORTAL_TOKEN } });
    render(<Providers>portal</Providers>);
    await settle();
    expect(
      posthogRequests,
      "the portal route reached PostHog — the credential is in the URL of every one of these",
    ).toEqual([]);
  });

  it("still measures an ordinary in-app route — the gate is the portal, not analytics", async () => {
    setNavigationState({ pathname: "/documents", params: {} });
    render(<Providers>dashboard</Providers>);
    await waitFor(() => expect(posthogRequests.length).toBeGreaterThan(0), { timeout: 5000 });
  });
});
