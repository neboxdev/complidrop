/**
 * Sheet primitive (#181) — pins the drawer contract the responsive app shell
 * and marketing mobile menu both depend on: closed by default (no nav in the
 * DOM), opens to an accessible `role="dialog"` named by its title, and closes
 * via both the explicit Close affordance and Escape.
 */
import { describe, it, expect } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import {
  Sheet,
  SheetTrigger,
  SheetContent,
  SheetTitle,
  SheetClose,
} from "./sheet";

function Harness({ side }: { side?: "left" | "right" }) {
  return (
    <Sheet>
      <SheetTrigger aria-label="Open menu">menu</SheetTrigger>
      <SheetContent side={side}>
        <SheetTitle className="sr-only">Navigation</SheetTitle>
        <a href="/dashboard">Dashboard</a>
        <SheetClose aria-label="Close menu">close</SheetClose>
      </SheetContent>
    </Sheet>
  );
}

describe("Sheet (#181)", () => {
  it("is closed initially — neither the dialog nor its links are in the DOM", () => {
    render(<Harness />);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.queryByRole("link", { name: /dashboard/i })).toBeNull();
  });

  it("opens on trigger click and exposes an accessible dialog named by its title", async () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: /open menu/i }));
    const dialog = await waitFor(() => screen.getByRole("dialog"));
    expect(dialog).toHaveAccessibleName("Navigation");
    expect(
      within(dialog).getByRole("link", { name: /dashboard/i }),
    ).toBeInTheDocument();
  });

  it("closes via the explicit Close affordance", async () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: /open menu/i }));
    await waitFor(() => screen.getByRole("dialog"));
    fireEvent.click(screen.getByRole("button", { name: /close menu/i }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("closes on Escape", async () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: /open menu/i }));
    const dialog = await waitFor(() => screen.getByRole("dialog"));
    fireEvent.keyDown(dialog, { key: "Escape", code: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});
