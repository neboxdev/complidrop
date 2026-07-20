/**
 * Dashboard landing page — state matrix across three independent
 * queries (#36).
 *
 * The page fans out to useDashboardStats + useExpiryPipeline +
 * useRecentActivity, plus useMe for the greeting. Each can be in any of
 * loading / error / empty / populated, so we drive a representative set
 * of combinations: all-loading, all-populated, all-error (handled
 * gracefully via fallback values), and the partial-success path (one
 * hook fails, two resolve).
 */
import { describe, it, expect, beforeEach } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import DashboardPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  makeMe,
  sequencedResponses,
} from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

// All four "Get started" checklist signals (#191) now come from /api/dashboard/stats
// — vendor count, the server-derived anyVendorWithRequirements, and document count —
// so the dashboard no longer fans out to /api/vendors. This fully-populated STATS
// completes every step, so the checklist auto-hides.
const STATS = {
  totalDocuments: 12,
  compliant: 8,
  nonCompliant: 1,
  expiringSoon: 2,
  expired: 1,
  pendingExtraction: 3,
  totalVendors: 4,
  anyVendorWithRequirements: true,
  complianceRate: 67,
};
const PIPELINE = { expired: 1, bucket30: 2, bucket60: 1, bucket90: 3, beyond: 5 };
const ACTIVITY = [
  {
    id: "a_01",
    action: "document.uploaded",
    entityType: "Document",
    entityId: "d_completed_01",
    createdAt: "2026-05-26T12:00:00Z",
  },
];

describe("DashboardPage — clickable stat cards (#317 FP-041)", () => {
  it("the Non-compliant card deep-links to the filtered documents view", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );
    renderWithProviders(<DashboardPage />, { auth: authedMe });
    const link = await screen.findByRole("link", { name: /non-compliant: 1\. view these documents/i });
    expect(link).toHaveAttribute("href", "/documents?status=NonCompliant");
  });

  it("every stat card + the Expired/Next-30 buckets deep-link to the right filtered view", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );
    renderWithProviders(<DashboardPage />, { auth: authedMe });

    // Wait for the stats to LOAD (cards fall back to 0 before the query resolves)
    // by anchoring on a loaded value, then assert every deep-link href. A typo'd
    // href would ship green without these.
    expect(await screen.findByRole("link", { name: /compliant: 8\. view these documents/i })).toHaveAttribute("href", "/documents?status=Compliant");
    expect(screen.getByRole("link", { name: /total documents: 12\. view these documents/i })).toHaveAttribute("href", "/documents");
    expect(screen.getByRole("link", { name: /non-compliant: 1\. view these documents/i })).toHaveAttribute("href", "/documents?status=NonCompliant");
    expect(screen.getByRole("link", { name: /expiring within 30 days: 2\. view these documents/i })).toHaveAttribute("href", "/documents?status=ExpiringSoon");
    // Pipeline buckets (distinct aria-label format + the expiresWithin param path).
    expect(screen.getByRole("link", { name: /expired: 1 documents\. view them/i })).toHaveAttribute("href", "/documents?status=Expired");
    expect(screen.getByRole("link", { name: /next 30 days: 2 documents\. view them/i })).toHaveAttribute("href", "/documents?expiresWithin=30");
  });
});

