/**
 * Flow 2 smoke E2E (#39 AC #2): vendor opens a public portal link
 * and uploads a file.
 *
 * Public, token-based — no session cookies involved. The portal page
 * hand-rolls its fetch (it can't use the cookie-bearing api client),
 * so MSW-style mocks at the page.route level work the same as for
 * authed flows: GET portal info, POST upload, observe the inline
 * Received UI.
 *
 * Per CLAUDE.md: portal endpoints are PUBLIC, treat token as
 * untrusted. The scan-secrets gate enforces no token leakage into
 * uploaded artifacts; the test token is intentionally <16 chars to
 * stay under the scanner's URL-path threshold (production tokens are
 * much longer; the synthetic length only affects test data).
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk, waitForApi } from "../support/mock-api";
import { makeFakePdf, portalInfo } from "../support/fixtures";

const TOKEN = "smoke-tok-12";

test.describe("Flow 2 — vendor portal upload (#39)", () => {
  test("vendor opens /portal/{token}, drops a PDF, sees the Received row", async ({
    page,
  }) => {
    await mockApi(page, [
      {
        method: "GET",
        path: "/api/portal/:token",
        handler: jsonOk(portalInfo),
      },
      {
        method: "POST",
        path: "/api/portal/:token/upload",
        handler: jsonOk({
          uploadId: "u_smoke_01",
          extractionStatus: "Pending",
          message: "Received",
        }),
      },
    ]);

    // Arm the portal-info wait BEFORE goto — explicit timing surfaces
    // a mock-routing problem as a clear timeout on this line.
    const portalInfoResponse = waitForApi(page, "GET", "/api/portal/:token", {
      status: 200,
    });

    await page.goto(`/portal/${TOKEN}`);
    await portalInfoResponse;

    // Greeting renders from the mocked PortalInfo.
    await expect(
      page.getByRole("heading", {
        name: new RegExp(`hi ${portalInfo.vendorName}`, "i"),
      }),
    ).toBeVisible({ timeout: 10_000 });

    // Head-injection token-absence assertion (#127 / #85 followup).
    // The Vitest component helper `assertNotInDom` (frontend/src/test/
    // security.ts) scans `document.body` only — `<head>` is documented
    // as out of scope, with the explicit promise that head-injection
    // leaks are caught in the E2E layer. This is that layer. A
    // regression that injected the portal token into a `<meta name=
    // "vendor-token" content="…">`, a `<title>`, or a `<script>` const
    // inside `<head>` would slip past every component test (scoped
    // to body) AND scan-secrets.mjs (its pattern table targets cookie
    // names + URL paths, not raw token strings in meta tags). The
    // innerHTML scan covers attribute values, hidden nodes, text
    // content of `<title>`, and `<script>` body — every channel a
    // future meta-injection regression would route through. Token
    // has no HTML-special chars so the literal scan is sufficient (a
    // production-shaped base64 token has none either; if a future
    // caller passes a different shape, mirror security.ts's
    // escaped-form scan here too).
    await expectTokenNotInHead(page, TOKEN);

    // Arm the upload POST wait BEFORE setInputFiles so the response
    // is observable before assertions.
    const uploadResponse = waitForApi(
      page,
      "POST",
      "/api/portal/:token/upload",
      { status: 200 },
    );

    await page.locator('input[type="file"]').setInputFiles(makeFakePdf("vendor-coi.pdf"));
    await uploadResponse;

    // Received card appears with the file name. Scope the
    // "Processing" assertion to the row containing the uploaded file
    // so a future copy change in the dropzone idle/quota states
    // can't accidentally satisfy a substring-only match.
    await expect(page.getByText("Received").first()).toBeVisible({
      timeout: 10_000,
    });
    const row = page.locator("li").filter({ hasText: "vendor-coi.pdf" });
    await expect(row).toBeVisible();
    await expect(row.getByText(/processing/i)).toBeVisible();

    // Re-assert the head is still clean after the upload mutation
    // settles. Some leak paths surface only after a state change —
    // e.g. a future "show last-uploaded file metadata in a meta tag"
    // feature inadvertently emitting `<meta name="last-upload-token"
    // content="…">` would pass the pre-upload check above but fail
    // here, catching the regression in the same spec.
    await expectTokenNotInHead(page, TOKEN);
  });
});

/**
 * Asserts that the rendered `<head>` of the current page does NOT
 * contain `token` (literal substring scan of `head.innerHTML`). The
 * companion to the Vitest `assertNotInDom` helper at
 * frontend/src/test/security.ts — that one scans `document.body` by
 * contract and explicitly defers head coverage to here (#127).
 *
 * Scans innerHTML rather than textContent: a `<meta>` injection's
 * `content="…"` attribute leak contributes nothing to textContent
 * but is the most realistic head-injection vector.
 */
async function expectTokenNotInHead(
  page: import("@playwright/test").Page,
  token: string,
): Promise<void> {
  const headHtml = await page.locator("head").innerHTML();
  expect(
    headHtml,
    `portal token must not appear inside <head> (length ${token.length}, ` +
      `prefix "${token.slice(0, 4)}" suffix "${token.slice(-4)}") — a ` +
      `regression injected the token via a meta tag, <title>, or <script>; ` +
      `chase the leak with a full-head dump locally.`,
  ).not.toContain(token);
}
