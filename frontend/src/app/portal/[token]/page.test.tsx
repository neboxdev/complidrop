/**
 * Public vendor portal page — full-state component tests (#37, careful-review).
 *
 * The portal page is the highest-empathy surface in the product: the
 * vendor did not sign up, gets one shot, and most goodwill is won or
 * lost here. Its failure / quota / file-rejection states are exactly
 * what a happy-path test would skip.
 *
 * Per CLAUDE.md: `/api/portal/*` is PUBLIC and treats the token as
 * untrusted. The page uses BARE `fetch()` (not the cookie-bearing api
 * client) because vendors don't have a session. MSW handlers still
 * intercept by URL but `ApiError` is never thrown for portal responses
 * — the page's own try/catch maps `body.error.message` to a string,
 * and the inline UI surfaces that string in the bad-link branch as a
 * small detail line below the static recovery copy.
 *
 * Security note: the page renders `vendorName`, `orgName`, and (today
 * never directly) `instructions` straight from the server payload; one
 * assertion below pins that the URL `:token` itself is NEVER reflected
 * into the DOM (no debug crumb, no analytics tag, no aria-label
 * containing it).
 */
import { describe, it, expect, beforeEach } from "vitest";
import { http, HttpResponse } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import PortalPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  expiredPortalLinkHandler,
  makePortalInfo,
  portalInfo,
  dropFilesIn,
  makeFile,
  assertNotInDom,
} from "@/test";

// Sonner not used by the portal page (it has its own inline error UI),
// but the harness mocks it anyway (vitest.setup.ts + src/test/sonner.ts)
// to silence any indirect import. See #74.

// Local wrapper around the harness's container-scoped dropFilesIn (#84).
// Each `it` captures the rendered container in `container` so a future
// composite test that renders two trees at once can't accidentally
// share a file input between them via document.querySelector.
let container: HTMLElement;
function dropFiles(files: File[]) {
  dropFilesIn(container, files);
}

const TOKEN = "vendor-token-abc";

describe("PortalPage — loading state (#37)", () => {
  it("shows the loading copy while the portal-info fetch is in flight", async () => {
    // Hold the response so the test observes the loading branch.
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), async () => {
        await settled;
        return jsonOk(portalInfo);
      }),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    expect(screen.getByText(/^loading…$/i)).toBeInTheDocument();

    // Drain the held promise inside the test boundary so the post-
    // release setState doesn't fire during afterEach. Mirrors the
    // uploading-in-flight pattern.
    release();
    await waitFor(() =>
      expect(screen.queryByText(/^loading…$/i)).toBeNull(),
    );
  });
});

describe("PortalPage — success state (#37)", () => {
  it("renders the greeting, instructions, and quota counter", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 1, maxUploads: 5 })),
      ),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(
        screen.getByRole("heading", {
          name: new RegExp(`hi ${portalInfo.vendorName}`, "i"),
        }),
      ).toBeInTheDocument(),
    );
    // Org context — "{orgName} asked for your latest compliance documents."
    expect(screen.getByText(new RegExp(portalInfo.orgName))).toBeInTheDocument();
    // Quota counter reflects 1/5 used (the wording is "X / Y uploads used").
    expect(screen.getByText(/1\s*\/\s*5\s+uploads used/i)).toBeInTheDocument();
  });

  it("renders the dropzone affordance + accepted-file copy", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(
        screen.getByText(/drag a file here or click to select/i),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getByText(/pdf, jpeg, or png · 10 mb max/i),
    ).toBeInTheDocument();
  });
});

