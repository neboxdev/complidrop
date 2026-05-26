/**
 * Flow 1b smoke E2E (#90 — AC #1 literal coverage):
 * existing-user log-in → land on dashboard.
 *
 * Companion to auth-flow.spec.ts (the sign-up half). #39's AC #1
 * reads "a new user can sign up, **log in**, and land on the authed
 * dashboard (cookie-based session)." Sign-up is exercised by
 * auth-flow.spec.ts; this spec covers the log-in half so the AC has
 * tier-1 coverage at the E2E layer in addition to the comprehensive
 * Vitest tests in login/page.test.tsx.
 *
 * Why log-in is worth its own E2E:
 * smoke is supposed to prove "the wiring at the integration level"
 * per ADR 0010, and log-in is the most-used auth entry point for
 * returning customers. A regression where useLogin.onSuccess no
 * longer seeds the cache (or the form's router.push is broken) would
 * silently rebuild the entire dashboard layout against
 * `me.data === null` and bounce the user to /login — exactly the
 * cross-page break smoke E2E exists to catch.
 *
 * Same Set-Cookie / cache-seeding trade-off as auth-flow.spec.ts: the
 * mock does NOT emit Set-Cookie (scan-secrets gate would flag it);
 * the SPA flow works because `useLogin.onSuccess` seeds the Me into
 * the TanStack Query cache, which the dashboard layout reads before
 * useMe's queryFn fires.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk, waitForApi } from "../support/mock-api";
import {
  authedMe,
  authedMeRoute,
  emptyDashboardRoutes,
} from "../support/fixtures";

test.describe("Flow 1b — log-in → dashboard (#90)", () => {
  test("an existing user signs in, lands on /dashboard with the full chrome visible", async ({
    page,
  }) => {
    let loginBody: Record<string, unknown> | undefined;

    await mockApi(page, [
      {
        method: "POST",
        path: "/api/auth/login",
        handler: async (route, request) => {
          loginBody = JSON.parse(request.postData() ?? "{}");
          return jsonOk(authedMe)(route);
        },
      },
      // useMe() on the dashboard layout reads from cache (seeded by
      // useLogin.onSuccess). If it ever fires queryFn within the 60s
      // staleTime, this handler keeps the dashboard authed instead of
      // bouncing to /login.
      authedMeRoute,
      // The dashboard page renders three independent queries. Mock
      // them with empty payloads so traces aren't polluted with
      // test.no_mock 404s.
      ...emptyDashboardRoutes,
    ]);

    await page.goto("/login");

    await expect(
      page.getByRole("heading", { name: /welcome back/i }),
    ).toBeVisible();

    // Arm the login POST listener BEFORE the click so the test pins
    // "the form actually submitted" rather than "the SPA happened to
    // navigate." Mirrors the shape in auth-flow.spec.ts.
    const loginResponse = waitForApi(page, "POST", "/api/auth/login", {
      status: 200,
    });

    await page.locator('input[name="email"]').fill("owner@smoke.test");
    await page.locator('input[name="password"]').fill("verystrongsmokepass1");

    await page.getByRole("button", { name: /sign in/i }).click();
    await loginResponse;

    // Landed on /dashboard.
    await page.waitForURL((u) => u.pathname === "/dashboard", {
      timeout: 10_000,
    });

    // Full-chrome assertions (mirrors auth-flow.spec.ts): org name in
    // sidebar + Documents nav link + welcome heading on the dashboard
    // body. A regression in either (dashboard)/layout.tsx (sidebar)
    // OR /dashboard/page.tsx (body) trips here.
    await expect(
      page.getByText(authedMe.organizationName).first(),
    ).toBeVisible();
    await expect(
      page.getByRole("link", { name: /documents/i }).first(),
    ).toBeVisible();
    await expect(
      page.getByRole("heading", { name: /welcome,/i }),
    ).toBeVisible();

    // Request-shape pin — the form actually sent what the user typed.
    expect(loginBody).toMatchObject({
      email: "owner@smoke.test",
      password: "verystrongsmokepass1",
    });
  });
});
