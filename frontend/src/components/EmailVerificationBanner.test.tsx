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

  it("resends the verification email and toasts the server message", async () => {
    server.use(
      http.post(url("/api/auth/resend-verification"), () =>
        jsonOk({ message: "Verification email sent." }),
      ),
    );
    renderWithProviders(<EmailVerificationBanner email="owner@acme.test" />);

    fireEvent.click(screen.getByRole("button", { name: /resend/i }));

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Verification email sent."),
    );
  });

  it("shows a jargon-free toast and never leaks HTTP jargon on failure", async () => {
    server.use(
      http.post(url("/api/auth/resend-verification"), () =>
        jsonError("server.error", "Something went wrong. Try again.", { status: 502 }),
      ),
    );
    renderWithProviders(<EmailVerificationBanner email="owner@acme.test" />);

    fireEvent.click(screen.getByRole("button", { name: /resend/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    const msg = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(msg).not.toMatch(/bad gateway/i);
    expect(msg).not.toMatch(/502/);
    expect(msg).not.toMatch(/failed to fetch/i);
  });
});
