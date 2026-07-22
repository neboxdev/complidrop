/**
 * Document detail page — the polling test that #36 is built around.
 *
 * AC #2: a Pending/Processing document must advance to Completed AND
 * to Failed in the UI without a manual reload (mocked timer + sequenced
 * responses).
 * AC #3: the extraction-error card renders from processingError on the
 * failed path.
 *
 * Driven through MSW + fake timers. The page hand-rolls a `useQuery`
 * with `refetchInterval` returning 3_000 while status is Pending /
 * Processing, false otherwise (see [id]/page.tsx:64-68). Advancing the
 * test clock past 3s fires the next fetch; the sequence of MSW
 * responses drives the page from one terminal state to another.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import DocumentDetailPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  makeDocumentDetail,
  makeComplianceCheck,
  sequencedJsonOk,
  toastSuccess,
  toastError,
} from "@/test";
import { api } from "@/lib/api";

// sonner mock is provided by the harness (vitest.setup.ts +
// src/test/sonner.ts). The toastSuccess / toastError spy references
// imported above pin the reextract + saveFields mutation toast paths
// (#122) — `resetSonner()` runs in the harness `afterEach` so call
// counts never leak between tests.

describe("DocumentDetailPage — not-checked explainer (#316 FP-063)", () => {
  it("explains an orphan (no-vendor) Pending doc and offers an inline vendor assign", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_orphan",
            extractionStatus: "Completed",
            complianceStatus: "Pending",
            vendorId: null,
            vendorName: null,
            complianceChecks: [],
          }),
        ),
      ),
      http.get(url("/api/vendors"), () => jsonOk([{ id: "v1", name: "Acme Catering" }])),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_orphan" } });

    expect(await screen.findByText(/not checked yet/i)).toBeInTheDocument();
    expect(screen.getByText(/isn't linked to a vendor yet/i)).toBeInTheDocument();
    // The inline assign picker (also FP-065's orphan picker) — the ONLY place to
    // assign a vendor to an already-uploaded doc.
    expect(screen.getByPlaceholderText(/assign a vendor/i)).toBeInTheDocument();
  });

  it("explains a vendor-without-checklist Pending doc and links to set requirements up", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_nochk",
            extractionStatus: "Completed",
            complianceStatus: "Pending",
            vendorId: "v9",
            vendorName: "Bob's Flowers",
            complianceChecks: [],
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_nochk" } });

    // Pin the vendor-name + space boundary so the JSX whitespace regression (#358) can't return.
    expect(
      await screen.findByText(/Bob's Flowers doesn't have a requirements checklist yet/i),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /set up bob's flowers requirements/i })).toHaveAttribute(
      "href",
      "/vendors/v9",
    );
  });

  it("treats a deleted vendor's surviving doc as no-vendor, not 'needs a checklist' (#422)", async () => {
    // After a vendor soft-delete, #422 re-grades the surviving document to
    // Pending: the VendorId FK is retained but the vendor nav resolves null
    // through the soft-delete filter, so vendorName arrives null and the
    // check rows are shed. Keying the explainer on vendorId would misclassify
    // this doc as "has a vendor, no checklist" — promising an automatic check
    // that can never happen and linking to the deleted vendor's dead page.
    // The honest state is the no-vendor branch with its working assign remedy
    // (same vendorName keying as the list page's Assign-vendor cell).
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_delvendor",
            extractionStatus: "Completed",
            complianceStatus: "Pending",
            vendorId: "v_deleted",
            vendorName: null,
            complianceChecks: [],
          }),
        ),
      ),
      http.get(url("/api/vendors"), () => jsonOk([{ id: "v1", name: "Acme Catering" }])),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_delvendor" } });

    // The no-vendor explainer: copy + the inline assign picker.
    expect(await screen.findByText(/not checked yet/i)).toBeInTheDocument();
    expect(screen.getByText(/isn't linked to a vendor yet/i)).toBeInTheDocument();
    expect(screen.getByPlaceholderText(/assign a vendor/i)).toBeInTheDocument();
    // Never the checklist claim nor the "Set up … requirements" link to the
    // deleted vendor (that page renders "Couldn't load this vendor.").
    expect(screen.queryByText(/doesn't have a requirements checklist/i)).toBeNull();
    expect(screen.queryByRole("link", { name: /set up .+ requirements/i })).toBeNull();
  });
});

describe("DocumentDetailPage — manual entry on a failed read (#316 FP-064)", () => {
  it("offers manual-entry fields instead of a dead-end when extraction returned nothing", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_failed",
            extractionStatus: "Failed",
            complianceStatus: "Pending",
            vendorId: "v2",
            vendorName: "Caterer Co",
            complianceChecks: [],
            fields: [],
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_failed" } });

    expect(await screen.findByText(/couldn't pull the details from this file/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/effective date/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/expiration date/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/general liability limit/i)).toBeInTheDocument();
    expect(screen.queryByText(/no details read yet/i)).toBeNull();
  });

  it("saves manual entries under the backend's canonical (snake_case) field keys", async () => {
    // The whole point of FP-064 is that the typed values reach the compliance
    // engine. That requires the backend's snake_case canonical keys — a
    // PascalCase key matches case-insensitively but NOT the underscore, so it
    // would silently no-op. Pin the wire contract so that regresses loudly.
    let putBody: unknown = null;
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_manual",
            extractionStatus: "Failed",
            complianceStatus: "Pending",
            vendorId: "v2",
            vendorName: "Caterer Co",
            complianceChecks: [],
            fields: [],
          }),
        ),
      ),
      http.put(url("/api/documents/:id/fields"), async ({ request }) => {
        putBody = await request.json();
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_manual" } });

    const limit = await screen.findByLabelText(/general liability limit/i);
    fireEvent.change(limit, { target: { value: "1000000" } });
    const save = screen.getByRole("button", { name: /save changes/i });
    await waitFor(() => expect(save).not.toBeDisabled());
    fireEvent.click(save);

    await waitFor(() => expect(putBody).not.toBeNull());
    const wire = JSON.stringify(putBody);
    expect(wire).toContain("general_liability_limit");
    expect(wire).toContain("1000000");
    // Guard the exact bug this test exists for: never the PascalCase column name.
    expect(wire).not.toContain("GeneralLiabilityLimit");
  });
});

describe("DocumentDetailPage — Batch C (#317)", () => {
  it("FP-062: 'Read again' confirms before discarding hand-corrected fields", async () => {
    let reextractCalled = 0;
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_edited",
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            fields: [
              {
                id: "f1",
                fieldName: "expiration_date",
                fieldValue: "2026-11-01",
                fieldType: "date",
                confidence: 1,
                isManuallyEdited: true,
                originalValue: "2025-01-01",
              },
            ],
          }),
        ),
      ),
      http.post(url("/api/documents/:id/reextract"), () => {
        reextractCalled += 1;
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_edited" } });

    fireEvent.click(await screen.findByRole("button", { name: /read again/i }));
    // Opens a confirm dialog instead of firing immediately.
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveAccessibleName(/read this file again/i);
    expect(within(dialog).getByText(/replaces the 1 value you changed/i)).toBeInTheDocument();
    expect(reextractCalled).toBe(0);
    // Confirm → fires reextract.
    fireEvent.click(within(dialog).getByRole("button", { name: /read again/i }));
    await waitFor(() => expect(reextractCalled).toBe(1));
  });

  it("FP-062: 'Read again' fires immediately when there's nothing corrected to lose", async () => {
    let reextractCalled = 0;
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_clean",
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            fields: [
              {
                id: "f1",
                fieldName: "expiration_date",
                fieldValue: "2026-11-01",
                fieldType: "date",
                confidence: 0.95,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
      http.post(url("/api/documents/:id/reextract"), () => {
        reextractCalled += 1;
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_clean" } });

    fireEvent.click(await screen.findByRole("button", { name: /read again/i }));
    await waitFor(() => expect(reextractCalled).toBe(1));
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("FP-062: confirms on UNSAVED pending edits too (the common type-then-reread case)", async () => {
    let reextractCalled = 0;
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_pending_edit",
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            fields: [
              {
                id: "f1",
                fieldName: "policy_number",
                fieldValue: "POL-1",
                fieldType: "string",
                confidence: 0.9,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
      http.post(url("/api/documents/:id/reextract"), () => {
        reextractCalled += 1;
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_pending_edit" } });

    // Type an unsaved correction — discardCount should now count it even though
    // isManuallyEdited is still false on the server payload.
    const input = await screen.findByDisplayValue("POL-1");
    fireEvent.change(input, { target: { value: "POL-2" } });
    fireEvent.click(screen.getByRole("button", { name: /read again/i }));
    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByText(/replaces the 1 value you changed/i)).toBeInTheDocument();
    expect(reextractCalled).toBe(0);
  });

  it("FP-067: the processing-error card shows only on terminal Failed, not between retries", async () => {
    // Pending with a transient processingError → NO 'contact support' card.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_retry",
            extractionStatus: "Pending",
            processingError: "Gemini 503",
          }),
        ),
      ),
    );
    const { unmount } = renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_retry" } });
    await waitFor(() => expect(screen.getByText(/reading the document/i)).toBeInTheDocument());
    expect(screen.queryByText(/couldn't read this document/i)).toBeNull();
    unmount();

    // Failed (terminal) → the card shows.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_failed2",
            extractionStatus: "Failed",
            processingError: "Gemini 503",
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_failed2" } });
    expect(await screen.findByText(/couldn't read this document/i)).toBeInTheDocument();
  });

  it("FP-065: shows the vendor name as a link to the vendor page", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_vendor",
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            vendorId: "v7",
            vendorName: "Acme Catering",
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_vendor" } });
    expect(await screen.findByRole("link", { name: "Acme Catering" })).toHaveAttribute("href", "/vendors/v7");
  });

  it("FP-060: delete from the detail page confirms, then deletes + returns to the list", async () => {
    let deleted = false;
    const pushSpy = vi.fn();
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ id: "d_del", originalFileName: "coi.pdf", extractionStatus: "Completed" })),
      ),
      http.delete(url("/api/documents/:id"), () => {
        deleted = true;
        return jsonOk<void>(undefined);
      }),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_del" },
      router: { push: pushSpy },
    });

    fireEvent.click(await screen.findByRole("button", { name: /remove coi\.pdf/i }));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: /^remove$/i }));
    await waitFor(() => expect(deleted).toBe(true));
    await waitFor(() => expect(pushSpy).toHaveBeenCalledWith("/documents"));
  });
});

describe("DocumentDetailPage — what we checked (#239)", () => {
  it("lists the met requirements in plain English for a compliant document", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_ok",
            documentType: "coi",
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            complianceChecks: [
              makeComplianceCheck({ id: "c1", ruleFieldName: "general_liability_limit", ruleOperator: "min_value", ruleExpectedValue: "1000000", isPassed: true }),
              makeComplianceCheck({ id: "c2", ruleFieldName: "workers_comp_limit", ruleOperator: "required", ruleExpectedValue: null, isPassed: true }),
            ],
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_ok" } });

    expect(await screen.findByText(/what we checked/i)).toBeInTheDocument();
    expect(screen.getByText(/at least \$1,000,000 in general liability/i)).toBeInTheDocument();
    expect(screen.getByText(/workers' compensation coverage/i)).toBeInTheDocument();
  });

  it("shows the non-compliance explainer (not the checked card) when a requirement failed", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_bad",
            documentType: "coi",
            extractionStatus: "Completed",
            complianceStatus: "NonCompliant",
            complianceChecks: [
              makeComplianceCheck({ id: "c1", ruleFieldName: "general_liability_limit", ruleOperator: "min_value", ruleExpectedValue: "1000000", isPassed: false }),
            ],
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_bad" } });

    expect(await screen.findByText(/why isn.t this compliant/i)).toBeInTheDocument();
    expect(screen.queryByText(/what we checked/i)).toBeNull();
  });

  it("does not show the checked card when a document has no recorded checks", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ id: "d_none", documentType: "coi", extractionStatus: "Completed", complianceStatus: "Compliant", complianceChecks: [] })),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_none" } });

    // Wait for load to settle, then confirm neither verdict card renders (no rules → nothing to show).
    await waitFor(() => expect(screen.queryByText(/loading document/i)).not.toBeInTheDocument());
    expect(screen.queryByText(/what we checked/i)).toBeNull();
    expect(screen.queryByText(/why isn.t this compliant/i)).toBeNull();
  });

  it("explains the Verified tile when a document hasn't been hand-verified", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ id: "d_nv", extractionStatus: "Completed", isManuallyVerified: false })),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_nv" } });

    expect(await screen.findByText(/optional: confirm the read fields look right/i)).toBeInTheDocument();
    expect(screen.getByText(/^not yet$/i)).toBeInTheDocument();
  });
});

describe("DocumentDetailPage — sample banner (#238)", () => {
  it("shows the sample banner and clears + redirects to /documents on a sample doc", async () => {
    const push = vi.fn();
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_sample",
            originalFileName: "Sample Certificate of Insurance.pdf",
            isSample: true,
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
          }),
        ),
      ),
      http.delete(url("/api/sample"), () =>
        jsonOk({ message: "Sample data cleared.", clearedDocuments: 1, clearedVendors: 1 }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_sample" },
      router: { push },
    });

    expect(await screen.findByText(/this is a sample certificate/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /clear sample data/i }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/documents"));
    expect(toastSuccess).toHaveBeenCalledWith("Sample data cleared.");
  });

  it("does not show the sample banner on a normal document", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ id: "d_real", isSample: false, extractionStatus: "Completed" })),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_real" } });

    await waitFor(() => expect(screen.queryByText(/loading document/i)).not.toBeInTheDocument());
    expect(screen.queryByText(/this is a sample certificate/i)).not.toBeInTheDocument();
  });
});

describe("DocumentDetailPage — basic states (#36)", () => {
  it("isLoading: renders the loading copy", () => {
    // Hold the response so the test observes the loading branch.
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.get(url("/api/documents/:id"), async () => {
        await settled;
        return jsonOk(makeDocumentDetail());
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_01" },
    });

    expect(screen.getByText(/loading document/i)).toBeInTheDocument();
    release();
  });

  it("threads an AbortSignal into the detail poll query (#222)", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () => jsonOk(makeDocumentDetail({ id: "d_x_01" }))),
    );
    const getSpy = vi.spyOn(api, "get"); // call-through; just record the calls

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_01" },
    });

    await waitFor(() =>
      expect(screen.queryByText(/loading document/i)).not.toBeInTheDocument(),
    );

    // The detail query is the 3s poll #222 names explicitly: it must thread a real
    // AbortSignal so an unmount mid-poll cancels it. If the queryFn dropped `{ signal }`,
    // opts.signal is undefined and this fails.
    const detailCall = getSpy.mock.calls.find(
      ([path]) => typeof path === "string" && path.includes("/api/documents/d_x_01"),
    );
    expect(detailCall).toBeDefined();
    expect(
      (detailCall?.[1] as { signal?: AbortSignal } | undefined)?.signal,
    ).toBeInstanceOf(AbortSignal);
    getSpy.mockRestore();
  });

  it("error (404): renders the not-found fallback with a link back", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonError("documents.not_found", "No such document.", { status: 404 }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_missing_01" },
    });

    await waitFor(() =>
      expect(screen.getByText(/document not found/i)).toBeInTheDocument(),
    );
    expect(
      screen.getByRole("link", { name: /back to documents/i }),
    ).toHaveAttribute("href", "/documents");
    // The 404 path stays on the minimal copy — no error card, no
    // role=alert. Pin the negative so a regression that bucketed
    // 404 into the new 5xx error card path is caught here.
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByText(/couldn't load document/i)).toBeNull();
  });

  it("error (5xx initial load): renders error card with role=alert + Retry, NOT the not-found copy (#97 symmetrization)", async () => {
    // The detail page's `!detail.data` early-return used to collapse
    // 404 / 5xx / network failure into a single "Document not found"
    // message — surfaced by the test-quality reviewer during the #97
    // review as the inverse asymmetry of the list page's no-data 5xx
    // path. Now the detail page splits 404 (minimal copy) from 5xx
    // (error card with Retry + role=alert) just like the list page.
    // A brown-out on initial load must not look like the document
    // was deleted.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonError("server.error", "DB down.", { status: 500 }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_outage_01" },
    });

    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert).toHaveTextContent(/couldn't load document/i);
    expect(alert).toHaveTextContent("DB down.");
    expect(
      screen.getByRole("button", { name: /retry/i }),
    ).toBeInTheDocument();
    // Negative: the 404 not-found copy must NOT appear on a 5xx.
    expect(screen.queryByText(/document not found/i)).toBeNull();
    // The back-to-documents link still renders (matches the list-page
    // pattern of preserving navigation chrome even on the error path).
    expect(
      screen.getByRole("link", { name: /all documents/i }),
    ).toHaveAttribute("href", "/documents");
  });

  it("error (5xx initial load): non-JSON body falls back to GENERIC_FALLBACK_MESSAGE (#97 + #77)", async () => {
    // Symmetric with the list page's same pin — a 502 HTML proxy
    // page on the initial load must NOT leak `statusText` or raw
    // HTML into the error card body. The api.ts layer converts to
    // GENERIC_FALLBACK_MESSAGE; the page must surface that string,
    // not the raw statusText.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        Promise.resolve(
          new Response("<html>502 Bad Gateway</html>", {
            status: 502,
            statusText: "Bad Gateway",
            headers: { "Content-Type": "text/html" },
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_outage_02" },
    });

    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert).toHaveTextContent(/couldn't load document/i);
    expect(alert).toHaveTextContent("Something went wrong. Try again.");
    // The raw statusText MUST NOT leak through under any path.
    expect(alert).not.toHaveTextContent(/bad gateway/i);
    expect(alert).not.toHaveTextContent(/<html>/i);
  });

  it("error (5xx initial load): Retry button re-issues the fetch and swaps to the populated detail on 200 (#97 symmetrization)", async () => {
    // Pins the 5xx-error-card Retry affordance for the detail page,
    // mirroring the list page's retry-on-5xx test. A regression that
    // wired Retry to a no-op or the wrong query would slip past the
    // basic 5xx render test above.
    let calls = 0;
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        if (calls === 1) {
          return jsonError("server.error", "DB blip.", { status: 500 });
        }
        return jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
          }),
        );
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_outage_03" },
    });

    // First render: error card.
    await waitFor(() =>
      expect(screen.getByText(/couldn't load document/i)).toBeInTheDocument(),
    );
    expect(calls).toBe(1);

    fireEvent.click(screen.getByRole("button", { name: /retry/i }));

    // Second fetch fires, lands the populated detail. Full state
    // swap: file name + extraction-status testid present, the error
    // card body + Retry button gone.
    await waitFor(() => expect(calls).toBe(2));
    await waitFor(() =>
      expect(screen.getByText("coi.pdf")).toBeInTheDocument(),
    );
    expect(screen.getByTestId("extraction-status")).toHaveTextContent(
      "Read",
    );
    expect(screen.queryByText(/couldn't load document/i)).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("populated: renders fields + extraction badge + compliance badge", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            extractionConfidence: 0.92,
            complianceStatus: "Compliant",
            expirationDate: "2026-12-31T00:00:00Z",
            isManuallyVerified: true,
            fields: [
              {
                id: "f1",
                fieldName: "PolicyNumber",
                fieldValue: "POL-12345",
                fieldType: "string",
                confidence: 0.95,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_completed_01" },
    });

    await waitFor(() =>
      expect(screen.getByText("coi.pdf")).toBeInTheDocument(),
    );
    // Extraction + compliance badges visible — pinned via #92 testids
    // (fast-tier coverage of the testid contract; the E2E spec depends
    // on these attributes existing, and a regression that removed them
    // would otherwise surface only at the slow Playwright tier).
    expect(screen.getByTestId("extraction-status")).toHaveTextContent("Read");
    expect(screen.getByTestId("compliance-status")).toHaveTextContent("Compliant");
    // #263: the Expires cell renders the CALENDAR date the document says
    // (2026-12-31), never the local-shifted previous day. The vitest TZ is
    // pinned to America/New_York, where the old bare toLocaleDateString
    // rendered 12/30/2026 for this fixture.
    expect(
      screen.getByText(
        new Date("2026-12-31T00:00:00Z").toLocaleDateString(undefined, { timeZone: "UTC" }),
      ),
    ).toBeInTheDocument();
    // Field row rendered.
    expect(screen.getByText("Policy Number")).toBeInTheDocument();
    // RTL idiom for "input is rendered with this value" — better than
    // `document.querySelector('input[value=…]')` because it's
    // container-scoped and won't pick up an input from a stray portal.
    expect(screen.getByDisplayValue("POL-12345")).toBeInTheDocument();
  });
});

describe("DocumentDetailPage — extraction-error card (#36 AC #3)", () => {
  it("renders processingError content when the failed-path field is set", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Failed",
            processingError:
              "OCR confidence below threshold; manual review required.",
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_failed_01" },
    });

    await waitFor(() =>
      expect(screen.getByText(/couldn't read this document/i)).toBeInTheDocument(),
    );
    expect(
      screen.getByText(/OCR confidence below threshold/i),
    ).toBeInTheDocument();
  });

  it("does NOT render the extraction-error card when processingError is null", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ extractionStatus: "Completed" })),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_completed_01" },
    });

    await waitFor(() =>
      expect(screen.getByText("coi.pdf")).toBeInTheDocument(),
    );
    expect(screen.queryByText(/couldn't read this document/i)).toBeNull();
  });
});

describe("DocumentDetailPage — polling transitions (#36 AC #2)", () => {
  beforeEach(() => {
    // `shouldAdvanceTime: true` is REQUIRED here because RTL's
    // `waitFor` polls via real `setTimeout`, which is itself faked by
    // `vi.useFakeTimers()` — pure fake timers cause `waitFor` to hang.
    // The race-against-real-time concern (real ms elapsed during
    // waitFor potentially crossing the 3-second refetchInterval
    // boundary) is absorbed by snapshotting the call count BEFORE each
    // explicit advance and asserting deltas, not absolute counts.
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("Pending → Completed: UI advances without a manual reload", async () => {
    // Sequenced responses: first call = extraction Pending +
    // compliance NonCompliant (so the only "Pending" text in the DOM
    // is the extraction badge); second call (3s later) = extraction
    // Completed + compliance Compliant. The page's refetchInterval
    // returns 3000 while extraction is Pending/Processing, false on
    // terminal states.
    let calls = 0;
    const seq = sequencedJsonOk(
      makeDocumentDetail({
        extractionStatus: "Pending",
        complianceStatus: "NonCompliant",
      }),
      makeDocumentDetail({
        extractionStatus: "Completed",
        extractionConfidence: 0.91,
        complianceStatus: "Compliant",
      }),
    );
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        return seq();
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_01" },
    });

    // First render: Pending → the badge reads "Waiting to read" (#188). The
    // "Reading the document…" copy fires when fields are empty and the
    // document is still Pending/Processing.
    await waitFor(() =>
      expect(screen.getByText("Waiting to read")).toBeInTheDocument(),
    );
    expect(screen.getByText(/reading the document/i)).toBeInTheDocument();
    expect(calls).toBeGreaterThanOrEqual(1);

    // Snapshot pre-advance count so the assertion is delta-based: the
    // explicit 3-second advance must trigger AT LEAST one refetch, and
    // the post-state must be Completed. Tolerates one extra auto-fire
    // from `shouldAdvanceTime: true` absorbing real wall-clock during
    // the preceding `waitFor`.
    const beforeAdvance = calls;
    await vi.advanceTimersByTimeAsync(3000);

    await waitFor(() =>
      expect(screen.getByText("Read")).toBeInTheDocument(),
    );
    expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);
    // Pin the negative assertion via the #92 testid rather than the
    // older `within(extractionCell)` + `closest('div')` scope-pattern,
    // which was structurally coupled to the SummaryCell DOM tree
    // (`<p>Extraction</p>` + sibling `<div>{badge}</div>` both inside
    // CardContent). Asserting on the testid is stable regardless of
    // future DOM reshuffles and is the canonical "ambiguous-by-design
    // surface" rule from CLAUDE.md.
    expect(screen.getByTestId("extraction-status")).not.toHaveTextContent(
      "Waiting to read",
    );
    expect(screen.getByTestId("extraction-status")).toHaveTextContent(
      "Read",
    );

    // Completed is terminal — refetchInterval returns false, no more
    // polls. Snapshot the post-completed count then advance another 10s
    // and confirm it doesn't change.
    const afterCompleted = calls;
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toBe(afterCompleted);
  });

  it("populated-detail polling-failure: keeps the cached detail visible, surfaces the stale-data banner, and stops polling (#97)", async () => {
    // Symmetric with the documents-list AC #5: a poll failure on the
    // detail page must NOT clobber the cached detail (the existing
    // `!detail.data` early-return only protects the never-loaded
    // case). When the initial load lands a 200 and a subsequent poll
    // fires a 5xx, TanStack Query preserves `data` while flipping
    // `isError=true`. Without the AC #5 fix, the user would still
    // see the cached fields but the polling would keep hammering the
    // backend every 3 s. With the fix:
    //   - refetchInterval short-circuits on error (no more polls)
    //   - StaleDataBanner renders above the summary section so the
    //     user knows the detail may be stale
    //   - The full cached payload (title, badges, fields) stays rendered
    let calls = 0;
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        if (calls === 1) {
          // Initial load: a Processing document with one extracted
          // field. Processing status drives the 3s refetchInterval
          // that the AC #5 fix must short-circuit on the next error.
          return jsonOk(
            makeDocumentDetail({
              extractionStatus: "Processing",
              complianceStatus: "Pending",
              fields: [
                {
                  id: "f1",
                  fieldName: "PolicyNumber",
                  fieldValue: "POL-PRE-ERR",
                  fieldType: "string",
                  confidence: 0.91,
                  isManuallyEdited: false,
                  originalValue: null,
                },
              ],
            }),
          );
        }
        // Subsequent polls (and an explicit Try-again click) return 5xx.
        return jsonError("server.error", "Brown-out", { status: 502 });
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_99" },
    });

    // Initial load lands — cached detail visible.
    await waitFor(() =>
      expect(screen.getByText("coi.pdf")).toBeInTheDocument(),
    );
    expect(screen.getByDisplayValue("POL-PRE-ERR")).toBeInTheDocument();
    expect(screen.getByTestId("extraction-status")).toHaveTextContent(
      "Reading…",
    );
    expect(calls).toBe(1);

    // Advance past the 3s refetchInterval — the next fetch errors.
    await vi.advanceTimersByTimeAsync(3000);
    await waitFor(() => expect(calls).toBeGreaterThanOrEqual(2));

    // The stale-data banner appears with the server message — co-pin
    // role=status + headline + body so a regression that drops any of
    // the three fails here. role=status (NOT role=alert) so assistive
    // tech announces politely rather than interrupting.
    const banner = await waitFor(() => screen.getByRole("status"));
    expect(banner).toHaveAttribute("aria-live", "polite");
    expect(banner).toHaveTextContent(/couldn't refresh document/i);
    expect(banner).toHaveTextContent(/brown-out/i);

    // The cached detail STAYS visible — file name, the field input,
    // and the extraction badge are all still rendered.
    expect(screen.getByText("coi.pdf")).toBeInTheDocument();
    expect(screen.getByDisplayValue("POL-PRE-ERR")).toBeInTheDocument();
    expect(screen.getByTestId("extraction-status")).toHaveTextContent(
      "Reading…",
    );
    // The "Document not found" copy is the unloaded-data branch, NOT
    // the poll-failure branch — must NOT appear when cached data
    // exists. And role=alert is reserved for the no-data error card
    // path (added in the AC #6 detail-page initial-load 5xx
    // symmetrization) — the cached-data + poll-failure path uses
    // role=status (the banner) NOT role=alert. Pin both negatives so
    // a regression that re-introduced role=alert (interruptive a11y
    // announcement) on the cached path is caught. Symmetric with the
    // list-page test at documents/page.test.tsx. (#97 review —
    // test-quality reviewer)
    expect(screen.queryByText(/document not found/i)).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();

    // Polling short-circuits on error — advancing 60s of fake time
    // (20 polling windows at the 3s interval, plus enough headroom
    // for a hypothetical back-off-with-cap implementation up to ~30s)
    // must NOT trigger any more fetches. Tighter than the previous
    // 15s window so a future variant that backs off rather than
    // strict short-circuits would still be caught by this test if
    // its retry interval ever fired within a minute. (#97 review —
    // test-quality reviewer)
    const afterFirstError = calls;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(calls).toBe(afterFirstError);
  });

  it("detail stale-banner dismisses after a successful Try-again refetch (#97)", async () => {
    // Recovery path on the detail page: clicking Try again on the
    // banner, with a 200 response, must drop the banner AND swap the
    // displayed payload to the fresh response. Mirrors the
    // list-page Try-again recovery test.
    let calls = 0;
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        if (calls === 1) {
          return jsonOk(
            makeDocumentDetail({
              extractionStatus: "Processing",
              complianceStatus: "Pending",
            }),
          );
        }
        if (calls === 2) {
          // Second call (the polling refetch): 5xx → banner shows.
          return jsonError("server.error", "Brown-out", { status: 502 });
        }
        // Third call (Try-again click): 200 with Completed status →
        // banner dismisses, badge advances.
        return jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            extractionConfidence: 0.93,
            complianceStatus: "Compliant",
          }),
        );
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_98" },
    });

    await waitFor(() =>
      expect(screen.getByText("coi.pdf")).toBeInTheDocument(),
    );

    // Trigger the polling failure.
    await vi.advanceTimersByTimeAsync(3000);
    const banner = await waitFor(() => screen.getByRole("status"));
    expect(banner).toHaveTextContent(/couldn't refresh document/i);

    // Click Try again on the banner — its Retry affordance.
    fireEvent.click(screen.getByRole("button", { name: /try again/i }));

    // Banner dismisses + the badge reflects the new Completed status.
    // Negative-pair: pin that the OLD Processing badge is GONE on the
    // extraction-status testid (not just that Completed appeared) —
    // a regression that left both badges side-by-side would pass the
    // positive assertion. Symmetric with the list-page recovery
    // test's negative on the old row. (#97 review — test-quality
    // reviewer)
    await waitFor(() =>
      expect(screen.getByTestId("extraction-status")).toHaveTextContent(
        "Read",
      ),
    );
    expect(screen.getByTestId("extraction-status")).not.toHaveTextContent(
      "Reading…",
    );
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.queryByText(/couldn't refresh document/i)).toBeNull();
  });

  it("Try-again that fails too on detail: banner stays visible, button re-enables (#97)", async () => {
    // Symmetric with the list-page negative-recovery test: a poll
    // failure → Try-again → ALSO fails → banner must STAY visible,
    // button must re-enable so the user can keep retrying. Catches a
    // regression that incorrectly clears isError on click or that
    // sticks the disabled state after a failed retry. (#97 review —
    // correctness + test-quality reviewers)
    let calls = 0;
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        if (calls === 1) {
          return jsonOk(
            makeDocumentDetail({
              extractionStatus: "Processing",
              complianceStatus: "Pending",
            }),
          );
        }
        // Every subsequent call (polling + Try-again click) fails.
        return jsonError("server.error", "Still down", { status: 502 });
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_97" },
    });

    await waitFor(() =>
      expect(screen.getByText("coi.pdf")).toBeInTheDocument(),
    );

    // Poll fires → fails → banner appears.
    await vi.advanceTimersByTimeAsync(3000);
    await waitFor(() =>
      expect(screen.getByText(/couldn't refresh document/i)).toBeInTheDocument(),
    );

    // Click Try again → also fails.
    fireEvent.click(screen.getByRole("button", { name: /try again/i }));
    await waitFor(() => expect(calls).toBeGreaterThanOrEqual(3));

    // Banner remains with the new server message.
    const banner = screen.getByRole("status");
    expect(banner).toHaveTextContent(/couldn't refresh document/i);
    expect(banner).toHaveTextContent(/still down/i);
    // Cached detail stays rendered — no fallback page.
    expect(screen.getByText("coi.pdf")).toBeInTheDocument();
    expect(screen.queryByText(/document not found/i)).toBeNull();
    expect(screen.queryByText(/couldn't load document/i)).toBeNull();

    // Try-again button is re-enabled once isFetching settles.
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: /try again/i }),
      ).not.toBeDisabled(),
    );

    // Pin that the short-circuit-on-error contract stays sticky
    // across the failed manual retry: after the second 502, polling
    // must NOT resume for the original 3s interval. Symmetric with
    // the list-page test's same pin. (#97 second-pass review —
    // test-quality reviewer)
    const afterRetry = calls;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(calls).toBe(afterRetry);
  });

  it("Processing → Failed: UI advances to the failed badge + processingError card", async () => {
    let calls = 0;
    const seq = sequencedJsonOk(
      makeDocumentDetail({
        extractionStatus: "Processing",
        complianceStatus: "NonCompliant",
      }),
      makeDocumentDetail({
        extractionStatus: "Failed",
        complianceStatus: "NonCompliant",
        processingError: "OCR engine timed out after 30 seconds.",
      }),
    );
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        return seq();
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_02" },
    });

    await waitFor(() =>
      expect(screen.getByText("Reading…")).toBeInTheDocument(),
    );
    // The error card should NOT be visible while Processing.
    expect(screen.queryByText(/couldn't read this document/i)).toBeNull();
    expect(calls).toBeGreaterThanOrEqual(1);

    const beforeAdvance = calls;
    await vi.advanceTimersByTimeAsync(3000);

    // Failed badge appears AND the processingError card pops in.
    await waitFor(() =>
      expect(screen.getByText("Couldn't read")).toBeInTheDocument(),
    );
    expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);
    expect(screen.getByText(/couldn't read this document/i)).toBeInTheDocument();
    expect(
      screen.getByText(/OCR engine timed out after 30 seconds/i),
    ).toBeInTheDocument();

    // Failed is terminal — no further polls.
    const afterFailed = calls;
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toBe(afterFailed);
  });
});

describe("DocumentDetailPage — reextract mutation toasts (#122 / #74 followup)", () => {
  it("reextract success: toast.success fires with the documented queued copy", async () => {
    // The detail page renders a Re-extract button in the header; clicking it
    // POSTs /api/documents/:id/reextract. On 200 the mutation's onSuccess
    // fires `toast.success("Reading the file again…")` — copy that the support
    // team has been trained to spot in screenshots when triaging COI/permit
    // extraction failures. Pin the EXACT copy so a future contributor who
    // "tones down" the message ("Queued.") breaks this test deliberately
    // rather than silently changing the support runbook.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Failed",
            processingError: "OCR confidence below threshold.",
          }),
        ),
      ),
      http.post(url("/api/documents/:id/reextract"), () =>
        jsonOk<void>(undefined),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_reextract_ok_01" },
    });

    // Wait for the detail to land — Re-extract button is only rendered
    // alongside the loaded header (the loading / 404 / 5xx branches all
    // return early above the header).
    const button = await waitFor(() =>
      screen.getByRole("button", { name: /read again/i }),
    );
    fireEvent.click(button);

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledTimes(1));
    expect(toastSuccess).toHaveBeenCalledWith("Reading the file again…");
    // Negative — the error toast spy stays untouched on the success path.
    expect(toastError).not.toHaveBeenCalled();
  });

  it("reextract 5xx: toast.error fires with the server message (#77 jargon-free contract)", async () => {
    // The server message arrives via the api.ts ApiError envelope and is
    // already jargon-free per #77's `fetchOrFriendlyThrow` contract (no
    // raw `statusText`, no browser TypeError). The mutation's onError
    // pulls it off the ApiError and forwards to `toast.error(message)`;
    // a regression that hardcoded a "Re-extract failed" fallback string
    // would lose the diagnostic value of the server's actual message
    // ("Extraction queue is at capacity, please retry in a few minutes.").
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Failed",
            processingError: "OCR confidence below threshold.",
          }),
        ),
      ),
      http.post(url("/api/documents/:id/reextract"), () =>
        jsonError(
          "extraction.queue_at_capacity",
          "Extraction queue is at capacity, please retry in a few minutes.",
          { status: 503 },
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_reextract_err_01" },
    });

    const button = await waitFor(() =>
      screen.getByRole("button", { name: /read again/i }),
    );
    fireEvent.click(button);

    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(1));
    expect(toastError).toHaveBeenCalledWith(
      "Extraction queue is at capacity, please retry in a few minutes.",
    );
    // Pin the negatives the #77 jargon-free contract demands. The
    // patterns are tight: `/^service unavailable$/i` matches ONLY
    // the bare HTTP statusText (the realistic leak shape) — a
    // future legitimate server message that contains the words
    // "service unavailable" as part of a longer sentence would not
    // false-positive. `/\b503\b/` matches the interpolated status-
    // code leak shape (`Export failed (503)`, `HTTP 503`, …); a
    // legitimate vendor-portal token or COI policy number that
    // happens to contain "503" as part of a longer alphanumeric
    // run is filtered out by the word-boundary anchors. The
    // exact-string `.toHaveBeenCalledWith` above is the load-
    // bearing pin; these negatives are belt-and-braces against
    // regressions that swap the assertion target.
    const args = toastError.mock.calls[0]?.[0];
    expect(typeof args).toBe("string");
    expect(args).not.toMatch(/^service unavailable$/i);
    expect(args).not.toMatch(/\b503\b/);
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("reextract empty server message: toast.error falls back to GENERIC_FALLBACK_MESSAGE (#77 page-level fallback)", async () => {
    // Pins the page-level fallback ternary in `onError` —
    // `err instanceof Error && err.message?.trim() ? err.message :
    // GENERIC_FALLBACK_MESSAGE`. The api.ts layer ALSO substitutes
    // GENERIC_FALLBACK_MESSAGE when an envelope's `error.message` is
    // empty/whitespace (api.ts:244-245), so the page's ternary is
    // defense-in-depth. Without this test the page-level fallback
    // is unreached coverage — a regression that swapped the ternary
    // (e.g. `err.message ?? "Reextract failed"`) would slip past
    // the populated-message tests above.
    //
    // The test uses a whitespace-only envelope message — that
    // exercises the `.trim()` guard specifically; a bare empty
    // string would short-circuit at the api.ts layer and not
    // even reach the page's ternary, masking a regression there.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Failed",
            processingError: "OCR confidence below threshold.",
          }),
        ),
      ),
      http.post(url("/api/documents/:id/reextract"), () =>
        jsonError("server.error", "   ", { status: 500 }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_reextract_fallback_01" },
    });

    const button = await waitFor(() =>
      screen.getByRole("button", { name: /read again/i }),
    );
    fireEvent.click(button);

    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(1));
    expect(toastError).toHaveBeenCalledWith("Something went wrong. Try again.");
  });

  it("reextract network-unreachable: toast.error fires GENERIC_FALLBACK_MESSAGE, no TypeError leak (#77)", async () => {
    // The third leg of the #77 contract: a browser TypeError on
    // fetch() failure (offline, DNS, CORS drop) must NOT surface
    // as "Failed to fetch" or "TypeError: …" in the toast. The
    // api.ts `fetchOrFriendlyThrow` (api.ts:197) catches that and
    // synthesizes `new ApiError("network.unreachable",
    // GENERIC_FALLBACK_MESSAGE, 0)`. The page's onError then
    // forwards that message verbatim. Pin the round-trip end-to-end.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Failed",
            processingError: "OCR confidence below threshold.",
          }),
        ),
      ),
      http.post(url("/api/documents/:id/reextract"), () => {
        // MSW's HttpResponse.error() simulates fetch() rejection
        // with a TypeError — the production path that
        // fetchOrFriendlyThrow's catch block converts to ApiError.
        return Response.error();
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_reextract_network_01" },
    });

    const button = await waitFor(() =>
      screen.getByRole("button", { name: /read again/i }),
    );
    fireEvent.click(button);

    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(1));
    expect(toastError).toHaveBeenCalledWith("Something went wrong. Try again.");
    // #77 TypeError-leak negatives — these are the literal strings
    // a regression that bypassed fetchOrFriendlyThrow would surface.
    const args = toastError.mock.calls[0]?.[0];
    expect(args).not.toMatch(/typeerror/i);
    expect(args).not.toMatch(/failed to fetch/i);
  });
});

describe("DocumentDetailPage — recheck (Check again) mutation (#257)", () => {
  it("recheck success: POSTs the compliance-check endpoint and toasts the re-check copy", async () => {
    // The header renders a "Check again" button that re-runs ONLY compliance
    // evaluation (POST /api/compliance/check/:id) — no re-extraction. The #257
    // manual escape hatch. On 200 the mutation toasts the documented copy.
    let posted = false;
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ extractionStatus: "Completed" })),
      ),
      http.post(url("/api/compliance/check/:id"), () => {
        posted = true;
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_recheck_ok_01" },
    });

    const button = await waitFor(() =>
      screen.getByRole("button", { name: /check again/i }),
    );
    fireEvent.click(button);

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Re-checking compliance…"));
    expect(posted).toBe(true);
    expect(toastError).not.toHaveBeenCalled();
  });

  it("recheck 5xx: toast.error surfaces the server message, no jargon leak (#77)", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ extractionStatus: "Completed" })),
      ),
      http.post(url("/api/compliance/check/:id"), () =>
        jsonError("compliance.failed", "We couldn't re-check this document. Try again.", { status: 500 }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_recheck_err_01" },
    });

    const button = await waitFor(() =>
      screen.getByRole("button", { name: /check again/i }),
    );
    fireEvent.click(button);

    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(1));
    expect(toastError).toHaveBeenCalledWith("We couldn't re-check this document. Try again.");
    const args = toastError.mock.calls[0]?.[0];
    expect(args).not.toMatch(/typeerror/i);
    expect(args).not.toMatch(/bad gateway/i);
  });
});

describe("DocumentDetailPage — saveFields mutation toasts (#122 / #74 followup)", () => {
  it("saveFields success: toast.success fires with the save-confirmation copy", async () => {
    // To reach the saveFields path the test must (a) load a doc with at
    // least one field rendered, (b) edit that field's input to populate
    // the `edits` state and enable the Save changes button, then (c)
    // click the button. The PUT lands a 200 → onSuccess clears `edits`,
    // invalidates the query, and fires `toast.success("Fields updated")`.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            fields: [
              {
                id: "f1",
                fieldName: "PolicyNumber",
                fieldValue: "POL-OLD-001",
                fieldType: "string",
                confidence: 0.91,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
      http.put(url("/api/documents/:id/fields"), () =>
        jsonOk<void>(undefined),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_save_ok_01" },
    });

    const input = await waitFor(() =>
      screen.getByDisplayValue("POL-OLD-001"),
    );
    // Edit the input → populates edits → enables Save changes.
    fireEvent.change(input, { target: { value: "POL-NEW-002" } });

    const save = screen.getByRole("button", { name: /save changes/i });
    await waitFor(() => expect(save).not.toBeDisabled());
    fireEvent.click(save);

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledTimes(1));
    expect(toastSuccess).toHaveBeenCalledWith("Fields updated");
    expect(toastError).not.toHaveBeenCalled();
  });

  it("saveFields 409 conflict: toast.error fires with the server conflict message", async () => {
    // A 409 conflict from /api/documents/:id/fields is the realistic
    // failure shape — e.g. the document was reextracted between when
    // the user opened the page and when they clicked save, invalidating
    // the field row ids the PUT was targeting. The server message
    // ("Document has been reextracted; reload to see the latest fields.")
    // is what the user needs to recover. Pin that toast.error fires
    // with that EXACT server message, not a hardcoded "Save failed"
    // fallback.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            fields: [
              {
                id: "f1",
                fieldName: "PolicyNumber",
                fieldValue: "POL-OLD-001",
                fieldType: "string",
                confidence: 0.91,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
      http.put(url("/api/documents/:id/fields"), () =>
        jsonError(
          "documents.stale_fields",
          "Document has been reextracted; reload to see the latest fields.",
          { status: 409 },
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_save_err_01" },
    });

    const input = await waitFor(() =>
      screen.getByDisplayValue("POL-OLD-001"),
    );
    fireEvent.change(input, { target: { value: "POL-NEW-002" } });

    const save = screen.getByRole("button", { name: /save changes/i });
    await waitFor(() => expect(save).not.toBeDisabled());
    fireEvent.click(save);

    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(1));
    expect(toastError).toHaveBeenCalledWith(
      "Document has been reextracted; reload to see the latest fields.",
    );
    // #77 jargon-free invariants. The patterns are tight to the
    // realistic leak SHAPE — `/^conflict$/i` matches ONLY the bare
    // HTTP statusText (not a future legitimate server message that
    // includes the word "conflict" as part of a longer sentence
    // like "Field-edit conflict during save: reload and retry");
    // `/\b409\b/` matches the interpolated status-code leak shape
    // with word-boundary anchors so a vendor policy number that
    // happens to contain "409" doesn't false-positive. The exact-
    // string `.toHaveBeenCalledWith` above is the load-bearing pin.
    const args = toastError.mock.calls[0]?.[0];
    expect(typeof args).toBe("string");
    expect(args).not.toMatch(/^conflict$/i);
    expect(args).not.toMatch(/\b409\b/);
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("saveFields empty server message: toast.error falls back to GENERIC_FALLBACK_MESSAGE (#77 page-level fallback)", async () => {
    // Mirror of the reextract empty-message test — pins the page-
    // level `?.trim() ? err.message : GENERIC_FALLBACK_MESSAGE`
    // ternary on the saveFields path. Whitespace-only envelope
    // message specifically exercises the trim guard.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            fields: [
              {
                id: "f1",
                fieldName: "PolicyNumber",
                fieldValue: "POL-OLD-001",
                fieldType: "string",
                confidence: 0.91,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
      http.put(url("/api/documents/:id/fields"), () =>
        jsonError("server.error", "   ", { status: 500 }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_save_fallback_01" },
    });

    const input = await waitFor(() =>
      screen.getByDisplayValue("POL-OLD-001"),
    );
    fireEvent.change(input, { target: { value: "POL-NEW-002" } });

    const save = screen.getByRole("button", { name: /save changes/i });
    await waitFor(() => expect(save).not.toBeDisabled());
    fireEvent.click(save);

    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(1));
    expect(toastError).toHaveBeenCalledWith("Something went wrong. Try again.");
  });

  it("saveFields network-unreachable: toast.error fires GENERIC_FALLBACK_MESSAGE, no TypeError leak (#77)", async () => {
    // Mirror of the reextract network-unreachable test — pins the
    // fetchOrFriendlyThrow round-trip on the PUT path so a
    // regression that bypassed it for /fields surfaces here.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            fields: [
              {
                id: "f1",
                fieldName: "PolicyNumber",
                fieldValue: "POL-OLD-001",
                fieldType: "string",
                confidence: 0.91,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
      http.put(url("/api/documents/:id/fields"), () => Response.error()),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_save_network_01" },
    });

    const input = await waitFor(() =>
      screen.getByDisplayValue("POL-OLD-001"),
    );
    fireEvent.change(input, { target: { value: "POL-NEW-002" } });

    const save = screen.getByRole("button", { name: /save changes/i });
    await waitFor(() => expect(save).not.toBeDisabled());
    fireEvent.click(save);

    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(1));
    expect(toastError).toHaveBeenCalledWith("Something went wrong. Try again.");
    const args = toastError.mock.calls[0]?.[0];
    expect(args).not.toMatch(/typeerror/i);
    expect(args).not.toMatch(/failed to fetch/i);
  });
});

describe("DocumentDetailPage — responsive header (#181)", () => {
  it("wraps the header so a long filename never crowds the Re-extract / View actions", async () => {
    // A long COI filename used to sit in a `flex justify-between` header with no
    // wrap, pushing the action buttons off a 390px screen. The header now
    // stacks below sm and the h1 breaks long words. (Class-presence proxy —
    // JSDOM applies no stylesheet.)
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed", // terminal → no polling in this test
            originalFileName:
              "a-very-long-certificate-of-insurance-filename.pdf",
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_01" },
    });

    const heading = await waitFor(() =>
      screen.getByRole("heading", {
        name: /a-very-long-certificate-of-insurance-filename\.pdf/i,
      }),
    );
    expect(heading.className).toContain("break-words");
    const header = heading.closest("header");
    expect(header?.className).toContain("flex-col");
    expect(header?.className).toContain("sm:flex-row");
  });
});

describe("DocumentDetailPage — editable document type (#186)", () => {
  it("renders the current type and PATCHes the new value on change", async () => {
    let patchBody: unknown = null;
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ id: "d_type_01", documentType: "coi", extractionStatus: "Completed" })),
      ),
      http.patch(url("/api/documents/:id"), async ({ request }) => {
        patchBody = await request.json();
        return jsonOk({ message: "Document updated." });
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_type_01" },
    });

    const select = (await waitFor(() =>
      screen.getByRole("combobox", { name: /type/i }),
    )) as HTMLSelectElement;
    // Reflects the stored type via its human label.
    expect(select.value).toBe("coi");

    fireEvent.change(select, { target: { value: "permit" } });

    await waitFor(() => expect(patchBody).toEqual({ documentType: "permit" }));
    expect(toastSuccess).toHaveBeenCalledWith("Document type updated");
  });

  it("surfaces a friendly toast when the type update fails", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ id: "d_type_02", documentType: "coi", extractionStatus: "Completed" })),
      ),
      http.patch(url("/api/documents/:id"), () =>
        jsonError("document.invalid_type", "That document type isn't recognized.", { status: 400 }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_type_02" },
    });

    const select = await waitFor(() => screen.getByRole("combobox", { name: /type/i }));
    fireEvent.change(select, { target: { value: "license" } });

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith("That document type isn't recognized."),
    );
  });
});

describe("DocumentDetailPage — confidence hints instead of raw % (#188)", () => {
  it("shows tiered hints for low/mid confidence and NO raw percentage", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_conf_01",
            extractionStatus: "Completed",
            fields: [
              {
                id: "f-high",
                fieldName: "policy_number",
                fieldValue: "POL-1",
                fieldType: "string",
                confidence: 0.97, // high → no hint
                isManuallyEdited: false,
                originalValue: null,
              },
              {
                id: "f-mid",
                fieldName: "general_liability_limit",
                fieldValue: "1000000",
                fieldType: "string",
                confidence: 0.8, // mid → "Double-check this"
                isManuallyEdited: false,
                originalValue: null,
              },
              {
                id: "f-low",
                fieldName: "expiration_date",
                fieldValue: "2026-12-31",
                fieldType: "string",
                confidence: 0.5, // low → "Please verify"
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_conf_01" },
    });

    await waitFor(() => expect(screen.getByText("Double-check this")).toBeInTheDocument());
    expect(screen.getByText("Please verify")).toBeInTheDocument();
    // The raw "NN% confident" copy is gone entirely.
    expect(screen.queryByText(/% confident/i)).toBeNull();
    expect(screen.queryByText(/\d+%/)).toBeNull();
    // High-confidence field gets NO hint (only one "Double-check"/"Please verify" each).
    expect(screen.getAllByText(/double-check this|please verify/i)).toHaveLength(2);
  });
});

describe("DocumentDetailPage — a11y live-region announcement (#189)", () => {
  it("announces in a polite live region when the document finishes reading on a poll", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let calls = 0;
      server.use(
        http.get(url("/api/documents/:id"), () => {
          calls++;
          return jsonOk(
            makeDocumentDetail({
              id: "d_live",
              extractionStatus: calls === 1 ? "Processing" : "Completed",
              complianceStatus: "Compliant",
            }),
          );
        }),
      );

      const { container } = renderWithProviders(<DocumentDetailPage />, {
        auth: authedMe,
        params: { id: "d_live" },
      });
      await waitFor(() =>
        expect(screen.getByTestId("extraction-status")).toHaveTextContent("Reading…"),
      );

      const live = container.querySelector('[aria-live="polite"]') as HTMLElement;
      expect(live.textContent).toBe("");

      await vi.advanceTimersByTimeAsync(3000);
      await waitFor(() => expect(live.textContent).toMatch(/finished processing/i));
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("DocumentDetailPage — non-compliance explainer (#193)", () => {
  it("explains each failed requirement in plain English with an Email-vendor CTA", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_nc_01",
            extractionStatus: "Completed",
            complianceStatus: "NonCompliant",
            vendorName: "Beachfront Janitorial",
            vendorContactEmail: "ops@beachfront.test",
            complianceChecks: [
              makeComplianceCheck({
                id: "chk_fail",
                isPassed: false,
                ruleErrorMessage: "General liability must be at least $1,000,000",
                ruleFieldName: "general_liability_limit",
                actualValue: "500000",
              }),
              makeComplianceCheck({
                id: "chk_pass",
                isPassed: true,
                ruleErrorMessage: "Certificate holder must be listed",
              }),
            ],
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_nc_01" },
    });

    await waitFor(() =>
      expect(screen.getByText(/why isn't this compliant/i)).toBeInTheDocument(),
    );
    // The failed reason renders in plain English with money formatted.
    expect(
      screen.getByText(
        /General liability must be at least \$1,000,000 — this document shows \$500,000\./i,
      ),
    ).toBeInTheDocument();
    // The single passed requirement is acknowledged.
    expect(screen.getByText(/1 other requirement met/i)).toBeInTheDocument();
    // The primary CTA is a mailto to the vendor, pre-filled with the reason.
    const cta = screen.getByRole("link", {
      name: /email beachfront janitorial to fix this/i,
    });
    const href = cta.getAttribute("href") ?? "";
    expect(href.startsWith("mailto:ops@beachfront.test?")).toBe(true);
    expect(decodeURIComponent(href)).toContain(
      "General liability must be at least $1,000,000",
    );
    // No raw enum / snake_case leaks into the explainer.
    expect(screen.queryByText(/NonCompliant/)).toBeNull();
    expect(screen.queryByText(/general_liability_limit/)).toBeNull();
  });

  it("does not render the explainer when there are no failed checks", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            complianceChecks: [makeComplianceCheck({ id: "ok", isPassed: true })],
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_completed_01" },
    });
    await waitFor(() => expect(screen.getByText("coi.pdf")).toBeInTheDocument());
    expect(screen.queryByText(/why isn't this compliant/i)).toBeNull();
  });

  it("falls back to a no-recipient mailto + a vendor-page tip when the vendor has no email", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            complianceStatus: "NonCompliant",
            vendorName: "Acme",
            vendorId: "v_acme_01",
            vendorContactEmail: null,
            complianceChecks: [makeComplianceCheck({ isPassed: false })],
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_nc_02" },
    });
    const cta = await waitFor(() =>
      screen.getByRole("link", { name: /email acme to fix this/i }),
    );
    expect(cta.getAttribute("href")?.startsWith("mailto:?")).toBe(true);
    expect(screen.getByText(/add an email for acme/i)).toBeInTheDocument();
    // The tip links to the vendor's page so the missing email is one click away.
    expect(
      screen.getByRole("link", { name: /the vendor's page/i }),
    ).toHaveAttribute("href", "/vendors/v_acme_01");
  });

  it("synthesizes a plain-English reason in the card when the owner set no message — no operator/snake_case leak", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_nc_03",
            extractionStatus: "Completed",
            complianceStatus: "NonCompliant",
            vendorName: "Acme",
            vendorContactEmail: "ops@acme.test",
            complianceChecks: [
              makeComplianceCheck({
                isPassed: false,
                ruleErrorMessage: null,
                ruleFieldName: "general_liability_limit",
                ruleOperator: "min_value",
                ruleExpectedValue: "1000000",
                actualValue: "500000",
              }),
            ],
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_nc_03" },
    });
    await waitFor(() =>
      expect(screen.getByText(/why isn't this compliant/i)).toBeInTheDocument(),
    );
    // Synthesized reason reads in plain English with money formatting.
    expect(
      screen.getByText(
        /General liability limit must be at least \$1,000,000 — this document shows \$500,000\./i,
      ),
    ).toBeInTheDocument();
    // Raw operator token / snake_case field name never reach the DOM.
    expect(screen.queryByText(/min_value/)).toBeNull();
    expect(screen.queryByText(/general_liability_limit/)).toBeNull();
  });

  it("lists every failed requirement and pluralizes the met-count", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_nc_04",
            extractionStatus: "Completed",
            complianceStatus: "NonCompliant",
            vendorName: "Acme",
            vendorContactEmail: "ops@acme.test",
            complianceChecks: [
              makeComplianceCheck({
                id: "f1",
                isPassed: false,
                ruleErrorMessage: "General liability must be at least $1,000,000",
              }),
              makeComplianceCheck({
                id: "f2",
                isPassed: false,
                ruleErrorMessage: "A current workers' comp certificate is required",
              }),
              makeComplianceCheck({ id: "p1", isPassed: true }),
              makeComplianceCheck({ id: "p2", isPassed: true }),
            ],
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_nc_04" },
    });
    await waitFor(() =>
      expect(screen.getByText(/why isn't this compliant/i)).toBeInTheDocument(),
    );
    // BOTH distinct failed reasons render (not just the first).
    expect(
      screen.getByText(/General liability must be at least \$1,000,000/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/current workers' comp certificate is required/i),
    ).toBeInTheDocument();
    // Two passed → plural "requirements".
    expect(screen.getByText(/2 other requirements met/i)).toBeInTheDocument();
  });
});

describe("DocumentDetailPage — ManualRequired review CTA (#193)", () => {
  it("renders the amber review card, enables Save without edits, and outlines low-confidence fields", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_mr_01",
            extractionStatus: "ManualRequired",
            complianceStatus: "Pending",
            fields: [
              {
                id: "f-low",
                fieldName: "policy_number",
                fieldValue: "POL-1",
                fieldType: "string",
                confidence: 0.5,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_mr_01" },
    });
    await waitFor(() =>
      expect(screen.getByText(/double-check these details/i)).toBeInTheDocument(),
    );
    // Save is pre-emptively enabled so the user can confirm "looks right".
    expect(
      screen.getByRole("button", { name: /save changes/i }),
    ).not.toBeDisabled();
    // The low-confidence field input is outlined (amber/rose border class).
    expect(screen.getByDisplayValue("POL-1").className).toMatch(
      /border-(amber|rose)-400/,
    );
  });

  it("does not render the amber card for a Completed document", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ extractionStatus: "Completed" })),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_completed_01" },
    });
    await waitFor(() => expect(screen.getByText("coi.pdf")).toBeInTheDocument());
    expect(screen.queryByText(/double-check these details/i)).toBeNull();
  });
});

describe("DocumentDetailPage — View file streams through the authenticated proxy (#254)", () => {
  // jsdom's URL lacks the object-URL statics. ATTACH them rather than
  // replacing the global URL constructor — MSW and the api client both do
  // `new URL(...)`, so swapping the constructor for a stub object kills every
  // mocked request in the test. Removed again below so no other suite in this
  // file can accidentally depend on them.
  function stubObjectUrl() {
    const createObjectURL = vi.fn().mockReturnValue("blob:mock-view-file");
    const revokeObjectURL = vi.fn();
    Object.assign(URL, { createObjectURL, revokeObjectURL });
    return { createObjectURL, revokeObjectURL };
  }

  afterEach(() => {
    delete (URL as unknown as Record<string, unknown>).createObjectURL;
    delete (URL as unknown as Record<string, unknown>).revokeObjectURL;
    // The window.open spies must not leak a null-returning mock into later
    // suites in this file (no global restoreMocks in this project).
    vi.restoreAllMocks();
  });

  function mountCompleted() {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(makeDocumentDetail({ extractionStatus: "Completed" })),
      ),
    );
    return renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_completed_01" },
    });
  }

  it("opens a tab synchronously, then navigates it to the fetched blob", async () => {
    const { createObjectURL } = stubObjectUrl();
    const tab = { location: { href: "" }, close: vi.fn() };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(tab as unknown as Window);
    let served = 0;
    server.use(
      http.get(url("/api/documents/:id/file"), () => {
        served++;
        return new Response("PDFBYTES", {
          status: 200,
          headers: { "Content-Type": "application/pdf" },
        });
      }),
    );

    mountCompleted();
    fireEvent.click(await screen.findByRole("button", { name: /view file/i }));

    // The tab handle is grabbed inside the click's own task — that's what
    // keeps popup blockers out of the way — and navigated once bytes land.
    expect(openSpy).toHaveBeenCalledWith("about:blank", "_blank");
    await waitFor(() => expect(tab.location.href).toBe("blob:mock-view-file"));
    expect(served).toBe(1);
    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(tab.close).not.toHaveBeenCalled();
    expect(toastError).not.toHaveBeenCalled();
    // "View file" is a button-driven proxy fetch now, never an anchor — the
    // old raw-blob-URL link 409'd on every click (FP-060).
    expect(screen.queryByRole("link", { name: /view file/i })).toBeNull();
  });

  it("revokes the object URL when the viewer tab closes — and not before", async () => {
    // The revoke is tied to the TAB's lifetime (5s tab.closed poll), not a
    // fixed timer: a timed revoke would break F5 / save-as in a tab the user
    // deliberately keeps open to compare against the fields.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const { revokeObjectURL } = stubObjectUrl();
      const tab = { location: { href: "" }, close: vi.fn(), closed: false };
      vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
      server.use(
        http.get(url("/api/documents/:id/file"), () =>
          new Response("PDFBYTES", {
            status: 200,
            headers: { "Content-Type": "application/pdf" },
          }),
        ),
      );

      mountCompleted();
      fireEvent.click(await screen.findByRole("button", { name: /view file/i }));
      await waitFor(() => expect(tab.location.href).toBe("blob:mock-view-file"));

      // Tab still open → polls fire but never revoke.
      await vi.advanceTimersByTimeAsync(10_000);
      expect(revokeObjectURL).not.toHaveBeenCalled();

      // Tab closes → the next poll revokes exactly once and clears the interval.
      tab.closed = true;
      await vi.advanceTimersByTimeAsync(5_000);
      expect(revokeObjectURL).toHaveBeenCalledExactlyOnceWith("blob:mock-view-file");
      await vi.advanceTimersByTimeAsync(15_000);
      expect(revokeObjectURL).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("closes the tab and surfaces the server message when the fetch fails", async () => {
    stubObjectUrl();
    const tab = { location: { href: "" }, close: vi.fn() };
    vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    server.use(
      http.get(url("/api/documents/:id/file"), () =>
        jsonError("document.not_found", "Document not found.", { status: 404 }),
      ),
    );

    mountCompleted();
    fireEvent.click(await screen.findByRole("button", { name: /view file/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Document not found."));
    expect(tab.close).toHaveBeenCalledTimes(1);
    expect(tab.location.href).toBe("");
    // #77 contract: never HTTP jargon — no interpolated status code, no bare statusText.
    const arg = toastError.mock.calls[0]?.[0] as string;
    expect(arg).not.toMatch(/\b404\b/);
    expect(arg).not.toMatch(/^not found$/i);
  });

  it("explains in plain English when the browser blocks the new tab", async () => {
    stubObjectUrl();
    vi.spyOn(window, "open").mockReturnValue(null);
    server.use(
      http.get(url("/api/documents/:id/file"), () =>
        new Response("PDFBYTES", {
          status: 200,
          headers: { "Content-Type": "application/pdf" },
        }),
      ),
    );

    mountCompleted();
    fireEvent.click(await screen.findByRole("button", { name: /view file/i }));

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        "Your browser blocked the new tab. Allow pop-ups for this site and try again.",
      ),
    );
  });
});

describe("DocumentDetailPage — humanized processing error (#193)", () => {
  it("shows friendly copy + a support link and hides the raw code behind a disclosure", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_err_01",
            extractionStatus: "Failed",
            processingError:
              "extraction.too_many_attempts: Exceeded 5 attempts (6 so far).",
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_err_01" },
    });
    await waitFor(() =>
      expect(screen.getByText(/couldn't read this document/i)).toBeInTheDocument(),
    );
    // Friendly mapped copy is shown (says "file", distinct from the headline).
    const friendly = screen.getByText(
      /tried several times but couldn't read this file/i,
    );
    expect(friendly).toBeInTheDocument();
    // The raw code must NOT leak into the visible friendly body copy.
    expect(friendly.textContent).not.toMatch(/extraction\.too_many_attempts/);
    // The raw code appears EXACTLY ONCE, and only inside the <details> disclosure.
    const summary = screen.getByText(/details for support/i);
    expect(summary.tagName.toLowerCase()).toBe("summary");
    const rawNodes = screen.getAllByText(/extraction\.too_many_attempts/i);
    expect(rawNodes).toHaveLength(1);
    expect(rawNodes[0].closest("details")).not.toBeNull();
    // A support mailto link is present.
    expect(
      screen
        .getByRole("link", { name: /contact support/i })
        .getAttribute("href")
        ?.startsWith("mailto:"),
    ).toBe(true);
  });
});

describe("DocumentDetailPage — compliance verdict is a today-snapshot (#399)", () => {
  it("clarifies that the compliance verdict is current as of today, next to the expiration", async () => {
    // The product can't yet check its own "coverage dates include the event" requirement, so the
    // verdict must not read as a promise about a future date. The Compliance cell carries an honest
    // "current as of today" clarifier pointing the reader at the expiration date to check coverage
    // against the date they actually need it. Copy honesty only — no verdict logic changes.
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_compliant",
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            vendorId: "v2",
            vendorName: "Caterer Co",
            expirationDate: "2026-09-30T00:00:00Z",
            complianceChecks: [],
          }),
        ),
      ),
      http.get(url("/api/vendors"), () => jsonOk([])),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_compliant" } });

    // The verdict badge still says "Compliant" (verdict semantics unchanged)…
    expect(await screen.findByTestId("compliance-status")).toHaveTextContent(/compliant/i);
    // …but it's now framed as a today-snapshot that the reader should check against their own date.
    expect(screen.getByText(/current as of today/i)).toBeInTheDocument();
    expect(screen.getByText(/check the expiration date against the date you need coverage/i)).toBeInTheDocument();
  });
});

describe("DocumentDetailPage — pending edits vs. 'Read again' (#363)", () => {
  // The bug: `edits` was cleared ONLY in saveFields.onSuccess, so a value typed
  // before a re-read survived it and the next Save silently PUT the stale value
  // back over the fresh extraction AND the verdict recomputed from it in the same
  // transaction (ADR 0030).
  //
  // Field ids: ExtractionWorker.PersistSuccess RemoveRange-s the DocumentField
  // rows and re-adds them with `Id = Guid.NewGuid()`, so a re-read really does
  // hand back DIFFERENT ids — the fixtures below model that (`f1` → `f1_new`).
  // That is what made the pre-fix bug invisible rather than merely confusing: the
  // new ids remount the inputs, so the screen showed the fresh values while
  // `edits` still held the stale one that Save would send. An in-place refetch (a
  // poll, or the post-save invalidate) keeps the ids and does NOT remount — the
  // "keeps the edit visible across an in-place refetch" case below covers that
  // half, which is why the display must be driven by state and not the DOM.
  //
  // No fake timers here — reextract.onSuccess invalidates the detail query, which
  // drives the refetch deterministically. The Pending→Completed polling transition
  // itself is already pinned by the "#36 AC #2" block above.
  const withValue = (value: string, fieldId = "f1", overrides = {}) =>
    makeDocumentDetail({
      id: "d_363",
      extractionStatus: "Completed",
      complianceStatus: "Compliant",
      fields: [
        {
          id: fieldId,
          fieldName: "expiration_date",
          fieldValue: value,
          fieldType: "date",
          confidence: 0.95,
          isManuallyEdited: false,
          originalValue: null,
        },
      ],
      ...overrides,
    });

  const confirmReadAgain = async () => {
    fireEvent.click(screen.getByRole("button", { name: /read again/i }));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: /read again/i }));
  };

  it("shows the FRESH re-read value, not the stale typed one, and leaves nothing pending to save", async () => {
    const seq = sequencedJsonOk(withValue("2026-11-01"), withValue("2027-03-15", "f1_new"));
    server.use(
      http.get(url("/api/documents/:id"), () => seq()),
      http.post(url("/api/documents/:id/reextract"), () => jsonOk<void>(undefined)),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363" } });

    // Type a correction, then re-read the file and confirm the discard warning.
    const input = await screen.findByDisplayValue("2026-11-01");
    fireEvent.change(input, { target: { value: "2025-01-01" } });
    expect(screen.getByRole("button", { name: /save changes/i })).not.toBeDisabled();
    await confirmReadAgain();

    // The re-read landed: the input must track the SERVER's fresh value. Without
    // the edits-clear the controlled input keeps rendering the overlay ("2025-01-01")
    // even though the row remounted under a new id.
    await waitFor(() => expect(screen.getByLabelText(/expiration date/i)).toHaveValue("2027-03-15"));
    expect(screen.queryByDisplayValue("2025-01-01")).toBeNull();
    // …and the discarded edit is genuinely gone, not merely hidden: nothing pending.
    // This is the assertion that catches the ACTUAL production shape of the bug,
    // where the remount made the screen look right while `edits` stayed poisoned.
    expect(screen.getByRole("button", { name: /save changes/i })).toBeDisabled();
  });

  it("does not smuggle the discarded edit into a LATER save of a different field", async () => {
    // The data-corruption assertion. After a re-read the user edits some other
    // field and saves; the stale pre-re-read value must not ride along in that PUT
    // and overwrite the fresh extraction + its verdict.
    let putBody: unknown = null;
    // Re-read hands back new row ids (Guid.NewGuid() per field), same as the worker.
    const twoFields = (expiration: string, suffix = "") =>
      makeDocumentDetail({
        id: "d_363b",
        extractionStatus: "Completed",
        complianceStatus: "Compliant",
        fields: [
          {
            id: `f1${suffix}`,
            fieldName: "expiration_date",
            fieldValue: expiration,
            fieldType: "date",
            confidence: 0.95,
            isManuallyEdited: false,
            originalValue: null,
          },
          {
            id: `f2${suffix}`,
            fieldName: "policy_number",
            fieldValue: "POL-1",
            fieldType: "string",
            confidence: 0.9,
            isManuallyEdited: false,
            originalValue: null,
          },
        ],
      });
    const seq = sequencedJsonOk(twoFields("2026-11-01"), twoFields("2027-03-15", "_new"));
    server.use(
      http.get(url("/api/documents/:id"), () => seq()),
      http.post(url("/api/documents/:id/reextract"), () => jsonOk<void>(undefined)),
      http.put(url("/api/documents/:id/fields"), async ({ request }) => {
        putBody = await request.json();
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363b" } });

    fireEvent.change(await screen.findByDisplayValue("2026-11-01"), {
      target: { value: "2025-01-01" },
    });
    await confirmReadAgain();
    await waitFor(() => expect(screen.getByLabelText(/expiration date/i)).toHaveValue("2027-03-15"));

    // Now edit an unrelated field and save.
    fireEvent.change(screen.getByLabelText(/policy number/i), { target: { value: "POL-2" } });
    const save = screen.getByRole("button", { name: /save changes/i });
    await waitFor(() => expect(save).not.toBeDisabled());
    fireEvent.click(save);

    await waitFor(() => expect(putBody).not.toBeNull());
    const sent = (putBody as { fields: { fieldName: string; fieldValue: string }[] }).fields;
    expect(sent).toEqual([{ fieldName: "policy_number", fieldValue: "POL-2" }]);
    // Explicitly: the discarded expiration edit is absent from the wire.
    expect(JSON.stringify(putBody)).not.toContain("2025-01-01");
    expect(sent.some((f) => f.fieldName === "expiration_date")).toBe(false);
  });

  it("KEEPS the pending edit when the re-read request itself fails", async () => {
    // Why the clear lives in onSuccess and not onMutate: a reextract that never
    // got queued must not cost the user their unsaved correction for nothing.
    server.use(
      http.get(url("/api/documents/:id"), () => jsonOk(withValue("2026-11-01"))),
      http.post(url("/api/documents/:id/reextract"), () =>
        jsonError("extraction.queue_failed", "Couldn't queue that re-read. Try again.", {
          status: 500,
        }),
      ),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363" } });

    fireEvent.change(await screen.findByDisplayValue("2026-11-01"), {
      target: { value: "2025-01-01" },
    });
    await confirmReadAgain();

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(screen.getByLabelText(/expiration date/i)).toHaveValue("2025-01-01");
    expect(screen.getByRole("button", { name: /save changes/i })).not.toBeDisabled();
  });

  it("KEEPS the pending edit across 'Check again' (a re-grade never touches field values)", async () => {
    // Two guards in one. (a) The opposite over-correction: recheck re-runs
    // compliance only, so clearing edits there would throw away work for no
    // reason. (b) The in-place-refetch half of the controlled binding: recheck
    // invalidates the detail query and the refetch returns the SAME field ids, so
    // React reuses the inputs rather than remounting them — the pending overlay
    // has to survive that and stay on screen, because the value displayed must
    // always be the value a Save would send.
    //
    // The refetch deliberately returns a DIFFERENT server value under the same
    // field id, so (b) is a real contest between overlay and server rather than a
    // tautology against byte-identical data. The doc-level `expirationDate` moves
    // with it purely as an observable landing signal for the second payload.
    const seq = sequencedJsonOk(
      withValue("2026-11-01", "f1", { expirationDate: "2026-11-01T00:00:00Z" }),
      withValue("2029-09-09", "f1", { expirationDate: "2029-09-09T00:00:00Z" }),
    );
    server.use(
      http.get(url("/api/documents/:id"), () => seq()),
      http.post(url("/api/compliance/check/:id"), () => jsonOk<void>(undefined)),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363" } });

    fireEvent.change(await screen.findByDisplayValue("2026-11-01"), {
      target: { value: "2025-01-01" },
    });
    fireEvent.click(screen.getByRole("button", { name: /check again/i }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Re-checking compliance…"));
    // Wait for the SECOND payload to actually render (the Expires cell is fed
    // straight from the server field), so the assertion below can't pass merely
    // because the refetch hadn't landed yet.
    const expectedExpires = new Date("2029-09-09T00:00:00Z").toLocaleDateString(undefined, {
      timeZone: "UTC",
    });
    await waitFor(() => expect(screen.getByText(expectedExpires)).toBeInTheDocument());

    // Server moved underneath the overlay; the pending edit still wins on screen.
    expect(screen.getByLabelText(/expiration date/i)).toHaveValue("2025-01-01");
    expect(screen.getByRole("button", { name: /save changes/i })).not.toBeDisabled();
  });

  it("does not discard a field typed WHILE a save of another field is in flight", async () => {
    // #363 review (CONFIRMED): the post-save clear must be exactly as wide as what
    // the PUT actually persisted. The inputs stay editable while the mutation is
    // pending, so a whole-map reset silently eats anything typed into a different
    // field during the round-trip — and the controlled input snaps back to the
    // server value, so the user watches their correction vanish.
    let releaseSave: (() => void) | null = null;
    const savePut = new Promise<void>((resolve) => {
      releaseSave = resolve;
    });
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            id: "d_363c",
            extractionStatus: "Completed",
            complianceStatus: "Compliant",
            fields: [
              {
                id: "f1",
                fieldName: "expiration_date",
                fieldValue: "2026-11-01",
                fieldType: "date",
                confidence: 0.95,
                isManuallyEdited: false,
                originalValue: null,
              },
              {
                id: "f2",
                fieldName: "policy_number",
                fieldValue: "POL-1",
                fieldType: "string",
                confidence: 0.9,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
      http.put(url("/api/documents/:id/fields"), async () => {
        await savePut; // hold the PUT open so we can type into the gap
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363c" } });

    // Save an edit to expiration_date…
    fireEvent.change(await screen.findByDisplayValue("2026-11-01"), {
      target: { value: "2027-05-05" },
    });
    fireEvent.click(screen.getByRole("button", { name: /save changes/i }));

    // …and correct a DIFFERENT field while that save is still in flight.
    await waitFor(() => expect(screen.getByRole("button", { name: /save changes/i })).toBeDisabled());
    fireEvent.change(screen.getByLabelText(/policy number/i), { target: { value: "POL-2" } });
    releaseSave!();

    // The in-flight correction survives the save's edit-clear: still on screen and
    // still pending. A whole-map `setEdits({})` reverted it to "POL-1" here.
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Fields updated"));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /save changes/i })).not.toBeDisabled(),
    );
    expect(screen.getByLabelText(/policy number/i)).toHaveValue("POL-2");
  });

  it("does not discard a re-edit of the SAME field made while its save is in flight", async () => {
    // #363 review round 2 (CONFIRMED): clearing by field NAME still ate the newer
    // value when the user corrected the very field being saved. The overlay entry
    // may only be dropped if it still holds exactly what this PUT persisted.
    let releaseSave: (() => void) | null = null;
    const savePut = new Promise<void>((resolve) => {
      releaseSave = resolve;
    });
    server.use(
      http.get(url("/api/documents/:id"), () => jsonOk(withValue("2026-11-01"))),
      http.put(url("/api/documents/:id/fields"), async () => {
        await savePut;
        return jsonOk<void>(undefined);
      }),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363" } });

    // Save "2027-05-05"…
    fireEvent.change(await screen.findByDisplayValue("2026-11-01"), {
      target: { value: "2027-05-05" },
    });
    fireEvent.click(screen.getByRole("button", { name: /save changes/i }));
    await waitFor(() => expect(screen.getByRole("button", { name: /save changes/i })).toBeDisabled());

    // …then correct THAT SAME field again before the request settles.
    fireEvent.change(screen.getByLabelText(/expiration date/i), {
      target: { value: "2027-06-06" },
    });
    releaseSave!();

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Fields updated"));
    // The newer correction survives — it was never what the PUT sent. Clearing by
    // name alone reverted this to the persisted "2027-05-05" and disabled Save.
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /save changes/i })).not.toBeDisabled(),
    );
    expect(screen.getByLabelText(/expiration date/i)).toHaveValue("2027-06-06");
  });

  it("keeps the saved values on screen when the post-save refetch fails", async () => {
    // invalidateQueries resolves even when the refetch errors, so a blind clear
    // would drop the overlay while the cache still held PRE-save data — rendering
    // a value that contradicts the database, with Save disabled so the user
    // couldn't re-apply it. The overlay stays until fresh data actually lands.
    let getCalls = 0;
    server.use(
      http.get(url("/api/documents/:id"), () => {
        getCalls += 1;
        return getCalls === 1
          ? jsonOk(withValue("2026-11-01"))
          : jsonError("server.error", "Something went wrong. Try again.", { status: 500 });
      }),
      http.put(url("/api/documents/:id/fields"), () => jsonOk<void>(undefined)),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363" } });

    fireEvent.change(await screen.findByDisplayValue("2026-11-01"), {
      target: { value: "2027-05-05" },
    });
    fireEvent.click(screen.getByRole("button", { name: /save changes/i }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Fields updated"));
    await waitFor(() => expect(getCalls).toBeGreaterThanOrEqual(2));
    // The PUT succeeded, so "2027-05-05" IS what the database holds — keep showing
    // it rather than the stale cached "2026-11-01".
    expect(screen.getByLabelText(/expiration date/i)).toHaveValue("2027-05-05");
  });

  it("renders typed values in the manual-entry grid and clears it on a re-read", async () => {
    // The FP-064 recovery form (zero extracted fields) binds to `edits` alone, with
    // no server value to fall back on. A broken binding would leave it visually
    // untypable while the existing wire-level test still passed.
    const empty = makeDocumentDetail({
      id: "d_363d",
      extractionStatus: "Failed",
      complianceStatus: "Pending",
      vendorId: "v2",
      vendorName: "Caterer Co",
      complianceChecks: [],
      fields: [],
    });
    server.use(
      http.get(url("/api/documents/:id"), () => jsonOk(empty)),
      http.post(url("/api/documents/:id/reextract"), () => jsonOk<void>(undefined)),
    );

    renderWithProviders(<DocumentDetailPage />, { auth: authedMe, params: { id: "d_363d" } });

    const limit = await screen.findByLabelText(/general liability limit/i);
    fireEvent.change(limit, { target: { value: "1000000" } });
    // The typed value actually renders (not just lands in `edits`).
    expect(limit).toHaveValue("1000000");

    // A re-read discards manual entries too — same overlay, same promise.
    await confirmReadAgain();
    await waitFor(() =>
      expect(screen.getByLabelText(/general liability limit/i)).toHaveValue(""),
    );
    expect(screen.getByRole("button", { name: /save changes/i })).toBeDisabled();
  });
});
