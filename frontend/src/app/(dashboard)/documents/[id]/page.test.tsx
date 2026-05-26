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
import { screen, waitFor } from "@testing-library/react";
import DocumentDetailPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
} from "@/test";

const { toastSuccess, toastError } = vi.hoisted(() => ({
  toastSuccess: vi.fn(),
  toastError: vi.fn(),
}));
vi.mock("sonner", () => ({
  toast: { success: toastSuccess, error: toastError },
  Toaster: () => null,
}));

// Minimal fixture builder for the detail endpoint — only the fields the
// page actually reads. The page accepts a `DocDetail` shape defined
// inline at [id]/page.tsx; we keep this fixture local so a future page
// refactor doesn't ripple to harness fixtures.
function makeDetail(
  overrides: Partial<{
    id: string;
    originalFileName: string;
    documentType: string;
    extractionStatus: string;
    extractionConfidence: number | null;
    complianceStatus: string;
    effectiveDate: string | null;
    expirationDate: string | null;
    daysUntilExpiry: number | null;
    isManuallyVerified: boolean;
    blobStorageUrl: string | null;
    processingError: string | null;
    fields: Array<{
      id: string;
      fieldName: string;
      fieldValue: string | null;
      fieldType: string | null;
      confidence: number;
      isManuallyEdited: boolean;
      originalValue: string | null;
    }>;
  }> = {},
) {
  return {
    id: "d_x_01",
    originalFileName: "coi.pdf",
    documentType: "COI",
    documentSubType: null,
    vendorName: null,
    extractionStatus: "Pending",
    extractionConfidence: null,
    complianceStatus: "Pending",
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
    ...overrides,
  };
}

beforeEach(() => {
  toastSuccess.mockClear();
  toastError.mockClear();
});

describe("DocumentDetailPage — basic states (#36)", () => {
  it("isLoading: renders the loading copy", () => {
    // Hold the response so the test observes the loading branch.
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.get(url("/api/documents/:id"), async () => {
        await settled;
        return jsonOk(makeDetail());
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
          makeDetail({
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
    expect(
      (document.querySelector(
        'input[value="POL-12345"]',
      ) as HTMLInputElement) ?? null,
    ).not.toBeNull();
  });
});

describe("DocumentDetailPage — extraction-error card (#36 AC #3)", () => {
  it("renders processingError content when the failed-path field is set", async () => {
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDetail({
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
        jsonOk(makeDetail({ extractionStatus: "Completed" })),
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
            ? makeDetail({
                extractionStatus: "Pending",
                complianceStatus: "NonCompliant",
              })
            : makeDetail({
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
    expect(calls).toBe(1);

    // Tick past the 3-second poll window. The page refetches, sees
    // Completed, and the badge swaps.
    await vi.advanceTimersByTimeAsync(3000);

    await waitFor(() =>
      expect(screen.getByText("Completed")).toBeInTheDocument(),
    );
    expect(screen.queryByText("Pending")).toBeNull();
    expect(calls).toBe(2);

    // Completed is terminal — refetchInterval returns false, no more
    // polls. Advance another 10s and confirm the call count stays put.
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toBe(2);
  });

  it("Processing → Failed: UI advances to the failed badge + processingError card", async () => {
    let calls = 0;
    server.use(
      http.get(url("/api/documents/:id"), () => {
        calls++;
        return jsonOk(
          calls === 1
            ? makeDetail({
                extractionStatus: "Processing",
                complianceStatus: "NonCompliant",
              })
            : makeDetail({
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

    await vi.advanceTimersByTimeAsync(3000);

    // Failed badge appears AND the processingError card pops in.
    await waitFor(() =>
      expect(screen.getByText("Failed")).toBeInTheDocument(),
    );
    expect(screen.getByText(/extraction error/i)).toBeInTheDocument();
    expect(
      screen.getByText(/OCR engine timed out after 30 seconds/i),
    ).toBeInTheDocument();

    // Failed is terminal — no further polls.
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toBe(2);
  });
});
