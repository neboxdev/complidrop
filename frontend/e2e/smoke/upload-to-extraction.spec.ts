/**
 * Flow 3 smoke E2E (#39 AC #3): authed user uploads a document, sees
 * extraction status advance, lands on the detail view.
 *
 * Spans three pages: /documents (list), the upload action, and
 * /documents/[id] (detail). The detail page polls every 3s while
 * extraction is Pending/Processing — this test sequences the GET
 * /api/documents/:id responses so the FIRST call returns Pending and
 * the SECOND call (after the poll fires) returns Completed. Total
 * wall-clock is ~4s for the polling segment, which keeps the smoke
 * suite under the ADR's "< 90s on CI" target.
 *
 * The TanStack Query cache from `useUploadDocument.onSuccess`
 * invalidates `['documents']`, so the list re-fetches when the user
 * returns to /documents. The detail page's standalone `useQuery` is
 * scoped to `['documents', id]` and uses its own refetchInterval.
 */
import { test, expect } from "@playwright/test";
import { mockApi, jsonOk } from "../support/mock-api";
import { authedMe } from "../support/fixtures";

const DOC_ID = "d_smoke_pending_01";

test.describe("Flow 3 — upload → extraction → detail (#39)", () => {
  test("authed user uploads a doc, polls status from Pending to Completed, sees the field", async ({
    page,
  }) => {
    let detailCalls = 0;

    await mockApi(page, [
      // Cookie-based session: useMe() returns the authed Me on every
      // page-load probe.
      {
        method: "GET",
        path: "/api/auth/me",
        handler: jsonOk(authedMe),
      },
      // Documents list: empty initially. After the upload mutation
      // invalidates the cache, the SAME route returns a list with
      // the new pending row. We use a call counter to sequence.
      {
        method: "GET",
        path: "/api/documents",
        handler: async (route) => {
          // Always include the smoke doc — both before and after
          // upload, so the link-to-detail is rendered without race.
          await route.fulfill({
            status: 200,
            contentType: "application/json",
            body: JSON.stringify({
              data: {
                items: [
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
                ],
                total: 1,
                page: 1,
                pageSize: 50,
              },
              error: null,
            }),
          });
        },
      },
      // Upload endpoint: returns the upload id + pending status.
      {
        method: "POST",
        path: "/api/documents/upload",
        handler: jsonOk({
          id: DOC_ID,
          originalFileName: "smoke.pdf",
          extractionStatus: "Pending",
        }),
      },
      // Detail page: first call → Pending, subsequent calls (after
      // the 3s poll) → Completed. The refetchInterval predicate
      // returns false on Completed so polling stops naturally.
      {
        method: "GET",
        path: "/api/documents/:id",
        handler: async (route) => {
          detailCalls++;
          const pending = {
            id: DOC_ID,
            originalFileName: "smoke.pdf",
            documentType: "COI",
            documentSubType: null,
            vendorName: null,
            extractionStatus: "Pending",
            extractionConfidence: null,
            complianceStatus: "NonCompliant", // not "Pending" so the
            // detail page's badge negation assertion isn't ambiguous
            // if a future tightening matches Pending text twice.
            effectiveDate: null,
            expirationDate: null,
            daysUntilExpiry: null,
            isManuallyVerified: false,
            uploadedBy: null,
            blobStorageUrl: null,
            generalLiabilityLimit: null,
            fields: [],
            extractionFields: null,
            extractionPromptVersion: null,
            processingError: null,
            createdAt: "2026-05-26T12:00:00Z",
            updatedAt: "2026-05-26T12:00:00Z",
          };
          const completed = {
            ...pending,
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
          };
          await route.fulfill({
            status: 200,
            contentType: "application/json",
            body: JSON.stringify({
              data: detailCalls === 1 ? pending : completed,
              error: null,
            }),
          });
        },
      },
    ]);

    await page.goto("/documents");
    await expect(
      page.getByRole("heading", { name: /^documents$/i }),
    ).toBeVisible();

    // Trigger an upload via the dropzone input.
    const fileBytes = Buffer.from("%PDF-1.7\nsmoke-pdf\n%%EOF");
    await page
      .locator('input[type="file"]')
      .setInputFiles({
        name: "smoke.pdf",
        mimeType: "application/pdf",
        buffer: fileBytes,
      });

    // After upload + invalidation, the list still shows the smoke row
    // (the mock returns it in every call). Click into the detail page.
    await page.getByRole("link", { name: /smoke\.pdf/ }).click();
    await page.waitForURL((u) => u.pathname === `/documents/${DOC_ID}`, {
      timeout: 10_000,
    });

    // First detail render: Pending.
    await expect(page.getByText("Pending").first()).toBeVisible();
    expect(detailCalls).toBeGreaterThanOrEqual(1);

    // Wait for the 3s refetchInterval to fire and the status to flip
    // to Completed. Allow up to 10s so flaky-CI doesn't trip.
    await expect(page.getByText("Completed").first()).toBeVisible({
      timeout: 10_000,
    });
    expect(detailCalls).toBeGreaterThanOrEqual(2);

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
