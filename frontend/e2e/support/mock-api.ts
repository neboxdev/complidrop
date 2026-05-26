/**
 * Playwright network-mock helper (#38).
 *
 * Policy: NO live calls to Stripe / Resend / Document AI / Gemini /
 * Postgres / Azure Blob from any E2E test. The CompliDrop API itself
 * is also mocked — these tests prove FRONTEND wiring, not the full
 * stack. A real-stack run is a separate quality concern out of scope
 * for #38/#39.
 *
 * Mechanism: `mockApi(page, routes)` installs a catch-all interceptor
 * on `**\/api\/**` and dispatches per-route. Anything not matched
 * returns a JSON envelope error so the test SEES the gap instead of
 * timing out on a real network connection.
 *
 * The `playwright.config.ts` `webServer.env` also pins
 * `NEXT_PUBLIC_API_URL=http://127.0.0.1:1` so any code path that
 * forgot to install a mock fails LOUDLY (ECONNREFUSED) rather than
 * silently leaking traffic to a real origin. Note: this CI safety
 * net relies on Playwright actually STARTING the dev server. Locally,
 * `webServer.reuseExistingServer: true` means a pre-existing
 * `next dev --port 3100` from a previous session inherits .env.local
 * and bypasses the pin — see the README's "Local dev caveat" section.
 */
import type { Page, Route, Request } from "@playwright/test";

export type ApiRouteHandler = (route: Route, request: Request) => Promise<void> | void;

/**
 * The route table is matched in declared order; first MATCH wins.
 *
 * Matching is EXACT segment-count with `:param` wildcards — not
 * prefix matching. `path: "/api/portal"` will NOT match
 * `/api/portal/abc/info`; declare the full path or use `:token`:
 *
 *     { path: "/api/portal/:token" }            // matches one segment
 *     { path: "/api/portal/:token/upload" }     // matches two segments
 *
 * Order matters: a wildcard route declared BEFORE a literal sibling
 * with the same segment count will shadow the literal. Declare
 * more-specific (literal) paths first.
 */
export type MockRoutes = Array<{
  method?: "GET" | "POST" | "PUT" | "DELETE" | "PATCH";
  path: string;
  handler: ApiRouteHandler;
}>;

const ENVELOPE_404 = {
  data: null,
  error: { code: "test.no_mock", message: "No mock registered for this URL" },
};

export async function mockApi(page: Page, routes: MockRoutes): Promise<void> {
  await page.route("**/api/**", async (route, request) => {
    const url = new URL(request.url());
    const method = request.method().toUpperCase();
    for (const entry of routes) {
      const entryMethod = (entry.method ?? "GET").toUpperCase();
      if (entryMethod !== method) continue;
      if (!pathMatches(entry.path, url.pathname)) continue;
      await entry.handler(route, request);
      return;
    }
    // Unhandled: fail loudly so the test author sees the missing mock.
    await route.fulfill({
      status: 404,
      contentType: "application/json",
      body: JSON.stringify(ENVELOPE_404),
    });
  });
}

/**
 * Convenience helpers that match the `ApiEnvelope<T>` shape the
 * frontend's `lib/api.ts` parses. Tests build their handler bodies
 * via these instead of hand-rolling the envelope every time.
 */
export function jsonOk<T>(data: T) {
  return async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ data, error: null }),
    });
  };
}

export function jsonError(code: string, message: string, status = 400) {
  return async (route: Route) => {
    await route.fulfill({
      status,
      contentType: "application/json",
      body: JSON.stringify({ data: null, error: { code, message } }),
    });
  };
}

// EXACT segment-count path matcher with `:param` wildcards. Avoids
// pulling in a routing library for two test segments. Rejects empty
// segments in the template so a typo'd `/api//portal` is caught at
// registration via the explicit `EMPTY_SEGMENT` throw.
function pathMatches(template: string, actual: string): boolean {
  if (template.includes("//")) {
    throw new Error(
      `mockApi: template "${template}" contains an empty segment ('//') — likely a typo`,
    );
  }
  const tParts = template.split("/").filter(Boolean);
  const aParts = actual.split("/").filter(Boolean);
  if (tParts.length !== aParts.length) return false;
  for (let i = 0; i < tParts.length; i++) {
    if (tParts[i].startsWith(":")) continue; // path param wildcard
    if (tParts[i] !== aParts[i]) return false;
  }
  return true;
}
