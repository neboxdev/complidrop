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
import { afterEach, describe, it, expect, vi } from "vitest";
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
  sequencedJsonOk,
  sequencedResponses,
} from "@/test";

afterEach(() => {
  // Belt-and-suspenders — any test that flipped fake timers (the new
  // page-level polling test) and threw before its `finally` block must
  // not leak fake timers into the next test.
  vi.useRealTimers();
});

// sonner mock + toastSuccess/toastError spies are provided by the
// harness (see vitest.setup.ts + src/test/sonner.ts). The harness's
// afterEach resets all toast spies between tests — no per-file
// beforeEach mockClear needed (#74).

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

  it("error: a 5xx surfaces an error card with role=alert, the server message, and a Retry affordance, NOT the empty fallback (#80)", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonError("server.error", "DB down.", { status: 500 }),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    // Co-pin role=alert + headline text on the SAME element so a
    // regression that drops either property fails loudly (the prior
    // assertion was via getByText only and would have silently
    // tolerated removing role=alert — degrading a11y without a
    // failing test). Wrap in waitFor so a future React work-loop
    // change that splits loading-gone from error-card-shown doesn't
    // race the assertion. (#80 followup review)
    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert).toHaveTextContent(/couldn't load documents/i);
    expect(alert).toHaveTextContent("DB down.");

    expect(
      screen.getByRole("heading", { name: /^documents$/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /retry/i }),
    ).toBeInTheDocument();
    // Negative: the empty-state copy must NOT appear on error.
    expect(screen.queryByText(/no documents yet/i)).toBeNull();
  });

  it("error: non-JSON 5xx (e.g. a proxy HTML page) renders the jargon-free fallback, NOT a raw status-text leak (#80 + #77)", async () => {
    // Pins the api.ts ↔ page integration for the generic-fallback
    // branch the new ternary expects. Previously only api.ts's unit
    // test exercised the GENERIC_FALLBACK_MESSAGE conversion; this
    // test wires the network-failure / non-JSON-body case end-to-end
    // through the page render so a future api.ts regression that
    // dropped the fallback OR a page regression that re-introduced
    // raw statusText would fail at the list-page layer too.
    server.use(
      http.get(url("/api/documents"), () =>
        Promise.resolve(
          new Response("<html>502 Bad Gateway</html>", {
            status: 502,
            statusText: "Bad Gateway",
            headers: { "Content-Type": "text/html" },
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert).toHaveTextContent(/couldn't load documents/i);
    expect(alert).toHaveTextContent("Something went wrong. Try again.");
    // The raw statusText MUST NOT leak through under any path.
    expect(alert).not.toHaveTextContent(/bad gateway/i);
    expect(alert).not.toHaveTextContent(/<html>/i);
  });

  it("retry-on-5xx: clicking Retry fires a second fetch; a subsequent 200 swaps the error card for the populated list (#80)", async () => {
    // Pins the affordance #80 added: the Retry button must actually
    // re-issue the documents fetch. Without this test, swapping
    // `onClick={() => docs.refetch()}` for a no-op or a wrong query
    // would slip past every other test in this file.
    //
    // Migrated to `sequencedResponses` in #124 — the helper covers
    // exactly this mixed 500→200 shape the success-only
    // `sequencedJsonOk` couldn't express. The external `calls`
    // counter stays because the helper's internal counter is
    // intentionally not exposed (see polling.ts module docstring).
    let calls = 0;
    const seq = sequencedResponses(
      () => jsonError("server.error", "DB blip.", { status: 500 }),
      () =>
        jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }],
            total: 1,
          }),
        ),
    );
    server.use(
      http.get(url("/api/documents"), () => {
        calls++;
        return seq();
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
    // Full state swap, not just the headline gone: error card body
    // text AND the Retry button must also disappear, otherwise a
    // future refactor that leaves the error <td> stale alongside the
    // new rows would pass the previous loose assertion. (#80 followup)
    expect(screen.queryByText(/couldn't load documents/i)).toBeNull();
    expect(screen.queryByText("DB blip.")).toBeNull();
    expect(screen.queryByRole("button", { name: /retry/i })).toBeNull();
  });

  it("populated-list polling-failure: keeps the rendered rows visible, surfaces the stale-data banner, and stops polling (#80 + #97)", async () => {
    // The regression #97 was filed against: a poll failure on a
    // populated list flipped `isError=true` and the new error branch
    // hid the rows. Fix: gate the error branch on `items.length === 0`
    // so the cached rows survive a transient poll failure, AND render
    // the StaleDataBanner so the user knows what they're seeing may
    // be stale. Pins the full contract so a future refactor that
    // re-removes the gate OR drops the banner fails here loudly.
    // Also verifies `refetchInterval` stops polling on error so the
    // backend isn't hammered during the outage.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let calls = 0;
      server.use(
        http.get(url("/api/documents"), () => {
          calls++;
          // First call: populated list with a Pending row (so polling
          // would normally fire). Subsequent calls: 5xx.
          if (calls === 1) {
            return jsonOk(
              makeDocumentsResponse({
                items: [
                  {
                    ...documentsAllStatuses[0], // Pending row
                    vendorName: "Acme Sub",
                  },
                ],
                total: 1,
              }),
            );
          }
          return jsonError("server.error", "Brown-out", { status: 502 });
        }),
      );

      renderWithProviders(<DocumentsPage />, { auth: authedMe });

      // First render: populated row visible.
      await waitFor(() =>
        expect(screen.getByText("coi-pending.pdf")).toBeInTheDocument(),
      );
      expect(calls).toBe(1);

      // Tick past the 5s refetchInterval; the next fetch errors.
      await vi.advanceTimersByTimeAsync(5000);
      await waitFor(() => expect(calls).toBeGreaterThanOrEqual(2));

      // The stale-data banner appears with the server's message — the
      // discreet "couldn't refresh" signal the user needs to know
      // their data may be stale. Co-pin role=status + headline text on
      // the SAME element so a regression that drops either fails. The
      // banner is role=status (NOT role=alert) because the cached
      // data is still readable — assistive tech announces it politely
      // rather than interrupting the user.
      const banner = await waitFor(() => screen.getByRole("status"));
      expect(banner).toHaveAttribute("aria-live", "polite");
      expect(banner).toHaveTextContent(/couldn't refresh documents/i);
      expect(banner).toHaveTextContent(/brown-out/i);

      // Critical: the populated row STAYS visible, and the full-page
      // error card does NOT replace it.
      expect(screen.getByText("coi-pending.pdf")).toBeInTheDocument();
      expect(screen.queryByText(/couldn't load documents/i)).toBeNull();
      expect(screen.queryByText(/no documents yet/i)).toBeNull();
      // role=alert is reserved for the full-page error card — the
      // populated+failed path uses role=status. Pin the absence so a
      // regression that re-introduced role=alert (and the interruptive
      // a11y announcement) is caught here.
      expect(screen.queryByRole("alert")).toBeNull();

      // Polling short-circuits on error — advancing 60s of fake time
      // (12 polling windows at the 5s interval, plus enough headroom
      // for a hypothetical back-off-with-cap implementation up to
      // ~30s) must NOT trigger any more fetches. Tighter than 15s so a
      // future variant that backs off rather than strict short-circuits
      // would still be caught by this test if its retry interval ever
      // fired within a minute. (#97 review — test-quality reviewer)
      const afterFirstError = calls;
      await vi.advanceTimersByTimeAsync(60_000);
      expect(calls).toBe(afterFirstError);
    } finally {
      vi.useRealTimers();
    }
  });

  it("stale-banner dismisses after a successful Try-again refetch (#97)", async () => {
    // Pins the recovery path: when the user clicks Try again on the
    // stale-data banner and the next response is a 200, the banner
    // must disappear AND the list must show the updated rows. A
    // regression that left the banner stuck visible (e.g. by reading
    // a stale isError flag) would surface here. Mirrors the
    // retry-on-5xx test for the full-page error card, but for the
    // populated+stale path #97 introduced.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let calls = 0;
      server.use(
        http.get(url("/api/documents"), () => {
          calls++;
          if (calls === 1) {
            return jsonOk(
              makeDocumentsResponse({
                items: [
                  {
                    ...documentsAllStatuses[0], // Pending row
                    vendorName: "Acme Sub",
                  },
                ],
                total: 1,
              }),
            );
          }
          if (calls === 2) {
            // Second call (the polling refetch): 5xx → banner appears.
            return jsonError("server.error", "Brown-out", { status: 502 });
          }
          // Third call (the Try-again click): 200 with the updated
          // row → banner dismisses, list reflects the new state.
          return jsonOk(
            makeDocumentsResponse({
              items: [
                {
                  ...documentsAllStatuses[2], // Completed row
                  vendorName: "Acme Sub",
                },
              ],
              total: 1,
            }),
          );
        }),
      );

      renderWithProviders(<DocumentsPage />, { auth: authedMe });

      // First render: populated.
      await waitFor(() =>
        expect(screen.getByText("coi-pending.pdf")).toBeInTheDocument(),
      );

      // Tick past 5s refetch interval → poll errors → banner appears.
      await vi.advanceTimersByTimeAsync(5000);
      const banner = await waitFor(() => screen.getByRole("status"));
      expect(banner).toHaveTextContent(/couldn't refresh documents/i);

      // Click Try again — the banner's Retry affordance.
      fireEvent.click(screen.getByRole("button", { name: /try again/i }));

      // The banner disappears once isError flips back to false on the
      // 200 response, AND the list reflects the new payload — co-pin
      // positive (new row present) AND negative (old row gone) so a
      // regression that spread the new items alongside the old (e.g.
      // a future refactor to an append-style cache update) would
      // surface here. (#97 review — test-quality reviewer)
      await waitFor(() =>
        expect(screen.getByText("coi-completed.pdf")).toBeInTheDocument(),
      );
      expect(screen.queryByText("coi-pending.pdf")).toBeNull();
      expect(screen.queryByRole("status")).toBeNull();
      expect(screen.queryByText(/couldn't refresh documents/i)).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it("Try-again that fails too: banner stays visible, button re-enables once isFetching settles (#97)", async () => {
    // Pins the negative-recovery path the existing tests don't cover:
    // a regression that incorrectly cleared isError on click (or that
    // left the disabled state pinned after a failed retry) would slip
    // past every previous assertion. The banner must STAY visible
    // after the second failure, and the Try-again button must re-enable
    // so the user can keep retrying. (#97 review — correctness +
    // test-quality reviewers)
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let calls = 0;
      server.use(
        http.get(url("/api/documents"), () => {
          calls++;
          if (calls === 1) {
            return jsonOk(
              makeDocumentsResponse({
                items: [
                  {
                    ...documentsAllStatuses[0], // Pending row
                    vendorName: "Acme Sub",
                  },
                ],
                total: 1,
              }),
            );
          }
          // Every subsequent call (polling + Try-again click) fails.
          return jsonError("server.error", "Still down", { status: 502 });
        }),
      );

      renderWithProviders(<DocumentsPage />, { auth: authedMe });

      await waitFor(() =>
        expect(screen.getByText("coi-pending.pdf")).toBeInTheDocument(),
      );

      // First polling refetch fails → banner appears.
      await vi.advanceTimersByTimeAsync(5000);
      await waitFor(() =>
        expect(screen.getByText(/couldn't refresh documents/i)).toBeInTheDocument(),
      );

      // Click Try again → second failure → banner stays.
      fireEvent.click(screen.getByRole("button", { name: /try again/i }));
      // Wait for the in-flight retry to settle (isFetching → false).
      await waitFor(() => expect(calls).toBeGreaterThanOrEqual(3));

      // Banner remains visible with the latest server message.
      const banner = screen.getByRole("status");
      expect(banner).toHaveTextContent(/couldn't refresh documents/i);
      expect(banner).toHaveTextContent(/still down/i);
      // Stale row stays rendered — full-page error card MUST NOT
      // replace it.
      expect(screen.getByText("coi-pending.pdf")).toBeInTheDocument();
      expect(screen.queryByText(/couldn't load documents/i)).toBeNull();

      // The Try-again button is re-enabled — user can keep retrying.
      // A regression that pinned the disabled state across failures
      // would lock the user out of the only recovery affordance.
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: /try again/i }),
        ).not.toBeDisabled(),
      );

      // Pin that the short-circuit-on-error contract stays sticky
      // across the failed manual retry: after the second 502,
      // polling must NOT resume for the original 5s interval. A
      // regression that reset the short-circuit on Try-again
      // (e.g. by inadvertently flipping isError → false → true
      // through the refetch lifecycle) would re-arm the 5s interval
      // and hammer the backend during the outage. (#97 second-pass
      // review — test-quality reviewer)
      const afterRetry = calls;
      await vi.advanceTimersByTimeAsync(30_000);
      expect(calls).toBe(afterRetry);
    } finally {
      vi.useRealTimers();
    }
  });

  it("polling resumes after recovery when items still have Pending/Processing rows (#97)", async () => {
    // Pins that the short-circuit-on-error contract is NOT sticky —
    // once isError flips back to success, refetchInterval re-evaluates
    // the predicate and resumes polling at 5s for any remaining
    // Pending/Processing rows. A regression that hard-pinned polling
    // off after the first error (e.g. by reading a sticky local flag
    // instead of `q.state.status`) would silently leave the dashboard
    // unable to update Pending → Completed transitions after any
    // transient brown-out. (#97 review — correctness reviewer)
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      let calls = 0;
      const pending = makeDocumentsResponse({
        items: [
          {
            ...documentsAllStatuses[0], // Pending row
            vendorName: "Acme Sub",
          },
        ],
        total: 1,
      });
      const completed = makeDocumentsResponse({
        items: [
          {
            ...documentsAllStatuses[2], // Completed row
            vendorName: "Acme Sub",
          },
        ],
        total: 1,
      });
      server.use(
        http.get(url("/api/documents"), () => {
          calls++;
          // Call 1: Pending. Call 2: 5xx (banner appears, polling
          // stops). Call 3: Try-again succeeds with STILL-Pending
          // payload (the document hasn't finished extracting yet).
          // After call 3, polling MUST resume — confirmed by call 4
          // landing within a 5s tick after the Pending recovery.
          if (calls === 1 || calls === 3) return jsonOk(pending);
          if (calls === 2) {
            return jsonError("server.error", "Brown-out", { status: 502 });
          }
          // Call 4: terminal Completed → polling stops naturally.
          return jsonOk(completed);
        }),
      );

      renderWithProviders(<DocumentsPage />, { auth: authedMe });

      await waitFor(() =>
        expect(screen.getByText("coi-pending.pdf")).toBeInTheDocument(),
      );

      // Tick into the poll-failure window.
      await vi.advanceTimersByTimeAsync(5000);
      await waitFor(() =>
        expect(screen.getByText(/couldn't refresh documents/i)).toBeInTheDocument(),
      );

      // Snapshot the call count BEFORE the click so the delta assertion
      // covers both bumps (one from the Try-again click → call 3, one
      // from the resumed polling → call 4). Snapshotting AFTER the
      // banner-dismiss waitFor would leave a tiny race window: under
      // `shouldAdvanceTime: true`, virtual time advances with real
      // wall-clock, so a slow CI runner could in principle fire the
      // resumed-polling refetch before the snapshot. Snapshot-then-act
      // is the canonical pattern across this file. (#97 second-pass
      // review — test-quality reviewer)
      const beforeRecovery = calls;

      // Click Try again → 200 with still-Pending → banner dismisses +
      // polling resumes.
      fireEvent.click(screen.getByRole("button", { name: /try again/i }));
      await waitFor(() => expect(screen.queryByRole("status")).toBeNull());

      // The polling-resumed assertion: after the success refetch,
      // refetchInterval re-evaluates against the Pending row and
      // schedules the next 5s tick. Advance past it — a new fetch
      // must fire. A regression that left polling off would leave
      // `calls` unchanged. Delta is `+2`: one from the Try-again
      // click (call 3), one from the resumed polling (call 4).
      await vi.advanceTimersByTimeAsync(5000);
      await waitFor(() =>
        expect(calls).toBeGreaterThanOrEqual(beforeRecovery + 2),
      );

      // The 4th call lands the Completed payload → list shows the
      // new row, banner stays absent, polling stops naturally.
      await waitFor(() =>
        expect(screen.getByText("coi-completed.pdf")).toBeInTheDocument(),
      );
      expect(screen.queryByRole("status")).toBeNull();
    } finally {
      vi.useRealTimers();
    }
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
      const seq = sequencedJsonOk(
        makeDocumentsResponse({
          items: [
            {
              ...documentsAllStatuses[1], // Processing row
              complianceStatus: "NonCompliant",
              // Override the fixture's "Processing Vendor" cell so
              // `getByText(/^Processing$/)` matches only the extraction
              // badge, not the vendor-name cell.
              vendorName: "Acme Sub",
            },
          ],
          total: 1,
        }),
        makeDocumentsResponse({
          items: [
            {
              ...documentsAllStatuses[2], // Completed row
              // Same vendor-name override so the assertion matches the
              // extraction badge unambiguously.
              vendorName: "Acme Sub",
            },
          ],
          total: 1,
        }),
      );
      server.use(
        http.get(url("/api/documents"), () => {
          calls++;
          return seq();
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

  it("mobile reflow: the list is a stacked-table with labeled cells and a NAMED delete control (#181)", async () => {
    // Pins the responsive-table reflow contract: the table opts into the
    // `.stacked-table` card reflow (so columns aren't clipped / don't need a
    // horizontal swipe below md) AND the icon-only delete button — which was
    // previously nameless — now exposes an accessible name.
    //
    // NOTE: JSDOM applies no stylesheet, so this asserts the CONTRACT HOOKS
    // (the `.stacked-table` opt-in class + the per-cell `data-label`s the CSS
    // `::before` reads) — not the rendered ≤md layout. Deleting the
    // `@media (max-width: 47.999rem)` block in globals.css would still pass
    // here; the actual 390px no-clip behavior is a visual guarantee tracked in
    // the QA plan (docs/qa) / e2e, not a unit-testable property.
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(
          makeDocumentsResponse({
            items: [{ ...documentsAllStatuses[2] }], // coi-completed.pdf
            total: 1,
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByText("coi-completed.pdf")).toBeInTheDocument(),
    );

    const table = document.querySelector("table.stacked-table");
    expect(table).not.toBeNull();
    // The compliance + expiry cells carry mobile card labels (rendered via a
    // CSS ::before from data-label, so they never collide with text queries).
    expect(table?.querySelector('td[data-label="Compliance"]')).not.toBeNull();
    expect(table?.querySelector('td[data-label="Expires"]')).not.toBeNull();
    // The destructive control is reachable by its accessible name.
    expect(
      screen.getByRole("button", { name: /remove coi-completed\.pdf/i }),
    ).toBeInTheDocument();
  });
});
