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
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import DocumentsPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  documentsAllStatuses,
  makeDocument,
  makeDocumentsResponse,
  sequencedJsonOk,
  sequencedResponses,
  dropFilesIn,
  makeFile,
  toastError,
  toastInfo,
  navState,
  setNavigationState,
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

describe("DocumentsPage — URL-addressable filters (#317 FP-041)", () => {
  it("seeds the status filter from ?status= and sends it to the server", async () => {
    let requestedUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        requestedUrl = request.url;
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );
    renderWithProviders(<DocumentsPage />, { auth: authedMe, searchParams: { status: "NonCompliant" } });
    await waitFor(() => expect(requestedUrl).toContain("status=NonCompliant"));
  });

  it("honors a ?vendor= deep link by filtering the list to that vendor (FP-071 pairing)", async () => {
    let requestedUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        requestedUrl = request.url;
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );
    renderWithProviders(<DocumentsPage />, { auth: authedMe, searchParams: { vendor: "v_123" } });
    await waitFor(() => expect(requestedUrl).toContain("vendorId=v_123"));
  });

  it("writes a filter change into the URL so the view is shareable", async () => {
    const replaceSpy = vi.fn();
    server.use(http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))));
    renderWithProviders(<DocumentsPage />, { auth: authedMe, router: { replace: replaceSpy } });
    await waitFor(() => expect(screen.getByText(/no documents yet/i)).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText(/filter by compliance status/i), {
      target: { value: "Expired" },
    });

    await waitFor(() => expect(replaceSpy).toHaveBeenCalled());
    expect(replaceSpy.mock.calls.some(([href]) => String(href).includes("status=Expired"))).toBe(true);
  });

  it("does not navigate on mount — a deep-linked filter is already in the URL (#370)", async () => {
    // The old mirror effect echoed every filter back on mount, so arriving at
    // ?status=Expired immediately replaced to the identical URL. That echo was
    // the loop: its input (searchParams) lagged its own output by a commit.
    // Deriving from the URL means mount is a pure read.
    const replaceSpy = vi.fn();
    server.use(http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))));
    renderWithProviders(<DocumentsPage />, {
      auth: authedMe,
      searchParams: { status: "Expired" },
      router: { replace: replaceSpy },
    });
    await waitFor(() => expect(screen.getByText(/no documents match your filters/i)).toBeInTheDocument());
    // Give the 300ms search debounce room to misfire if it were going to.
    await new Promise((r) => setTimeout(r, 400));
    expect(replaceSpy).not.toHaveBeenCalled();
  });
});

