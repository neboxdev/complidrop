/**
 * StaleDataBanner — component contract pinned (#97).
 *
 * The banner is the discreet "couldn't refresh" indicator the list +
 * detail pages render above their cached content when a polling
 * refetch failed. Three things matter at the component layer:
 *
 *   1. role="status" + aria-live="polite" — distinguishes from the
 *      full-page error card's role="alert". Polite announcements let
 *      assistive tech finish reading the current element before
 *      announcing the banner, matching the "subtle, doesn't grab
 *      focus" visual treatment. A regression that flipped this to
 *      role="alert" would silently make every poll failure interrupt
 *      the user's reading.
 *
 *   2. Message fallback discipline: server-message-wins-when-present,
 *      otherwise GENERIC_FALLBACK_MESSAGE. No raw "Failed to fetch",
 *      no statusText leaks — matches the api.ts contract (#77).
 *
 *   3. Retry wire-up: clicking Try again must call the passed
 *      onRetry, and isRetrying disables the button.
 */
import { describe, it, expect, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { StaleDataBanner } from "./StaleDataBanner";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

describe("StaleDataBanner — contract (#97)", () => {
  it("renders the headline noun and a polite-live status role", () => {
    render(<StaleDataBanner onRetry={() => {}} noun="documents" />);

    const status = screen.getByRole("status");
    expect(status).toHaveAttribute("aria-live", "polite");
    expect(status).toHaveTextContent(/couldn't refresh documents/i);
  });

  it("default noun fallback renders 'data' when caller omits noun", () => {
    // Pins the default-arg contract — a future refactor that drops
    // the default would still pass the previous test (which passes
    // an explicit `noun`), so this catches that regression
    // independently. The page integration relies on the page-level
    // noun overrides, but library consumers may not pass one.
    render(<StaleDataBanner onRetry={() => {}} />);
    expect(screen.getByRole("status")).toHaveTextContent(
      /couldn't refresh data/i,
    );
  });

  it("renders the server-provided message when present", () => {
    render(
      <StaleDataBanner
        onRetry={() => {}}
        message="Upstream rate-limited."
        noun="documents"
      />,
    );
    expect(screen.getByRole("status")).toHaveTextContent(
      "Upstream rate-limited.",
    );
  });

  it("falls back to GENERIC_FALLBACK_MESSAGE when message is null", () => {
    render(
      <StaleDataBanner onRetry={() => {}} message={null} noun="documents" />,
    );
    expect(screen.getByRole("status")).toHaveTextContent(
      GENERIC_FALLBACK_MESSAGE,
    );
  });

  it("falls back to GENERIC_FALLBACK_MESSAGE when message is whitespace-only", () => {
    // Mirrors api.ts's `body.error?.message?.trim()` discipline — a
    // server that returns `{ error: { message: "  " } }` must NOT
    // surface an empty banner. Pinning here catches a regression
    // that swaps the falsy-trim guard for a bare `?? fallback` (which
    // would let the whitespace string through).
    render(
      <StaleDataBanner onRetry={() => {}} message="   " noun="documents" />,
    );
    expect(screen.getByRole("status")).toHaveTextContent(
      GENERIC_FALLBACK_MESSAGE,
    );
  });

  it("falls back to GENERIC_FALLBACK_MESSAGE when message is omitted entirely (undefined)", () => {
    render(<StaleDataBanner onRetry={() => {}} noun="documents" />);
    expect(screen.getByRole("status")).toHaveTextContent(
      GENERIC_FALLBACK_MESSAGE,
    );
  });

  it("never leaks browser TypeError / raw statusText into the banner copy (#77 jargon-free policy)", () => {
    // The banner is one of two sites that surface a query.error
    // message to users; the other (the full-page error card) already
    // pins this invariant. Pinning here too prevents a future
    // refactor that special-cases banner copy from accidentally
    // re-introducing jargon.
    render(
      <StaleDataBanner
        onRetry={() => {}}
        // Hypothetical leaks the api.ts wrapper is meant to prevent —
        // if the banner is ever wired up to a bare-fetch source that
        // bypasses fetchOrFriendlyThrow, the message could carry one
        // of these. Pin that the banner doesn't render them when the
        // server returns the friendly fallback string.
        message={GENERIC_FALLBACK_MESSAGE}
        noun="documents"
      />,
    );
    const status = screen.getByRole("status");
    expect(status).toHaveTextContent(GENERIC_FALLBACK_MESSAGE);
    expect(status).not.toHaveTextContent(/failed to fetch/i);
    expect(status).not.toHaveTextContent(/typeerror/i);
    expect(status).not.toHaveTextContent(/bad gateway/i);
  });

  it("clicking Try again invokes onRetry exactly once", () => {
    const onRetry = vi.fn();
    render(<StaleDataBanner onRetry={onRetry} noun="documents" />);

    fireEvent.click(screen.getByRole("button", { name: /try again/i }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it("isRetrying disables the Try-again button so parallel refetches can't queue", () => {
    const onRetry = vi.fn();
    render(
      <StaleDataBanner onRetry={onRetry} isRetrying={true} noun="documents" />,
    );

    const button = screen.getByRole("button", { name: /try again/i });
    expect(button).toBeDisabled();

    // Disabled buttons swallow clicks at the React layer — onRetry must
    // not fire. (RTL's fireEvent.click on a disabled button is a no-op
    // for onClick handlers; pinned explicitly because TanStack Query's
    // isFetching toggles between calls and a regression that dropped
    // the disabled attribute would otherwise pass silently.)
    fireEvent.click(button);
    expect(onRetry).not.toHaveBeenCalled();
  });

  it("isRetrying surfaces a visual + a11y loading affordance (aria-busy + animate-spin)", () => {
    // Two regression surfaces are pinned together:
    //   - The button's aria-busy attribute mirrors the visual
    //     spinning-icon signal for screen-reader users. A regression
    //     that dropped this would leave assistive-tech users with no
    //     signal that the retry is in flight (the disabled state
    //     alone reads as "this button is unavailable", not "this
    //     button is working").
    //   - The icon's animate-spin class is the sighted-user signal.
    //     A regression that dropped the class would leave the button
    //     visually frozen during the retry.
    // Both must hold whenever isRetrying=true. (#97 review —
    // test-quality reviewer)
    render(
      <StaleDataBanner onRetry={() => {}} isRetrying={true} noun="documents" />,
    );
    const button = screen.getByRole("button", { name: /try again/i });
    expect(button).toHaveAttribute("aria-busy", "true");

    // The button wraps the RotateCw icon — query its only SVG child
    // and pin the animate-spin class. Container-scoped via the
    // button so a future refactor that adds a second SVG elsewhere
    // in the banner doesn't accidentally satisfy this assertion.
    const icon = button.querySelector("svg");
    expect(icon).not.toBeNull();
    expect(icon).toHaveClass("animate-spin");
  });

  it("isRetrying=false leaves the button non-busy + the icon non-spinning (negative pair)", () => {
    // Pairs with the isRetrying=true test so a regression that
    // hard-coded aria-busy="true" or always-spinning would fail
    // here. Without the negative test, the previous case alone
    // would pass even if isRetrying was effectively ignored.
    render(
      <StaleDataBanner onRetry={() => {}} isRetrying={false} noun="documents" />,
    );
    const button = screen.getByRole("button", { name: /try again/i });
    // React renders boolean false attribute as the literal string
    // "false" (because aria-* attributes are stringly-typed). Either
    // way, the assertion is "NOT true".
    expect(button).not.toHaveAttribute("aria-busy", "true");
    expect(button).not.toBeDisabled();

    const icon = button.querySelector("svg");
    expect(icon).not.toBeNull();
    expect(icon).not.toHaveClass("animate-spin");
  });
});
