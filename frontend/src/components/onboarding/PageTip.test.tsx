/**
 * PageTip — dismissible, device-local first-visit tip (#191).
 */
import { describe, it, expect, beforeEach } from "vitest";
import { fireEvent, screen } from "@testing-library/react";
import { PageTip } from "./PageTip";
import { renderWithProviders } from "@/test";

describe("PageTip (#191)", () => {
  beforeEach(() => localStorage.clear());

  it("shows on first visit, then dismissing it hides it and persists to localStorage", () => {
    const { unmount } = renderWithProviders(
      <PageTip id="docs_test" title="A helpful tip">
        Tip body content
      </PageTip>,
      { auth: null },
    );
    expect(screen.getByText("A helpful tip")).toBeInTheDocument();
    expect(screen.getByText(/tip body content/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /dismiss tip/i }));
    expect(screen.queryByText("A helpful tip")).toBeNull();

    // Persisted: a fresh mount stays hidden.
    unmount();
    renderWithProviders(
      <PageTip id="docs_test" title="A helpful tip">
        Tip body content
      </PageTip>,
      { auth: null },
    );
    expect(screen.queryByText("A helpful tip")).toBeNull();
  });

  it("stays hidden when already dismissed", () => {
    localStorage.setItem("cd_tip_docs_test", "1");
    renderWithProviders(
      <PageTip id="docs_test" title="A helpful tip">
        body
      </PageTip>,
      { auth: null },
    );
    expect(screen.queryByText("A helpful tip")).toBeNull();
  });
});
