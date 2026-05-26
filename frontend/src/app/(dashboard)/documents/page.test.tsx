/**
 * Documents list page — state matrix + the live status badges that the
 * polling refetch updates (#36).
 *
 * The polling INTERVAL is tested at the hook level (useDocuments.test.tsx);
 * here we pin the LIST-RENDERING contract — each extraction status
 * surfaces the right badge text + the right compliance-status badge —
 * and the empty / loading / error / populated states.
 *
 * Upload is exercised via the test-id-free dropzone path is fragile
 * to drive end-to-end; covered in the polling test (#34 example) and
 * the hook test instead.
 */
import { afterEach, describe, it, expect, vi, beforeEach } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import DocumentsPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  documentsAllStatuses,
  makeDocumentsResponse,
} from "@/test";

afterEach(() => {
  // Belt-and-suspenders — any test that flipped fake timers (the new
  // page-level polling test) and threw before its `finally` block must
  // not leak fake timers into the next test.
  vi.useRealTimers();
});

const { toastSuccess, toastError } = vi.hoisted(() => ({
  toastSuccess: vi.fn(),
  toastError: vi.fn(),
}));
vi.mock("sonner", () => ({
  toast: { success: toastSuccess, error: toastError },
  Toaster: () => null,
}));

beforeEach(() => {
  toastSuccess.mockClear();
  toastError.mockClear();
});

