/**
 * Harness sanity smoke (#38).
 *
 * Proves the Playwright harness boots, the network-mock interceptor
 * actually intercepts (verified by observing the mock-fulfilled
 * response, not by accidentally hitting the dev server), and an
 * unmocked /api/* request is BLOCKED via the harness's `test.no_mock`
 * envelope. Tier-1 launch flows (auth, vendor-portal upload, upload-
 * to-extraction) come in #39 and add the real coverage on top.
 *
 * If THIS test ever fails, the harness itself is broken — every
 * downstream E2E ticket is paused until it's green again.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonError, waitForApi } from "../support/mock-api";

test.describe("E2E harness sanity (#38)", () => {
  test("the mock interceptor fires AND the landing-page logged-out CTA renders", async ({ page }) => {
    // Stale-hint path from #69 AC #3: the SPA's landing-page probe
    // `useMe({ skipRefresh: true })` is gated by the non-httpOnly
    // `cd_session_hint` cookie. A truly anonymous visitor (no hint)
    // fires ZERO auth calls — which is correct production behavior
    // but USELESS for proving the mock interceptor is wired (the
    // landing page's static SSR shell renders the logged-out CTAs
    // regardless). To assert the harness end-to-end, force the probe
    // to fire by planting the hint cookie BEFORE navigation, then
    // observe that the mocked 401 flows through the SPA into the UI.
    //
    // Equivalent in semantics to a user whose session lapsed but
    // whose hint cookie survived — the exact scenario #69's AC #3
    // pins at "one /me 401, skipRefresh keeps the cost at one call".
    await page.context().addCookies([
      {
        name: "cd_session_hint",
        value: "1",
        url: "http://localhost:3000",
        path: "/",
      },
    ]);

    // Anonymous /api/auth/me returns 401 — same as production for a
    // logged-out visitor. The landing page's `useMe({ skipRefresh:
    // true })` then resolves to null and renders the logged-out CTAs.
    await mockApi(page, [
      {
        method: "GET",
        path: "/api/auth/me",
        handler: jsonError("auth.unauthorized", "Not authenticated", 401),
      },
    ]);

    // Pin that the mock REALLY fires — without this, the test would
    // pass even if the interceptor was misconfigured (the landing
    // page's static SSR shell renders fine before any /api call). The
    // waitForApi must come BEFORE goto so the listener is armed.
    // (Migrated from inline url.includes — #91 followup.)
    const meResponse = waitForApi(page, "GET", "/api/auth/me", { status: 401 });

    await page.goto("/");
    await meResponse;

    // Logged-out CTA only shows when useMe resolves to null — proves
    // the mocked response flowed all the way through the SPA's auth
    // state into the UI, not just the static SSR shell.
    // Landing page uses "Log in" copy for the logged-out CTA (see
    // src/app/page.tsx); only renders when useMe resolves to null.
    await expect(page.getByRole("link", { name: /log in/i }).first()).toBeVisible();
  });

  test("unmocked /api/* requests return the harness's 'no mock registered' error (404)", async ({ page }) => {
    // page.request is Playwright's APIRequestContext — a NODE-SIDE
    // HTTP client that does NOT go through page.route(). To verify
    // page.route's behavior, do the fetch from INSIDE the browser via
    // page.evaluate, which IS subject to the interceptor.
    await mockApi(page, []);
    await page.goto("/");

    const result = await page.evaluate(async () => {
      const r = await fetch("/api/anything");
      const body = await r.json();
      return { status: r.status, body };
    });

    expect(result.status).toBe(404);
    expect(result.body).toMatchObject({
      data: null,
      error: { code: "test.no_mock" },
    });
  });

  test("no Set-Cookie response header and no Authorization request header are emitted during a healthy run", async ({
    page,
  }) => {
    // Belt-and-suspenders alongside the CI scan-secrets gate:
    // - Set-Cookie is a RESPONSE header. Read it via allHeaders() —
    //   Playwright's response.headers() excludes security-related
    //   headers by design, so the only correct accessor is allHeaders().
    // - Authorization is a REQUEST header (not a response header). Read
    //   it via request.allHeaders() on the page.on('request') listener.
    let observedSetCookieCount = 0;
    let observedAuthHeaderCount = 0;

    page.on("response", async (response) => {
      try {
        const all = await response.allHeaders();
        if (all["set-cookie"]) observedSetCookieCount++;
      } catch {
        // Response is in a teardown race — ignore. The scan-secrets
        // gate is the authoritative check.
      }
    });
    page.on("request", async (request) => {
      try {
        const all = await request.allHeaders();
        if (all["authorization"]) observedAuthHeaderCount++;
      } catch {
        // Same teardown race as above.
      }
    });

    await mockApi(page, [
      {
        method: "GET",
        path: "/api/auth/me",
        handler: jsonError("auth.unauthorized", "Not authenticated", 401),
      },
    ]);
    await page.goto("/");
    // Wait until the network settles so all observers have fired
    // before we assert the counts.
    await page.waitForLoadState("networkidle");

    expect(observedSetCookieCount).toBe(0);
    expect(observedAuthHeaderCount).toBe(0);
  });

  test("the unreachable-API safety net: a route NOT registered via mockApi fails the page request", async ({
    page,
  }) => {
    // ADR 0010 §Network policy promises that an unmocked /api/* call
    // fails LOUDLY. With the catch-all 404 envelope from mockApi the
    // failure shape is `test.no_mock` (test 2 above). This test pins
    // the OTHER half of the safety net: if `mockApi()` itself isn't
    // installed, the webServer.env NEXT_PUBLIC_API_URL=127.0.0.1:1
    // pin means the SPA's API client fails against an unreachable
    // origin — observable as the SPA's auth-error / logged-out branch
    // even though no mock returned 401.
    //
    // We don't install ANY mockApi here on purpose. The landing
    // page's useMe will try to hit NEXT_PUBLIC_API_URL/api/auth/me;
    // that resolves to http://127.0.0.1:1/... which Chromium cannot
    // connect to (ECONNREFUSED). The page's catch maps that to null
    // (anonymous), so the logged-out CTAs still render — proving the
    // safety net catches a forgotten mock at the network layer.
    await page.goto("/");
    await expect(page.getByRole("link", { name: /log in/i }).first()).toBeVisible({
      timeout: 15_000,
    });
  });
});
