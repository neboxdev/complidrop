/**
 * Vendor detail page — smoke (#36).
 *
 * Tier-1 page but most contract lives at the useVendors hook level
 * (see useVendors.test.tsx for the portal-link generate/revoke
 * invalidation contract). Here we pin:
 *   - Loading copy while the detail fetch is in flight.
 *   - Populated render: name, contact, portal link list.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import VendorDetailPage from "./page";
import type { VendorDetail } from "@/hooks/useVendors";
import {
  renderWithProviders,
  server,
  url,
  jsonOk,
  jsonError,
  authedMe,
  toastSuccess,
  toastError,
} from "@/test";

// sonner mock + spies are provided by the harness; afterEach in the
// setup file resets all toast spies between tests (#74). These smoke
// renders don't drive any mutation path, so no toast should fire —
// the negative assertions in each test pin that contract.

const VENDOR_DETAIL = {
  id: "v_acme_01",
  name: "Acme Subcontractor",
  contactEmail: "ops@acme.test",
  contactPhone: null,
  category: "electrical",
  complianceTemplateId: null,
  complianceTemplateName: null,
  portalLinks: [
    {
      id: "pl_01",
      token: "abc",
      fullUrl: "http://example.test/portal/abc",
      isActive: true,
      uploadCount: 0,
      maxUploads: 5,
      expiresAt: null,
      createdAt: "2026-05-26T00:00:00Z",
    },
  ],
  createdAt: "2026-01-01T00:00:00Z",
  updatedAt: "2026-05-26T00:00:00Z",
  coverage: { status: "NoRequirements" as const, missingTypes: [] as string[], coveredThrough: null },
  contactEmailStatus: null,
};

describe("VendorDetailPage — requirement contents at decision time (#239)", () => {
  it("shows what the chosen checklist checks, in plain English", async () => {
    const vendorWithTemplate = {
      ...VENDOR_DETAIL,
      complianceTemplateId: "t_caterer",
      complianceTemplateName: "Caterer",
    };
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(vendorWithTemplate)),
      http.get(url("/api/compliance/templates"), () =>
        jsonOk([{ id: "t_caterer", name: "Caterer", isSystemTemplate: true }]),
      ),
      http.get(url("/api/compliance/templates/t_caterer"), () =>
        jsonOk({
          id: "t_caterer",
          name: "Caterer",
          isSystemTemplate: true,
          rules: [
            { id: "r1", documentType: "coi", fieldName: "general_liability_limit", operator: "min_value", expectedValue: "1000000", errorMessage: null, sortOrder: 1 },
            { id: "r2", documentType: "coi", fieldName: "workers_comp_limit", operator: "required", expectedValue: null, errorMessage: null, sortOrder: 2 },
          ],
        }),
      ),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    // The "We'll check every document for:" panel renders the chosen checklist's rules as
    // plain, money-formatted sentences — the #237 highest-leverage gap (assign on faith).
    expect(await screen.findByText(/we'll check every document for/i)).toBeInTheDocument();
    expect(screen.getByText(/at least \$1,000,000 in general liability/i)).toBeInTheDocument();
    expect(screen.getByText(/workers' compensation coverage/i)).toBeInTheDocument();
  });

  it("tells the user to add requirements when the assigned checklist has none", async () => {
    const vendorWithEmpty = { ...VENDOR_DETAIL, complianceTemplateId: "t_empty", complianceTemplateName: "Empty" };
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(vendorWithEmpty)),
      http.get(url("/api/compliance/templates"), () =>
        jsonOk([{ id: "t_empty", name: "Empty", isSystemTemplate: false }]),
      ),
      http.get(url("/api/compliance/templates/t_empty"), () =>
        jsonOk({ id: "t_empty", name: "Empty", isSystemTemplate: false, rules: [] }),
      ),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    expect(await screen.findByText(/this checklist has no requirements yet/i)).toBeInTheDocument();
  });
});

describe("VendorDetailPage — smoke (#36)", () => {
  it("loading: renders the loading copy while fetch is in flight", () => {
    const settled = new Promise<void>(() => {});
    server.use(
      http.get(url("/api/vendors/:id"), async () => {
        await settled;
        return jsonOk(VENDOR_DETAIL);
      }),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
    });

    expect(screen.getByText(/loading vendor/i)).toBeInTheDocument();
    // Loading-state smoke renders no mutation paths, so toasts must
    // not fire (#74 review).
    expect(toastSuccess).not.toHaveBeenCalled();
    expect(toastError).not.toHaveBeenCalled();
  });

  it("populated: renders the vendor name + contact + portal link", async () => {
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
    });

    await waitFor(() =>
      expect(
        screen.getByRole("heading", { level: 1, name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );
    // Portal link URL surfaces somewhere — readable input field or
    // anchor; assert via the URL text.
    expect(
      screen.getByDisplayValue("http://example.test/portal/abc"),
    ).toBeInTheDocument();
    // Populated smoke renders no mutation path, so toasts must not
    // fire (#74 review). A regression that auto-fired a toast on
    // mount would trip this.
    expect(toastSuccess).not.toHaveBeenCalled();
    expect(toastError).not.toHaveBeenCalled();
  });

  it("portal-link icon controls expose accessible names + a coarse-pointer touch target (#181)", async () => {
    // Before #181 the Copy and Revoke controls were icon-only buttons with no
    // accessible name. AC #3 also requires destructive icon buttons (Revoke is
    // destructive) to present a ≥44px hit area on touch — inherited from the
    // Button base coarse-pointer class.
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
    });

    await waitFor(() =>
      expect(
        screen.getByRole("heading", { level: 1, name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );

    expect(
      screen.getByRole("button", { name: /copy upload link/i }),
    ).toBeInTheDocument();
    const revoke = screen.getByRole("button", { name: /revoke link/i });
    expect(revoke).toBeInTheDocument();
    expect(revoke.className).toContain("pointer-coarse:min-h-11");
  });
});

describe("VendorDetailPage — requirement UX + email link (#190)", () => {
  function mountWith(vendor: VendorDetail, templates: { id: string; name: string; isSystemTemplate: boolean }[] = []) {
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(vendor)),
      http.get(url("/api/compliance/templates"), () => jsonOk(templates)),
      // The page now fetches the SELECTED template's detail to show what it checks (#239);
      // answer it generically so a test that assigns a template doesn't hit an unhandled request.
      http.get(url("/api/compliance/templates/:tid"), ({ params }) =>
        jsonOk({ id: params.tid, name: "Checklist", isSystemTemplate: true, rules: [] }),
      ),
    );
    return renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: vendor.id },
    });
  }

  it("relabels the template control to plain English with helper copy", async () => {
    mountWith(VENDOR_DETAIL);
    // The select is wired to the new label, not the old "Compliance template".
    const select = await screen.findByLabelText(/what this vendor must prove/i);
    expect(select).toBeInTheDocument();
    expect(screen.queryByText(/compliance template/i)).toBeNull();
    expect(
      screen.getByText(/we check every document against it/i),
    ).toBeInTheDocument();
  });

  it("warns when no requirement checklist is assigned", async () => {
    mountWith(VENDOR_DETAIL); // complianceTemplateId is null in the fixture
    expect(
      await screen.findByText(/won't be marked covered or not until you choose one/i),
    ).toBeInTheDocument();
  });

  it("links to /rules to create a checklist when the org has none", async () => {
    mountWith(VENDOR_DETAIL, []); // zero templates available
    const link = await screen.findByRole("link", { name: /create a requirement checklist/i });
    expect(link).toHaveAttribute("href", "/rules");
  });

  it("emails the vendor's existing active link in one click without minting a new one", async () => {
    let generated = 0;
    let emailedLinkId: string | null = null;
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)), // already has active pl_01
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.post(url("/api/vendors/:id/portal-link"), () => {
        generated++;
        return jsonOk({ id: "pl_new_01", token: "tok", url: "http://example.test/portal/tok", maxUploads: 20 });
      }),
      http.post(url("/api/vendors/:id/portal-link/:linkId/email"), ({ params }) => {
        emailedLinkId = params.linkId as string;
        return jsonOk({ sentTo: "ops@acme.test" });
      }),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    const emailBtn = await screen.findByRole("button", {
      name: /email link to acme subcontractor/i,
    });
    expect(emailBtn).not.toBeDisabled();
    fireEvent.click(emailBtn);

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Upload link emailed to ops@acme.test"),
    );
    // Reused the existing active link (pl_01) — no new link minted.
    expect(emailedLinkId).toBe("pl_01");
    expect(generated).toBe(0);
    expect(toastError).not.toHaveBeenCalled();
  });

  it("mints a link then emails it when the vendor has no active link", async () => {
    let generated = 0;
    let emailedLinkId: string | null = null;
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk({ ...VENDOR_DETAIL, portalLinks: [] })),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.post(url("/api/vendors/:id/portal-link"), () => {
        generated++;
        return jsonOk({ id: "pl_new_01", token: "tok", url: "http://example.test/portal/tok", maxUploads: 20 });
      }),
      http.post(url("/api/vendors/:id/portal-link/:linkId/email"), ({ params }) => {
        emailedLinkId = params.linkId as string;
        return jsonOk({ sentTo: "ops@acme.test" });
      }),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    fireEvent.click(
      await screen.findByRole("button", { name: /email link to acme subcontractor/i }),
    );

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Upload link emailed to ops@acme.test"),
    );
    // No active link existed → generated one, emailed THAT link's id.
    expect(generated).toBe(1);
    expect(emailedLinkId).toBe("pl_new_01");
  });

  it("does not warn once a requirement checklist is assigned", async () => {
    mountWith(
      { ...VENDOR_DETAIL, complianceTemplateId: "t1", complianceTemplateName: "Caterer" },
      [{ id: "t1", name: "Caterer", isSystemTemplate: true }],
    );
    // Wait for the page to settle on its loaded state.
    await screen.findByRole("heading", { level: 1, name: /acme subcontractor/i });
    expect(
      screen.queryByText(/won't be marked covered or not until you choose one/i),
    ).toBeNull();
  });

  it("disables the email button (with a nudge) when the vendor has no contact email", async () => {
    mountWith({ ...VENDOR_DETAIL, contactEmail: null });
    const emailBtn = await screen.findByRole("button", {
      name: /email link to acme subcontractor/i,
    });
    expect(emailBtn).toBeDisabled();
    expect(
      screen.getByText(/add a contact email above and save to email the upload link/i),
    ).toBeInTheDocument();
  });

  it("warns that reminders are paused when the contact email was marked as spam (#340)", async () => {
    mountWith({ ...VENDOR_DETAIL, contactEmailStatus: "complained" });
    expect(await screen.findByText(/marked a reminder as spam/i)).toBeInTheDocument();
    expect(screen.getByText(/reminders to it are paused/i)).toBeInTheDocument();
  });

  it("warns that reminders are paused when the contact email bounced (#340)", async () => {
    mountWith({ ...VENDOR_DETAIL, contactEmailStatus: "bounced" });
    expect(await screen.findByText(/is undeliverable/i)).toBeInTheDocument();
  });

  it("shows no email-status warning when the contact email is deliverable (#340)", async () => {
    mountWith(VENDOR_DETAIL); // contactEmailStatus: null
    await screen.findByRole("heading", { level: 1, name: /acme subcontractor/i });
    expect(screen.queryByText(/reminders to it are paused/i)).toBeNull();
  });

  it("surfaces a friendly toast (no HTTP jargon) when emailing fails", async () => {
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.post(url("/api/vendors/:id/portal-link"), () =>
        jsonOk({ id: "pl_new_01", token: "tok", url: "http://example.test/portal/tok", maxUploads: 20 }),
      ),
      http.post(url("/api/vendors/:id/portal-link/:linkId/email"), () =>
        jsonError("email.send_failed", "We couldn't send the email just now. Copy the link and try again, or send it yourself.", { status: 502 }),
      ),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    fireEvent.click(
      await screen.findByRole("button", { name: /email link to acme subcontractor/i }),
    );

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        "We couldn't send the email just now. Copy the link and try again, or send it yourself.",
      ),
    );
    const toastArg = toastError.mock.calls.at(-1)?.[0] as string;
    expect(toastArg).not.toMatch(/bad gateway/i);
    expect(toastArg).not.toMatch(/502/);
  });

  it("disables Save with a visible reason when the name is blanked (#264 / FP-074)", async () => {
    // A blank name would render an invisible, unclickable row in the vendors
    // list (the name is the row's link) — the client blocks the save with a
    // reason; the server enforces the same 400.
    mountWith(VENDOR_DETAIL);
    await screen.findByRole("heading", { level: 1, name: /acme subcontractor/i });

    const name = screen.getByLabelText("Name");
    const save = screen.getByRole("button", { name: /save changes/i });
    expect(save).toBeEnabled();
    expect(screen.queryByText(/vendor name is required/i)).toBeNull();

    fireEvent.change(name, { target: { value: "   " } });
    expect(save).toBeDisabled();
    expect(screen.getByText(/vendor name is required/i)).toBeInTheDocument();

    fireEvent.change(name, { target: { value: "Acme Rewired" } });
    expect(save).toBeEnabled();
    expect(screen.queryByText(/vendor name is required/i)).toBeNull();
  });

  it("copy link nudges the user to paste it into an email", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
      writable: true,
    });
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)), // reuses active pl_01
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    fireEvent.click(await screen.findByRole("button", { name: /^copy link$/i }));

    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith("http://example.test/portal/abc"),
    );
    expect(toastSuccess).toHaveBeenCalledWith(
      "Link copied — now paste it into an email to Acme Subcontractor.",
    );
  });

  it("gates Email/Copy behind the plan with an upgrade path when the portal is not included (#261)", async () => {
    // The server 403s link generation/emailing for Free orgs; the page gates
    // proactively so the user gets an upgrade path instead of a rejection toast.
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.get(url("/api/billing/subscription"), () =>
        jsonOk({
          plan: "free",
          status: "active",
          documentLimit: 5,
          documentsUsed: 0,
          hasVendorPortal: false,
          currentPeriodEnd: null,
          extractionSpend: 0,
        }),
      ),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    const emailBtn = await screen.findByRole("button", {
      name: /email link to acme subcontractor/i,
    });
    // The gate lands when the subscription query resolves.
    await waitFor(() => expect(emailBtn).toBeDisabled());
    expect(screen.getByRole("button", { name: /^copy link$/i })).toBeDisabled();
    expect(screen.getByText(/vendor upload links are a pro feature/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /upgrade your plan/i })).toHaveAttribute(
      "href",
      "/settings",
    );
    expect(toastError).not.toHaveBeenCalled();
  });

  it("keeps Email/Copy enabled with no upgrade note when the plan includes the portal (#261)", async () => {
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.get(url("/api/billing/subscription"), () =>
        jsonOk({
          plan: "pro",
          status: "active",
          documentLimit: null,
          documentsUsed: 0,
          hasVendorPortal: true,
          currentPeriodEnd: null,
          extractionSpend: 0,
        }),
      ),
    );

    const { queryClient } = renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
    });

    const emailBtn = await screen.findByRole("button", {
      name: /email link to acme subcontractor/i,
    });
    // Settlement anchor: the loading state renders identically (enabled, no note),
    // so wait for the entitlement to actually land before asserting — otherwise an
    // inverted gate (gating on === true) could pass inside the race window.
    await waitFor(() =>
      expect(queryClient.getQueryState(["billing", "subscription"])?.status).toBe("success"),
    );
    expect(emailBtn).toBeEnabled();
    expect(screen.getByRole("button", { name: /^copy link$/i })).toBeEnabled();
    expect(screen.queryByText(/vendor upload links are a pro feature/i)).toBeNull();
  });

  it("leaves the actions enabled while the entitlement is unknown (server stays the fence)", async () => {
    // Subscription never resolves — only an explicit `false` gates, so a Pro
    // user on a cold cache never sees a flash of "Pro feature" lockout.
    const settled = new Promise<void>(() => {});
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.get(url("/api/billing/subscription"), async () => {
        await settled;
        return jsonOk({});
      }),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    const emailBtn = await screen.findByRole("button", {
      name: /email link to acme subcontractor/i,
    });
    expect(emailBtn).toBeEnabled();
    expect(screen.getByRole("button", { name: /^copy link$/i })).toBeEnabled();
    expect(screen.queryByText(/vendor upload links are a pro feature/i)).toBeNull();
  });

  it("copy link surfaces a friendly toast (no browser jargon) when the clipboard write fails", async () => {
    const writeText = vi.fn().mockRejectedValue(new TypeError("Document is not focused"));
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
      writable: true,
    });
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );

    renderWithProviders(<VendorDetailPage />, { auth: authedMe, params: { id: "v_acme_01" } });

    fireEvent.click(await screen.findByRole("button", { name: /^copy link$/i }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    const toastArg = toastError.mock.calls.at(-1)?.[0] as string;
    // A raw browser TypeError must never reach the user (CLAUDE.md error-message policy).
    expect(toastArg).not.toMatch(/typeerror/i);
    expect(toastArg).toBe("Something went wrong. Try again.");
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});

describe("VendorDetailPage — remove vendor (#319 FP-073)", () => {
  it("removes the vendor behind a confirm, then routes back to /vendors", async () => {
    let deleteCalls = 0;
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(VENDOR_DETAIL)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.delete(url("/api/vendors/:id"), () => {
        deleteCalls += 1;
        return jsonOk({ id: "v_acme_01" });
      }),
    );
    const pushSpy = vi.fn();
    renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
      router: { push: pushSpy },
    });

    // Open the confirm from the header, then confirm inside the dialog.
    fireEvent.click(await screen.findByRole("button", { name: /remove vendor/i }));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: /remove vendor/i }));

    await waitFor(() => expect(deleteCalls).toBe(1));
    await waitFor(() => expect(pushSpy).toHaveBeenCalledWith("/vendors"));
  });
});

describe("VendorDetailPage — contact email validation (#369)", () => {
  function mount(vendor: VendorDetail = VENDOR_DETAIL) {
    server.use(
      http.get(url("/api/vendors/:id"), () => jsonOk(vendor)),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
      http.get(url("/api/compliance/templates/:tid"), ({ params }) =>
        jsonOk({ id: params.tid, name: "Checklist", isSystemTemplate: true, rules: [] }),
      ),
    );
    return renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: vendor.id },
    });
  }

  async function emailField() {
    return await screen.findByLabelText(/^contact email$/i);
  }

  const saveButton = () => screen.getByRole("button", { name: /save changes/i });

  it("blocks Save and explains why when the edited email is malformed", async () => {
    // The reported hole: the list add-form guarded this, the edit form did not, so a typo
    // here saved 200 OK and then broke every reminder send silently (ADR 0025 retries in place).
    mount();
    const input = await emailField();
    expect(saveButton()).toBeEnabled();

    fireEvent.change(input, { target: { value: "jane@acme,com" } });

    expect(saveButton()).toBeDisabled();
    expect(screen.getByText(/enter a valid email address/i)).toBeInTheDocument();
    expect(input).toHaveAttribute("aria-invalid", "true");
    // The message is wired as the input's description, so a screen reader announces the
    // reason rather than leaving Save mysteriously dead (#76 label/description contract).
    expect(input.getAttribute("aria-describedby")).toBe(
      screen.getByText(/enter a valid email address/i).id,
    );
  });

  it("blocks a pasted display-name address", async () => {
    mount();
    fireEvent.change(await emailField(), { target: { value: "Jane Smith <jane@acme.com>" } });
    expect(saveButton()).toBeDisabled();
  });

  it("keeps Save enabled when the field is cleared — no contact email is a valid state", async () => {
    mount();
    fireEvent.change(await emailField(), { target: { value: "   " } });
    expect(saveButton()).toBeEnabled();
    expect(screen.queryByText(/enter a valid email address/i)).toBeNull();
  });

  it("recovers once the typo is corrected, and sends the trimmed address", async () => {
    let sent: unknown = undefined;
    server.use(
      http.put(url("/api/vendors/:id"), async ({ request }) => {
        sent = await request.json();
        return jsonOk({ id: "v_acme_01" });
      }),
    );
    mount();
    const input = await emailField();

    fireEvent.change(input, { target: { value: "jane@acme,com" } });
    expect(saveButton()).toBeDisabled();

    fireEvent.change(input, { target: { value: "  jane@acme.com  " } });
    expect(saveButton()).toBeEnabled();
    expect(screen.queryByText(/enter a valid email address/i)).toBeNull();

    fireEvent.click(saveButton());

    await waitFor(() => expect(sent).toBeDefined());
    // Trimmed on the wire so the value that satisfied the enabled-state IS the value stored.
    expect((sent as { contactEmail: string }).contactEmail).toBe("jane@acme.com");
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Vendor updated"));
  });
});