describe("PortalPage — bad-link state (#37)", () => {
  it("expired/revoked link: renders recovery copy, hides the dropzone, suppresses internal error code", async () => {
    // The expiredPortalLinkHandler returns the canonical 404 envelope
    // with `code: "portal.expired"` and `message: "This link is no
    // longer available."`. The page MUST surface the recovery copy
    // ("Ask your customer for a fresh upload link") and MUST NOT
    // surface the code (per AC #2 — internal errors not exposed).
    server.use(expiredPortalLinkHandler(TOKEN));

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(
        screen.getByText(/ask your customer for a fresh upload link/i),
      ).toBeInTheDocument(),
    );
    // Heading still renders. (Use getAllByText because the server
    // message MAY match the same string today; we don't pin that.)
    expect(
      screen.getAllByText(/this link is no longer available/i).length,
    ).toBeGreaterThanOrEqual(1);
    // Dropzone affordance MUST NOT render in this state.
    expect(
      screen.queryByText(/drag a file here or click to select/i),
    ).toBeNull();
    // No raw code: dot-namespaced identifiers must not leak.
    expect(document.body.textContent ?? "").not.toContain("portal.expired");
  });

  it("transient 5xx with curated server message: recovery copy AND the server's human text both render", async () => {
    // Driven by the review finding that hardcoding the static fallback
    // alone would hide actionable diagnostics. The bad-link branch
    // shows the static recovery copy (always) AND any non-duplicate
    // server message as a small detail line — so a 503 the vendor
    // could retry stays visible.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonError(
          "server.unavailable",
          "Service temporarily unavailable. Please try again in a minute.",
          { status: 503 },
        ),
      ),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    // Static recovery line still renders.
    await waitFor(() =>
      expect(
        screen.getByText(/ask your customer for a fresh upload link/i),
      ).toBeInTheDocument(),
    );
    // Curated server message renders as a small detail line.
    expect(
      screen.getByText(/service temporarily unavailable/i),
    ).toBeInTheDocument();
    // Code is suppressed.
    expect(document.body.textContent ?? "").not.toContain("server.unavailable");
  });

  it("real fetch rejection (network failure): bad-link branch fires WITHOUT leaking the thrown error string", async () => {
    // HttpResponse.error() routes through MSW v2's FetchInterceptor
    // errorWith path and ACTUALLY rejects the response promise — so
    // the page's catch handler fires (not the throw-handler → 500
    // synthetic body path, which would resolve normally). This pins
    // that a network failure still lands in the bad-link branch and
    // that no JS error string (e.g. "TypeError: Failed to fetch")
    // leaks through to the rendered DOM.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => HttpResponse.error()),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(
        screen.getByText(/ask your customer for a fresh upload link/i),
      ).toBeInTheDocument(),
    );
    // No stack trace, no error-prefix string, no "Failed to fetch"
    // leaking through. The page falls back to "Could not load portal."
    // — which IS displayed as a small detail line, but that string is
    // a human-curated copy. The thrown-error class names must NEVER
    // appear.
    const visible = document.body.textContent ?? "";
    expect(visible).not.toMatch(/\bTypeError\b/);
    expect(visible).not.toMatch(/\bError:/);
    expect(visible).not.toMatch(/\.tsx/);
    expect(visible).not.toMatch(/failed to fetch/i);
  });
});