describe("DocumentsPage — filter<->URL sync is two-way (#370)", () => {
  it("scenario A: Clear from ?vendor=&status= settles on a bare URL — no resurrected vendor filter", async () => {
    // Against the pre-fix code the mirror effect re-ran after the handler's
    // replace, reading the PRE-navigation searchParams snapshot, and dispatched
    // "/documents?vendor=v1" — so the vendor filter survived the click meant to
    // clear it and the list stayed filtered.
    const requestedUrls: string[] = [];
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        requestedUrls.push(request.url);
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );
    renderWithProviders(<DocumentsPage />, {
      auth: authedMe,
      searchParams: { vendor: "v1", status: "Expired" },
      pathname: "/documents",
    });
    await waitFor(() => expect(requestedUrls.some((u) => u.includes("vendorId=v1"))).toBe(true));

    // Everything from here on is what the click is answerable for.
    const requestsBeforeClick = requestedUrls.length;
    navState.router.replace.mockClear();

    fireEvent.click(await screen.findByRole("button", { name: /^clear$/i }));
    await new Promise((r) => setTimeout(r, 400));

    // The load-bearing assertion. Asserting only the FINAL url would not
    // discriminate: the pre-fix page oscillates (bare -> ?vendor=v1 -> bare …)
    // as each deferred navigation re-triggers the mirror effect, and it happens
    // to settle empty. The defect is that the resurrection is DISPATCHED at all
    // — the user sees the filter come back and the list refetch behind it.
    const hrefsAfterClick = navState.router.replace.mock.calls.map(([href]) => String(href));
    expect(hrefsAfterClick.filter((href) => href.includes("vendor="))).toEqual([]);

    // No request issued after the click may re-apply the cleared filters.
    const requestsAfterClick = requestedUrls.slice(requestsBeforeClick);
    for (const requested of requestsAfterClick) {
      const sp = new URL(requested).searchParams;
      expect(sp.get("vendorId")).toBeNull();
      expect(sp.get("status")).toBeNull();
    }

    // And the URL the user could copy is bare.
    expect(navState.searchParams.toString()).toBe("");
    expect(screen.queryByRole("button", { name: /^clear$/i })).toBeNull();
  });

  it("scenario B: a same-route nav to a bare /documents drops the filtered view (#370)", async () => {
    // Clicking "Documents" in the sidebar while filtered does not remount the
    // page, so the old useState initializers never re-ran: the list stayed
    // filtered under a bare URL, and the next filter change wrote that stale
    // residue back into the query string.
    const requestedUrls: string[] = [];
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        requestedUrls.push(request.url);
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );
    renderWithProviders(<DocumentsPage />, {
      auth: authedMe,
      searchParams: { status: "Expired" },
      pathname: "/documents",
    });
    await waitFor(() => expect(requestedUrls.some((u) => u.includes("status=Expired"))).toBe(true));
    expect(screen.getByLabelText(/filter by compliance status/i)).toHaveValue("Expired");

    // The sidebar link: same route, bare URL, no remount.
    setNavigationState({ searchParams: {}, pathname: "/documents" });

    // The control follows the URL…
    await waitFor(() =>
      expect(screen.getByLabelText(/filter by compliance status/i)).toHaveValue(""),
    );
    // …and so does the list.
    await waitFor(() => {
      const last = requestedUrls[requestedUrls.length - 1];
      expect(new URL(last).searchParams.get("status")).toBeNull();
    });
    expect(screen.queryByRole("button", { name: /^clear$/i })).toBeNull();
  });

  it("two filter changes inside one navigation window both survive (#370)", async () => {
    // Deriving from the URL introduces a clobber risk the old four-state-cells
    // version did not have: `router.replace` commits in a transition, so the
    // second change reads a query string that does not yet contain the first.
    // Composing it on that stale string silently drops the first filter.
    server.use(
      http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))),
    );
    renderWithProviders(<DocumentsPage />, { auth: authedMe, pathname: "/documents" });
    await waitFor(() => expect(screen.getByText(/no documents yet/i)).toBeInTheDocument());

    // No await between them — the deferred commit cannot have landed.
    fireEvent.change(screen.getByLabelText(/filter by compliance status/i), {
      target: { value: "Expired" },
    });
    fireEvent.change(screen.getByLabelText(/filter by document type/i), {
      target: { value: "permit" },
    });

    await waitFor(() => expect(navState.searchParams.get("type")).toBe("permit"));
    expect(navState.searchParams.get("status")).toBe("Expired");
    expect(screen.getByLabelText(/filter by compliance status/i)).toHaveValue("Expired");
    expect(screen.getByLabelText(/filter by document type/i)).toHaveValue("permit");
  });

  it("a filter touched before Clear lands does not resurrect the cleared filters (#370)", async () => {
    // Same window, opposite direction: Clear is also an in-flight navigation,
    // so a dropdown touched immediately after must compose on the CLEARED url.
    server.use(
      http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))),
    );
    renderWithProviders(<DocumentsPage />, {
      auth: authedMe,
      searchParams: { vendor: "v1", status: "Expired" },
      pathname: "/documents",
    });
    await waitFor(() => expect(screen.getByText(/no documents match your filters/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: /^clear$/i }));
    fireEvent.change(screen.getByLabelText(/filter by document type/i), {
      target: { value: "permit" },
    });

    await waitFor(() => expect(navState.searchParams.get("type")).toBe("permit"));
    expect(navState.searchParams.get("vendor")).toBeNull();
    expect(navState.searchParams.get("status")).toBeNull();
  });

  it("a URL-driven filter change resets pagination to page 1 (#370)", async () => {
    // The page-1 reset used to live in each dropdown's onChange, so a filter
    // that arrived from the URL — Back, a deep link, the sidebar — left the
    // user stranded on a page the new result set may not have.
    const requestedUrls: string[] = [];
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        requestedUrls.push(request.url);
        const p = new URL(request.url).searchParams.get("page") ?? "1";
        return jsonOk(
          makeDocumentsResponse({
            items: [makeDocument({ id: `d_${p}`, originalFileName: `row-p${p}.pdf` })],
            total: 60,
            page: Number(p),
            pageSize: 25,
          }),
        );
      }),
    );
    renderWithProviders(<DocumentsPage />, { auth: authedMe, pathname: "/documents" });
    await waitFor(() => expect(screen.getByText("row-p1.pdf")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    await waitFor(() =>
      expect(
        new URL(requestedUrls[requestedUrls.length - 1]).searchParams.get("page"),
      ).toBe("2"),
    );

    // A filter arriving from OUTSIDE the dropdowns (Back / deep link).
    setNavigationState({ searchParams: { status: "Expired" }, pathname: "/documents" });

    await waitFor(() => {
      const sp = new URL(requestedUrls[requestedUrls.length - 1]).searchParams;
      expect(sp.get("status")).toBe("Expired");
      expect(sp.get("page")).toBe("1");
    });
  });

  it("the search box round-trips through the URL and re-seeds when the URL changes", async () => {
    server.use(http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))));
    renderWithProviders(<DocumentsPage />, { auth: authedMe, pathname: "/documents" });
    await waitFor(() => expect(screen.getByText(/no documents yet/i)).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText(/search documents/i), { target: { value: "acme" } });
    // The debounced value is written THROUGH to the URL, not into a second
    // state cell — that's what makes a searched view shareable.
    await waitFor(() => expect(navState.searchParams.get("search")).toBe("acme"));
    expect(screen.getByLabelText(/search documents/i)).toHaveValue("acme");

    // An external URL change re-seeds the draft rather than leaving stale text
    // in a box that no longer describes the list.
    setNavigationState({ searchParams: {}, pathname: "/documents" });
    await waitFor(() => expect(screen.getByLabelText(/search documents/i)).toHaveValue(""));
  });

  it("Clear also drops search text typed inside the debounce window", async () => {
    // The draft is the one piece of state the URL cannot clear: within 300ms of
    // typing, `search` is still "" so the URL-sync sees no change. Without an
    // explicit reset the box keeps its text and the pending timer writes it
    // straight back into the URL the click just cleared.
    server.use(http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))));
    renderWithProviders(<DocumentsPage />, {
      auth: authedMe,
      searchParams: { status: "Expired" },
      pathname: "/documents",
    });
    await waitFor(() => expect(screen.getByText(/no documents match your filters/i)).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText(/search documents/i), { target: { value: "acme" } });
    fireEvent.click(screen.getByRole("button", { name: /^clear$/i }));

    expect(screen.getByLabelText(/search documents/i)).toHaveValue("");
    await new Promise((r) => setTimeout(r, 400));
    expect(navState.searchParams.toString()).toBe("");
  });

  it("Clear drops a ?vendor= deep link from the URL (regression: Clear was a dead control)", async () => {
    const replaceSpy = vi.fn();
    server.use(http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))));
    renderWithProviders(<DocumentsPage />, {
      auth: authedMe,
      searchParams: { vendor: "v1" },
      router: { replace: replaceSpy },
    });
    // The Clear button is present because the vendor filter is active.
    fireEvent.click(await screen.findByRole("button", { name: /^clear$/i }));
    // The fix: Clear navigates to the bare path so the read-only vendor param goes too.
    expect(replaceSpy).toHaveBeenCalledWith("/documents", { scroll: false });
  });
});

