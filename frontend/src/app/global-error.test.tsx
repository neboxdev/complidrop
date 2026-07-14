/**
 * Pins the App Router global error boundary (ADR 0037):
 *   1. It reports the technical error to Sentry.
 *   2. The user-facing copy follows the #77 / #254 error-copy policy — it shows
 *      GENERIC_FALLBACK_MESSAGE and NEVER the raw error message / HTTP jargon.
 *   3. "Try again" calls the Next-provided reset().
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import * as Sentry from "@sentry/nextjs";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import GlobalError from "./global-error";

vi.mock("@sentry/nextjs", () => ({ captureException: vi.fn() }));

describe("GlobalError", () => {
  beforeEach(() => {
    vi.mocked(Sentry.captureException).mockClear();
  });

  it("reports the error to Sentry", () => {
    const error = new Error("Cannot read properties of undefined (reading 'foo')");
    render(<GlobalError error={error} reset={vi.fn()} />);
    expect(Sentry.captureException).toHaveBeenCalledWith(error);
  });

  it("shows the friendly fallback copy, never the raw error / HTTP jargon", () => {
    const error = new Error("502 Bad Gateway — TypeError: Failed to fetch");
    render(<GlobalError error={error} reset={vi.fn()} />);

    expect(screen.getByText(GENERIC_FALLBACK_MESSAGE)).toBeInTheDocument();
    // The raw render-error string must not reach the screen.
    expect(document.body).not.toHaveTextContent(/bad gateway/i);
    expect(document.body).not.toHaveTextContent(/failed to fetch/i);
    expect(document.body).not.toHaveTextContent(/typeerror/i);
    expect(document.body).not.toHaveTextContent(/\b502\b/);
  });

  it("calls reset() when the user clicks Try again", () => {
    const reset = vi.fn();
    render(<GlobalError error={new Error("boom")} reset={reset} />);
    fireEvent.click(screen.getByRole("button", { name: /try again/i }));
    expect(reset).toHaveBeenCalledTimes(1);
  });
});
