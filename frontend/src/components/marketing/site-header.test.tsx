/**
 * Marketing header mobile menu (#181) — pins the AC that the landing page's
 * nav is reachable on a phone. Below md the secondary nav (Event venues / FAQ /
 * Glossary) and the auth actions collapse behind a hamburger; this exercises
 * the drawer the way a phone visitor would.
 *
 * JSDOM applies no CSS, so the desktop inline nav stays in the DOM alongside
 * the drawer — drawer assertions scope to `getByRole("dialog")` via `within()`.
 */
import { describe, it, expect } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { MarketingHeader } from "./site-header";
import { renderWithProviders, authedMe } from "@/test";

describe("MarketingHeader — mobile menu (#181)", () => {
  it("anonymous: the hamburger opens a menu exposing the secondary nav + auth CTAs", async () => {
    renderWithProviders(<MarketingHeader />, { auth: null });

    expect(screen.queryByRole("dialog")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /open menu/i }));

    const menu = await waitFor(() => screen.getByRole("dialog"));
    // Pricing + Support must be reachable in the drawer — a phone is the primary
    // surface for a cold-email visitor, and price + a human are what they scan
    // for before signing up. (#195)
    expect(
      within(menu).getByRole("link", { name: /pricing/i }),
    ).toHaveAttribute("href", "/#pricing");
    expect(
      within(menu).getByRole("link", { name: /support/i }),
    ).toHaveAttribute("href", "/contact");
    expect(
      within(menu).getByRole("link", { name: /event venues/i }),
    ).toBeInTheDocument();
    expect(within(menu).getByRole("link", { name: /faq/i })).toBeInTheDocument();
    expect(
      within(menu).getByRole("link", { name: /glossary/i }),
    ).toBeInTheDocument();
    expect(
      within(menu).getByRole("link", { name: /log in/i }),
    ).toHaveAttribute("href", "/login");
    expect(
      within(menu).getByRole("link", { name: /get started/i }),
    ).toHaveAttribute("href", "/register");
  });

  it("authenticated: the menu swaps the auth CTAs for a single dashboard link", async () => {
    renderWithProviders(<MarketingHeader />, { auth: authedMe });

    fireEvent.click(screen.getByRole("button", { name: /open menu/i }));
    const menu = await waitFor(() => screen.getByRole("dialog"));
    expect(
      within(menu).getByRole("link", { name: /go to dashboard/i }),
    ).toHaveAttribute("href", "/dashboard");
    expect(
      within(menu).queryByRole("link", { name: /get started/i }),
    ).toBeNull();
  });

  it("closes the menu when a nav link is tapped", async () => {
    renderWithProviders(<MarketingHeader />, { auth: null });

    fireEvent.click(screen.getByRole("button", { name: /open menu/i }));
    const menu = await waitFor(() => screen.getByRole("dialog"));
    fireEvent.click(within(menu).getByRole("link", { name: /faq/i }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("closes the menu on Escape", async () => {
    renderWithProviders(<MarketingHeader />, { auth: null });

    fireEvent.click(screen.getByRole("button", { name: /open menu/i }));
    const menu = await waitFor(() => screen.getByRole("dialog"));
    fireEvent.keyDown(menu, { key: "Escape", code: "Escape" });

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});