describe("DocumentsPage — upload UX (#317 FP-054/FP-055)", () => {
  it("FP-055: the staging copy is count-aware for a multi-file batch", async () => {
    server.use(http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))));
    const { container } = renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/no documents yet/i)).toBeInTheDocument());

    dropFilesIn(container, [makeFile("a.pdf"), makeFile("b.pdf")]);
    expect(await screen.findByText(/these 2 files are for/i)).toBeInTheDocument();
  });

  it("FP-054: warns that an active filter may be hiding a just-uploaded doc", async () => {
    server.use(
      http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))),
      http.get(url("/api/vendors"), () => jsonOk([{ id: "v1", name: "Acme Catering" }])),
      http.post(url("/api/documents/upload"), () =>
        jsonOk({ id: "d1", originalFileName: "a.pdf", extractionStatus: "Pending" }),
      ),
    );
    const { container } = renderWithProviders(<DocumentsPage />, {
      auth: authedMe,
      searchParams: { status: "Expired" },
    });
    await waitFor(() => expect(screen.getByText(/no documents match your filters/i)).toBeInTheDocument());

    dropFilesIn(container, [makeFile("a.pdf")]);
    fireEvent.click(await screen.findByRole("option", { name: "Acme Catering" }));
    await waitFor(() => expect(screen.getByRole("button", { name: /upload 1 file/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole("button", { name: /upload 1 file/i }));

    await waitFor(() =>
      expect(toastInfo).toHaveBeenCalledWith(expect.stringMatching(/active filter may be hiding/i)),
    );
  });
});

