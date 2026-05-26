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

const TOKEN = "smoke-portal-token-abc";

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

    await page.goto(`/portal/${TOKEN}`);

    // Greeting renders from the mocked PortalInfo.
    await expect(
      page.getByRole("heading", {
        name: new RegExp(`hi ${portalInfo.vendorName}`, "i"),
      }),
    ).toBeVisible();

    // Drop a PDF via the hidden file input that react-dropzone
    // renders. setInputFiles drives the same code path users hit when
    // clicking the dropzone and picking a file.
    const fileBytes = Buffer.from("%PDF-1.7\nfake-but-pdf-shaped\n%%EOF");
    await page
      .locator('input[type="file"]')
      .setInputFiles({
        name: "vendor-coi.pdf",
        mimeType: "application/pdf",
        buffer: fileBytes,
      });

    // Received card appears with the file name; "Processing…" tag is
    // the page's per-file status during extraction.
    await expect(page.getByText(/^received$/i)).toBeVisible();
    await expect(page.getByText("vendor-coi.pdf")).toBeVisible();
    await expect(page.getByText(/processing…/i)).toBeVisible();
  });
});