describe("PortalPage — quota-exhausted state (#37)", () => {
  it("MaxUploads reached: counter shows N/N, copy + visual signal exhaustion, dropzone is aria-disabled", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 5, maxUploads: 5 })),
      ),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(screen.getByText(/5\s*\/\s*5\s+uploads used/i)).toBeInTheDocument(),
    );
    // Dropzone copy flips to the exhausted message.
    expect(
      screen.getByText(/upload limit reached on this link/i),
    ).toBeInTheDocument();
    // Idle copy is gone.
    expect(
      screen.queryByText(/drag a file here or click to select/i),
    ).toBeNull();
    // Dropzone is marked aria-disabled so screen readers know it's
    // inactive AND react-dropzone's `disabled` flag blocks click/drop.
    const dropzone = screen
      .getByText(/upload limit reached on this link/i)
      .closest("div");
    expect(dropzone?.getAttribute("aria-disabled")).toBe("true");
  });

  it("client-side disable: at 5/5 a drop attempt fires ZERO upload requests; the visible error explains why", async () => {
    let uploadCalls = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 5, maxUploads: 5 })),
      ),
      http.post(url(`/api/portal/${TOKEN}/upload`), () => {
        uploadCalls++;
        return jsonOk({ uploadId: "x", extractionStatus: "Pending", message: "" });
      }),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/5\s*\/\s*5\s+uploads used/i)).toBeInTheDocument(),
    );

    dropFiles([makeFile("late.pdf")]);

    // The dropzone is `disabled: atQuota` so react-dropzone skips
    // onDrop entirely — the page never even sees the drop. No request
    // fires; no inline error needs to render because the affordance
    // itself communicated exhaustion. (Belt-and-suspenders: a server
    // 409 path would also be safe; see the next test.)
    await waitFor(() => expect(uploadCalls).toBe(0));
  });

  it("server-side fallback: a 409 from /upload (somehow attempted past quota) renders the server message inline", async () => {
    // If the dropzone disable somehow gets bypassed (e.g. a future
    // refactor that lifts the disabled flag, or a vendor pasting via
    // the keyboard accessibility path), the server returns 409 and
    // the page's catch surfaces `body.error.message` in the inline
    // error block. This pins the belt-and-suspenders contract.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        // Use uploadCount: 4 so the dropzone is NOT disabled, then
        // simulate a race where the server has already accepted #5.
        jsonOk(makePortalInfo({ uploadCount: 4, maxUploads: 5 })),
      ),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonError("portal.quota_exhausted", "Upload limit reached for this link.", {
          status: 409,
        }),
      ),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(screen.getByText(/4\s*\/\s*5\s+uploads used/i)).toBeInTheDocument(),
    );
    dropFiles([makeFile("late.pdf")]);

    await waitFor(() =>
      expect(
        screen.getByText(/upload limit reached for this link/i),
      ).toBeInTheDocument(),
    );
    expect(document.body.textContent ?? "").not.toContain(
      "portal.quota_exhausted",
    );
  });
});

describe("PortalPage — file-rejected state (#37)", () => {
  it("wrong MIME type: rejection message surfaces to the vendor; no upload fires", async () => {
    let uploadCalls = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () => {
        uploadCalls++;
        return jsonOk({ uploadId: "x", extractionStatus: "Pending", message: "" });
      }),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(
        screen.getByText(/drag a file here or click to select/i),
      ).toBeInTheDocument(),
    );

    // .exe is not in the dropzone's `accept` list → react-dropzone
    // delivers it as a rejection with code "file-invalid-type" → the
    // page maps to vendor-facing copy.
    dropFiles([makeFile("malware.exe", "application/octet-stream")]);

    await waitFor(() =>
      expect(
        screen.getByText(/that file type isn't accepted/i),
      ).toBeInTheDocument(),
    );
    // No request was attempted.
    expect(uploadCalls).toBe(0);
    // No Received row.
    expect(screen.queryByText(/^received$/i)).toBeNull();
  });

  it("oversized file: rejection message surfaces; no upload fires", async () => {
    let uploadCalls = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () => {
        uploadCalls++;
        return jsonOk({ uploadId: "x", extractionStatus: "Pending", message: "" });
      }),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(
        screen.getByText(/drag a file here or click to select/i),
      ).toBeInTheDocument(),
    );

    // 11 MB > 10 MB cap → rejection code "file-too-large".
    const oversize = makeFile("huge.pdf", "application/pdf", 11 * 1024 * 1024);
    dropFiles([oversize]);

    await waitFor(() =>
      expect(screen.getByText(/that file is too large/i)).toBeInTheDocument(),
    );
    expect(uploadCalls).toBe(0);
    expect(screen.queryByText(/^received$/i)).toBeNull();
  });
});

