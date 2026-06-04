/**
 * GetStartedChecklist — data-driven first-run checklist that auto-hides at 100% (#191).
 */
import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import { GetStartedChecklist, type OnboardingChecklist } from "./GetStartedChecklist";
import { renderWithProviders } from "@/test";

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
});
