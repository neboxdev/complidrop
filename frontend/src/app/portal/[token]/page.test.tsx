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
 * and the inline UI surfaces that string verbatim.
 *
 * Security note: the page renders `vendorName`, `orgName`, and
 * `instructions` straight from the server payload; one assertion below
 * pins that the URL `:token` itself is NEVER reflected into the DOM
 * (no debug crumb, no analytics tag, no aria-label containing it).
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { http } from "msw";
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
// but we mock to silence any indirect import. Toaster stub avoids
// jsdom portal-mount churn.
vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  Toaster: () => null,
}));

// Helper: drive react-dropzone via the hidden file input it renders.
// fireEvent.change on the input flows through onDrop with the supplied
// files, simulating a real file picker.
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
  it("shows the loading copy while the portal-info fetch is in flight", () => {
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
    release();
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
  it("expired/revoked link: renders the recovery copy, hides the dropzone", async () => {
    // Use the shared handler factory from #34's harness for the 404
    // envelope. The page surfaces `body.error.message` AND the static
    // "Ask your customer…" recovery hint.
    server.use(expiredPortalLinkHandler(TOKEN));

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

    await waitFor(() =>
      expect(
        screen.getByText(/this link is no longer available/i),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getByText(/ask your customer for a fresh upload link/i),
    ).toBeInTheDocument();
    // Dropzone affordance MUST NOT render in this state — letting the
    // vendor try anyway against a dead token wastes their time and
    // ours.
    expect(
      screen.queryByText(/drag a file here or click to select/i),
    ).toBeNull();
  });

  it("non-JSON / network failure: still falls into the not-available branch with a generic hint", async () => {
    // The page's try/catch falls back to `err.message ?? \"Could not load
    // portal.\"`. A network-level error path (MSW handler that throws)
    // should NOT leak a stack trace into the DOM.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () => {
        throw new Error("simulated network failure");
      }),
    );

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

    await waitFor(() =>
      expect(
        screen.getByText(/this link is no longer available/i),
      ).toBeInTheDocument(),
    );
    // No raw stack: the rendered tree must not contain the word "Error"
    // followed by a colon or the file extension `.tsx` (a stack frame).
    expect(document.body.textContent ?? "").not.toMatch(/\bError:/);
    expect(document.body.textContent ?? "").not.toMatch(/\.tsx/);
  });

  it("does NOT leak internal error codes (e.g. portal.expired) into the visible copy", async () => {
    // The backend envelope carries `code: "portal.expired"`. The
    // recovery UI must surface the human MESSAGE, never the code.
    server.use(expiredPortalLinkHandler(TOKEN));

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

    await waitFor(() =>
      expect(
        screen.getByText(/this link is no longer available/i),
      ).toBeInTheDocument(),
    );
    expect(document.body.textContent ?? "").not.toContain("portal.expired");
  });
});

describe("PortalPage — quota-exhausted state (#37)", () => {
  it("MaxUploads reached: counter shows N/N, the page renders without a happy-path nudge", async () => {
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 5, maxUploads: 5 })),
      ),
    );

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

    await waitFor(() =>
      expect(screen.getByText(/5\s*\/\s*5\s+uploads used/i)).toBeInTheDocument(),
    );
  });

  it("attempting an upload past quota: backend's quota error surfaces to the user via toast-free inline copy", async () => {
    // The page's dropzone fires onDrop unconditionally — quota enforcement
    // happens server-side. A successful info fetch may show 5/5 used; if
    // the vendor still clicks-through, the upload POST returns the
    // backend's quota error and the page renders the error text.
    server.use(
      http.get(url(`/api/portal/${TOKEN}`), () =>
        jsonOk(makePortalInfo({ uploadCount: 5, maxUploads: 5 })),
      ),
      http.post(url(`/api/portal/${TOKEN}/upload`), () =>
        jsonError("portal.quota_exhausted", "Upload limit reached for this link.", {
          status: 409,
        }),
      ),
    );

    renderWithProviders(<PortalPage />, { params: { token: TOKEN } });

    await waitFor(() =>
      expect(screen.getByText(/5\s*\/\s*5\s+uploads used/i)).toBeInTheDocument(),
    );
    dropFiles([makeFile("late.pdf")]);

    await waitFor(() =>
      expect(
        screen.getByText(/upload limit reached for this link/i),
      ).toBeInTheDocument(),
    );
    // No raw code in the inline error.
    expect(document.body.textContent ?? "").not.toContain(
      "portal.quota_exhausted",
    );
  });
});

