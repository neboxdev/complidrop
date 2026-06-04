/**
 * Verify-email landing (#184) — pins the three outcomes the page renders from
 * the `?token=`: success (token redeemed), error (server message surfaced,
 * jargon-free), and missing-token (no network call, clear guidance). Drives the
 * real useVerifyEmail hook → api client → MSW so an envelope/mapping regression
 * fails here.
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { screen, waitFor } from "@testing-library/react";
import { VerifyEmailClient } from "./verify-email-client";
import { ME_KEY } from "@/hooks/useAuth";
import { renderWithProviders, server, url, jsonOk, jsonError, makeMe } from "@/test";

describe("VerifyEmailClient (#184)", () => {
  it("redeems a valid token and shows the confirmed state", async () => {
    server.use(
      http.post(url("/api/auth/verify-email"), () =>
        jsonOk({ message: "Email confirmed. Thanks!" }),
      ),
    );

    renderWithProviders(<VerifyEmailClient />, { searchParams: { token: "good-token" } });

    await waitFor(() => expect(screen.getByText("Email confirmed")).toBeInTheDocument());
    expect(screen.getByText(/thanks/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /continue to dashboard/i })).toBeInTheDocument();
  });

  it("invalidates the Me cache on success so the dashboard banner clears (#184 ↔ #182 seam)", async () => {
    // The SOLE mechanism that drops the 'confirm your email' banner after a user
    // verifies (the layout reads me.data.emailVerified). A regression removing
    // useVerifyEmail's invalidateQueries would leave a just-verified user staring
    // at the banner — pinned here.
    server.use(
      http.post(url("/api/auth/verify-email"), () => jsonOk({ message: "Email confirmed. Thanks!" })),
    );
    const { queryClient } = renderWithProviders(<VerifyEmailClient />, {
      auth: makeMe({ emailVerified: false }),
      searchParams: { token: "good-token" },
    });

    await waitFor(() => expect(screen.getByText("Email confirmed")).toBeInTheDocument());
    await waitFor(() =>
      expect(queryClient.getQueryState([...ME_KEY])?.isInvalidated).toBe(true),
    );
  });

  it("surfaces the server's error message on an expired/invalid token", async () => {
    server.use(
      http.post(url("/api/auth/verify-email"), () =>
        jsonError("auth.verification_expired", "This verification link has expired.", {
          status: 400,
        }),
      ),
    );

    renderWithProviders(<VerifyEmailClient />, { searchParams: { token: "stale" } });

    await waitFor(() =>
      expect(screen.getByText(/couldn't confirm your email/i)).toBeInTheDocument(),
    );
    expect(screen.getByText("This verification link has expired.")).toBeInTheDocument();
    // No HTTP jargon leaks into the card.
    expect(screen.queryByText(/bad request/i)).toBeNull();
    expect(screen.queryByText(/\b400\b/)).toBeNull();
  });

  it("shows an invalid-link state with no network call when the token is missing", () => {
    let called = false;
    server.use(
      http.post(url("/api/auth/verify-email"), () => {
        called = true;
        return jsonOk({ message: "should not happen" });
      }),
    );

    renderWithProviders(<VerifyEmailClient />, { searchParams: {} });

    expect(screen.getByText(/invalid verification link/i)).toBeInTheDocument();
    expect(called).toBe(false);
  });
});