describe("DocumentsPage — sample badge (#238)", () => {
  it("badges a sample document and leaves a normal one unbadged", async () => {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(
          makeDocumentsResponse({
            items: [
              makeDocument({ id: "d_sample", originalFileName: "Sample Certificate of Insurance.pdf", isSample: true }),
              makeDocument({ id: "d_real", originalFileName: "real-coi.pdf", isSample: false }),
            ],
            total: 2,
          }),
        ),
      ),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });

    const sampleRow = await screen.findByRole("row", { name: /Sample Certificate of Insurance\.pdf/i });
    expect(within(sampleRow).getByText("Sample")).toBeInTheDocument();

    const realRow = screen.getByRole("row", { name: /real-coi\.pdf/i });
    expect(within(realRow).queryByText("Sample")).toBeNull();
  });
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
        screen.getByText(/no documents yet — drop a coi, license, or permit above/i),
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

    // Each badge renders its HUMANIZED label (#188), keyed off the raw enum:
    // extraction Pending/Processing/Completed/Failed → these phrases; compliance
    // Compliant stays "Compliant". The confidence "· 94%" suffix is gone now.
    expect(screen.getByText("Waiting to read")).toBeInTheDocument(); // Pending extraction
    expect(screen.getByText("Reading…")).toBeInTheDocument(); // Processing
    expect(screen.getByText("Read")).toBeInTheDocument(); // Completed
    expect(screen.getByText("Couldn't read")).toBeInTheDocument(); // Failed
    // "Compliant" is also a status-filter <option> label, so scope this to the
    // table to assert the BADGE specifically (not the filter dropdown option).
    const table = document.querySelector("table") as HTMLElement;
    expect(within(table).getByText("Compliant")).toBeInTheDocument(); // compliance verdict
    // The naked confidence percentage no longer leaks into the list badge.
    expect(screen.queryByText(/· \d+%/)).toBeNull();

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

      // Humanized extraction labels (#188): Processing → "Reading…", Completed → "Read".
      await waitFor(() =>
        expect(screen.getByText("Reading…")).toBeInTheDocument(),
      );

      const beforeAdvance = calls;
      await vi.advanceTimersByTimeAsync(5000);

      await waitFor(() =>
        expect(screen.getByText("Read")).toBeInTheDocument(),
      );
      expect(calls).toBeGreaterThanOrEqual(beforeAdvance + 1);
      expect(screen.queryByText("Reading…")).toBeNull();
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
    // #263: the Expires cell shows the CALENDAR date (fixture says 2026-12-31).
    // Vitest runs pinned to America/New_York, where the old local-shifted
    // rendering produced 12/30/2026.
    expect(table?.querySelector('td[data-label="Expires"]')?.textContent).toContain(
      new Date("2026-12-31T00:00:00Z").toLocaleDateString(undefined, { timeZone: "UTC" }),
    );
    // The destructive control is reachable by its accessible name.
    expect(
      screen.getByRole("button", { name: /remove coi-completed\.pdf/i }),
    ).toBeInTheDocument();
  });
});

