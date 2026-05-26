/**
 * Shared bits the rest of the test harness builds on.
 *
 * Keep this file free of imports from `msw` / `@testing-library/*` so it stays
 * cheap to pull into a fixture module or a non-component test.
 */

/**
 * The base URL `frontend/src/lib/api.ts` falls back to when
 * `NEXT_PUBLIC_API_URL` is unset. `vitest.setup.ts` pins this same value into
 * `process.env` BEFORE the api module is imported, so MSW handlers and the
 * request URLs always agree on the origin.
 */
export const TEST_API_BASE = "http://localhost:5292";

/**
 * Wrap a payload in the standard `ApiEnvelope<T>` success shape.
 *
 *     server.use(http.get(url("/api/dashboard/stats"), () => jsonOk({ ... })));
 */
export function jsonOk<T>(data: T, init: { status?: number } = {}): Response {
  return new Response(JSON.stringify({ data, error: null }), {
    status: init.status ?? 200,
    headers: { "Content-Type": "application/json" },
  });
}

/**
 * Wrap an error in the standard `ApiEnvelope<T>` error shape. The shape
 * matches what `frontend/src/lib/api.ts:request()` parses (`code` + `message`,
 * optional `correlationId`), so handlers can simulate every server-side
 * failure path without bypassing the api client's error mapping.
 */
export function jsonError(
  code: string,
  message: string,
  init: { status?: number; correlationId?: string } = {},
): Response {
  return new Response(
    JSON.stringify({
      data: null,
      error: { code, message, correlationId: init.correlationId },
    }),
    {
      status: init.status ?? 400,
      headers: { "Content-Type": "application/json" },
    },
  );
}

/**
 * Resolve a path against `TEST_API_BASE` so handler URLs read naturally:
 *
 *     http.get(url("/api/auth/me"), …)
 *
 * MSW matches exact URLs (host included), so building handlers without this
 * helper is the most common foot-gun in this harness.
 */
export function url(path: string): string {
  return `${TEST_API_BASE}${path}`;
}
