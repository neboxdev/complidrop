/**
 * #370 — the documents filter/URL contract, verified against the REAL Next.js
 * router in a real browser.
 *
 * WHY THIS FILE EXISTS. The vitest suite covers this page thoroughly, and it
 * shipped fully green TWICE against a page that was broken in production —
 * once with the filters flashing backwards, once with every deep link rendering
 * an unfiltered list. Both times the cause was the same: `src/test/navigation.ts`
 * models the ordering between `useSearchParams()` and `window.location`, and a
 * harness that models it wrongly cannot express the bug. These specs have no
 * such model. They drive the actual router, so they cannot be fooled the same
 * way. See docs/adr/0039-documents-url-source-of-truth-overlay.md.
 *
 * CRUCIALLY these use CLIENT-SIDE navigation (clicking an in-app link), not
 * `page.goto`. A hard load sets `window.location` correctly before React ever
 * renders, so a `goto`-based version passes against the broken code and proves
 * nothing. The bug only exists on a router navigation, where Next updates
 * `searchParams` in the render phase and `window.location` afterwards in a
 * commit-phase `useInsertionEffect`.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk } from "../support/mock-api";
import { authedMeRoute, emptyDashboardRoutes } from "../support/fixtures";

const emptyList = jsonOk({ items: [], total: 0, page: 1, pageSize: 20 });

const listRoutes = (sink: string[]) => [
  authedMeRoute,
  // Non-zero, so the dashboard renders its StatCards rather than the
  // "Get started" onboarding state (which carries no filter deep links).
  {
    method: "GET" as const,
    path: "/api/dashboard/stats",
    handler: jsonOk({
      totalDocuments: 6,
      compliant: 2,
      nonCompliant: 3,
      expiringSoon: 1,
      expired: 1,
      pendingExtraction: 0,
      totalVendors: 2,
      complianceRate: 33,
    }),
  },
  ...emptyDashboardRoutes,
  {
    method: "GET" as const,
    path: "/api/documents",
    handler: async (
      route: import("@playwright/test").Route,
      request: import("@playwright/test").Request,
    ) => {
      sink.push(request.url());
      await emptyList(route);
    },
  },
  {
    method: "GET" as const,
    path: "/api/vendors",
    handler: jsonOk({ items: [], total: 0 }),
  },
];

test.describe("#370 — documents filters vs the URL (real router)", () => {
  test("the dashboard's Non-compliant card lands on a FILTERED list", async ({ page }) => {
    const requested: string[] = [];
    await mockApi(page, listRoutes(requested));

    await page.goto("/dashboard");
    await expect(page.getByRole("heading", { name: /welcome/i }).first()).toBeVisible();

    // Client-side nav — the production path that a `window.location`-preferring
    // page renders unfiltered, permanently.
    await page.getByRole("link", { name: /non-compliant/i }).first().click();
    await expect(page.getByRole("heading", { name: /^documents$/i })).toBeVisible();

    await expect(page.getByLabel(/filter by compliance status/i)).toHaveValue("NonCompliant");
    await expect
      .poll(() => requested.some((u) => u.includes("status=NonCompliant")), { timeout: 10_000 })
      .toBe(true);
    // The blocker's exact signature: the control is right but the LIST is not.
    const unfiltered = requested.filter((u) => !u.includes("status=NonCompliant"));
    expect(unfiltered, `unfiltered /api/documents requests: ${JSON.stringify(unfiltered)}`).toEqual(
      [],
    );
    expect(new URL(page.url()).searchParams.get("status")).toBe("NonCompliant");
  });

  test("scenario B: the sidebar's Documents link drops the filtered view", async ({ page }) => {
    const requested: string[] = [];
    await mockApi(page, listRoutes(requested));

    await page.goto("/documents?status=Expired");
    await expect(page.getByLabel(/filter by compliance status/i)).toHaveValue("Expired");

    requested.length = 0;
    // Same-route client-side nav: no remount, so the old `useState` initializers
    // never re-ran and the list stayed filtered under a bare URL.
    await page.getByRole("link", { name: /^documents$/i }).first().click();

    await expect(page.getByLabel(/filter by compliance status/i)).toHaveValue("");
    await expect.poll(() => new URL(page.url()).searchParams.get("status")).toBeNull();
    await expect
      .poll(() => requested.some((u) => !u.includes("status=")), { timeout: 10_000 })
      .toBe(true);
  });

  test("a filter touched right after a client-side nav keeps the deep-linked param", async ({
    page,
  }) => {
    const requested: string[] = [];
    await mockApi(page, listRoutes(requested));

    await page.goto("/dashboard");
    await expect(page.getByRole("heading", { name: /welcome/i }).first()).toBeVisible();
    await page.getByRole("link", { name: /compliant/i }).first().click();
    await expect(page.getByRole("heading", { name: /^documents$/i })).toBeVisible();

    // Composing the write on `window.location` — still the PREVIOUS route's
    // during a navigation render — silently drops the deep-linked param here.
    await page.getByLabel(/filter by document type/i).selectOption("permit");

    await expect.poll(() => new URL(page.url()).searchParams.get("type")).toBe("permit");
    expect(new URL(page.url()).searchParams.get("status")).toBeTruthy();
    // And the pick must not snap back while the transition catches up.
    await expect(page.getByLabel(/filter by document type/i)).toHaveValue("permit");
  });
});
