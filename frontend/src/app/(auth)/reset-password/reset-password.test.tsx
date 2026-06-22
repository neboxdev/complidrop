/**
 * Reset-password client (#183) — redeems the ?token= and sets a new password,
 * validates strength + confirmation client-side, and surfaces server errors
 * jargon-free. Missing token → a clear invalid-link state with no request.
 */
import { describe, it, expect, beforeEach } from "vitest";
import { http } from "msw";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import { ResetPasswordClient } from "./reset-password-client";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  resetSonner,
} from "@/test";

describe("ResetPasswordClient (#183)", () => {
  beforeEach(() => resetSonner());

  it("submits the new password with the token and shows the success state", async () => {
    let captured: { token: string; newPassword: string } | null = null;
    server.use(
      http.post(url("/api/auth/reset-password"), async ({ request }) => {
        captured = (await request.json()) as typeof captured;
        return jsonOk({ message: "Your password has been reset." });
      }),
    );
    renderWithProviders(<ResetPasswordClient />, { searchParams: { token: "good-token" } });

    fireEvent.change(screen.getByLabelText(/^new password$/i), { target: { value: "BrandNewPass123" } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: "BrandNewPass123" } });
    fireEvent.click(screen.getByRole("button", { name: /reset password/i }));

    await waitFor(() => expect(captured).toEqual({ token: "good-token", newPassword: "BrandNewPass123" }));
    await waitFor(() => expect(screen.getByText(/password reset/i)).toBeInTheDocument());
  });

  it("blocks weak passwords and mismatches client-side (no request)", async () => {
    let called = false;
    server.use(
      http.post(url("/api/auth/reset-password"), () => {
        called = true;
        return jsonOk({ message: "x" });
      }),
    );
    renderWithProviders(<ResetPasswordClient />, { searchParams: { token: "t" } });

    fireEvent.change(screen.getByLabelText(/^new password$/i), { target: { value: "short" } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: "short" } });
    fireEvent.click(screen.getByRole("button", { name: /reset password/i }));

    await waitFor(() => expect(screen.getByText(/at least 12 characters/i)).toBeInTheDocument());
    expect(called).toBe(false);
  });

  it("swaps to the expired-link card with a 'Request a new link' path when the token is invalid/expired (FP-032)", async () => {
    server.use(
      http.post(url("/api/auth/reset-password"), () =>
        jsonError("auth.reset_invalid", "This reset link is invalid or has expired. Request a new one.", { status: 400 }),
      ),
    );
    renderWithProviders(<ResetPasswordClient />, { searchParams: { token: "stale" } });

    fireEvent.change(screen.getByLabelText(/^new password$/i), { target: { value: "BrandNewPass123" } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: "BrandNewPass123" } });
    fireEvent.click(screen.getByRole("button", { name: /reset password/i }));

    // Terminal token error swaps the form for a recovery card — not a vanishing
    // toast over a now-dead form.
    expect(await screen.findByText(/this reset link has expired/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /request a new link/i })).toHaveAttribute("href", "/forgot-password");
    expect(screen.queryByRole("button", { name: /reset password/i })).toBeNull();
  });

  it("shows an invalid-link state with no request when the token is missing", () => {
    let called = false;
    server.use(
      http.post(url("/api/auth/reset-password"), () => {
        called = true;
        return jsonOk({ message: "x" });
      }),
    );
    renderWithProviders(<ResetPasswordClient />, { searchParams: {} });

    expect(screen.getByText(/invalid reset link/i)).toBeInTheDocument();
    expect(called).toBe(false);
  });
});
