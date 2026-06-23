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
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
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

    // Branded skeleton (role=status), not a bare "Loading…" text. (#196)
    expect(
      screen.getByRole("status", { name: /loading your upload page/i }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/^loading…$/i)).toBeNull();

    // Drain the held promise inside the test boundary so the post-
    // release setState doesn't fire during afterEach. Mirrors the
    // uploading-in-flight pattern.
    release();
    await waitFor(() =>
      expect(
        screen.queryByRole("status", { name: /loading your upload page/i }),
      ).toBeNull(),
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
    // Org context on initial render appears in exactly one place: the subhead
    // ("{org} asked for…"). FP-122 retitled the instructions card to the neutral
    // "What to upload" (no org name), and the sr-only live region is empty until an
    // upload happens — so the count is 1, not 2.
    expect(
      screen.getAllByText(new RegExp(portalInfo.orgName)),
    ).toHaveLength(1);
    // The owner's instructions are now RENDERED (previously fetched but never
    // shown — the core #196 bug). Assert a unique phrase from the fixture.
    expect(
      screen.getByText(/please upload your current COI and any state license/i),
    ).toBeInTheDocument();
    // Quota counter reflects 1/5 used (the wording is "X / Y uploads used").
    expect(screen.getByText(/1\s*\/\s*5\s+uploads used/i)).toBeInTheDocument();
    // FP-123: the tab title names the page (and the org) instead of the marketing default.
    expect(document.title).toMatch(/upload for acme inc/i);
  });

  // Both empty AND whitespace-only must suppress the block — the production
  // guard is `info.instructions?.trim()`, so the whitespace case specifically
  // pins the `.trim()` (an empty string is already falsy without it).
  it.each([
    ["an empty string", ""],
    ["whitespace only", "   \n\t "],
  ])("does not render an instructions block when instructions are %s", async (_label, value) => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ instructions: value })),
      ),
    );
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: new RegExp(`hi ${portalInfo.vendorName}`, "i") }),
      ).toBeInTheDocument(),
    );
    // No "What to upload" instructions header when there are no instructions (FP-122 retitle).
    expect(screen.queryByText(/what to upload/i)).toBeNull();
  });

  it("surfaces the camera on mobile: the file input accepts images + the copy mentions a photo (#196)", async () => {
    server.use(http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)));
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(
        screen.getByText(/tap to choose a file or take a photo/i),
      ).toBeInTheDocument(),
    );
    // The load-bearing assertion is the `accept` attribute (a sound proxy for
    // "the native picker offers Take Photo"). NOTE: jsdom applies no CSS, so
    // BOTH responsive copy spans are in the DOM regardless of viewport — the
    // copy assertion above proves the string exists, not that it's shown only
    // on mobile (true viewport behavior would need the Playwright tier).
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    expect(input.getAttribute("accept")).toMatch(/image\/\*/);
    expect(input.getAttribute("accept")).toMatch(/application\/pdf/);
  });

  it("a phone photo (HEIC) is now accepted and uploads — no 'Most Compatible' dead-end (#220)", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonOk({ uploadId: "u_heic", extractionStatus: "Pending", message: "Received" }),
      ),
    );
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/tap to choose a file or take a photo/i)).toBeInTheDocument(),
    );
    // An iPhone HEIC capture is now in the dropzone's accept map (transcoded to
    // JPEG server-side on ingest, #220), so it uploads instead of being rejected
    // client-side with the old "switch to Most Compatible" dead-end.
    dropFiles([makeFile("coi.heic", "image/heic", 2048)]);

    await waitFor(() => {
      expect(screen.getByText(/^received$/i)).toBeInTheDocument();
      expect(screen.getByText("coi.heic")).toBeInTheDocument();
    });
    // The #196 stopgap copy is gone now that HEIC just works.
    expect(screen.queryByText(/most compatible/i)).toBeNull();
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
    // FP-123: HEIC is now named in the accepted-formats line (it's accepted + transcoded server-side).
    expect(
      screen.getByText(/pdf, jpeg, png, or heic · 10 mb max/i),
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

  it("FP-120: transient 5xx renders the TRANSIENT 'try again' state, NOT the dead-link copy", async () => {
    // FP-120 [P0]: a 5xx (or network blip) does NOT mean the link is gone. Telling Tony
    // "this link is no longer available — ask your customer" on a transient failure sent him
    // to bother Pat over a healthy link. A 5xx must land in the retryable transient branch.
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

    // Transient recovery copy + a Try again affordance.
    await waitFor(() =>
      expect(screen.getByText(/we couldn't load this page/i)).toBeInTheDocument(),
    );
    expect(
      screen.getByRole("button", { name: /try again/i }),
    ).toBeInTheDocument();
    expect(screen.getByText(/your upload link is probably fine/i)).toBeInTheDocument();
    // The DEAD-LINK copy must NOT render on a transient failure — that was the P0 misdirection.
    expect(
      screen.queryByText(/ask your customer for a fresh upload link/i),
    ).toBeNull();
    expect(screen.queryByText(/this link is no longer available/i)).toBeNull();
    // Code is suppressed.
    expect(document.body.textContent ?? "").not.toContain("server.unavailable");
  });

  it("FP-120: clicking 'Try again' refetches and recovers when the blip clears", async () => {
    // The transient branch's whole point: the link is fine, so a retry that now succeeds
    // must drop the error and render the real upload page.
    let attempt = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => {
        attempt++;
        if (attempt === 1) return jsonError("server.unavailable", "Down briefly.", { status: 503 });
        return jsonOk(portalInfo);
      }),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    const tryAgain = await screen.findByRole("button", { name: /try again/i });
    fireEvent.click(tryAgain);

    // Second fetch succeeds → the greeting renders, the error state is gone.
    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: new RegExp(`hi ${portalInfo.vendorName}`, "i") }),
      ).toBeInTheDocument(),
    );
    expect(screen.queryByText(/we couldn't load this page/i)).toBeNull();
    expect(attempt).toBe(2);
  });

  it("FP-120: a real fetch rejection (network failure) also lands in the transient branch WITHOUT leaking jargon", async () => {
    // HttpResponse.error() rejects the response promise so the page's catch handler fires.
    // A network failure is transient (not a dead link), and no JS error string
    // ("TypeError: Failed to fetch") may leak into the DOM.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => HttpResponse.error()),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));

    await waitFor(() =>
      expect(screen.getByText(/we couldn't load this page/i)).toBeInTheDocument(),
    );
    // Transient, not dead-link.
    expect(
      screen.queryByText(/ask your customer for a fresh upload link/i),
    ).toBeNull();
    // No stack trace / error-prefix / "Failed to fetch" leaking through.
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
        // FP-125: copy reworded to name the accepted FORMATS ("file format", not "file type").
        screen.getByText(/we can't read that file format/i),
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
      expect(screen.getByText(/over the 10 mb limit/i)).toBeInTheDocument(),
    );
    expect(uploadCalls).toBe(0);
    expect(screen.queryByText(/^received$/i)).toBeNull();
  });

  it("FP-123: a 0-byte file is rejected client-side with the empty-file copy; no upload fires", async () => {
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
      expect(screen.getByText(/drag a file here or click to select/i)).toBeInTheDocument(),
    );

    // minSize: 1 on the dropzone → a 0-byte pick is rejected as file-too-small with clear copy,
    // never sent to the backend (which would otherwise return a wrong-shaped message).
    dropFiles([makeFile("empty.pdf", "application/pdf", 0)]);

    await waitFor(() =>
      expect(screen.getByText(/that file is empty/i)).toBeInTheDocument(),
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
    // "What happens next" closes the loop for a cold vendor (#240) — no silent "did it work?".
    // "document and" (not "documents and") pins the SINGULAR branch — it can't pass on plural output.
    expect(screen.getByText(/review your document and reach out/i)).toBeInTheDocument();
    expect(screen.getByText(/you can close this page/i)).toBeInTheDocument();
  });

  it("two successful uploads: the 'what happens next' copy uses the plural 'documents'", async () => {
    let n = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonOk({ uploadId: `u_${n++}`, extractionStatus: "Pending", message: "Received" }),
      ),
    );

    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/drag a file here or click to select/i)).toBeInTheDocument(),
    );

    dropFiles([makeFile("coi.pdf"), makeFile("license.pdf")]);

    await waitFor(() =>
      expect(screen.getByText(/review your documents and reach out/i)).toBeInTheDocument(),
    );
    expect(screen.getByText("coi.pdf")).toBeInTheDocument();
    expect(screen.getByText("license.pdf")).toBeInTheDocument();
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
    // ok-1.pdf is in the Received list. fail.pdf is NAMED in the error block (FP-124) but must NOT
    // appear in Received (it never got a 200). ok-3.pdf was never attempted (loop broke), so it's
    // nowhere. Scope the Received-exclusion with within() now that the failed file is named elsewhere.
    const received = screen.getByText(/^received$/i).closest("div")!;
    expect(within(received).getByText("ok-1.pdf")).toBeInTheDocument();
    expect(within(received).queryByText("fail.pdf")).toBeNull();
    // FP-124: the failed file is named inside the error region.
    const errorBlock = screen.getByTestId("portal-error-other");
    expect(within(errorBlock).getByText("fail.pdf")).toBeInTheDocument();
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
    // Transient-error guidance — the discriminator-specific copy. FP-123 dropped the
    // "or retry now" invitation (the button itself is the retry; the copy sets the wait expectation).
    expect(
      screen.getByText(/try again in about an hour/i),
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

  it("FP-124: 'other' kind (network failure) NOW shows a file-preserving retry + names the failed file", async () => {
    // FP-124 [P1]: previously the retry button rendered ONLY on the rate-limit branch, so a
    // mid-upload network blip stranded the vendor with no recovery despite the file being captured
    // in state. Now retry renders on EVERY retryable failure (kind !== quota_exhausted) AND the
    // failed file is named. This test pins the new contract end-to-end: failure → named file +
    // retry button → click replays the SAME file → success.
    let attempt = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () => {
        attempt++;
        if (attempt === 1) return HttpResponse.error(); // network failure → kind "other"
        return jsonOk({ uploadId: "u_ok", extractionStatus: "Pending", message: "Received" });
      }),
    );

    await renderAndDrop(makeFile("doomed.pdf"));

    // The "other" error block surfaces with the jargon-free fallback.
    const alert = await screen.findByTestId("portal-error-other");
    expect(alert).toHaveAttribute("role", "alert");
    expect(alert).toHaveAttribute("aria-live", "polite");
    expect(screen.getByText(/upload failed\. please try again/i)).toBeInTheDocument();
    // FP-124: the failed file is NAMED so a multi-file dropper knows which one to re-send.
    expect(screen.getByText("doomed.pdf")).toBeInTheDocument();
    // FP-124: the retry button NOW renders for kind="other" (it's retryable — only quota is dead).
    const retryBtn = screen.getByRole("button", { name: /retry upload/i });
    // The rate-limit-only escalation/quota copy must NOT appear on this branch.
    expect(screen.queryByText(/try again in about an hour/i)).toBeNull();
    expect(screen.queryByText(/this link is exhausted/i)).toBeNull();
    expect(screen.queryByTestId("portal-error-rate_limit")).toBeNull();
    expect(screen.queryByTestId("portal-error-quota_exhausted")).toBeNull();
    // No browser internals leak.
    const visible = document.body.textContent ?? "";
    expect(visible).not.toMatch(/\bTypeError\b/);
    expect(visible).not.toMatch(/failed to fetch/i);

    // Clicking retry replays the captured file; the second attempt succeeds.
    fireEvent.click(retryBtn);
    await waitFor(() => {
      expect(screen.getByText(/^received$/i)).toBeInTheDocument();
      expect(screen.queryByTestId("portal-error-other")).toBeNull();
    });
    expect(attempt).toBe(2);
  });

  it("FP-124: quota_exhausted from the SERVER shows NO retry button (a burned link is not retryable)", async () => {
    // uploadFile captures retryFile on every server error, INCLUDING the permanent quota one — so
    // the retry gate must exclude quota_exhausted explicitly (kind !== "quota_exhausted"), or a dead
    // link would offer a futile retry. This pins that exclusion.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 4, maxUploads: 5 })),
      ),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonError("vendor.portal_quota_exceeded", "Upload quota reached for this link.", {
          status: 429,
        }),
      ),
    );

    await renderAndDrop(makeFile("coi.pdf"));

    await waitFor(() =>
      expect(screen.getByTestId("portal-error-quota_exhausted")).toBeInTheDocument(),
    );
    // Even though uploadFile captured the file, the permanent kind shows no retry.
    expect(screen.queryByRole("button", { name: /retry upload/i })).toBeNull();
    expect(screen.getByText(/this link is exhausted/i)).toBeInTheDocument();
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
      // FP-130: the polite sr-only region also announces the in-flight state (distinct wording from
      // the visible "Uploading…" so the two don't collide in getByText).
      expect(
        screen.getByText(/uploading your document, please wait/i),
      ).toBeInTheDocument();
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

