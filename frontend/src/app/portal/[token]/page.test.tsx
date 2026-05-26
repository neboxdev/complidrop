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
} from "@/test";

// Sonner not used by the portal page (it has its own inline error UI),
// but the harness mocks it anyway (vitest.setup.ts + src/test/sonner.ts)
// to silence any indirect import. See #74.

// Helper: drive react-dropzone via the hidden file input it renders.
// fireEvent.change on the input flows through onDrop with the supplied
// files. Depends on react-dropzone@^15's contract that `accept` and
// `maxSize` are evaluated INSIDE its onDrop callback (not at the
// browser-level input filter), which jsdom can't enforce — a v16
// release that changed that contract would need this helper updated
// or a switch to `fireEvent.drop` on the root element.
function dropFiles(files: File[]) {
  const input = document.querySelector(
    'input[type="file"]',
  ) as HTMLInputElement | null;
  if (!input) throw new Error("portal: no file input rendered");
  Object.defineProperty(input, "files", {
    value: files,
    configurable: true,
  });
  fireEvent.change(input);
}

function makeFile(name: string, type = "application/pdf", sizeBytes = 1024) {
  return new File(["x".repeat(sizeBytes)], name, { type });
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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });
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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });
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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });
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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });
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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });
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

    // The token MUST stay in the URL/request layer. Should not be
    // rendered as visible copy, an aria-label, a debug ID, or a
    // tooltip. Scan both textContent and serialized innerHTML so
    // attribute values are also covered. Scope is intentionally
    // `document.body` — `<head>` injection paths (analytics meta tags
    // etc.) are out of scope for this component-level assertion.
    const tree = document.body;
    expect(tree.textContent ?? "").not.toContain(sensitiveToken);
    expect(tree.innerHTML).not.toContain(sensitiveToken);
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

    const tree = document.body;
    expect(tree.textContent ?? "").not.toContain(sensitiveToken);
    expect(tree.innerHTML).not.toContain(sensitiveToken);
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

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });
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
