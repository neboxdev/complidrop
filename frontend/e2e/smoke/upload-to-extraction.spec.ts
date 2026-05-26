/**
 * Flow 3 smoke E2E (#39 AC #3): authed user uploads a document, sees
 * extraction status advance through the full Pending → Processing →
 * Completed state machine, lands on the detail view.
 *
 * Spans three pages: /documents (list), the upload action, and
 * /documents/[id] (detail). The detail page polls every 3s while
 * extraction is Pending/Processing — this test sequences the GET
 * /api/documents/:id responses so:
 *   call #1 → Pending
 *   call #2 (after 3s poll) → Processing
 *   call #3 (after another 3s poll) → Completed
 *
 * Total wall-clock is ~7-8s for the polling segment. Stays under
 * the ADR's "< 90s on CI" target.
 *
 * Upload race avoidance: the list mock returns an empty list BEFORE
 * the upload POST and the populated list ONLY after the upload
 * succeeds — so the link-to-detail click can't race ahead of the
 * mutation's cache invalidation. Combined with an explicit
 * waitForResponse on the upload POST, the test pins that the upload
 * actually fired.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk } from "../support/mock-api";
import {
  authedMeRoute,
  makeDocumentDetail,
  makeFakePdf,
} from "../support/fixtures";

const DOC_ID = "d_smoke_pending_01";

test.describe("Flow 3 — upload → extraction → detail (#39)", () => {
  test("authed user uploads a doc, polls status Pending → Processing → Completed, sees the field", async ({
    page,
  }) => {
    let detailCalls = 0;
    let didUpload = false;

    await mockApi(page, [
      authedMeRoute,
      // Documents list: empty BEFORE upload, populated AFTER. The
      // `didUpload` flag flips inside the upload POST handler below;
      // closures share scope across route entries within this
      // mockApi call. This gates the link click on the upload's
      // cache-invalidation actually happening.
      {
        method: "GET",
        path: "/api/documents",
        handler: async (route) => {
          await route.fulfill({
            status: 200,
            contentType: "application/json",
            body: JSON.stringify({
              data: {
                items: didUpload
                  ? [
                      {
                        id: DOC_ID,
                        originalFileName: "smoke.pdf",
                        documentType: "COI",
                        vendorName: null,
                        vendorId: null,
                        extractionStatus: "Pending",
                        extractionConfidence: null,
                        complianceStatus: "Pending",
                        effectiveDate: null,
                        expirationDate: null,
                        daysUntilExpiry: null,
                        createdAt: "2026-05-26T12:00:00Z",
                      },
                    ]
                  : [],
                total: didUpload ? 1 : 0,
                page: 1,
                pageSize: 50,
              },
              error: null,
            }),
          });
        },
      },
      // Upload endpoint: flips `didUpload` so the next list-fetch
      // (triggered by useUploadDocument.onSuccess's invalidation)
      // returns the new row.
      {
        method: "POST",
        path: "/api/documents/upload",
        handler: async (route) => {
          didUpload = true;
          return jsonOk({
            id: DOC_ID,
            originalFileName: "smoke.pdf",
            extractionStatus: "Pending",
          })(route);
        },
      },
      // Detail page sequence:
      //   call #1 → Pending     (initial mount)
      //   call #2 → Processing  (worker grabbed the doc)
      //   call #3+ → Completed  (extraction finished)
      // Asserts the full state machine, not just one transition.
      {
        method: "GET",
        path: "/api/documents/:id",
        handler: async (route) => {
          detailCalls++;
          const pending = makeDocumentDetail({
            id: DOC_ID,
            extractionStatus: "Pending",
            complianceStatus: "NonCompliant",
          });
          const processing = makeDocumentDetail({
            id: DOC_ID,
            extractionStatus: "Processing",
            complianceStatus: "NonCompliant",
          });
          const completed = makeDocumentDetail({
            id: DOC_ID,
            extractionStatus: "Completed",
            extractionConfidence: 0.94,
            complianceStatus: "Compliant",
            expirationDate: "2026-12-31T00:00:00Z",
            fields: [
              {
                id: "f_policy",
                fieldName: "PolicyNumber",
                fieldValue: "POL-SMOKE-12345",
                fieldType: "string",
                confidence: 0.95,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          });
          const payload =
            detailCalls === 1 ? pending : detailCalls === 2 ? processing : completed;
          await route.fulfill({
            status: 200,
            contentType: "application/json",
            body: JSON.stringify({ data: payload, error: null }),
          });
        },
      },
    ]);

    await page.goto("/documents");
    await expect(
      page.getByRole("heading", { name: /^documents$/i }),
    ).toBeVisible();

    // Arm the upload-response wait BEFORE setInputFiles so the test
    // pins the upload actually fired (sibling to flow 2's pattern).
    const uploadResponse = page.waitForResponse(
      (res) =>
        res.url().includes("/api/documents/upload") && res.status() === 200,
      { timeout: 15_000 },
    );

    await page
      .locator('input[type="file"]')
      .setInputFiles(makeFakePdf("smoke.pdf"));
    await uploadResponse;

    // The list mock's `didUpload` gate is now true. The link surfaces
    // via the post-upload refetch. Click into the detail page.
    await page.getByRole("link", { name: /smoke\.pdf/ }).click();
    await page.waitForURL((u) => u.pathname === `/documents/${DOC_ID}`, {
      timeout: 10_000,
    });

    // First detail render: Pending. Use .first() because the table-
    // header text "Extraction" + the badge "Pending" both appear,
    // and the compliance-status was overridden to NonCompliant so
    // there's no Pending-Pending ambiguity.
    await expect(page.getByText("Pending").first()).toBeVisible();
    expect(detailCalls).toBeGreaterThanOrEqual(1);

    // After 3s, refetchInterval fires: Pending → Processing.
    await expect(page.getByText("Processing").first()).toBeVisible({
      timeout: 10_000,
    });
    expect(detailCalls).toBeGreaterThanOrEqual(2);

    // After another 3s, refetchInterval fires again: Processing →
    // Completed. The predicate returns false on Completed so polling
    // stops naturally.
    await expect(page.getByText("Completed").first()).toBeVisible({
      timeout: 10_000,
    });
    expect(detailCalls).toBeGreaterThanOrEqual(3);

    // Extracted field surfaces in the detail UI. The field NAME
    // appears as a label; the field VALUE is on the input's `value`
    // property (set via React's defaultValue, which doesn't write a
    // DOM `value=""` attribute), so we read it via Playwright's
    // inputValue() rather than getByDisplayValue (RTL-only API).
    await expect(page.getByText("PolicyNumber")).toBeVisible();
    const allInputValues = await Promise.all(
      (await page.locator("input").all()).map((i) => i.inputValue()),
    );
    expect(allInputValues).toContain("POL-SMOKE-12345");
  });
});