describe("PortalPage — FP-121 in-session quota counting", () => {
  it("a successful upload increments the quota counter without a reload", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 1, maxUploads: 3 })),
      ),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonOk({ uploadId: "u1", extractionStatus: "Pending", message: "Received" }),
      ),
    );
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/1\s*\/\s*3\s+uploads used/i)).toBeInTheDocument(),
    );

    dropFiles([makeFile("coi.pdf")]);

    // FP-121: the old code only ever rendered the initial server count, so it stuck at "1 / 3"
    // after a success. The counter must reflect the in-session upload (2 / 3) live.
    await waitFor(() =>
      expect(screen.getByText(/2\s*\/\s*3\s+uploads used/i)).toBeInTheDocument(),
    );
  });

  it("the final in-session upload disables the dropzone (atQuota counts in-session uploads)", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 1, maxUploads: 2 })),
      ),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonOk({ uploadId: "u1", extractionStatus: "Pending", message: "Received" }),
      ),
    );
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/drag a file here or click to select/i)).toBeInTheDocument(),
    );

    dropFiles([makeFile("coi.pdf")]); // 1 (server) + 1 (in-session) = 2 = max

    await waitFor(() =>
      expect(screen.getByText(/2\s*\/\s*2\s+uploads used/i)).toBeInTheDocument(),
    );
    // The dropzone flips to the exhausted state with NO reload — atQuota now counts uploaded.length.
    expect(screen.getByText(/upload limit reached on this link/i)).toBeInTheDocument();
  });
});