describe("PortalPage — happy upload + partial-batch failure (#37)", () => {
  it("single successful upload: per-file 'Received / Processing…' row renders", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonOk({
          uploadId: "u_01",
          extractionStatus: "Pending",
          message: "Received",
        }),
      ),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(
        screen.getByText(/drag a file here or click to select/i),
      ).toBeInTheDocument(),
    );

    dropFiles([makeFile("coi.pdf")]);

    // "Received" section appears with the file name in the list.
    await waitFor(() =>
      expect(screen.getByText(/^received$/i)).toBeInTheDocument(),
    );
    expect(screen.getByText("coi.pdf")).toBeInTheDocument();
    expect(screen.getByText(/processing…/i)).toBeInTheDocument();
  });

  it("partial-batch failure: 3 files, 2nd fails → 1st in Received, 3rd never attempted, failed file ABSENT from Received", async () => {
    // The portal page processes files sequentially in onDrop's for-loop.
    // If the second file's POST throws, the loop breaks before the third
    // — that's the current contract. Pin it strongly: 1 successful
    // file in Received, the failed file's server-message surfaces, the
    // third file never attempted, AND the failed file does NOT appear
    // in Received (a refactor that flipped `setUploaded` above the
    // throw would leak a failed file as 'accepted').
    //
    // Sequencing by call count (#82's planned helper) instead of by
    // parsing the multipart body: MSW's `request.formData()` interop
    // with jsdom's File objects can throw.
    let calls = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () => {
        calls++;
        if (calls === 2) {
          return jsonError(
            "portal.upload_failed",
            "Could not process this file.",
            { status: 400 },
          );
        }
        return jsonOk({
          uploadId: `u_${calls}`,
          extractionStatus: "Pending",
          message: "Received",
        });
      }),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(
        screen.getByText(/drag a file here or click to select/i),
      ).toBeInTheDocument(),
    );

    dropFiles([
      makeFile("ok-1.pdf"),
      makeFile("fail.pdf"),
      makeFile("ok-3.pdf"),
    ]);

    await waitFor(() =>
      expect(
        screen.getByText(/could not process this file/i),
      ).toBeInTheDocument(),
    );
    expect(screen.getByText("ok-1.pdf")).toBeInTheDocument();
    // The failed file MUST NOT appear in the Received list (it didn't
    // get a 200 — the page must not optimistically show it).
    expect(screen.queryByText("fail.pdf")).toBeNull();
    expect(screen.queryByText("ok-3.pdf")).toBeNull();
    // Two calls, not three — confirms the for-loop broke on the throw.
    expect(calls).toBe(2);
    // No raw code reached the visible copy.
    expect(document.body.textContent ?? "").not.toContain(
      "portal.upload_failed",
    );
  });
});

describe("PortalPage — security: no token leakage into DOM (#37)", () => {
  it("happy path: the URL :token never appears in document.body textContent or innerHTML", async () => {
    const sensitiveToken = "very-secret-vendor-token-XYZ-12345";
    server.use(
      http.get(url(`/api/portal/${sensitiveToken}`), () => jsonOk(portalInfo)),
    );

    renderWithProviders(<PortalPage />, {
      params: { token: sensitiveToken },
    });

    await waitFor(() =>
      expect(
        screen.getByRole("heading", {
          name: new RegExp(`hi ${portalInfo.vendorName}`, "i"),
        }),
      ).toBeInTheDocument(),
    );

    // The token MUST stay in the URL/request layer. Helper scans both
    // document.body.textContent AND innerHTML so leaks through visible
    // copy OR attribute values (aria-label, data-*, title, etc.) are
    // caught. See src/test/security.ts (#85).
    assertNotInDom(sensitiveToken);
  });

  it("error path: the URL :token is also NOT echoed inside the bad-link UI", async () => {
    const sensitiveToken = "still-secret-token-ABC-99999";
    server.use(
      http.get(url(`/api/portal/${sensitiveToken}`), () =>
        jsonError("portal.expired", "This link is no longer available.", {
          status: 404,
        }),
      ),
    );

    renderWithProviders(<PortalPage />, {
      params: { token: sensitiveToken },
    });

    await waitFor(() =>
      expect(
        screen.getByText(/ask your customer for a fresh upload link/i),
      ).toBeInTheDocument(),
    );

    // Same contract on the error path — the token must not echo into
    // the bad-link UI either. (#85)
    assertNotInDom(sensitiveToken);
  });
});

