/**
 * Forgot-password page (#183) — submits the email and ALWAYS shows the same
 * neutral confirmation (the server returns 200 whether or not the email exists,
 * so the UI must never reveal account existence either).
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import ForgotPasswordPage from "./page";
import { renderWithProviders, server, url, jsonOk, jsonError } from "@/test";

describe("ForgotPasswordPage (#183)", () => {
  it("sends the email and shows the neutral 'check your email' confirmation", async () => {
    let captured: { email: string } | null = null;
    server.use(
      http.post(url("/api/auth/forgot-password"), async ({ request }) => {
        captured = (await request.json()) as { email: string };
        return jsonOk({ message: "If that email is registered, we've sent a link." });
      }),
    );
    renderWithProviders(<ForgotPasswordPage />, { auth: null });

    fireEvent.change(screen.getByLabelText(/email/i), { target: { value: "owner@acme.test" } });
    fireEvent.click(screen.getByRole("button", { name: /send reset link/i }));

    await waitFor(() => expect(screen.getByText(/check your email/i)).toBeInTheDocument());
    expect(captured).toEqual({ email: "owner@acme.test" });
  });

  // Anti-enumeration is preserved by the FIRST test (a 200 for any email → the
  // same neutral confirmation). A THROWN failure (network/429/5xx) is different:
  // the request did NOT succeed, so flipping to "check your email" would have the
  // user trust a link that never sent. FP-031: show the error, not false success.
  it("shows an error (not a false 'check your email') when the request fails", async () => {
    server.use(
      http.post(url("/api/auth/forgot-password"), () =>
        jsonError("server.error", "We couldn't send the email. Try again in a minute.", { status: 500 }),
      ),
    );
    renderWithProviders(<ForgotPasswordPage />, { auth: null });

    fireEvent.change(screen.getByLabelText(/email/i), { target: { value: "owner@acme.test" } });
    fireEvent.click(screen.getByRole("button", { name: /send reset link/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/couldn't send the email/i);
    expect(screen.queryByText(/check your email/i)).not.toBeInTheDocument();
  });
});
