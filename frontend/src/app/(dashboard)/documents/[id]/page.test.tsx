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
import { fireEvent, screen, waitFor } from "@testing-library/react";
import DocumentDetailPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  makeDocumentDetail,
  sequencedJsonOk,
  toastSuccess,
  toastError,
} from "@/test";

// sonner mock is provided by the harness (vitest.setup.ts +
// src/test/sonner.ts). The toastSuccess / toastError spy references
// imported above pin the reextract + saveFields mutation toast paths
// (#122) — `resetSonner()` runs in the harness `afterEach` so call
// counts never leak between tests.

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
