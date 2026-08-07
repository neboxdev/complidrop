/**
 * Account management (#183) — Security section (change password / change email)
 * and Danger zone (export / account closure). Drives the real hooks → api client
 * → MSW so envelope/mapping regressions fail here. Error copy must stay
 * jargon-free, and since #398 the closure copy must also stay TRUE — see
 * "claims no erasure and no irreversibility" below.
 */
import { describe, it, expect, beforeEach, vi } from "vitest";
import { http } from "msw";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import { SecuritySection, DataExportSection, DangerZone } from "./account-management";
import { ME_KEY } from "@/hooks/useAuth";
import { counselRegisterRow, flattenCopy } from "@/test/counsel-brief";
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

describe("DataExportSection — export (#183 / #320 FP-113)", () => {
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
      renderWithProviders(<DataExportSection />);

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

  it("names the columns the export actually serializes, and the omissions (#398)", () => {
    // `AuthEndpoints.ExportAccount` selects `d.OriginalFileName, d.DocumentType,
    // d.ExpirationDate, d.ComplianceStatus, d.CreatedAt` — document ROWS, never
    // the blobs. "a JSON copy of your … documents" read as "my files" to the
    // person clicking a data-portability button; "the details we hold for each
    // document" (round 1's replacement) read as the thing the product itself
    // calls "Extracted fields", which is equally absent — as are the
    // `ComplianceCheck` rows and the `AuditLog`. So the card enumerates what IS
    // in the dump and states what is not, and this pins both halves.
    //
    // Round 2 (S1): a finite omission list reads as COMPLETE, so every absence a
    // portability reader would look for has to be in it. The checklists
    // (`ComplianceTemplate` / `ComplianceRule`) are user-authored and have their own
    // nav item, and the vendor projection drops `Vendor.ComplianceTemplateId`, so the
    // assignment is absent as well — both named now.
    const { container } = renderWithProviders(<DataExportSection />);
    const text = (container.textContent ?? "").replace(/\s+/g, " ");
    expect(text).toMatch(
      /one row per document — file name, type, expiration date, status and the date you added it/i,
    );
    expect(text).toMatch(/the uploaded files themselves aren't included/i);
    expect(text).toMatch(
      /neither are the fields we read from your documents, their compliance-check results, your requirement checklists and which vendor is on each, the record of reminders already sent, or your activity log/i,
    );
    // The claim round 2 retired: nothing may promise "the details we hold", which
    // the export does not contain.
    expect(text).not.toMatch(/the details we hold/i);
  });

  it("shows a jargon-free toast on export failure — the friendly server message, never an HTTP status", async () => {
    // api.getBlob (#254) now surfaces the server's friendly envelope message (not always the generic
    // fallback the old bare fetch used); the contract is "no HTTP jargon", which this still pins.
    server.use(http.get(url("/api/auth/account/export"), () =>
      jsonError("server.error", "We couldn't prepare your export just now.", { status: 502 })),
    );
    renderWithProviders(<DataExportSection />);

    fireEvent.click(screen.getByRole("button", { name: /export my data/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    const msg = String(toastError.mock.calls.at(-1)?.[0] ?? "");
    expect(msg).toBe("We couldn't prepare your export just now.");
    expect(msg).not.toMatch(/502|bad gateway|failed to fetch/i);
  });
});

// Named for the control that ships, not for the endpoint behind it (#398 round 2 / S3). This is
// the one line CI prints for every case below, and the same correction round 2 made in
// providers.test.tsx — a title asserting a claim the ticket retired is a record that outlives it.
describe("DangerZone — close account (#183 / #398)", () => {
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
    fireEvent.click(screen.getByRole("button", { name: /^close my account$/i }));

    fireEvent.change(screen.getByLabelText(/enter your password to confirm/i), {
      target: { value: "Password1234" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^close account$/i }));

    await waitFor(() => expect(captured).toEqual({ password: "Password1234" }));
    // The success toast is the LAST deletion claim a closing customer reads, and until #398
    // round 2 nothing asserted it at all — "Your account has been deleted." could be restored
    // here and ship green. The frontend twin of the backend's AssertNoErasureClaim (C2).
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Your account is closed."));
    await waitFor(() => expect(navState.router.push).toHaveBeenCalledWith("/login"));
    // useDeleteAccount's onSuccess must tear down the cached session (setMeCache(null)
    // + qc.clear()) so the deleted user can't keep rendering as authenticated.
    await waitFor(() => expect(queryClient.getQueryData([...ME_KEY])).toBeUndefined());
  });

  it("claims no erasure and no irreversibility, and names what closure keeps (#398)", () => {
    // THE claim this card exists to get right. `AuthEndpoints.DeleteAccount`
    // scrubs `User.Email` + `FullName` and soft-deletes the user + org; the
    // vendors (contact email/phone included), documents, the uploaded blobs in
    // Azure, reminder logs, the Subscription row and the audit trail are all
    // RETAINED — ADR 0013 § Consequences calls that a deliberate MVP boundary,
    // and the same ADR names support-reversibility as one of its benefits. So
    // "Permanently delete … This can't be undone" was false in BOTH halves: the
    // FTC Act §5 deceptive-deletion shape, and the reason #398 is a blocker.
    // Copy only — ADR 0013's behaviour is unchanged and the retention question
    // is routed to counsel as CLM-7, not answered here.
    const { container } = renderWithProviders(<DangerZone />, { auth: authedMe });
    const text = (container.textContent ?? "").replace(/\s+/g, " ");

    expect(text).not.toMatch(/permanent/i);
    expect(text).not.toMatch(/can(no|')t be undone/i);
    // "Delete" itself is the representation the finding turns on — nothing here
    // deletes, so no control or sentence may say it.
    expect(text).not.toMatch(/delete/i);
    // …and the truthful replacements actually render.
    expect(text).toMatch(/clears your name and email from your account record/i);
    expect(text).toMatch(
      /your vendors, documents, the files uploaded for them, and our record of account activity are kept/i,
    );
  });

  it("renders the closure notice the §0 CLM-7 register quotes, link inline (#398)", () => {
    // `marketing-claims.test.ts` pins every other CLM-7 quote by scanning source,
    // and cannot see this one: linking the Privacy Policy puts a `<Link>` mid-
    // sentence, so the sentence is no longer contiguous in the file. Same split
    // CLM-5 has lived on since #404 — rendering is what puts the link's own text
    // back inline, and it compares the brief against what the customer reads
    // rather than against JSX. Without this the register would quote a sentence
    // nothing checks, which is the failure the register pins exist for.
    const CLOSURE_NOTICE =
      "Your vendors, documents, the files uploaded for them, and our record of account " +
      "activity are kept, and we handle them as described in our Privacy Policy.";
    expect(
      counselRegisterRow("CLM-7").quoted.map(flattenCopy),
      "the §0 CLM-7 row no longer quotes the closure notice (item (a))",
    ).toContainEqual(CLOSURE_NOTICE);

    const { container } = renderWithProviders(<DangerZone />, { auth: authedMe });
    expect(flattenCopy(container.textContent ?? "")).toContain(CLOSURE_NOTICE);
  });

  it("links the Privacy Policy it defers the retention disclosure to (#398)", () => {
    // The sentence above sends the customer to "our Privacy Policy" at the moment
    // of decision, and the dashboard shell renders no footer and no legal links —
    // so before this the pointer went nowhere a signed-in user could follow. ADR
    // 0013 Amendment 1 § Alternatives already rejected leaving the disclosure to
    // /privacy on the grounds that "a policy nobody opens is where the original
    // claim already hid".
    renderWithProviders(<DangerZone />, { auth: authedMe });
    expect(screen.getByRole("link", { name: /privacy policy/i })).toHaveAttribute(
      "href",
      "/privacy",
    );
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
    // Verbatim from `AuthEndpoints.DeleteAccount`'s 502 arm. "not closed", not
    // "not deleted" (#398 round 2): this string is rendered under a button
    // labelled "Close my account", so an erasure word here re-opens the claim
    // the ticket exists to remove — on the one surface the sweep didn't look at.
    const serverMessage =
      "We couldn't cancel your paid plan, so your account was not closed. Please try again, or cancel the plan from Manage billing first.";
    server.use(
      http.post(url("/api/auth/account/delete"), () =>
        jsonError("billing.cancel_failed", serverMessage, { status: 502 }),
      ),
    );
    const { queryClient } = renderWithProviders(<DangerZone />, { auth: authedMe });

    fireEvent.click(screen.getByRole("button", { name: /^close my account$/i }));
    fireEvent.change(screen.getByLabelText(/enter your password to confirm/i), {
      target: { value: "Password1234" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^close account$/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith(serverMessage));
    expect(toastError).not.toHaveBeenCalledWith(expect.stringMatching(/502|bad gateway|failed to fetch/i));
    expect(navState.router.push).not.toHaveBeenCalledWith("/login");
    expect(queryClient.getQueryData([...ME_KEY])).toBeDefined();
  });
});
