/**
 * GetStartedChecklist — data-driven first-run checklist that auto-hides at 100% (#191).
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen } from "@testing-library/react";
import {
  GetStartedChecklist,
  useOnboardingChecklist,
  type OnboardingChecklist,
} from "./GetStartedChecklist";
import { renderWithProviders, server, url, jsonOk, authedMe } from "@/test";

// Harness that drives GetStartedChecklist from the real data-derivation hook, so
// we can pin the API-shape → per-step `done` mapping (#191 "ticks from real data").
function ChecklistHarness() {
  const checklist = useOnboardingChecklist();
  return <GetStartedChecklist checklist={checklist} />;
}

function makeChecklist(done: boolean[]): OnboardingChecklist {
  const labels = [
    { key: "vendor", label: "Add your first vendor", href: "/vendors" },
    { key: "requirements", label: "Choose what they must prove", href: "/vendors" },
    { key: "document", label: "Collect a document", href: "/documents" },
    { key: "reminders", label: "Expiry reminders are on", href: "/reminders" },
  ];
  const steps = labels.map((l, i) => ({ ...l, hint: "", done: done[i] }));
  const completedCount = steps.filter((s) => s.done).length;
  return { steps, completedCount, isComplete: completedCount === steps.length, isLoading: false };
}

describe("GetStartedChecklist (#191)", () => {
  it("shows progress and renders incomplete steps as links, completed ones as struck-through", () => {
    renderWithProviders(
      <GetStartedChecklist checklist={makeChecklist([false, false, false, true])} />,
      { auth: null },
    );
    expect(screen.getByText("Get started")).toBeInTheDocument();
    expect(screen.getByText("1 of 4")).toBeInTheDocument();

    // Incomplete steps are actionable links to where they're done.
    expect(screen.getByRole("link", { name: /add your first vendor/i })).toHaveAttribute("href", "/vendors");
    expect(screen.getByRole("link", { name: /collect a document/i })).toHaveAttribute("href", "/documents");

    // The pre-checked reminders step is NOT a link (it's already done).
    expect(screen.queryByRole("link", { name: /expiry reminders are on/i })).toBeNull();
  });

  it("auto-hides once every step is done", () => {
    renderWithProviders(
      <GetStartedChecklist checklist={makeChecklist([true, true, true, true])} />,
      { auth: null },
    );
    expect(screen.queryByText("Get started")).toBeNull();
  });

  it("derives each step's done-state from /api/dashboard/stats (real data → ticks)", async () => {
    // One vendor, no requirement checklist assigned, no documents yet.
    server.use(
      http.get(url("/api/dashboard/stats"), () =>
        jsonOk({
          totalDocuments: 0,
          compliant: 0,
          nonCompliant: 0,
          expiringSoon: 0,
          expired: 0,
          pendingExtraction: 0,
          totalVendors: 1,
          anyVendorWithRequirements: false,
          complianceRate: 0,
        }),
      ),
    );

    renderWithProviders(<ChecklistHarness />, { auth: authedMe });

    // vendor=done, requirements=NOT done, document=NOT done, reminders=done ⇒ 2 of 4.
    expect(await screen.findByText("2 of 4")).toBeInTheDocument();
    // The done vendor step is struck-through (not a link); the two undone steps are links.
    expect(screen.queryByRole("link", { name: /add your first vendor/i })).toBeNull();
    expect(screen.getByRole("link", { name: /choose what they must prove/i })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /collect a document/i })).toBeInTheDocument();
  });

  it("stays hidden while stats are still loading (no cold-cache flash)", () => {
    const settled = new Promise<void>(() => {}); // never resolves
    server.use(
      http.get(url("/api/dashboard/stats"), async () => {
        await settled;
        return jsonOk({});
      }),
    );

    renderWithProviders(<ChecklistHarness />, { auth: authedMe });
    expect(screen.queryByText("Get started")).toBeNull();
  });
});

describe("GetStartedChecklist — plan-aware document hint (#261)", () => {
  // Vendor upload links are a Pro entitlement (the server 403s link generation
  // on Free), so the "Collect a document" hint must not recommend them to a
  // Free org. The subscription default handler (handlers.ts) answers 401 —
  // entitlement unknown — which must also fall back to the plan-safe copy.
  const STATS_NO_DOCS = {
    totalDocuments: 0,
    compliant: 0,
    nonCompliant: 0,
    expiringSoon: 0,
    expired: 0,
    pendingExtraction: 0,
    totalVendors: 1,
    anyVendorWithRequirements: false,
    complianceRate: 0,
  };

  function subscription(hasVendorPortal: boolean) {
    return jsonOk({
      plan: hasVendorPortal ? "pro" : "free",
      status: "active",
      documentLimit: hasVendorPortal ? null : 5,
      documentsUsed: 0,
      hasVendorPortal,
      currentPeriodEnd: null,
      extractionSpend: 0,
    });
  }

  it("recommends the upload link when the plan includes the portal", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS_NO_DOCS)),
      http.get(url("/api/billing/subscription"), () => subscription(true)),
    );

    renderWithProviders(<ChecklistHarness />, { auth: authedMe });

    // findByText waits for the subscription to resolve — the upload-link
    // phrasing only renders once hasVendorPortal=true lands.
    expect(
      await screen.findByText(/upload a coi, or send the vendor an upload link/i),
    ).toBeInTheDocument();
  });

  it("stays plan-safe for a Free org (no upload-link recommendation)", async () => {
    server.use(
      http.get(url("/api/dashboard/stats"), () => jsonOk(STATS_NO_DOCS)),
      http.get(url("/api/billing/subscription"), () => subscription(false)),
    );

    renderWithProviders(<ChecklistHarness />, { auth: authedMe });

    expect(await screen.findByText(/upload a coi you have on file/i)).toBeInTheDocument();
    expect(screen.queryByText(/send the vendor an upload link/i)).toBeNull();
  });

  it("falls back to the plan-safe hint while the entitlement is unknown", async () => {
    // No subscription override — the harness default answers 401 (anonymous),
    // so the hook never learns the entitlement and must not recommend a
    // feature the org may not have.
    server.use(http.get(url("/api/dashboard/stats"), () => jsonOk(STATS_NO_DOCS)));

    renderWithProviders(<ChecklistHarness />, { auth: authedMe });

    expect(await screen.findByText(/upload a coi you have on file/i)).toBeInTheDocument();
    expect(screen.queryByText(/send the vendor an upload link/i)).toBeNull();
  });
});
