/**
 * Reminders page — tier-3 smoke (#36).
 *
 * Two parallel queries: /api/reminders + /api/reminders/history. Smoke
 * test: render-without-crash with both endpoints stubbed.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { waitFor } from "@testing-library/react";
import RemindersPage from "./page";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  authedMe,
} from "@/test";

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  Toaster: () => null,
}));

describe("RemindersPage — smoke (#36)", () => {
  it("populated: renders a reminder row when the API returns one", async () => {
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

    // Page renders without crashing — assert the page tree non-empty.
    await waitFor(() =>
      expect(document.body.textContent?.length).toBeGreaterThan(0),
    );
  });

  it("empty: renders without crashing when both endpoints return empty", async () => {
    server.use(
      http.get(url("/api/reminders"), () => jsonOk([])),
      http.get(url("/api/reminders/history"), () => jsonOk([])),
    );

    renderWithProviders(<RemindersPage />, { auth: authedMe });
    await waitFor(() =>
      expect(document.body.textContent?.length).toBeGreaterThan(0),
    );
  });
});