describe("DocumentsPage — capture vendor + type at upload (#186)", () => {
  it("drop stages the file behind a vendor/type step; upload is blocked until a vendor is chosen, then sends both", async () => {
    let uploadCalled = false;
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(makeDocumentsResponse({ items: [], total: 0 })),
      ),
      http.get(url("/api/vendors"), () =>
        jsonOk([{ id: "v1", name: "Acme Catering" }]),
      ),
      http.post(url("/api/documents/upload"), () => {
        uploadCalled = true;
        return jsonOk({
          id: "d_new",
          originalFileName: "coi.pdf",
          extractionStatus: "Pending",
        });
      }),
    );

    // The jsdom + undici fetch path doesn't serialize FormData into an
    // inspectable multipart body (request.text() yields "[object FormData]"),
    // so spy on FormData.append to pin the exact wire contract — that the page
    // passes the chosen vendorId and document type into the upload form.
    const appendSpy = vi.spyOn(FormData.prototype, "append");
    try {
      const { container } = renderWithProviders(<DocumentsPage />, { auth: authedMe });
      await waitFor(() =>
        expect(screen.getByText(/no documents yet/i)).toBeInTheDocument(),
      );

      // Dropping does NOT upload immediately — it stages the file.
      dropFilesIn(container, [makeFile("coi.pdf")]);
      await waitFor(() =>
        expect(screen.getByText(/add details before uploading/i)).toBeInTheDocument(),
      );
      expect(uploadCalled).toBe(false);

      // Upload is gated on choosing a vendor.
      expect(screen.getByRole("button", { name: /upload 1 file/i })).toBeDisabled();

      // Pick the vendor AND change the document type away from the default,
      // then upload.
      fireEvent.click(await screen.findByRole("option", { name: "Acme Catering" }));
      fireEvent.change(screen.getByLabelText(/^document type$/i), {
        target: { value: "permit" },
      });
      await waitFor(() =>
        expect(screen.getByRole("button", { name: /upload 1 file/i })).not.toBeDisabled(),
      );
      fireEvent.click(screen.getByRole("button", { name: /upload 1 file/i }));

      // The upload carried the chosen vendor AND the user-selected type — pins
      // the page wiring (DocumentTypeSelect onChange → stagedType → upload form),
      // the exact wiring that stops documents from landing orphaned-and-Pending.
      await waitFor(() => expect(uploadCalled).toBe(true));
      expect(appendSpy).toHaveBeenCalledWith("vendorId", "v1");
      expect(appendSpy).toHaveBeenCalledWith("documentType", "permit");
      expect(appendSpy).toHaveBeenCalledWith("file", expect.any(File));
    } finally {
      appendSpy.mockRestore();
    }
  });

  it("paginates: Next requests page 2 and the pager reflects the new page (#187)", async () => {
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        const p = new URL(request.url).searchParams.get("page") ?? "1";
        const item =
          p === "2"
            ? makeDocument({ id: "d_p2", originalFileName: "page2.pdf" })
            : makeDocument({ id: "d_p1", originalFileName: "page1.pdf" });
        return jsonOk(
          makeDocumentsResponse({ items: [item], total: 30, page: Number(p), pageSize: 25 }),
        );
      }),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText("page1.pdf")).toBeInTheDocument());
    expect(screen.getByText(/page 1 of 2/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /prev/i })).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: /next/i }));

    await waitFor(() => expect(screen.getByText("page2.pdf")).toBeInTheDocument());
    expect(screen.getByText(/page 2 of 2/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /next/i })).toBeDisabled();

    // Prev navigates back to page 1 (the other direction of reachability).
    fireEvent.click(screen.getByRole("button", { name: /prev/i }));
    await waitFor(() => expect(screen.getByText("page1.pdf")).toBeInTheDocument());
    expect(screen.getByText(/page 1 of 2/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /prev/i })).toBeDisabled();
  });

  // keepPreviousData is pinned at the hook level (useDocuments.test.tsx) rather
  // than here: at the page level the self-heal clamp + nulled data interact when
  // the placeholder is removed, so a page-level test would only fail as a slow
  // timeout. The hook test fails fast at the intended assertion. (#187 review)

  it("deleting the last row on a later page steps back to the previous page (#187)", async () => {
    let lastUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        lastUrl = request.url;
        const p = new URL(request.url).searchParams.get("page") ?? "1";
        // 26 docs total → page 2 holds exactly one row.
        return jsonOk(
          makeDocumentsResponse({
            items: [makeDocument({ id: `only-on-p${p}`, originalFileName: `row-p${p}.pdf` })],
            total: 26,
            page: Number(p),
            pageSize: 25,
          }),
        );
      }),
      http.delete(url("/api/documents/:id"), () => new Response(null, { status: 204 })),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText("row-p1.pdf")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    await waitFor(() => expect(new URL(lastUrl).searchParams.get("page")).toBe("2"));

    // Delete the only row on page 2: open the accessible confirm dialog (#189),
    // then confirm → the handler steps back to page 1.
    fireEvent.click(screen.getByRole("button", { name: /remove row-p2\.pdf/i }));
    fireEvent.click(await screen.findByRole("button", { name: /^remove$/i }));
    await waitFor(() => expect(new URL(lastUrl).searchParams.get("page")).toBe("1"));
  });

  it("self-heals when the total shrinks below the current page (concurrent change) (#187)", async () => {
    // Models another session deleting rows while we sit on page 2: the next
    // fetch returns a smaller total, and the page must re-base to a valid page
    // rather than strand the user on an empty out-of-range page.
    let shrunk = false;
    let lastUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        lastUrl = request.url;
        const p = new URL(request.url).searchParams.get("page") ?? "1";
        if (shrunk) {
          // Only one page of data exists now.
          return jsonOk(
            makeDocumentsResponse({
              items: [makeDocument({ id: "d_only", originalFileName: "survivor.pdf" })],
              total: 1,
              page: Number(p),
              pageSize: 25,
            }),
          );
        }
        return jsonOk(
          makeDocumentsResponse({
            items: [makeDocument({ id: `d_${p}`, originalFileName: `row-p${p}.pdf` })],
            total: 30,
            page: Number(p),
            pageSize: 25,
          }),
        );
      }),
    );

    const { queryClient } = renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText("row-p1.pdf")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    await waitFor(() => expect(screen.getByText("row-p2.pdf")).toBeInTheDocument());

    // The data shrinks underneath us; invalidate to force a same-page refetch
    // (page stays 2, so this exercises the render-time clamp, NOT the
    // filter-reset path). The page-2 refetch returns total=1, so the page must
    // re-base to 1 of 1 instead of stranding on an empty page 2.
    shrunk = true;
    queryClient.invalidateQueries({ queryKey: ["documents"] });

    await waitFor(() => expect(screen.getByText("survivor.pdf")).toBeInTheDocument());
    expect(screen.getByText(/page 1 of 1/i)).toBeInTheDocument();
    expect(new URL(lastUrl).searchParams.get("page")).toBe("1");
  });

  it("status filter adds the status query param AND resets pagination to page 1 (#187)", async () => {
    // Navigate to page 2 FIRST so the reset-to-page-1 assertion isn't
    // tautological (the page renders at page 1 by default). (#187 review)
    let lastUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        lastUrl = request.url;
        const p = new URL(request.url).searchParams.get("page") ?? "1";
        return jsonOk(
          makeDocumentsResponse({
            items: [makeDocument({ id: `d_${p}`, originalFileName: `row-p${p}.pdf` })],
            total: 30,
            page: Number(p),
            pageSize: 25,
          }),
        );
      }),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText("row-p1.pdf")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    await waitFor(() => expect(new URL(lastUrl).searchParams.get("page")).toBe("2"));

    fireEvent.change(screen.getByLabelText(/filter by compliance status/i), {
      target: { value: "NonCompliant" },
    });

    await waitFor(() => {
      const sp = new URL(lastUrl).searchParams;
      expect(sp.get("status")).toBe("NonCompliant");
      expect(sp.get("page")).toBe("1");
    });
  });

  it("type filter adds the type query param (#187)", async () => {
    let lastUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        lastUrl = request.url;
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/no documents yet/i)).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText(/filter by document type/i), {
      target: { value: "permit" },
    });

    await waitFor(() => expect(new URL(lastUrl).searchParams.get("type")).toBe("permit"));
  });

  it("search box adds the (debounced) search query param (#187)", async () => {
    let lastUrl = "";
    server.use(
      http.get(url("/api/documents"), ({ request }) => {
        lastUrl = request.url;
        return jsonOk(makeDocumentsResponse({ items: [], total: 0 }));
      }),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/no documents yet/i)).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText(/search documents/i), {
      target: { value: "acme" },
    });

    await waitFor(() => expect(new URL(lastUrl).searchParams.get("search")).toBe("acme"));
  });

  it("filter-aware empty state when filters match nothing (#187)", async () => {
    server.use(
      http.get(url("/api/documents"), () => jsonOk(makeDocumentsResponse({ items: [], total: 0 }))),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/no documents yet/i)).toBeInTheDocument());

    fireEvent.change(screen.getByLabelText(/filter by document type/i), {
      target: { value: "permit" },
    });

    await waitFor(() =>
      expect(screen.getByText(/no documents match your filters/i)).toBeInTheDocument(),
    );
    expect(screen.queryByText(/no documents yet/i)).toBeNull();
  });

  it("a mid-batch upload failure keeps only the un-uploaded files staged — no duplicate re-upload on retry", async () => {
    let uploadCalls = 0;
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(makeDocumentsResponse({ items: [], total: 0 })),
      ),
      http.get(url("/api/vendors"), () =>
        jsonOk([{ id: "v1", name: "Acme Catering" }]),
      ),
      http.post(url("/api/documents/upload"), () => {
        uploadCalls++;
        // Call 1 (file A) succeeds, call 2 (file B) fails, call 3 (retry of B) succeeds.
        if (uploadCalls === 2) {
          return jsonError("server.error", "blip", { status: 500 });
        }
        return jsonOk({
          id: `d_${uploadCalls}`,
          originalFileName: "x.pdf",
          extractionStatus: "Pending",
        });
      }),
    );

    const { container } = renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByText(/no documents yet/i)).toBeInTheDocument(),
    );

    dropFilesIn(container, [makeFile("fileA.pdf"), makeFile("fileB.pdf")]);
    await waitFor(() => expect(screen.getByText("fileA.pdf")).toBeInTheDocument());
    expect(screen.getByText("fileB.pdf")).toBeInTheDocument();

    fireEvent.click(await screen.findByRole("option", { name: "Acme Catering" }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /upload 2 files/i })).not.toBeDisabled(),
    );
    fireEvent.click(screen.getByRole("button", { name: /upload 2 files/i }));

    // After the mid-batch failure: the succeeded file (A) is removed from the
    // staging list; the failed file (B) remains so it can be retried.
    await waitFor(() => expect(screen.queryByText("fileA.pdf")).toBeNull());
    expect(screen.getByText("fileB.pdf")).toBeInTheDocument();
    expect(uploadCalls).toBe(2);

    // Retry re-sends ONLY file B — never the already-uploaded file A. With the
    // bug, A would re-upload too (uploadCalls would reach 4 and mint a duplicate).
    fireEvent.click(screen.getByRole("button", { name: /upload 1 file/i }));
    await waitFor(() => expect(uploadCalls).toBe(3));
    await waitFor(() => expect(screen.queryByText("fileB.pdf")).toBeNull());
  });

  it("an orphaned row exposes an Assign affordance that PATCHes the chosen vendor", async () => {
    let patchedId: string | null = null;
    let patchBody: unknown = null;
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(
          makeDocumentsResponse({
            items: [
              makeDocument({
                id: "d_orphan",
                originalFileName: "orphan.pdf",
                vendorName: null,
                vendorId: null,
                extractionStatus: "Completed", // terminal → no polling
                complianceStatus: "Pending",
              }),
            ],
            total: 1,
          }),
        ),
      ),
      http.get(url("/api/vendors"), () =>
        jsonOk([{ id: "v1", name: "Acme Catering" }]),
      ),
      http.patch(url("/api/documents/:id"), async ({ params, request }) => {
        patchedId = params.id as string;
        patchBody = await request.json();
        return jsonOk({ message: "Document updated." });
      }),
    );

    renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText("orphan.pdf")).toBeInTheDocument());

    // The orphaned vendor cell shows an assign affordance instead of "—".
    fireEvent.click(screen.getByRole("button", { name: /assign vendor/i }));
    fireEvent.click(await screen.findByRole("option", { name: "Acme Catering" }));

    await waitFor(() => expect(patchedId).toBe("d_orphan"));
    expect(patchBody).toEqual({ vendorId: "v1" });
  });
});

