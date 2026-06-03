/**
 * Dashboard shell (#181) — pins the responsive-shell contract: a mobile
 * hamburger opens a nav drawer from which EVERY nav item + the org context is
 * reachable (the AC: "all nav is reachable at 390px"), the drawer closes when a
 * link is tapped, the active route is marked, and children still render.
 *
 * JSDOM applies no CSS, so the desktop `<aside>` (which is `hidden md:flex`)
 * stays in the DOM alongside the drawer — assertions about the drawer scope to
 * `getByRole("dialog")` via `within()` to stay unambiguous.
 */
import { describe, it, expect } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import DashboardLayout from "./layout";
import { renderWithProviders, authedMe } from "@/test";

const NAV_LABELS = [
  "Dashboard",
  "Documents",
  "Vendors",
  "Compliance rules",
  "Reminders",
  "Export",
  "Settings",
];

describe("DashboardLayout — responsive shell (#181)", () => {
  it("opens a mobile nav drawer with every nav item + org context reachable", async () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child content</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/dashboard" },
    );

    // Drawer is closed until the hamburger is tapped.
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(
      screen.getByRole("button", { name: /open navigation menu/i }),
    );

    const drawer = await waitFor(() => screen.getByRole("dialog"));
    for (const label of NAV_LABELS) {
      expect(
        within(drawer).getByRole("link", { name: label }),
      ).toBeInTheDocument();
    }
    // Org context (name + plan) renders in the drawer footer.
    expect(within(drawer).getByText("Acme Inc")).toBeInTheDocument();
    expect(within(drawer).getByText(/pro/i)).toBeInTheDocument();
  });

  it("closes the drawer when a nav link is tapped", async () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/dashboard" },
    );

    fireEvent.click(
      screen.getByRole("button", { name: /open navigation menu/i }),
    );
    const drawer = await waitFor(() => screen.getByRole("dialog"));
    fireEvent.click(within(drawer).getByRole("link", { name: "Documents" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("marks the active route with aria-current=page", () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/documents" },
    );

    // The desktop sidebar is always in the DOM (no CSS in JSDOM) and marks the
    // current route — a regression that dropped aria-current would fail here.
    const links = screen.getAllByRole("link", { name: "Documents" });
    expect(links.some((a) => a.getAttribute("aria-current") === "page")).toBe(
      true,
    );
  });

  it("renders its children", () => {
    renderWithProviders(
      <DashboardLayout>
        <div>hello child</div>
      </DashboardLayout>,
      { auth: authedMe },
    );
    expect(screen.getByText("hello child")).toBeInTheDocument();
  });

  it("closes the drawer on Escape", async () => {
    renderWithProviders(
      <DashboardLayout>
        <div>child</div>
      </DashboardLayout>,
      { auth: authedMe, pathname: "/dashboard" },
    );

    fireEvent.click(
      screen.getByRole("button", { name: /open navigation menu/i }),
    );
    const drawer = await waitFor(() => screen.getByRole("dialog"));
    fireEvent.keyDown(drawer, { key: "Escape", code: "Escape" });

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});
