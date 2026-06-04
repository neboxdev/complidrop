/**
 * WelcomeModal — 3-slide, skippable first-run intro on Base UI Dialog (#191).
 */
import { describe, it, expect, vi } from "vitest";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import { WelcomeModal } from "./WelcomeModal";
import { renderWithProviders, navState } from "@/test";

describe("WelcomeModal (#191)", () => {
  it("renders nothing when closed", () => {
    renderWithProviders(<WelcomeModal open={false} onClose={vi.fn()} />, { auth: null });
    expect(screen.queryByText(/stay audit-ready/i)).toBeNull();
  });

  it("walks the slides and finishes by routing to /vendors", async () => {
    const onClose = vi.fn();
    renderWithProviders(<WelcomeModal open onClose={onClose} />, { auth: null });

    // Slide 1 — the promise.
    expect(await screen.findByText(/stay audit-ready without the chase/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /^next$/i }));

    // Slide 2 — the four-step workflow.
    expect(await screen.findByText(/four steps to covered/i)).toBeInTheDocument();
    expect(screen.getByText(/add a vendor/i)).toBeInTheDocument();
    expect(screen.getByText(/collect the document/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /^next$/i }));

    // Slide 3 — the CTA navigates AND closes.
    fireEvent.click(await screen.findByRole("button", { name: /add your first vendor/i }));
    expect(navState.router.push).toHaveBeenCalledWith("/vendors");
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("Skip closes via onClose without advancing", async () => {
    const onClose = vi.fn();
    renderWithProviders(<WelcomeModal open onClose={onClose} />, { auth: null });

    fireEvent.click(await screen.findByRole("button", { name: /skip the tour/i }));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(navState.router.push).not.toHaveBeenCalled();
  });
});
