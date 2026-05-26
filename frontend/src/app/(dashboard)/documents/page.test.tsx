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
import { describe, it, expect, vi, beforeEach } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
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

  it("error: a 5xx still renders the page chrome, list shows the empty fallback (graceful degradation)", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonError("server.error", "DB down.", { status: 500 }),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    // Page heading still renders; the list shows the empty branch
    // because `docs.data?.items ?? []` is empty on error. This is
    // imperfect UX (the page doesn't surface the error to the user
    // today) and a candidate for a separate UX ticket, but the
    // assertion below pins TODAY's behavior so a future fix flips this
    // test rather than silently changing copy.
    //
    // Wait for the query to settle (isLoading → false) before checking
    // the empty fallback — the in-flight Loading row would otherwise
    // race the assertion.
    await waitFor(() =>
      expect(screen.queryByText(/loading documents/i)).toBeNull(),
    );
    expect(
      screen.getByRole("heading", { name: /^documents$/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/no documents yet/i),
    ).toBeInTheDocument();
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
