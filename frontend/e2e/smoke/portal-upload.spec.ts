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
 * uploaded artifacts; we use a synthetic token here.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk } from "../support/mock-api";
import { portalInfo } from "../support/fixtures";

// Keep tokens UNDER 16 chars so scan-secrets's "vendor portal token
// in URL path" pattern doesn't self-trip on a Playwright trace that
// captured this synthetic URL. Production tokens are much longer; a
// 12-char synthetic is enough to exercise the routing.
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

    // Wait for the GET /api/portal/{token} to fire BEFORE asserting
    // on the heading — explicit wait surfaces a mock-routing problem
    // (e.g. the interceptor not matching) as a clear timeout on this
    // line instead of as a misleading "heading not found" later.
    const portalInfoResponse = page.waitForResponse(
      (res) => res.url().includes(`/api/portal/${TOKEN}`) && res.status() === 200,
      { timeout: 15_000 },
    );

    await page.goto(`/portal/${TOKEN}`);
    await portalInfoResponse;

    // Greeting renders from the mocked PortalInfo.
    await expect(
      page.getByRole("heading", {
        name: new RegExp(`hi ${portalInfo.vendorName}`, "i"),
      }),
    ).toBeVisible({ timeout: 10_000 });

    // Wait on the upload POST response BEFORE asserting the Received
    // card. The hidden file input that react-dropzone wraps fires
    // onDrop on change; the page then POSTs to /upload and renders
    // the Received row on success. Arming the listener before the
    // setInputFiles call avoids the race where the response fires
    // before the listener is set up.
    const uploadResponse = page.waitForResponse(
      (res) =>
        res.url().includes(`/api/portal/${TOKEN}/upload`) &&
        res.status() === 200,
      { timeout: 15_000 },
    );

    // Drop a PDF via the hidden file input. setInputFiles drives the
    // same code path users hit when clicking the dropzone and
    // picking a file.
    const fileBytes = Buffer.from("%PDF-1.7\nfake-but-pdf-shaped\n%%EOF");
    await page
      .locator('input[type="file"]')
      .setInputFiles({
        name: "vendor-coi.pdf",
        mimeType: "application/pdf",
        buffer: fileBytes,
      });

    await uploadResponse;

    // Received card appears with the file name; "Processing…" tag is
    // the page's per-file status during extraction.
    //
    // Playwright's `getByText` uses substring matching by default and
    // doesn't normalize whitespace the way RTL does — the rendered
    // `<p>` text is " Received" (leading space from the CheckCircle2
    // icon sibling), so an `/^received$/i` strict regex misses while
    // a substring match cleanly hits.
    await expect(page.getByText("Received").first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("vendor-coi.pdf")).toBeVisible();
    await expect(page.getByText("Processing").first()).toBeVisible();
  });
});
