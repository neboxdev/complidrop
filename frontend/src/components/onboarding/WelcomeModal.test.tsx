/**
 * WelcomeModal — 3-slide, skippable first-run intro on Base UI Dialog (#191).
 * #318 FP-046: an explicit dismissal (Skip / X / final CTA) completes the tour;
 * an incidental one (backdrop / Escape) only minimizes it.
 */
import { describe, it, expect, vi } from "vitest";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import { WelcomeModal } from "./WelcomeModal";
import { renderWithProviders, navState } from "@/test";

describe("WelcomeModal (#191)", () => {
  it("renders nothing when closed", () => {
    renderWithProviders(<WelcomeModal open={false} onComplete={vi.fn()} onMinimize={vi.fn()} />, { auth: null });
    expect(screen.queryByText(/stay audit-ready/i)).toBeNull();
  });

  it("walks the slides and finishes by routing to /vendors", async () => {
    const onComplete = vi.fn();
    const onMinimize = vi.fn();
    renderWithProviders(<WelcomeModal open onComplete={onComplete} onMinimize={onMinimize} />, { auth: null });

    // Slide 1 — the promise (now spells out COIs at first use, FP-044).
    expect(await screen.findByText(/stay audit-ready without the chase/i)).toBeInTheDocument();
    expect(screen.getByText(/insurance certificates \(cois\)/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /^next$/i }));

    // Slide 2 — the four-step workflow.
    expect(await screen.findByText(/four steps to covered/i)).toBeInTheDocument();
    expect(screen.getByText(/add a vendor/i)).toBeInTheDocument();
    expect(screen.getByText(/collect the document/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /^next$/i }));

    // Slide 3 — the CTA navigates AND completes (explicit close-press).
    fireEvent.click(await screen.findByRole("button", { name: /add your first vendor/i }));
    expect(navState.router.push).toHaveBeenCalledWith("/vendors");
    expect(onComplete).toHaveBeenCalledTimes(1);
    expect(onMinimize).not.toHaveBeenCalled();
  });

  it("Skip completes the tour (explicit) without advancing", async () => {
    const onComplete = vi.fn();
    const onMinimize = vi.fn();
    renderWithProviders(<WelcomeModal open onComplete={onComplete} onMinimize={onMinimize} />, { auth: null });

    fireEvent.click(await screen.findByRole("button", { name: /skip the tour/i }));
    await waitFor(() => expect(onComplete).toHaveBeenCalledTimes(1));
    expect(onMinimize).not.toHaveBeenCalled();
    expect(navState.router.push).not.toHaveBeenCalled();
  });

  it("Escape only minimizes — it does NOT complete the tour (FP-046)", async () => {
    const onComplete = vi.fn();
    const onMinimize = vi.fn();
    renderWithProviders(<WelcomeModal open onComplete={onComplete} onMinimize={onMinimize} />, { auth: null });

    const dialog = await screen.findByRole("dialog");
    fireEvent.keyDown(dialog, { key: "Escape", code: "Escape" });

    await waitFor(() => expect(onMinimize).toHaveBeenCalledTimes(1));
    expect(onComplete).not.toHaveBeenCalled();
  });
});
