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
import { mockApi, jsonOk } from "../support/mock-api";
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

    // Arm the upload POST wait BEFORE setInputFiles so the response
    // is observable before assertions.
    const uploadResponse = page.waitForResponse(
      (res) =>
        res.url().includes(`/api/portal/${TOKEN}/upload`) &&
        res.status() === 200,
      { timeout: 15_000 },
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
  });
});