describe("DocumentsPage — a11y live-region announcement (#189)", () => {
  it("announces in a polite live region when a document finishes reading on a poll", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const seq = sequencedJsonOk(
        makeDocumentsResponse({
          items: [
            makeDocument({
              id: "d_live",
              originalFileName: "live.pdf",
              extractionStatus: "Processing",
              vendorName: "Acme Sub",
            }),
          ],
          total: 1,
        }),
        makeDocumentsResponse({
          items: [
            makeDocument({
              id: "d_live",
              originalFileName: "live.pdf",
              extractionStatus: "Completed",
              vendorName: "Acme Sub",
            }),
          ],
          total: 1,
        }),
      );
      server.use(http.get(url("/api/documents"), () => seq()));

      const { container } = renderWithProviders(<DocumentsPage />, { auth: authedMe });
      await waitFor(() => expect(screen.getByText("Reading…")).toBeInTheDocument());

      const live = container.querySelector('[aria-live="polite"]') as HTMLElement;
      expect(live).not.toBeNull();
      expect(live.textContent).toBe("");

      await vi.advanceTimersByTimeAsync(5000);
      await waitFor(() =>
        expect(live.textContent).toMatch(/live\.pdf finished processing/i),
      );
    } finally {
      vi.useRealTimers();
    }
  });
});