describe("DashboardPage — state matrix (#36)", () => {
  it("loading (no responses yet): page chrome renders, recent-activity shows the loading copy", () => {
    // Hold all three responses so the test observes the loading branch.
    const settled = new Promise<void>(() => {}); // never resolves
    server.use(
      http.get(url("/api/dashboard/stats"), async () => {
        await settled;
        return jsonOk(STATS);
      }),
      http.get(url("/api/dashboard/expiry-pipeline"), async () => {
        await settled;
        return jsonOk(PIPELINE);
      }),
      http.get(url("/api/dashboard/recent-activity"), async () => {
        await settled;
        return jsonOk(ACTIVITY);
      }),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    expect(
      screen.getByRole("heading", { name: /welcome, acme/i }),
    ).toBeInTheDocument();
    // Recent activity shows a branded skeleton (role=status) until activity
    // resolves — replaced the bare "Loading…" to kill layout shift. (#197)
    expect(
      screen.getByRole("status", { name: /loading recent activity/i }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/^loading…$/i)).toBeNull();
  });

  it("empty (zero-state org): the Get started checklist REPLACES the all-zeros stat grid (#191/#3)", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonOk({
          totalDocuments: 0,
          compliant: 0,
          nonCompliant: 0,
          expiringSoon: 0,
          expired: 0,
          pendingExtraction: 0,
          totalVendors: 0,
          anyVendorWithRequirements: false,
          complianceRate: 0,
        }),
      ),
      http.get(url("/api/dashboard/expiry-pipeline"), () =>
        jsonOk({ expired: 0, bucket30: 0, bucket60: 0, bucket90: 0, beyond: 0 }),
      ),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk([])),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    // The guided checklist appears...
    expect(await screen.findByText("Get started")).toBeInTheDocument();
    // ...and once stats resolve to a real zero, the all-zeros stat grid + compliance
    // card are replaced (waitFor: the grid shows its `?? 0` fallbacks until stats
    // loads, then `hasData` flips false and it's removed).
    await waitFor(() => expect(screen.queryByText(/total documents/i)).toBeNull());
    expect(screen.queryByText(/^0%$/)).toBeNull();
    // Recent activity still renders its empty copy.
    expect(screen.getByText(/no recent activity yet/i)).toBeInTheDocument();
  });

  it("populated: stats + pipeline + activity all render with their values", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    // Stats panel (STATS completes every checklist step, so the card is hidden):
    await waitFor(() => expect(screen.getByText("12")).toBeInTheDocument());
    expect(screen.getByText("8")).toBeInTheDocument(); // compliant
    expect(screen.getByText("67%")).toBeInTheDocument(); // compliance rate

    // Activity panel surfaces a single entry's pretty-printed action.
    expect(
      screen.getByText(/document uploaded/i),
    ).toBeInTheDocument();

    // Responsive (#181): the 5-bucket expiry pipeline stacks to 2 columns on a
    // phone and only expands to 5 at md — a fixed `grid-cols-5` would overflow
    // a 390px screen. Class-presence proxy (JSDOM applies no stylesheet).
    const pipelineGrid = document.querySelector(".md\\:grid-cols-5");
    expect(pipelineGrid).not.toBeNull();
    expect(pipelineGrid?.className).toContain("grid-cols-2");

    // #188: plain-English chrome — the heading + bucket labels, not "Expiry
    // pipeline" / "0-30d" / "90d+".
    expect(screen.getByText(/when documents expire/i)).toBeInTheDocument();
    expect(screen.getByText("Next 30 days")).toBeInTheDocument();
    expect(screen.getByText("90+ days")).toBeInTheDocument();
    expect(screen.getByText(/still being read/i)).toBeInTheDocument();
    expect(screen.queryByText("0-30d")).toBeNull();
  });

  it("error on /stats: an error card + Retry replaces the stat region — NEVER a zeroed grid or the checklist (#318 FP-040)", async () => {
    // A stats outage must NOT render a paying account as brand-new-empty. The whole
    // numbers region (grid + pipeline) is gated on a SUCCESSFUL read; on failure we
    // show one honest error card + Retry. Page chrome + the independent activity
    // panel still render.
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonError("server.error", "stats down", { status: 500 }),
      ),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    expect(await screen.findByText(/couldn't load your dashboard/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
    // Chrome + the activity panel (its own section) still render.
    expect(screen.getByRole("heading", { name: /welcome, acme/i })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/document uploaded/i)).toBeInTheDocument());
    // The lies FP-040 prevents: a zeroed grid, the brand-new-account checklist, the
    // numbers region rendering at all, or the old six `?? 0` fallback cards.
    expect(screen.queryByText(/total documents/i)).toBeNull();
    expect(screen.queryByText(/^get started$/i)).toBeNull();
    expect(screen.queryByText(/when documents expire/i)).toBeNull();
    expect(screen.queryAllByText(/^0$/).length).toBe(0);
  });

  it("error on /stats: Retry refetches and reveals the real grid once stats recover (#318 FP-040)", async () => {
    let calls = 0;
    server.use(
      http.get(url("/api/dashboard/stats"), () => {
        calls += 1;
        return calls === 1 ? jsonError("server.error", "boom", { status: 500 }) : jsonOk(STATS);
      }),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    fireEvent.click(await screen.findByRole("button", { name: /retry/i }));

    await waitFor(() => expect(screen.getByText("12")).toBeInTheDocument()); // totalDocuments
    expect(screen.queryByText(/couldn't load your dashboard/i)).toBeNull();
  });
});

describe("DashboardPage — the expiry pipeline fails independently of stats (#368)", () => {
  // /stats and /expiry-pipeline are two separate requests. Before #368 the pipeline
  // card was gated on `stats.isError` alone, so a stats-200 / pipeline-5xx pair
  // rendered five buckets off `pipeline.data?.x ?? 0` — a confident "Expired: 0" on
  // the surface an operator trusts to tell them what has lapsed.
  it("pipeline 5xx while stats succeed: an error + Try again replaces the buckets, NEVER a false 'Expired: 0'", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), () =>
        jsonError("server.error", "pipeline down", { status: 500 }),
      ),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    // Stats landed, so the numbers region and the pipeline card's heading still render
    // — the failure is scoped to the one card that owns the failed query.
    expect(await screen.findByText("12")).toBeInTheDocument();
    expect(screen.getByText(/when documents expire/i)).toBeInTheDocument();

    await waitFor(() =>
      expect(screen.getByText(/couldn't load when your documents expire/i)).toBeInTheDocument(),
    );
    expect(screen.getByRole("button", { name: /try again/i })).toBeInTheDocument();

    // THE REGRESSION ITSELF: no bucket may render without loaded data. With the fix
    // reverted these three fail — the grid renders zeros for an org holding 1 expired COI.
    expect(screen.queryByRole("link", { name: /expired: 0 documents/i })).toBeNull();
    expect(screen.queryByText("Expired")).toBeNull();
    expect(screen.queryByText("90+ days")).toBeNull();
  });

  it("Try again refetches the pipeline and reveals the real counts", async () => {
    const pipelineSeq = sequencedResponses(
      () => jsonError("server.error", "blip", { status: 500 }),
      () => jsonOk(PIPELINE),
    );
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), () => pipelineSeq()),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    fireEvent.click(await screen.findByRole("button", { name: /try again/i }));

    // PIPELINE.expired is 1 — recovering to a NON-zero count is what proves the
    // earlier zeros would have been a lie rather than a coincidence.
    expect(
      await screen.findByRole("link", { name: /expired: 1 documents\. view them/i }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/couldn't load when your documents expire/i)).toBeNull();
  });

  it("pipeline still in flight after stats land: a skeleton, not hard zeros", async () => {
    const never = new Promise<void>(() => {});
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS)),
      http.get(url("/api/dashboard/expiry-pipeline"), async () => {
        await never;
        return jsonOk(PIPELINE);
      }),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk(ACTIVITY)),
    );

    renderWithProviders(<DashboardPage />, { auth: authedMe });

    expect(await screen.findByText("12")).toBeInTheDocument(); // stats resolved
    expect(
      screen.getByRole("status", { name: /loading when documents expire/i }),
    ).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /expired: 0 documents/i })).toBeNull();
  });
});

