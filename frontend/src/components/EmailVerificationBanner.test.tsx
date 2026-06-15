/**
 * EmailVerificationBanner (#184) — pins the "confirm your email" call-to-action:
 * it names the address, the Resend button hits POST /api/auth/resend-verification
 * and surfaces the server's success message, and an error path shows a
 * jargon-free toast (never raw HTTP status text, per the frontend
 * error-message policy).
 */
import { describe, it, expect, beforeEach } from "vitest";
import { http } from "msw";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import { EmailVerificationBanner } from "./EmailVerificationBanner";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  toastSuccess,
  toastError,
  resetSonner,
} from "@/test";

describe("EmailVerificationBanner (#184)", () => {
  beforeEach(() => resetSonner());

  it("names the address and asks the user to confirm it", () => {
    renderWithProviders(<EmailVerificationBanner email="owner@acme.test" />);
    expect(screen.getByText("owner@acme.test")).toBeInTheDocument();
    expect(screen.getByRole("region", { name: /confirm your email/i })).toBeInTheDocument();
  });

  it("resends the verification email and toasts the SERVER's message (not the client fallback)", async () => {
    // Distinct from the component's "Verification email sent." fallback, so the
    // assertion proves the server message was actually surfaced — a regression
    // that dropped `res.message` would fail here instead of passing vacuously.
    server.use(
      http.post(url("/api/auth/resend-verification"), () =>
        jsonOk({ message: "We just emailed you a fresh link." }),
      ),
    );
    renderWithProviders(<EmailVerificationBanner email="owner@acme.test" />);

    fireEvent.click(screen.getByRole("button", { name: /resend/i }));

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("We just emailed you a fresh link."),
    );
  });

  it("surfaces the SERVER's failure message (not the client fallback) and never leaks HTTP jargon", async () => {
    // The mocked message is deliberately DISTINCT from GENERIC_FALLBACK_MESSAGE so this fails if the
    // banner ever dropped err.message and always toasted the fallback (matches #249's real 502 copy).
    const serverMsg = "We couldn't send your confirmation email just now. Please try again in a few minutes.";
    server.use(
      http.post(url("/api/auth/resend-verification"), () =>
        jsonError("email.send_failed", serverMsg, { status: 502 }),
      ),
    );
    renderWithProviders(<EmailVerificationBanner email="owner@acme.test" />);

    fireEvent.click(screen.getByRole("button", { name: /resend/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith(serverMsg));
    const msg = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(msg).not.toMatch(/bad gateway/i);
    expect(msg).not.toMatch(/502/);
    expect(msg).not.toMatch(/failed to fetch/i);
  });
});