// ---------------------------------------------------------------------------
// #265 — the dropzone must SAY why a file was refused (wrong type, oversize)
// instead of silently swallowing it, and must accept the iPhone HEIC default
// like the portal and backend already do.
// ---------------------------------------------------------------------------
describe("DocumentsPage — dropzone rejection feedback (#265)", () => {
  let container: HTMLElement;

  async function renderWithEmptyList() {
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(makeDocumentsResponse({ items: [], total: 0 })),
      ),
    );
    ({ container } = renderWithProviders(<DocumentsPage />, { auth: authedMe }));
    await waitFor(() =>
      expect(screen.getByText(/drag a file here or click to browse/i)).toBeInTheDocument(),
    );
  }

  it("wrong file type: surfaces the friendly toast and stages nothing", async () => {
    await renderWithEmptyList();

    // A Word doc — the exact first-session document Pat's vendor emails her.
    dropFilesIn(container, [
      makeFile("certificate.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
    ]);

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        expect.stringMatching(/can't read that file format/i),
      ),
    );
    // Nothing staged → the details card never appears.
    expect(screen.queryByText(/add details before uploading/i)).toBeNull();
  });

  it("oversize file: surfaces the 10 MB toast and stages nothing", async () => {
    await renderWithEmptyList();

    dropFilesIn(container, [makeFile("huge-scan.pdf", "application/pdf", 11 * 1024 * 1024)]);

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(expect.stringMatching(/over the 10 mb limit/i)),
    );
    expect(screen.queryByText(/add details before uploading/i)).toBeNull();
  });

  it("HEIC photo: accepted and staged like the portal and backend (#265 accept-list half)", async () => {
    await renderWithEmptyList();

    dropFilesIn(container, [makeFile("photo.heic", "image/heic")]);

    await waitFor(() =>
      expect(screen.getByText(/add details before uploading/i)).toBeInTheDocument(),
    );
    expect(screen.getByText("photo.heic")).toBeInTheDocument();
    expect(toastError).not.toHaveBeenCalled();
  });

  it("mixed drop: rejected file toasts while the valid file still stages", async () => {
    await renderWithEmptyList();

    dropFilesIn(container, [
      makeFile("coi.pdf", "application/pdf"),
      makeFile("notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
    ]);

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        expect.stringMatching(/can't read that file format/i),
      ),
    );
    await waitFor(() =>
      expect(screen.getByText(/add details before uploading/i)).toBeInTheDocument(),
    );
    expect(screen.getByText("coi.pdf")).toBeInTheDocument();
    expect(screen.queryByText("notes.docx")).toBeNull();
  });

  it("dropzone helper text names the HEIC format so the accept list and the copy agree", async () => {
    await renderWithEmptyList();

    expect(screen.getByText(/pdf, jpeg, png, or iphone photo \(heic\) · 10 mb max/i)).toBeInTheDocument();
  });

  it("file exactly at the 10 MB cap stages (the limit is inclusive)", async () => {
    // Pins which side of the boundary "exactly 10 MB" lands on — react-dropzone
    // rejects only file.size > maxSize, so the cap is inclusive.
    await renderWithEmptyList();

    dropFilesIn(container, [makeFile("exact.pdf", "application/pdf", 10 * 1024 * 1024)]);

    await waitFor(() =>
      expect(screen.getByText(/add details before uploading/i)).toBeInTheDocument(),
    );
    expect(toastError).not.toHaveBeenCalled();
  });

  it("the file input carries the mobile photo-picker accept override (#265)", async () => {
    // jsdom never validates against the input's accept attribute (validation runs in
    // onDrop), so only this attribute assertion catches the override being removed or
    // moved BEFORE the {...getInputProps()} spread — react-dropzone injects its own
    // narrower accept and last-prop-wins is the load-bearing detail. Mirrors the
    // portal's pin ("surfaces the camera on mobile", #196).
    await renderWithEmptyList();

    const input = container.querySelector('input[type="file"]')!;
    expect(input.getAttribute("accept")).toMatch(/image\/\*/);
    expect(input.getAttribute("accept")).toMatch(/application\/pdf/);
  });
});
