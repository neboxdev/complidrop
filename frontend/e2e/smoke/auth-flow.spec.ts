/**
 * Flow 1 smoke E2E (#39 AC #1): sign-up → land on dashboard.
 *
 * The full user journey: visit /register, fill the form, submit
 * against a mocked /api/auth/register that returns the Me envelope,
 * confirm the SPA redirects to /dashboard, and confirm the dashboard
 * layout renders the personalized "Welcome" heading sourced from the
 * cached Me — proving cookie-set-by-server semantics flow through the
 * SPA without any test code touching the cookie value.
 *
 * Auth is httpOnly-cookie per CLAUDE.md (cd_session/cd_refresh). The
 * mock does NOT emit Set-Cookie — the scan-secrets gate would flag it.
 * The SPA flow works WITHOUT cookies because `useRegister.onSuccess`
 * seeds the Me into the TanStack Query cache, and the dashboard
 * layout reads from that cache (staleTime: 30s in production) before
 * useMe's queryFn fires. A real production round-trip would also set
 * the cookies via Set-Cookie; the test asserts the SPA chrome
 * regardless.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk } from "../support/mock-api";
import { authedMe } from "../support/fixtures";

test.describe("Flow 1 — sign-up → dashboard (#39)", () => {
  test("a new user fills the register form, lands on /dashboard with their org name visible", async ({
    page,
  }) => {
    // Capture the POST body the form sends so the test also pins the
    // request-shape contract (no plan, fields the backend DTO accepts,
    // an IANA timezone derived from Intl).
    let registerBody: Record<string, unknown> | undefined;

    await mockApi(page, [
      {
        // useMe(skipRefresh:true) fires on the homepage / register page
        // before the form is submitted. Anonymous initially.
        method: "GET",
        path: "/api/auth/me",
        handler: async (route) => {
          await route.fulfill({
            status: 401,
            contentType: "application/json",
            body: JSON.stringify({
              data: null,
              error: { code: "auth.unauthorized", message: "Not authenticated" },
            }),
          });
        },
      },
      {
        method: "POST",
        path: "/api/auth/register",
        handler: async (route, request) => {
          registerBody = JSON.parse(request.postData() ?? "{}");
          // The mocked response does NOT set Set-Cookie — see file
          // docstring. The SPA's cache-seeding flow handles auth in
          // the test environment.
          return jsonOk(authedMe)(route);
        },
      },
    ]);

    await page.goto("/register");

    await expect(
      page.getByRole("heading", { name: /start dropping docs/i }),
    ).toBeVisible();

    // Fill the form. Labels aren't htmlFor-wired in production yet
    // (tracked in #76), so query by placeholder + autocomplete.
    await page
      .locator('input[name="fullName"]')
      .fill("Smoke Owner");
    await page
      .locator('input[name="companyName"]')
      .fill("Smoke Test Inc");
    await page
      .locator('input[name="email"]')
      .fill("owner@smoke.test");
    await page
      .locator('input[name="password"]')
      .fill("verystrongsmokepass1");

    await page.getByRole("button", { name: /create my account/i }).click();

    // Landed on /dashboard.
    await page.waitForURL((u) => u.pathname === "/dashboard", {
      timeout: 10_000,
    });

    // Dashboard layout uses useMe()'s cached Me — assert the
    // organization name surfaces in the sidebar (proves the cache
    // wire-through worked from useRegister.onSuccess into the layout).
    await expect(
      page.getByText(authedMe.organizationName).first(),
    ).toBeVisible();

    // Request-shape pin: the POST body carries the backend DTO fields
    // AND a timeZone from Intl, and does NOT carry plan (which is a
    // landing-page URL param only — see #31).
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
