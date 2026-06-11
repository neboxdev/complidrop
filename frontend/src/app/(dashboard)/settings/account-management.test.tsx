/**
 * Account management (#183) — Security section (change password / change email)
 * and Danger zone (export / delete). Drives the real hooks → api client → MSW so
 * envelope/mapping regressions fail here. Error copy must stay jargon-free.
 */
import { describe, it, expect, beforeEach, vi } from "vitest";
import { http } from "msw";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import { SecuritySection, DangerZone } from "./account-management";
import { ME_KEY } from "@/hooks/useAuth";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  toastSuccess,
  toastError,
  resetSonner,
  navState,
  authedMe,
} from "@/test";

describe("SecuritySection — change password (#183)", () => {
  beforeEach(() => resetSonner());

  it("changes the password and toasts success", async () => {
    let captured: { currentPassword: string; newPassword: string } | null = null;
    server.use(
      http.post(url("/api/auth/change-password"), async ({ request }) => {
        captured = (await request.json()) as typeof captured;
        return jsonOk({ message: "ok" });
      }),
    );
    renderWithProviders(<SecuritySection />);

    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: "Password1234" } });
    fireEvent.change(screen.getByLabelText(/^new password$/i), { target: { value: "ChangedPass5678" } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: "ChangedPass5678" } });
    fireEvent.click(screen.getByRole("button", { name: /update password/i }));

    await waitFor(() =>
      expect(captured).toEqual({ currentPassword: "Password1234", newPassword: "ChangedPass5678" }),
    );
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Your password has been updated."));
  });

  it("blocks the request when the confirmation doesn't match", async () => {
    let called = false;
    server.use(
      http.post(url("/api/auth/change-password"), () => {
        called = true;
        return jsonOk({ message: "ok" });
      }),
    );
    renderWithProviders(<SecuritySection />);

    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: "Password1234" } });
    fireEvent.change(screen.getByLabelText(/^new password$/i), { target: { value: "ChangedPass5678" } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: "doesNotMatch9" } });
    fireEvent.click(screen.getByRole("button", { name: /update password/i }));

    await waitFor(() => expect(screen.getByText(/passwords don't match/i)).toBeInTheDocument());
    expect(called).toBe(false);
  });

  it("surfaces a jargon-free toast when the server rejects the current password", async () => {
    server.use(
      http.post(url("/api/auth/change-password"), () =>
        jsonError("auth.invalid_password", "Your current password is incorrect.", { status: 400 }),
      ),
    );
    renderWithProviders(<SecuritySection />);

    fireEvent.change(screen.getByLabelText(/current password/i), { target: { value: "wrongpassword1" } });
    fireEvent.change(screen.getByLabelText(/^new password$/i), { target: { value: "ChangedPass5678" } });
    fireEvent.change(screen.getByLabelText(/confirm new password/i), { target: { value: "ChangedPass5678" } });
    fireEvent.click(screen.getByRole("button", { name: /update password/i }));

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith("Your current password is incorrect."),
    );
  });
});

describe("SecuritySection — change email (#183)", () => {
  beforeEach(() => resetSonner());

  it("requests a change and surfaces the server's confirmation copy", async () => {
    let captured: { password: string; newEmail: string } | null = null;
    server.use(
      http.post(url("/api/auth/change-email"), async ({ request }) => {
        captured = (await request.json()) as typeof captured;
        return jsonOk({ message: "We've sent a confirmation link to new@acme.test." });
      }),
    );
    renderWithProviders(<SecuritySection />);

    fireEvent.change(screen.getByLabelText(/new email/i), { target: { value: "new@acme.test" } });
    fireEvent.change(screen.getByLabelText(/confirm with your password/i), { target: { value: "Password1234" } });
    fireEvent.click(screen.getByRole("button", { name: /send confirmation link/i }));

    await waitFor(() => expect(captured).toEqual({ password: "Password1234", newEmail: "new@acme.test" }));
    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("We've sent a confirmation link to new@acme.test."),
    );
  });

  it("surfaces the server's rejection (taken email) jargon-free", async () => {
    server.use(
      http.post(url("/api/auth/change-email"), () =>
        jsonError("auth.email_taken", "That email address is already in use.", { status: 409 }),
      ),
    );
    renderWithProviders(<SecuritySection />);

    fireEvent.change(screen.getByLabelText(/new email/i), { target: { value: "taken@acme.test" } });
    fireEvent.change(screen.getByLabelText(/confirm with your password/i), { target: { value: "Password1234" } });
    fireEvent.click(screen.getByRole("button", { name: /send confirmation link/i }));

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith("That email address is already in use."),
    );
    const msg = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(msg).not.toMatch(/409|conflict|failed to fetch/i);
  });
});