describe("PortalPage — FP-130 accessibility", () => {
  it("announces the upload outcome in a polite live region (distinct from the visual Received card)", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonOk({ uploadId: "u1", extractionStatus: "Pending", message: "Received" }),
      ),
    );
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/drag a file here or click to select/i)).toBeInTheDocument(),
    );

    dropFiles([makeFile("coi.pdf")]);

    // A blind vendor must HEAR completion — the visible Received card is otherwise silent.
    await waitFor(() =>
      expect(screen.getByText(/upload complete\./i)).toBeInTheDocument(),
    );
  });

  it("the instructions scroll-box is keyboard-reachable (tabIndex + region role)", async () => {
    server.use(http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)));
    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

    // FP-131: the capped/scrollable instructions box is focusable so a keyboard user can scroll it.
    const region = await screen.findByRole("region", { name: /upload instructions/i });
    expect(region).toHaveAttribute("tabindex", "0");
  });
});

describe("PortalPage — idempotency key (#333)", () => {
  it("sends an Idempotency-Key header on the upload POST", async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), async ({ request }) => {
        keys.push(request.headers.get("Idempotency-Key"));
        return jsonOk({ uploadId: "u_idem", extractionStatus: "Pending", message: "Received" });
      }),
    );
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/drag a file here or click to select/i)).toBeInTheDocument(),
    );
    dropFiles([makeFile("coi.pdf")]);
    await waitFor(() => expect(screen.getByText(/^received$/i)).toBeInTheDocument());

    expect(keys).toHaveLength(1);
    expect(keys[0]).toBeTruthy();
  });

  it("reuses the SAME key when a failed upload is retried", async () => {
    // So a succeeded-but-response-lost upload replays the winner server-side instead of duplicating.
    const keys: (string | null)[] = [];
    let call = 0;
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => jsonOk(portalInfo)),
      http.post(url(`/api/portal/${TOKEN}/upload`), async ({ request }) => {
        keys.push(request.headers.get("Idempotency-Key"));
        call += 1;
        return call === 1
          ? jsonError("rate_limit.exceeded", "Too many requests. Please try again later.", { status: 429 })
          : jsonOk({ uploadId: "u_idem", extractionStatus: "Pending", message: "Received" });
      }),
    );
    ({ container } = renderWithProviders(<PortalPage />, { params: { token: TOKEN } }));
    await waitFor(() =>
      expect(screen.getByText(/drag a file here or click to select/i)).toBeInTheDocument(),
    );
    dropFiles([makeFile("coi.pdf")]);

    const retry = await screen.findByRole("button", { name: /retry upload/i });
    fireEvent.click(retry);
    await waitFor(() => expect(screen.getByText(/^received$/i)).toBeInTheDocument());

    expect(keys).toHaveLength(2);
    expect(keys[0]).toBeTruthy();
    expect(keys[1]).toBe(keys[0]);
  });
});