describe("DocumentsPage — state matrix (#36)", () => {
  it("loading: renders the loading row before the fetch resolves", () => {
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.get(url("/api/documents"), async () => {
        await settled;
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    expect(screen.getByText(/loading documents/i)).toBeInTheDocument();
    release();
  });

  it("empty: renders the no-documents-yet copy when the org has no documents", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(makeDocumentsResponse({ items: [], total: 0 })),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByText(/no documents yet\. drop one above to get started/i),
      ).toBeInTheDocument(),
    );
    // Total counter reflects empty.
    expect(screen.getByText(/0 total/i)).toBeInTheDocument();
  });

  it("error: a 5xx surfaces an error card with the server message and a Retry affordance, NOT the empty fallback (#80)", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonError("server.error", "DB down.", { status: 500 }),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    // Page chrome still renders. The list row reports the error
    // distinctly from the empty-org state (#80) — a brand-new org
    // with zero documents must NOT be visually indistinguishable from
    // a backend outage. `err.message` carries the human server copy
    // from the ApiError envelope.
    await waitFor(() =>
      expect(screen.queryByText(/loading documents/i)).toBeNull(),
    );
    expect(
      screen.getByRole("heading", { name: /^documents$/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/couldn't load documents/i),
    ).toBeInTheDocument();
    expect(screen.getByText("DB down.")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /retry/i }),
    ).toBeInTheDocument();
    // Negative: the empty-state copy must NOT appear on error.
    expect(screen.queryByText(/no documents yet/i)).toBeNull();
  });

  it("retry-on-5xx: clicking Retry fires a second fetch; a subsequent 200 swaps the error card for the populated list (#80)", async () => {
    // Pins the affordance #80 added: the Retry button must actually
    // re-issue the documents fetch. Without this test, swapping
    // `onClick={() => docs.refetch()}` for a no-op or a wrong query
    // would slip past every other test in this file.
    let calls = 0;
    server.use(
      http.get(url("/api/documents"), () => {
        calls++;
        if (calls === 1) {
          return jsonError("server.error", "DB blip.", { status: 500 });
        }
        return jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }],
            total: 1,
          }),
        );
      }),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    // Wait for the first fetch to land on the error card.
    await waitFor(() =>
      expect(screen.getByText(/couldn't load documents/i)).toBeInTheDocument(),
    );
    expect(calls).toBe(1);

    fireEvent.click(screen.getByRole("button", { name: /retry/i }));

    // The second fetch fires AND its 200 response swaps the error card
    // for the populated row — the user gets out of the failure state
    // without a manual reload.
    await waitFor(() => expect(calls).toBe(2));
    await waitFor(() =>
      expect(screen.getByText("coi-completed.pdf")).toBeInTheDocument(),
    );
    expect(screen.queryByText(/couldn't load documents/i)).toBeNull();
  });

  it("populated: renders every documentsAllStatuses row with extraction + compliance badges", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(
          makeDocumentsResponse({
            items: documentsAllStatuses.map((d) => ({ ...d })),
            total: documentsAllStatuses.length,
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    // Each row renders by file name (the link text).
    await waitFor(() =>
      expect(screen.getByText("coi-pending.pdf")).toBeInTheDocument(),
    );
    expect(screen.getByText("license-processing.pdf")).toBeInTheDocument();
    expect(screen.getByText("coi-completed.pdf")).toBeInTheDocument();
    expect(screen.getByText("permit-failed.pdf")).toBeInTheDocument();

    // Each status badge appears at least once. Using anchored regex
    // because the Completed badge in this fixture also renders the
    // extraction confidence as " · 94%" suffixed — `getAllByText
    // ("Completed")` (exact) would miss it. The anchors keep the test
    // tolerant to the optional confidence suffix while still rejecting
    // accidental column-header substring matches.
    expect(screen.getAllByText(/^Pending( |$)/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/^Processing( |$)/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/^Completed( |$)/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/^Failed( |$)/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText(/^Compliant( |$)/).length).toBeGreaterThanOrEqual(1);

    // Total counter is correct.
    expect(screen.getByText(/4 total/i)).toBeInTheDocument();
  });

  it("polling transition: a Processing row's badge swaps to Completed without a manual reload", async () => {
    // AC #2 requires the LIST page (not just the detail) to assert the
    // polling transition. useDocuments.test.tsx pins the 5s interval
    // contract at the hook level; this test pins that the LIST PAGE
    // actually re-renders the new badge after the refetch.
    //
    // Fake timers MUST be active before the component mounts so the
    // refetchInterval is scheduled on the fake queue (otherwise the
    // first interval was scheduled with real timers and is orphaned
    // when we flip — the polling never fires inside the test).
    // `shouldAdvanceTime: true` keeps RTL's waitFor's own setTimeout
    // polls served via real-time elapsed.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let calls = 0;
      server.use(
        http.get(url("/api/documents"), () => {
          calls++;
          return jsonOk(
            calls === 1
              ? makeDocumentsResponse({
                  items: [
                    {
                      ...documentsAllStatuses[1], // Processing row
                      complianceStatus: "NonCompliant",
                      // Override the fixture's "Processing Vendor" cell
                      // so `getByText(/^Processing$/)` matches only the
                      // extraction badge, not the vendor-name cell.
                      vendorName: "Acme Sub",
                    },
                  ],
                  total: 1,
                })
              : makeDocumentsResponse({
                  items: [
                    {
                      ...documentsAllStatuses[2], // Completed row
                      // Same vendor-name override so the assertion
                      // matches the extraction badge unambiguously.
                      vendorName: "Acme Sub",
                    },
                  ],
                  total: 1,
                }),
          );
        }),
      );

      renderWithProviders(<DocumentsPage />, { auth: authedMe });

      await waitFor(() =>
        expect(screen.getByText(/^Processing$/)).toBeInTheDocument(),
      );

      const beforeAdvance = calls;
      await vi.advanceTimersByTimeAsync(5000);

      await waitFor(() =>
        expect(screen.getByText(/^Completed/)).toBeInTheDocument(),
      );
      expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);
      expect(screen.queryByText(/^Processing$/)).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it("rows link to /documents/[id] so the user can drill into a single document", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }],
            total: 1,
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByText("coi-completed.pdf")).toBeInTheDocument(),
    );
    expect(
      screen.getByRole("link", { name: /coi-completed\.pdf/i }),
    ).toHaveAttribute("href", "/documents/d_completed_01");
  });
});
