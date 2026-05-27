/**
 * Flow 1 smoke E2E (#39 AC #1): sign-up → land on dashboard.
 *
 * Visit /register, fill the form, submit against a mocked POST
 * /api/auth/register that returns the Me envelope, confirm the SPA
 * redirects to /dashboard, and confirm the dashboard chrome renders
 * the personalized "Welcome" heading + the org name + a navigation
 * item, sourced from the cached Me — proving cookie-set-by-server
 * semantics flow through the SPA without any test code touching the
 * cookie value.
 *
 * Auth is httpOnly-cookie per CLAUDE.md (cd_session/cd_refresh). The
 * mock does NOT emit Set-Cookie — the scan-secrets gate would flag
 * it. The SPA flow works WITHOUT cookies because
 * `useRegister.onSuccess` seeds the Me into the TanStack Query cache,
 * and the dashboard layout reads from that cache (staleTime: 30s in
 * production) before useMe's queryFn fires. A real production
 * round-trip would also set the cookies via Set-Cookie; the test
 * asserts the SPA chrome regardless.
 *
 * Companion log-in coverage lives in `login-flow.spec.ts` (#90) and
 * pins the useLogin.onSuccess cache-seeding contract with an explicit
 * `meHits === 0` assertion on `/api/auth/me`.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk, waitForApi } from "../support/mock-api";
import {
  authedMe,
  authedMeRoute,
  emptyDashboardRoutes,
} from "../support/fixtures";

test.describe("Flow 1 — sign-up → dashboard (#39)", () => {
  test("a new user fills the register form, lands on /dashboard with the full chrome visible", async ({
    page,
  }) => {
    // Capture the POST body for the request-shape pin.
    let registerBody: Record<string, unknown> | undefined;

    await mockApi(page, [
      // POST /api/auth/register: returns the Me. NOTE: no Set-Cookie
      // emitted (the scan-secrets gate would flag it; the SPA's
      // cache-seeding handles auth state in the test environment).
      {
        method: "POST",
        path: "/api/auth/register",
        handler: async (route, request) => {
          registerBody = JSON.parse(request.postData() ?? "{}");
          return jsonOk(authedMe)(route);
        },
      },
      // useMe() on the dashboard layout reads from cache (seeded by
      // useRegister.onSuccess). It SHOULDN'T fire the queryFn within
      // its 60s staleTime, but if it does for any reason (e.g. cache
      // eviction), the authedMe handler keeps the dashboard authed
      // instead of bouncing to /login.
      authedMeRoute,
      // The dashboard PAGE renders three independent queries. Mock
      // them with empty payloads so trace files aren't polluted with
      // `test.no_mock` 404s and a future page-body assertion
      // (welcome heading is checked below) isn't fragile against
      // missing data.
      ...emptyDashboardRoutes,
    ]);

    await page.goto("/register");

    await expect(
      page.getByRole("heading", { name: /start dropping docs/i }),
    ).toBeVisible();

    // Arm the register POST listener BEFORE the click so the test
    // pins "the form actually submitted" rather than "the SPA happened
    // to navigate."
    const registerResponse = waitForApi(page, "POST", "/api/auth/register", {
      status: 200,
    });

    await page.locator('input[name="fullName"]').fill("Smoke Owner");
    await page.locator('input[name="companyName"]').fill("Smoke Test Inc");
    await page.locator('input[name="email"]').fill("owner@smoke.test");
    await page
      .locator('input[name="password"]')
      .fill("verystrongsmokepass1");

    await page.getByRole("button", { name: /create my account/i }).click();
    await registerResponse;

    // Landed on /dashboard.
    await page.waitForURL((u) => u.pathname === "/dashboard", {
      timeout: 10_000,
    });

    // Full-chrome assertions: org name in sidebar + Documents nav link
    // + welcome heading on the dashboard page body. A regression in
    // either the (dashboard)/layout.tsx (sidebar) OR /dashboard/page.tsx
    // (body) trips here.
    await expect(
      page.getByText(authedMe.organizationName).first(),
    ).toBeVisible();
    await expect(
      page.getByRole("link", { name: /documents/i }).first(),
    ).toBeVisible();
    await expect(
      page.getByRole("heading", { name: /welcome,/i }),
    ).toBeVisible();

    // Request-shape pin.
    expect(registerBody).toMatchObject({
      fullName: "Smoke Owner",
      companyName: "Smoke Test Inc",
      email: "owner@smoke.test",
      password: "verystrongsmokepass1",
    });
    expect((registerBody?.timeZone as string) ?? "").toMatch(/^(UTC|.+\/.+)$/);
    expect(registerBody).not.toHaveProperty("plan");
  });
});