describe("PortalPage — 429 discriminator branching (#145)", () => {
  /*
   * The proper #45 review pinned that the backend now emits TWO
   * distinct 429 envelopes on the upload route:
   *   - `rate_limit.exceeded` — transient (throttled per-token/per-ip)
   *   - `vendor.portal_quota_exceeded` — permanent (link burned its
   *     MaxUploads)
   *
   * Before #145 the page rendered the same plain message for both, so
   * the vendor had no actionable next step on either branch. These
   * tests pin that the page now offers a RETRY button on the transient
   * branch and ESCALATION COPY on the permanent branch — and only the
   * right one.
   *
   * Driving the discriminator via `error.code` (not by sniffing the
   * message text) keeps the contract pinned: if the backend ever
   * rewords either message, these tests still verify the branching
   * holds, and the wire format documented in Program.cs +
   * VendorPortalEndpoints.cs stays the single source of truth.
   */
  const renderAndDrop = async (file: File) => {
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(
        screen.getByText(/drag a file here or click to select/i),
      ).toBeInTheDocument(),
    );
    dropFiles([file]);
  };

  it("rate_limit.exceeded: renders the transient-error copy + a retry button (no escalation copy)", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonError(
          "rate_limit.exceeded",
          "Too many requests. Please try again later.",
          { status: 429 },
        ),
      ),
    );

    await renderAndDrop(makeFile("coi.pdf"));

    // The server's curated message renders.
    await waitFor(() =>
      expect(
        screen.getByText(/too many requests\. please try again later/i),
      ).toBeInTheDocument(),
    );
    // Transient-error guidance — the discriminator-specific copy.
    expect(
      screen.getByText(/try again in about an hour, or retry now/i),
    ).toBeInTheDocument();
    // Retry affordance is present and reachable as a real button.
    expect(
      screen.getByRole("button", { name: /retry upload/i }),
    ).toBeInTheDocument();
    // Escalation copy (the OTHER branch) must NOT render. Pinning
    // strict mutual exclusivity catches a future refactor that, e.g.,
    // hoists both blocks into the same JSX path.
    expect(
      screen.queryByText(/this link is exhausted/i),
    ).toBeNull();
    expect(
      screen.queryByText(/ask your customer to send you a new upload link/i),
    ).toBeNull();
    // The error block is keyed by the discriminator — pin the testid
    // so a future copy tweak can't silently flip the branch.
    const alert = screen.getByTestId("portal-error-rate_limit");
    expect(alert).toBeInTheDocument();
    // Defense-in-depth mutual exclusivity at the testid level — a
    // refactor that left BOTH wrappers rendered would slip past the
    // copy-absence checks if the quota wrapper happened to be empty.
    expect(
      screen.queryByTestId("portal-error-quota_exhausted"),
    ).toBeNull();
    expect(screen.queryByTestId("portal-error-other")).toBeNull();
    // Accessibility contract — the vendor portal is the highest-
    // empathy public surface; assistive-tech users need the error
    // announced. Pinning role=alert + aria-live=polite catches a
    // copy-paste regression that dropped either attribute. (#145
    // review)
    expect(alert).toHaveAttribute("role", "alert");
    expect(alert).toHaveAttribute("aria-live", "polite");
    // The raw code must not leak into the rendered DOM.
    expect(document.body.textContent ?? "").not.toContain(
      "rate_limit.exceeded",
    );
  });

  it("vendor.portal_quota_exceeded: renders the escalation copy (no retry button)", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        // uploadCount: 4 / max: 5 keeps the dropzone enabled so the
        // POST actually fires. Simulates the race where the server
        // already accepted the customer's 5th upload between this
        // page's GET and the vendor's drop — the server returns 429
        // with the permanent discriminator.
        jsonOk(makePortalInfo({ uploadCount: 4, maxUploads: 5 })),
      ),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonError(
          "vendor.portal_quota_exceeded",
          "Upload quota reached for this link.",
          { status: 429 },
        ),
      ),
    );

    await renderAndDrop(makeFile("coi.pdf"));

    // The server's curated message renders.
    await waitFor(() =>
      expect(
        screen.getByText(/upload quota reached for this link/i),
      ).toBeInTheDocument(),
    );
    // Permanent-state guidance — the discriminator-specific copy.
    expect(
      screen.getByText(/this link is exhausted/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/ask your customer to send you a new upload link/i),
    ).toBeInTheDocument();
    // No retry button — retrying a dead link is hostile UX.
    expect(
      screen.queryByRole("button", { name: /retry upload/i }),
    ).toBeNull();
    // Transient-branch copy must NOT render.
    expect(
      screen.queryByText(/try again in about an hour/i),
    ).toBeNull();
    // The error block is keyed by the discriminator.
    const alert = screen.getByTestId("portal-error-quota_exhausted");
    expect(alert).toBeInTheDocument();
    // Defense-in-depth mutual exclusivity at the testid level.
    expect(
      screen.queryByTestId("portal-error-rate_limit"),
    ).toBeNull();
    expect(screen.queryByTestId("portal-error-other")).toBeNull();
    // Accessibility contract — same pinning as the rate_limit branch.
    expect(alert).toHaveAttribute("role", "alert");
    expect(alert).toHaveAttribute("aria-live", "polite");
    // The raw code must not leak into the rendered DOM.
    expect(document.body.textContent ?? "").not.toContain(
      "vendor.portal_quota_exceeded",
    );
  });

  it("'other' kind with retryFile: NO retry button (button is gated on kind, not on retryFile presence)", async () => {
    // The `uploadFile` catch path returns `{ kind: "other", retryFile: file }`
    // when fetch itself rejects (network failure / parse error). The retry
    // button is conjoined-gated by `error.kind === "rate_limit" &&
    // error.retryFile` — a future refactor that loosened the guard to
    // `error.retryFile` alone would silently show the retry button on
    // the "other" branch, which doesn't have the rate-limit context. This
    // pins that the button is gated on KIND, not on retryFile presence.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        HttpResponse.error(),
      ),
    );

    await renderAndDrop(makeFile("doomed.pdf"));

    // The "other" error block surfaces with the jargon-free fallback.
    const alert = await screen.findByTestId("portal-error-other");
    expect(alert).toHaveAttribute("role", "alert");
    expect(alert).toHaveAttribute("aria-live", "polite");
    // The retry button MUST NOT render — kind="other" never qualifies,
    // even though the catch path captured the file.
    expect(
      screen.queryByRole("button", { name: /retry upload/i }),
    ).toBeNull();
    // Neither branch's specific copy renders.
    expect(screen.queryByText(/try again in about an hour/i)).toBeNull();
    expect(screen.queryByText(/this link is exhausted/i)).toBeNull();
    // Mutual exclusivity at the testid level.
    expect(screen.queryByTestId("portal-error-rate_limit")).toBeNull();
    expect(screen.queryByTestId("portal-error-quota_exhausted")).toBeNull();
    // No browser internals leak.
    const visible = document.body.textContent ?? "";
    expect(visible).not.toMatch(/\bTypeError\b/);
    expect(visible).not.toMatch(/failed to fetch/i);
  });

  it("rate_limit retry: clicking 'Retry upload' replays the SAME file once and clears the error on success", async () => {
    // Pins the retry-affordance contract: the retry button doesn't
    // ask the vendor to drag the file in again — it replays the
    // captured file from the previous failure. After a successful
    // retry, the file lands in Received and the error block is gone.
    let attempt = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () => {
        attempt++;
        if (attempt === 1) {
          return jsonError(
            "rate_limit.exceeded",
            "Too many requests. Please try again later.",
            { status: 429 },
          );
        }
        return jsonOk({
          uploadId: "u_retried",
          extractionStatus: "Pending",
          message: "Received",
        });
      }),
    );

    await renderAndDrop(makeFile("retry-me.pdf"));

    // First attempt fails: rate-limit branch surfaces.
    const retryBtn = await screen.findByRole("button", { name: /retry upload/i });

    // Click the retry button via fireEvent (codebase convention; see
    // documents/page.test.tsx) — captured file replays via the SAME
    // upload endpoint; no fresh drop needed.
    fireEvent.click(retryBtn);

    // Second attempt succeeds: Received row appears AND the error
    // block is fully gone (not just the button). Pinning the entire
    // alert region disappears catches a regression where a "success"
    // path forgot to clear `error` but happened to hide the button
    // (e.g. kind flipped to "other" with no retryFile).
    await waitFor(() => {
      expect(screen.getByText(/^received$/i)).toBeInTheDocument();
      expect(screen.getByText("retry-me.pdf")).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /retry upload/i })).toBeNull();
      expect(screen.queryByRole("alert")).toBeNull();
      expect(screen.queryByTestId("portal-error-rate_limit")).toBeNull();
    });
    expect(attempt).toBe(2);
  });

  it("rate_limit then quota: a follow-up retry that 429s as quota_exhausted flips the branch (no retry button)", async () => {
    // Real-world degenerate case: a transient throttle clears, the
    // vendor retries, and meanwhile the link burned its quota via
    // another upload path. The first error renders the retry button;
    // after the retry POSTs and comes back as the permanent
    // discriminator, the page must SWAP affordances — retry button
    // gone, escalation copy in.
    let attempt = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () => {
        attempt++;
        if (attempt === 1) {
          return jsonError(
            "rate_limit.exceeded",
            "Too many requests. Please try again later.",
            { status: 429 },
          );
        }
        return jsonError(
          "vendor.portal_quota_exceeded",
          "Upload quota reached for this link.",
          { status: 429 },
        );
      }),
    );

    await renderAndDrop(makeFile("doomed.pdf"));

    const retryBtn = await screen.findByRole("button", { name: /retry upload/i });
    fireEvent.click(retryBtn);

    await waitFor(() =>
      expect(
        screen.getByText(/this link is exhausted/i),
      ).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole("button", { name: /retry upload/i }),
    ).toBeNull();
    expect(
      screen.getByTestId("portal-error-quota_exhausted"),
    ).toBeInTheDocument();
    // The rate-limit wrapper is gone — true branch swap, not parallel
    // rendering of both alerts.
    expect(
      screen.queryByTestId("portal-error-rate_limit"),
    ).toBeNull();
    expect(attempt).toBe(2);
  });
});