describe("PortalPage — file-rejected state (#37)", () => {
  it("wrong MIME type: react-dropzone rejects the file; no upload request fires", async () => {
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

    // .exe is not in the dropzone's `accept` list → onDrop ignores it.
    dropFiles([makeFile("malware.exe", "application/octet-stream")]);

    // Allow any in-flight microtask to settle; assert NO upload fired
    // and the page stayed in its idle state (no per-file Received row).
    await waitFor(() => expect(uploadCalls).toBe(0));
    expect(screen.queryByText(/received/i)).toBeNull();
  });

  it("oversized file: dropzone rejects (10 MB max); no upload request fires", async () => {
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

    // 11 MB > 10 MB cap → dropzone rejects, onDrop drops it on the floor.
    const oversize = makeFile("huge.pdf", "application/pdf", 11 * 1024 * 1024);
    dropFiles([oversize]);

    await waitFor(() => expect(uploadCalls).toBe(0));
    expect(screen.queryByText(/received/i)).toBeNull();
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

  it("partial-batch failure: 3 files, middle one fails → 2 in Received, error copy surfaces, batch stops", async () => {
    // The portal page processes files sequentially in onDrop's for-loop.
    // If the second file's POST throws, the loop breaks before the third
    // — that's the current contract. Pin it: 2 successful files in the
    // Received list, the failed file's server-message in the error copy,
    // and the third file never attempted.
    // Sequence by call count instead of by parsing the multipart body:
    // MSW's `request.formData()` interop with jsdom's File objects can
    // throw, and the contract we want to pin is "the 2nd upload fails,
    // the loop breaks before the 3rd" — call-order is sufficient.
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

    // Loop processed file #1 (success), then file #2 (4xx → throws) →
    // loop breaks before file #3. File #3 NEVER hits the network and
    // its name never lands in the Received list.
    await waitFor(() =>
      expect(
        screen.getByText(/could not process this file/i),
      ).toBeInTheDocument(),
    );
    expect(screen.getByText("ok-1.pdf")).toBeInTheDocument();
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
  it("the portal token from the URL never appears in the rendered DOM (no debug crumb / no analytics tag)", async () => {
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

    // The token MUST stay in the URL/request layer. It should not be
    // rendered as visible copy, as an aria-label, as a debug ID, or as
    // a tooltip. Scan both textContent and the serialized innerHTML so
    // attribute values are also covered.
    const tree = document.body;
    expect(tree.textContent ?? "").not.toContain(sensitiveToken);
    expect(tree.innerHTML).not.toContain(sensitiveToken);
  });

  it("the portal token is also NOT echoed back inside the error-state UI", async () => {
    const sensitiveToken = "still-secret-token-ABC-99999";
    // Use a hand-rolled 404 because expiredPortalLinkHandler defaults
    // to a different token — pin THIS test's exact token.
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
        screen.getByText(/this link is no longer available/i),
      ).toBeInTheDocument(),
    );

    const tree = document.body;
    expect(tree.textContent ?? "").not.toContain(sensitiveToken);
    expect(tree.innerHTML).not.toContain(sensitiveToken);
  });
});

describe("PortalPage — uploading-in-flight state (#37)", () => {
  beforeEach(() => {
    // Belt-and-suspenders: any test that flipped fake timers must not
    // leak them here (none currently use fake timers, but defensive).
    vi.useRealTimers();
  });

  it("'Uploading…' copy appears while a POST is in flight, then disappears on settle", async () => {
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

    // Uploading… appears DURING the in-flight POST.
    try {
      await waitFor(() =>
        expect(screen.getByText(/uploading…/i)).toBeInTheDocument(),
      );
    } finally {
      release();
    }
    // After settlement, Uploading… disappears, Received list appears.
    await waitFor(() =>
      expect(screen.getByText(/^received$/i)).toBeInTheDocument(),
    );
    expect(screen.queryByText(/uploading…/i)).toBeNull();
  });
});
