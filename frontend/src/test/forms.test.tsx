/**
 * Pins the label-input wiring contract added in #76.
 *
 * Every form whose `<label>` text is visible to a user must associate
 * that label with its input via `htmlFor` + `id` — without it, screen
 * readers announce the input with no field-name context AND RTL's
 * `getByLabelText` does NOT resolve the input. After #76 the auth
 * forms (login, register) and the dashboard forms (vendors create,
 * vendor detail) wire every label this way; this test catches a future
 * regression where someone copy-pastes a new field without the
 * htmlFor/id pair.
 *
 * Strategy: render each form, query each known label by accessible
 * text, assert it resolves an input element. If a label loses its
 * htmlFor/id, `getByLabelText` returns null and the test fails with a
 * useful message identifying the form + label.
 */
import { describe, it, expect } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import LoginPage from "@/app/(auth)/login/page";
import RegisterForm from "@/app/(auth)/register/register-form";
import VendorsPage from "@/app/(dashboard)/vendors/page";
import VendorDetailPage from "@/app/(dashboard)/vendors/[id]/page";
import DocumentsPage from "@/app/(dashboard)/documents/page";
import DocumentDetailPage from "@/app/(dashboard)/documents/[id]/page";
import ExportPage from "@/app/(dashboard)/export/page";
import SettingsPage from "@/app/(dashboard)/settings/page";
import ForgotPasswordPage from "@/app/(auth)/forgot-password/page";
import { ResetPasswordClient } from "@/app/(auth)/reset-password/reset-password-client";
import { SecuritySection } from "@/app/(dashboard)/settings/account-management";
import {
  renderWithProviders,
  authedMe,
  server,
  url,
  jsonOk,
  makeDocumentDetail,
  makeDocumentsResponse,
  dropFilesIn,
  makeFile,
} from "@/test";
import { http } from "msw";

