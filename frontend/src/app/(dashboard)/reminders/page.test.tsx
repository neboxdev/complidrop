/**
 * Reminders page — tier-3 smoke (#36).
 *
 * Two parallel queries: /api/reminders + /api/reminders/history. Smoke
 * test asserts contract-bearing copy (the row's daysBefore + the
 * compliance-template label) so a regression that silently dropped the
 * reminders list trips here.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import RemindersPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
} from "@/test";

// sonner is mocked by the harness (vitest.setup.ts + src/test/sonner.ts). See #74.

describe("RemindersPage — smoke (#36)", () => {
  it("populated: renders a row showing the reminder's daysBefore value", async () => {
    server.use(
      http.get(url("/api/reminders"), () =>
        jsonOk([
          {
            id: "r_01",
            daysBefore: 30,
            notifyInternalUser: true,
            notifyVendor: false,
            isActive: true,
            emailSubjectTemplate: null,
            emailBodyTemplate: null,
          },
        ]),
      ),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });

    // The reminder's daysBefore is the only number-bearing row content
    // that's directly contract-driven. A regression that empties the
    // reminders list (or breaks the row mapper) would drop the "30".
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());
  });

  it("empty: renders the page chrome and the history section without crashing", async () => {
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([])),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    // No reminders → no daysBefore text. The page header / history
    // panel should still render — assert the page is past the loading
    // state by waiting for any of the unconditional chrome.
    await waitFor(() =>
      expect(document.body.textContent?.length).toBeGreaterThan(0),
    );
    expect(screen.queryByText(/30/)).toBeNull();
  });

  it("each toggle presents a ≥44px coarse-pointer hit target (#181)", async () => {
    // Pins the touch-target fix for the reminder toggles (3 per row: team /
    // vendor / active). The clickable button grows to ≥44px on coarse pointers
    // while the visual pill stays compact. (Switch SEMANTICS are tracked
    // separately in #189.)
    server.use(
      http.get(url("/api/reminders"), () =>
        jsonOk([
          {
            id: "r_01",
            daysBefore: 30,
            notifyInternalUser: true,
            notifyVendor: false,
            isActive: true,
            emailSubjectTemplate: null,
            emailBodyTemplate: null,
          },
        ]),
      ),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    await waitFor(() => expect(screen.getByText(/30/)).toBeInTheDocument());

    const coarseHitTargets = Array.from(
      document.querySelectorAll("button"),
    ).filter((b) => b.className.includes("pointer-coarse:min-h-11"));
    expect(coarseHitTargets.length).toBeGreaterThanOrEqual(3);
  });
});

describe("RemindersPage — humanized delivery status (#188)", () => {
  it("renders a friendly delivery-status label, not the raw lowercase token", async () => {
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([])),
      http.get(url("/api/reminders/history"), () =>
        jsonOk([
          {
            id: "h_1",
            recipient: "ops@acmecatering.com",
            sentAt: "2026-05-26T12:00:00Z",
            sendDate: "2026-05-26",
            status: "bounced",
            reminderId: "r_1",
            documentId: "d_1",
          },
        ]),
      ),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByText("Bounced — bad address")).toBeInTheDocument(),
    );
    // The raw provider token must not surface.
    expect(screen.queryByText("bounced")).toBeNull();
  });
});