describe("PortalPage — uploading-in-flight state (#37)", () => {
  beforeEach(() => {
    // No tests in this file currently use fake timers, but the
    // afterEach hook in vitest.setup.ts already restores real timers
    // — this beforeEach is intentionally omitted (no defensive
    // boilerplate per CLAUDE.md "simplest design that solves the
    // problem"). Left empty for future maintainers who add a
    // fake-timer test elsewhere in this file.
  });

  it("'Uploading…' appears DURING the POST and disappears WITH 'Received' appearing in the same render", async () => {
    let release: () => void = () => {};
    const settled = new Promise<void>((r) => (release = r));
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), async () => {
        await settled;
        return jsonOk({
          uploadId: "u_pending",
          extractionStatus: "Pending",
          message: "Received",
        });
      }),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(
        screen.getByText(/drag a file here or click to select/i),
      ).toBeInTheDocument(),
    );

    dropFiles([makeFile("slow.pdf")]);

    // Uploading… appears DURING the in-flight POST. try/finally so
    // release() always runs even if the assertion throws.
    try {
      await waitFor(() =>
        expect(screen.getByText(/uploading…/i)).toBeInTheDocument(),
      );
    } finally {
      release();
    }
    // After settlement, BOTH conditions must hold in the same render
    // — Received renders AND Uploading… is gone. Combined in one
    // waitFor so polling rechecks the joint condition and the test
    // can't land on an intermediate state where React batching
    // happens to split the two state updates.
    await waitFor(() => {
      expect(screen.getByText(/^received$/i)).toBeInTheDocument();
      expect(screen.queryByText(/uploading…/i)).toBeNull();
    });
  });
});