describe("DangerZone — export (#183)", () => {
  beforeEach(() => resetSonner());

  it("downloads the account export and toasts on success", async () => {
    // jsdom doesn't implement these URL statics — patch the methods directly
    // (DON'T replace the whole URL global, or MSW's internal `new URL()` breaks).
    const createObjectURL = vi.fn(() => "blob:stub");
    const revokeObjectURL = vi.fn();
    const origCreate = (URL as unknown as { createObjectURL?: unknown }).createObjectURL;
    const origRevoke = (URL as unknown as { revokeObjectURL?: unknown }).revokeObjectURL;
    URL.createObjectURL = createObjectURL as typeof URL.createObjectURL;
    URL.revokeObjectURL = revokeObjectURL as typeof URL.revokeObjectURL;
    const clickSpy = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(() => {});

    try {
      server.use(http.get(url("/api/auth/account/export"), () => jsonOk({ account: {} })));
      renderWithProviders(<DangerZone />);

      fireEvent.click(screen.getByRole("button", { name: /export my data/i }));

      await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Download started"));
      expect(createObjectURL).toHaveBeenCalled();
      expect(clickSpy).toHaveBeenCalled();
    } finally {
      clickSpy.mockRestore();
      URL.createObjectURL = origCreate as typeof URL.createObjectURL;
      URL.revokeObjectURL = origRevoke as typeof URL.revokeObjectURL;
    }
  });

  it("shows a jargon-free toast on export failure — never an HTTP status", async () => {
    server.use(http.get(url("/api/auth/account/export"), () => jsonError("server.error", "boom", { status: 502 })));
    renderWithProviders(<DangerZone />);

    fireEvent.click(screen.getByRole("button", { name: /export my data/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    const msg = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(msg).toBe("Something went wrong. Try again.");
    expect(msg).not.toMatch(/502|bad gateway|failed to fetch/i);
  });
});

describe("DangerZone — delete account (#183)", () => {
  beforeEach(() => resetSonner());

  it("requires a confirm step, then deletes and redirects to /login", async () => {
    let captured: { password: string } | null = null;
    server.use(
      http.post(url("/api/auth/account/delete"), async ({ request }) => {
        captured = (await request.json()) as typeof captured;
        return jsonOk({ message: "deleted" });
      }),
    );
    const { queryClient } = renderWithProviders(<DangerZone />, { auth: authedMe });

    // The password field is hidden until the user opts in.
    expect(screen.queryByLabelText(/enter your password to confirm/i)).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /^delete my account$/i }));

    fireEvent.change(screen.getByLabelText(/enter your password to confirm/i), {
      target: { value: "Password1234" },
    });
    fireEvent.click(screen.getByRole("button", { name: /permanently delete/i }));

    await waitFor(() => expect(captured).toEqual({ password: "Password1234" }));
    await waitFor(() => expect(navState.router.push).toHaveBeenCalledWith("/login"));
    // useDeleteAccount's onSuccess must tear down the cached session (setMeCache(null)
    // + qc.clear()) so the deleted user can't keep rendering as authenticated.
    await waitFor(() => expect(queryClient.getQueryData([...ME_KEY])).toBeUndefined());
  });

  it("warns that a paid plan is canceled on deletion (#255)", () => {
    // The API cancels the Stripe subscription before deleting (#255); the danger-zone
    // copy must promise that so a paying user isn't scared deletion keeps billing them.
    // ("no new charges will start", not "never charged again": cancel stops renewals,
    // but an already-open past_due invoice can still be retried by Stripe dunning.)
    renderWithProviders(<DangerZone />, { auth: authedMe });

    expect(
      screen.getByText(/if you have a paid plan, it will be canceled — no new charges will start/i),
    ).toBeInTheDocument();
  });

  it("surfaces the server's cancel-failure message verbatim and stays signed in (#255)", async () => {
    // The API aborts deletion with 502 billing.cancel_failed when the Stripe cancel
    // fails. The actionable server copy must reach the toast verbatim (error-message
    // policy) — not collapse into the generic fallback, and never leak HTTP jargon —
    // and the user must NOT be logged out or redirected (nothing was deleted).
    const serverMessage =
      "We couldn't cancel your paid plan, so your account was not deleted. Please try again, or cancel the plan from Manage billing first.";
    server.use(
      http.post(url("/api/auth/account/delete"), () =>
        jsonError("billing.cancel_failed", serverMessage, { status: 502 }),
      ),
    );
    const { queryClient } = renderWithProviders(<DangerZone />, { auth: authedMe });

    fireEvent.click(screen.getByRole("button", { name: /^delete my account$/i }));
    fireEvent.change(screen.getByLabelText(/enter your password to confirm/i), {
      target: { value: "Password1234" },
    });
    fireEvent.click(screen.getByRole("button", { name: /permanently delete/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith(serverMessage));
    expect(toastError).not.toHaveBeenCalledWith(expect.stringMatching(/502|bad gateway|failed to fetch/i));
    expect(navState.router.push).not.toHaveBeenCalledWith("/login");
    expect(queryClient.getQueryData([...ME_KEY])).toBeDefined();
  });
});
