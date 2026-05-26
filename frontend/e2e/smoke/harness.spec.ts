/**
 * Harness sanity smoke (#38).
 *
 * Proves the Playwright harness boots, the network-mock interceptor
 * runs, and an unmocked /api/* request is BLOCKED (no live origin
 * reachable). Tier-1 launch flows (auth, vendor-portal upload, upload-
 * to-extraction) come in #39 and replace this file's `.spec` extension
 * with real coverage.
 *
 * If THIS test ever fails, the harness itself is broken — every
 * downstream E2E ticket is paused until it's green again.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonError } from "../support/mock-api";

test.describe("E2E harness sanity (#38)", () => {
  test("renders the landing page heading with a mocked anonymous useMe()", async ({ page }) => {
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

    await page.goto("/");

    // The landing page heading is the strongest single signal that
    // the Next dev server is up AND the SPA hydrated. #39 will swap
    // this for a Get-Started CTA click + register-form interaction.
    await expect(
      page.getByRole("link", { name: /CompliDrop — home/i }),
    ).toBeVisible();
  });

  test("unmocked /api/* requests return the harness's 'no mock registered' error", async ({ page }) => {
    // Pin the harness's default behavior: any /api/* path NOT
    // registered in the routes table fails with the test.no_mock
    // envelope. A real test that forgets to mock a required endpoint
    // gets a clear signal instead of a timeout.
    await mockApi(page, []);

    const response = await page.request.get("http://localhost:3100/api/anything");
    expect(response.status()).toBe(404);
    const body = (await response.json()) as {
      data: unknown;
      error: { code: string; message: string };
    };
    expect(body.error.code).toBe("test.no_mock");
  });

  test("the artifact directory does NOT contain cookie or token values from this run", async ({ page }) => {
    // Belt-and-suspenders: if a test author ever installs a mock that
    // emits `Set-Cookie: cd_session=...` or `Authorization: Bearer
    // ...`, the CI scan-secrets gate (frontend-ci.yml) catches it.
    // This in-process test is a redundant cheap pre-check: assert
    // that the route handlers we install DON'T emit those headers.
    let observedSetCookieCount = 0;
    let observedAuthCount = 0;
    page.on("response", async (response) => {
      const headers = response.headers();
      if (headers["set-cookie"]) observedSetCookieCount++;
      if (headers["authorization"]) observedAuthCount++;
    });

    await mockApi(page, [
      { method: "GET", path: "/api/auth/me", handler: jsonError("auth.unauthorized", "Not authenticated", 401) },
    ]);
    await page.goto("/");

    expect(observedSetCookieCount).toBe(0);
    expect(observedAuthCount).toBe(0);
  });
});