describe("form labels wired via htmlFor + id (#76)", () => {
  it("LoginPage: every visible label resolves an input via getByLabelText", () => {
    renderWithProviders(<LoginPage />, { auth: null });

    expect(screen.getByLabelText(/^email$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^password$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
  });

  it("RegisterForm: every visible label resolves an input via getByLabelText", () => {
    renderWithProviders(<RegisterForm />, { auth: null });

    expect(screen.getByLabelText(/^full name$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^company$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^work email$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^password$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^industry$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^size$/i)).toBeInstanceOf(HTMLInputElement);
  });

  it("VendorsPage create form: every visible label resolves an input via getByLabelText", async () => {
    // VendorsPage fires GET /api/vendors on mount. Seed an empty list
    // so the page lands on its happy path and `await` the loading row
    // disappearing — a regression where the create-form moved INSIDE
    // the loading branch (or behind an error card) would otherwise
    // false-pass here because the form renders unconditionally today.
    server.use(http.get(url("/api/vendors"), () => jsonOk([])));
    renderWithProviders(<VendorsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.queryByText(/^loading…$/i)).toBeNull(),
    );
    expect(screen.getByLabelText(/^name$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^contact email$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
  });

  it("VendorDetailPage: LabeledInput helpers AND the compliance-template select are wired (#76 followup)", async () => {
    // Pin the per-LabeledInput useId pattern (4 instances on this
    // page) AND the inline-on-parent useId for the <select> (the
    // only non-Input form control wired in #76). A regression where
    // someone re-inlined the label without useId, or dropped the
    // select's htmlFor, would now fail this test instead of slipping
    // through unnoticed.
    server.use(
      http.get(url("/api/vendors/:id"), () =>
        jsonOk({
          id: "v_acme_01",
          name: "Acme",
          contactEmail: "ops@acme.test",
          contactPhone: "+1-555-0100",
          category: "electrical",
          complianceTemplateId: null,
          complianceTemplateName: null,
          portalLinks: [],
          createdAt: "2026-01-01T00:00:00Z",
          updatedAt: "2026-05-26T00:00:00Z",
        }),
      ),
      http.get(url("/api/compliance/templates"), () => jsonOk([])),
    );
    renderWithProviders(<VendorDetailPage />, {
      auth: authedMe,
      params: { id: "v_acme_01" },
    });

    await waitFor(() =>
      expect(
        screen.getByRole("heading", { name: /acme/i }),
      ).toBeInTheDocument(),
    );

    // Four LabeledInput sites.
    expect(screen.getByLabelText(/^name$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^contact email$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^contact phone$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    expect(screen.getByLabelText(/^category$/i)).toBeInstanceOf(
      HTMLInputElement,
    );
    // The one non-Input control: a native <select>. Type-checked
    // explicitly so a regression that swapped it for an Input (or
    // vice versa) fails here.
    expect(screen.getByLabelText(/^compliance template$/i)).toBeInstanceOf(
      HTMLSelectElement,
    );
  });

  it("DocumentsPage upload staging card: Vendor + Document type labels resolve their controls (#186)", async () => {
    // The staging card's labeled controls only render once a file is dropped.
    // Pins that the new Vendor (VendorPicker <Input>) and Document type
    // (DocumentTypeSelect <select>) labels are wired via htmlFor + id so
    // getByLabelText resolves them — the #76 contract for every new form.
    server.use(
      http.get(url("/api/documents"), () =>
        jsonOk(makeDocumentsResponse({ items: [], total: 0 })),
      ),
      http.get(url("/api/vendors"), () => jsonOk([{ id: "v1", name: "Acme" }])),
    );
    const { container } = renderWithProviders(<DocumentsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByText(/no documents yet/i)).toBeInTheDocument(),
    );

    dropFilesIn(container, [makeFile("coi.pdf")]);
    await waitFor(() =>
      expect(screen.getByText(/add details before uploading/i)).toBeInTheDocument(),
    );

    expect(screen.getByLabelText(/^vendor$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^document type$/i)).toBeInstanceOf(HTMLSelectElement);
  });

  it("DocumentDetailPage: dynamic field labels (`docfield-${id}`) are wired per row (#76 followup)", async () => {
    // The only string-interpolation id pattern in #76. Pins that two
    // distinct field rows produce non-colliding ids (the contract
    // that justifies `docfield-${f.id}` over a single useId() that
    // can't disambiguate per row).
    server.use(
      http.get(url("/api/documents/:id"), () =>
        jsonOk(
          makeDocumentDetail({
            extractionStatus: "Completed",
            fields: [
              {
                id: "f-policy-1",
                fieldName: "PolicyNumber",
                fieldValue: "POL-12345",
                fieldType: "string",
                confidence: 0.95,
                isManuallyEdited: false,
                originalValue: null,
              },
              {
                id: "f-exp-1",
                fieldName: "ExpirationDate",
                fieldValue: "2026-12-31",
                fieldType: "string",
                confidence: 0.92,
                isManuallyEdited: false,
                originalValue: null,
              },
            ],
          }),
        ),
      ),
    );
    renderWithProviders(<DocumentDetailPage />, {
      auth: authedMe,
      params: { id: "d_doc_01" },
    });

    await waitFor(() =>
      expect(screen.getByText("coi.pdf")).toBeInTheDocument(),
    );
    // Field names are humanized for display (#188): the camelCase fixture
    // names "PolicyNumber"/"ExpirationDate" render as "Policy Number"/"Expiration Date".
    const policy = screen.getByLabelText(/^Policy Number$/);
    const expiration = screen.getByLabelText(/^Expiration Date$/);
    expect(policy).toBeInstanceOf(HTMLInputElement);
    expect(expiration).toBeInstanceOf(HTMLInputElement);
    // Per-row id uniqueness: the two field inputs must resolve to
    // DIFFERENT elements. A regression where someone used a single
    // useId() outside the .map() would collide here.
    expect(policy).not.toBe(expiration);
    expect((policy as HTMLInputElement).id).not.toBe(
      (expiration as HTMLInputElement).id,
    );
  });

  it("SettingsPage org form: name input + time-zone select are wired via getByLabelText (#185)", () => {
    // The org settings form (#185) adds an Input (org name) + a native
    // <select> (time zone). Seed the billing query so the page mounts; the
    // org form renders unconditionally once the session is present.
    server.use(
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
    renderWithProviders(<SettingsPage />, { auth: authedMe });

    expect(screen.getByLabelText(/organization name/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^time zone$/i)).toBeInstanceOf(HTMLSelectElement);
  });

  it("ForgotPasswordPage: email label resolves an input (#183)", () => {
    renderWithProviders(<ForgotPasswordPage />, { auth: null });
    expect(screen.getByLabelText(/^email$/i)).toBeInstanceOf(HTMLInputElement);
  });

  it("ResetPasswordClient: new-password + confirm labels resolve inputs (#183)", () => {
    renderWithProviders(<ResetPasswordClient />, { auth: null, searchParams: { token: "t" } });
    expect(screen.getByLabelText(/^new password$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/confirm new password/i)).toBeInstanceOf(HTMLInputElement);
  });

  it("SecuritySection: change-password + change-email labels resolve inputs (#183)", () => {
    renderWithProviders(<SecuritySection />, { auth: authedMe });
    expect(screen.getByLabelText(/current password/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/^new password$/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/confirm new password/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/new email/i)).toBeInstanceOf(HTMLInputElement);
    expect(screen.getByLabelText(/confirm with your password/i)).toBeInstanceOf(HTMLInputElement);
  });

  it("ExportPage: From/To date labels resolve to date inputs via getByLabelText (#76 followup)", () => {
    // Missed by the original #76 sweep — the export-page From/To
    // labels were the only dashboard form-control labels not touched.
    // Now wired.
    renderWithProviders(<ExportPage />, { auth: authedMe });

    const from = screen.getByLabelText(/^from$/i);
    const to = screen.getByLabelText(/^to$/i);
    expect(from).toBeInstanceOf(HTMLInputElement);
    expect((from as HTMLInputElement).type).toBe("date");
    expect(to).toBeInstanceOf(HTMLInputElement);
    expect((to as HTMLInputElement).type).toBe("date");
  });
});
