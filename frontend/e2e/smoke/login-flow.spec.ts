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
 *
 * Credentials below are synthetic-by-design: `owner@smoke.test` is
 * RFC-6761 reserved, `verystrongsmokepass1` is a phrase-shaped string
 * with no secret entropy and intentionally outside the scan-secrets
 * pattern table (`frontend/e2e/scripts/scan-secrets.mjs` targets
 * cookie/JWT/SSN/EIN/portal-token shapes only).
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk, waitForApi } from "../support/mock-api";
import { authedMe, emptyDashboardRoutes } from "../support/fixtures";

test.describe("Flow 1b — log-in → dashboard (#90)", () => {
  test("an existing user signs in, lands on /dashboard with the full chrome visible", async ({
    page,
  }) => {
    let loginBody: Record<string, unknown> | undefined;
    // Track `/api/auth/me` hits to pin the useLogin.onSuccess cache-
    // seeding contract: the docstring claims the dashboard renders
    // WITHOUT a refetch because the Me envelope was seeded into the
    // TanStack Query cache by the login mutation's onSuccess. Without
    // this counter, the test would pass equally well if cache-seeding
    // broke and the dashboard quietly refetched /api/auth/me — exactly
    // the regression the docstring promises to catch. (#90 followup)
    let meHits = 0;

    await mockApi(page, [
      {
        method: "POST",
        path: "/api/auth/login",
        handler: async (route, request) => {
          loginBody = JSON.parse(request.postData() ?? "{}");
          return jsonOk(authedMe)(route);
        },
      },
      // Safety-net /api/auth/me handler — counted so the assertion at
      // the bottom of the test can prove the dashboard did NOT refetch
      // within useMe's 60s staleTime. If useLogin.onSuccess ever stops
      // seeding ME_KEY, this counter ticks up and the assertion fails
      // with a useful "cache wasn't seeded" signal.
      {
        method: "GET",
        path: "/api/auth/me",
        handler: async (route) => {
          meHits++;
          return jsonOk(authedMe)(route);
        },
      },
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

    // Fill by accessible label (#137). #76 wired every <label htmlFor=…>
    // ↔ <input id=…> on this form, so Playwright's getByLabel resolves
    // the same DOM node the user reaches via the label. Companion
    // Vitest migration: #132.
    await page.getByLabel(/^email$/i).fill("owner@smoke.test");
    await page.getByLabel(/^password$/i).fill("verystrongsmokepass1");

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

    // Cache-seeding contract pin — useLogin.onSuccess must seed
    // ME_KEY in the TanStack Query cache so the dashboard layout's
    // useMe() hook reads from cache within its 60s staleTime instead
    // of refetching. If this assertion fires, it's the exact
    // regression the docstring above promises to catch.
    expect(meHits).toBe(0);

    // Request-shape pin — the form actually sent what the user typed
    // and nothing else. Mirrors the negative pins in auth-flow.spec.ts
    // (`.not.toHaveProperty("plan")` + the strict TZ regex).
    expect(loginBody).toMatchObject({
      email: "owner@smoke.test",
      password: "verystrongsmokepass1",
    });
    // The login Zod schema (login/page.tsx) declares only email +
    // password. A regression that added a `rememberMe` checkbox to
    // the form without backend support, or that leaked an internal
    // flag through the payload, would slip past `toMatchObject`
    // (which allows extra properties). The exact-keyset pin catches
    // those.
    expect(Object.keys(loginBody ?? {}).sort()).toEqual([
      "email",
      "password",
    ]);
  });
});
