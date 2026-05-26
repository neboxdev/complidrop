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
import { screen, waitFor, within } from "@testing-library/react";
import DocumentDetailPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  makeDocumentDetail,
} from "@/test";

// sonner mock is provided by the harness (vitest.setup.ts +
// src/test/sonner.ts). This file doesn't assert on toast calls — the
// reextract / saveFields mutation paths that fire toasts are not
// driven by any test here. If a future test needs to assert on a
// toast, add `toastSuccess` / `toastError` to the @/test import. (#74)

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
    // Extraction + compliance badges visible.
    expect(screen.getByText("Completed")).toBeInTheDocument();
    expect(screen.getByText("Compliant")).toBeInTheDocument();
    // Field row rendered.
    expect(screen.getByText("PolicyNumber")).toBeInTheDocument();
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
      expect(screen.getByText(/extraction error/i)).toBeInTheDocument(),
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
    expect(screen.queryByText(/extraction error/i)).toBeNull();
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
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        return jsonOk(
          calls === 1
            ? makeDocumentDetail({
                extractionStatus: "Pending",
                complianceStatus: "NonCompliant",
              })
            : makeDocumentDetail({
                extractionStatus: "Completed",
                extractionConfidence: 0.91,
                complianceStatus: "Compliant",
              }),
        );
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_01" },
    });

    // First render: Pending. The "Extraction in progress…" copy fires
    // when fields are empty and the status is Pending/Processing.
    await waitFor(() =>
      expect(screen.getByText("Pending")).toBeInTheDocument(),
    );
    expect(screen.getByText(/extraction in progress/i)).toBeInTheDocument();
    expect(calls).toBeGreaterThanOrEqual(1);

    // Snapshot pre-advance count so the assertion is delta-based: the
    // explicit 3-second advance must trigger AT LEAST one refetch, and
    // the post-state must be Completed. Tolerates one extra auto-fire
    // from `shouldAdvanceTime: true` absorbing real wall-clock during
    // the preceding `waitFor`.
    const beforeAdvance = calls;
    await vi.advanceTimersByTimeAsync(3000);

    await waitFor(() =>
      expect(screen.getByText("Completed")).toBeInTheDocument(),
    );
    expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);
    // Scope the negative assertion to the Extraction summary cell, not
    // the whole document, so a future page restructure that surfaces
    // "Pending" elsewhere (e.g. a compliance-stub badge in a tooltip)
    // doesn't trip it.
    const extractionCell = screen.getByText("Extraction").closest("div")!;
    expect(within(extractionCell).queryByText("Pending")).toBeNull();

    // Completed is terminal — refetchInterval returns false, no more
    // polls. Snapshot the post-completed count then advance another 10s
    // and confirm it doesn't change.
    const afterCompleted = calls;
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toBe(afterCompleted);
  });

  it("Processing → Failed: UI advances to the failed badge + processingError card", async () => {
    let calls = 0;
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        return jsonOk(
          calls === 1
            ? makeDocumentDetail({
                extractionStatus: "Processing",
                complianceStatus: "NonCompliant",
              })
            : makeDocumentDetail({
                extractionStatus: "Failed",
                complianceStatus: "NonCompliant",
                processingError: "OCR engine timed out after 30 seconds.",
              }),
        );
      }),
    );

    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_x_02" },
    });

    await waitFor(() =>
      expect(screen.getByText("Processing")).toBeInTheDocument(),
    );
    // The error card should NOT be visible while Processing.
    expect(screen.queryByText(/extraction error/i)).toBeNull();
    expect(calls).toBeGreaterThanOrEqual(1);

    const beforeAdvance = calls;
    await vi.advanceTimersByTimeAsync(3000);

    // Failed badge appears AND the processingError card pops in.
    await waitFor(() =>
      expect(screen.getByText("Failed")).toBeInTheDocument(),
    );
    expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);
    expect(screen.getByText(/extraction error/i)).toBeInTheDocument();
    expect(
      screen.getByText(/OCR engine timed out after 30 seconds/i),
    ).toBeInTheDocument();

    // Failed is terminal — no further polls.
    const afterFailed = calls;
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toBe(afterFailed);
  });
});