describe("DashboardPage — first-run welcome modal (#191)", () => {
  beforeEach(() => localStorage.clear());

  function seedDashboard() {
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonOk({ ...STATS, totalDocuments: 0, totalVendors: 0, anyVendorWithRequirements: false }),
      ),
      http.get(url("/api/dashboard/expiry-pipeline"), () => jsonOk(PIPELINE)),
      http.get(url("/api/dashboard/recent-activity"), () => jsonOk([])),
    );
  }

  it("auto-opens for a never-onboarded user, hides on close, and persists completion", async () => {
    seedDashboard();
    let completed = 0;
    server.use(
      http.post(url("/api/auth/complete-onboarding"), () => {
        completed++;
        return jsonOk(makeMe({ hasCompletedOnboarding: true }));
      }),
    );

    renderWithProviders(<DashboardPage />, { auth: makeMe({ hasCompletedOnboarding: false }) });

    expect(await screen.findByText(/stay audit-ready without the chase/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /skip the tour/i }));

    // The dismissal hides the modal within this mount (tourDismissed)...
    await waitFor(() =>
      expect(screen.queryByText(/stay audit-ready without the chase/i)).toBeNull(),
    );
    // ...and flips the server flag exactly once (idempotent persistence).
    await waitFor(() => expect(completed).toBe(1));
  });

  it("does NOT auto-open once onboarding is already complete", async () => {
    seedDashboard();
    renderWithProviders(<DashboardPage />, { auth: authedMe }); // hasCompletedOnboarding: true

    await screen.findByRole("heading", { name: /welcome, acme/i });
    expect(screen.queryByText(/stay audit-ready without the chase/i)).toBeNull();
  });

  it("'Restart tour' hand-off re-opens the modal even for an onboarded user, then clears the flag", async () => {
    seedDashboard();
    server.use(
      http.post(url("/api/auth/complete-onboarding"), () =>
        jsonOk(makeMe({ hasCompletedOnboarding: true })),
      ),
    );
    localStorage.setItem("cd_restart_tour", "1"); // Settings handed off via this flag

    renderWithProviders(<DashboardPage />, { auth: authedMe }); // already onboarded

    // The modal replays despite hasCompletedOnboarding === true...
    expect(await screen.findByText(/stay audit-ready without the chase/i)).toBeInTheDocument();
    // ...and the one-shot flag is consumed so a refresh doesn't re-trigger it.
    fireEvent.click(screen.getByRole("button", { name: /skip the tour/i }));
    await waitFor(() => expect(localStorage.getItem("cd_restart_tour")).toBeNull());
  });
});
