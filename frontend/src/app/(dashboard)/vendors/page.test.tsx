/**
 * Vendors list page — state matrix + new-vendor mutation (#36).
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import VendorsPage from "./page";
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
// setup file resets all toast spies between tests (#74).

const VENDORS = [
  {
    id: "v_acme_01",
    name: "Acme Subcontractor",
    contactEmail: "ops@acme.test",
    contactPhone: null,
    category: "electrical",
    complianceTemplateId: "t_default_01",
    complianceTemplateName: "Default COI",
    documentCount: 3,
    activePortalLinks: 1,
    coverage: { status: "Covered", missingTypes: [], coveredThrough: null },
  },
];

describe("VendorsPage — coverage Status column (#319 FP-074)", () => {
  it("renders the coverage verdict per row and deep-links the doc count", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonOk([
          {
            id: "v_missing",
            name: "Gaps LLC",
            contactEmail: null,
            contactPhone: null,
            category: null,
            complianceTemplateId: "t1",
            complianceTemplateName: "Caterer",
            documentCount: 2,
            activePortalLinks: 0,
            isSample: false,
            coverage: { status: "Missing", missingTypes: ["insurance", "license"], coveredThrough: null },
          },
          {
            id: "v_ok",
            name: "Solid LLC",
            contactEmail: null,
            contactPhone: null,
            category: null,
            complianceTemplateId: "t1",
            complianceTemplateName: "Caterer",
            documentCount: 3,
            activePortalLinks: 0,
            isSample: false,
            coverage: { status: "Covered", missingTypes: [], coveredThrough: null },
          },
        ]),
      ),
    );
    renderWithProviders(<VendorsPage />, { auth: authedMe });

    // The "who is NOT ok?" answer is right in the list (FP-074).
    expect(await screen.findByText(/missing: insurance, license/i)).toBeInTheDocument();
    expect(screen.getByText("Covered")).toBeInTheDocument();
    // The doc count links to the vendor's filtered documents (FP-071).
    const missingRow = screen.getByRole("row", { name: /gaps llc/i });
    expect(within(missingRow).getByRole("link", { name: "2" })).toHaveAttribute(
      "href",
      "/documents?vendor=v_missing",
    );
  });
});

describe("VendorsPage — sample badge (#238)", () => {
  it("badges the sample vendor and leaves a normal one unbadged", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonOk([
          {
            id: "v_sample",
            name: "Brightside Catering Co.",
            contactEmail: null,
            contactPhone: null,
            category: "Caterer",
            complianceTemplateId: "t_caterer",
            complianceTemplateName: "Caterer",
            documentCount: 1,
            activePortalLinks: 0,
            isSample: true,
            coverage: { status: "Covered", missingTypes: [], coveredThrough: null },
          },
          {
            id: "v_real",
            name: "Acme Subcontractor",
            contactEmail: null,
            contactPhone: null,
            category: "electrical",
            complianceTemplateId: null,
            complianceTemplateName: null,
            documentCount: 0,
            activePortalLinks: 0,
            isSample: false,
            coverage: { status: "NoRequirements", missingTypes: [], coveredThrough: null },
          },
        ]),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    const sampleRow = await screen.findByRole("row", { name: /Brightside Catering Co\./i });
    expect(within(sampleRow).getByText("Sample")).toBeInTheDocument();

    const realRow = screen.getByRole("row", { name: /Acme Subcontractor/i });
    expect(within(realRow).queryByText("Sample")).toBeNull();
  });
});

describe("VendorsPage — dead contact email badge (#340)", () => {
  it("badges a vendor whose contact email bounced / was marked as spam, leaving a deliverable one unbadged", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonOk([
          {
            id: "v_bounced", name: "Bounce LLC", contactEmail: "dead@bounce.test",
            contactPhone: null, category: null, complianceTemplateId: null, complianceTemplateName: null,
            documentCount: 0, activePortalLinks: 0, isSample: false,
            coverage: { status: "NoRequirements", missingTypes: [], coveredThrough: null }, contactEmailStatus: "bounced",
          },
          {
            id: "v_spam", name: "Spam LLC", contactEmail: "spam@x.test",
            contactPhone: null, category: null, complianceTemplateId: null, complianceTemplateName: null,
            documentCount: 0, activePortalLinks: 0, isSample: false,
            coverage: { status: "NoRequirements", missingTypes: [], coveredThrough: null }, contactEmailStatus: "complained",
          },
          {
            id: "v_ok", name: "Fine LLC", contactEmail: "ok@x.test",
            contactPhone: null, category: null, complianceTemplateId: null, complianceTemplateName: null,
            documentCount: 0, activePortalLinks: 0, isSample: false,
            coverage: { status: "NoRequirements", missingTypes: [], coveredThrough: null }, contactEmailStatus: null,
          },
        ]),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    const bouncedRow = await screen.findByRole("row", { name: /Bounce LLC/i });
    expect(within(bouncedRow).getByText("Bounced")).toBeInTheDocument();
    const spamRow = screen.getByRole("row", { name: /Spam LLC/i });
    expect(within(spamRow).getByText("Spam report")).toBeInTheDocument();
    const okRow = screen.getByRole("row", { name: /Fine LLC/i });
    expect(within(okRow).queryByText("Bounced")).toBeNull();
    expect(within(okRow).queryByText("Spam report")).toBeNull();
  });
});

describe("VendorsPage — state matrix (#36)", () => {
  it("loading: renders the loading row before the fetch resolves", () => {
    const settled = new Promise<void>(() => {});
    server.use(
      http.get(url("/api/vendors"), async () => {
        await settled;
        return jsonOk(VENDORS);
      }),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    // Skeleton rows (no bare "Loading…") to avoid layout shift when vendors land. (#197)
    expect(screen.getByTestId("vendors-loading")).toBeInTheDocument();
    expect(screen.queryByText(/^loading…$/i)).toBeNull();
  });

  it("empty: renders the no-vendors-yet copy", async () => {
    server.use(http.get(url("/api/vendors"), () => jsonOk([])));

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByText(/no vendors yet/i)).toBeInTheDocument(),
    );
  });

  it("Add vendor stays disabled for an empty or whitespace-only name (#264)", async () => {
    // A whitespace name would pass `!name` but be rejected server-side —
    // the button now requires a non-blank trimmed name.
    server.use(http.get(url("/api/vendors"), () => jsonOk([])));

    renderWithProviders(<VendorsPage />, { auth: authedMe });
    const btn = await screen.findByRole("button", { name: /add vendor/i });
    expect(btn).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "   " } });
    expect(btn).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Acme Catering" } });
    expect(btn).toBeEnabled();
  });

  it("error: a 5xx surfaces an error card with role=alert, the server message, and a Retry affordance, NOT the empty fallback (#80)", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonError("server.error", "vendor index down", { status: 500 }),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    // Co-pin role=alert + headline text on the SAME element so a
    // regression that drops either property fails loudly (#80 followup
    // review). Wrap in waitFor for React-19 work-loop safety.
    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert).toHaveTextContent(/couldn't load vendors/i);
    expect(alert).toHaveTextContent("vendor index down");

    expect(
      screen.getByRole("heading", { name: /^vendors$/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /retry/i }),
    ).toBeInTheDocument();
    // Negative: the empty-state copy must NOT appear on error.
    expect(screen.queryByText(/no vendors yet/i)).toBeNull();
  });

  it("error: non-JSON 5xx renders the jargon-free fallback, NOT a raw status-text leak (#80 + #77)", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        Promise.resolve(
          new Response("<html>502</html>", {
            status: 502,
            statusText: "Bad Gateway",
            headers: { "Content-Type": "text/html" },
          }),
        ),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert).toHaveTextContent(/couldn't load vendors/i);
    expect(alert).toHaveTextContent("Something went wrong. Try again.");
    expect(alert).not.toHaveTextContent(/bad gateway/i);
    expect(alert).not.toHaveTextContent(/<html>/i);
  });

  it("retry-on-5xx: clicking Retry fires a second fetch; a subsequent 200 swaps the error card for the populated list (#80)", async () => {
    // Pins the affordance #80 added: the Retry button must actually
    // re-issue the vendors fetch. Without this test, swapping
    // `onClick={() => vendors.refetch()}` for a no-op or a wrong query
    // would slip past every other test in this file.
    let calls = 0;
    server.use(
      http.get(url("/api/vendors"), () => {
        calls++;
        if (calls === 1) {
          return jsonError("server.error", "DB blip.", { status: 500 });
        }
        return jsonOk(VENDORS);
      }),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByText(/couldn't load vendors/i)).toBeInTheDocument(),
    );
    expect(calls).toBe(1);

    fireEvent.click(screen.getByRole("button", { name: /retry/i }));

    // Second fetch fires AND its 200 response swaps the error card for
    // the populated row.
    await waitFor(() => expect(calls).toBe(2));
    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );
    // Full state swap, not just the headline gone (#80 followup).
    expect(screen.queryByText(/couldn't load vendors/i)).toBeNull();
    expect(screen.queryByText("DB blip.")).toBeNull();
    expect(screen.queryByRole("button", { name: /retry/i })).toBeNull();
  });

  it("populated: every vendor renders with their template + counts + portal-link badge", async () => {
    server.use(http.get(url("/api/vendors"), () => jsonOk(VENDORS)));

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );
    expect(screen.getByText("Default COI")).toBeInTheDocument();
    expect(screen.getByText("3")).toBeInTheDocument(); // documentCount
    expect(screen.getByText(/1 active/i)).toBeInTheDocument();
  });

  it("mobile reflow: the vendors list is a stacked-table with labeled cells (#181)", async () => {
    // Pins the responsive-table reflow so the Template / Docs / Active-links
    // columns collapse into a readable card below md instead of being clipped.
    server.use(http.get(url("/api/vendors"), () => jsonOk(VENDORS)));

    renderWithProviders(<VendorsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );

    const table = document.querySelector("table.stacked-table");
    expect(table).not.toBeNull();
    expect(table?.querySelector('td[data-label="Requirements"]')).not.toBeNull();
    expect(table?.querySelector('td[data-label="Active links"]')).not.toBeNull();
  });

  it("empty requirements render a Set-requirements link to the vendor, not a silent dash (#190)", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonOk([
          { ...VENDORS[0], id: "v_set", name: "Needs Reqs LLC", complianceTemplateId: null, complianceTemplateName: null },
        ]),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByRole("link", { name: /needs reqs llc/i })).toBeInTheDocument(),
    );

    const setLink = screen.getByRole("link", { name: /set requirements/i });
    expect(setLink).toHaveAttribute("href", "/vendors/v_set");
    // The old silent placeholder must be gone.
    expect(screen.queryByText(/^—$/)).toBeNull();
  });
});

describe("VendorsPage — add-vendor mutation (#36)", () => {
  it("happy path: POST /api/vendors then re-fetches list, toasts success, clears the form", async () => {
    let listCalls = 0;
    server.use(
      http.get(url("/api/vendors"), () => {
        listCalls++;
        return jsonOk(VENDORS);
      }),
      http.post(url("/api/vendors"), () => jsonOk({ id: "v_new_01" })),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );
    expect(listCalls).toBe(1);

    // Query by placeholder (stable contract) rather than positional
    // `document.querySelectorAll("input")[0/1]`, so a future header
    // search/filter input doesn't shift the indices silently.
    fireEvent.input(
      screen.getByPlaceholderText(/acme catering/i),
      { target: { value: "New Sub LLC" } },
    );
    fireEvent.input(
      screen.getByPlaceholderText(/ops@acmecatering\.com/i),
      { target: { value: "ops@new.test" } },
    );
    fireEvent.click(screen.getByRole("button", { name: /add vendor/i }));

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Vendor added"),
    );
    // Invalidation re-reads the list.
    await waitFor(() => expect(listCalls).toBe(2));
    // Form cleared — reads the same placeholder-scoped inputs.
    expect(
      (screen.getByPlaceholderText(/acme catering/i) as HTMLInputElement).value,
    ).toBe("");
    expect(
      (screen.getByPlaceholderText(/ops@acmecatering\.com/i) as HTMLInputElement).value,
    ).toBe("");
  });

  it("error: server-side error surfaces via toast.error with the human message", async () => {
    server.use(
      http.get(url("/api/vendors"), () => jsonOk(VENDORS)),
      http.post(url("/api/vendors"), () =>
        jsonError(
          "vendors.duplicate_name",
          "A vendor with that name already exists.",
          { status: 409 },
        ),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: /acme subcontractor/i }),
      ).toBeInTheDocument(),
    );

    fireEvent.input(
      screen.getByPlaceholderText(/acme catering/i),
      { target: { value: "Acme Subcontractor" } },
    );
    fireEvent.click(screen.getByRole("button", { name: /add vendor/i }));

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        "A vendor with that name already exists.",
      ),
    );
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});

function makeVendorRow(i: number) {
  return {
    id: `v_${i}`,
    name: `Vendor ${String(i).padStart(2, "0")}`,
    contactEmail: `vendor${i}@acme.test`,
    contactPhone: null,
    category: null,
    complianceTemplateId: null,
    complianceTemplateName: null,
    documentCount: 0,
    activePortalLinks: 0,
    coverage: { status: "NoRequirements", missingTypes: [], coveredThrough: null },
  };
}

describe("VendorsPage — client-side search + pagination (#187)", () => {
  it("search narrows the list by name and shows a no-match state", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonOk([makeVendorRow(1), { ...makeVendorRow(2), name: "Beachfront Janitorial" }]),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByRole("link", { name: "Vendor 01" })).toBeInTheDocument(),
    );

    fireEvent.change(screen.getByLabelText(/search vendors/i), {
      target: { value: "beachfront" },
    });

    expect(screen.getByRole("link", { name: "Beachfront Janitorial" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Vendor 01" })).toBeNull();

    fireEvent.change(screen.getByLabelText(/search vendors/i), {
      target: { value: "zzz-nomatch" },
    });
    expect(screen.getByText(/no vendors match your search/i)).toBeInTheDocument();
  });

  it("paginates client-side at 25 per page; Next reveals the rest (#187)", async () => {
    const many = Array.from({ length: 30 }, (_, i) => makeVendorRow(i + 1));
    server.use(http.get(url("/api/vendors"), () => jsonOk(many)));

    renderWithProviders(<VendorsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByRole("link", { name: "Vendor 01" })).toBeInTheDocument(),
    );

    // Page 1: the 25th vendor is visible, the 26th is not.
    expect(screen.getByRole("link", { name: "Vendor 25" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Vendor 26" })).toBeNull();
    expect(screen.getByText(/page 1 of 2/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /next/i }));

    // Page 2: the overflow vendors appear, page-1 ones are gone.
    expect(screen.getByRole("link", { name: "Vendor 26" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Vendor 30" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Vendor 01" })).toBeNull();
    expect(screen.getByText(/page 2 of 2/i)).toBeInTheDocument();

    // Prev returns to page 1.
    fireEvent.click(screen.getByRole("button", { name: /prev/i }));
    expect(screen.getByRole("link", { name: "Vendor 01" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Vendor 26" })).toBeNull();
    expect(screen.getByText(/page 1 of 2/i)).toBeInTheDocument();
  });

  it("searching from a later page snaps back to page 1 (#187)", async () => {
    const many = Array.from({ length: 30 }, (_, i) => makeVendorRow(i + 1));
    server.use(http.get(url("/api/vendors"), () => jsonOk(many)));

    renderWithProviders(<VendorsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByRole("link", { name: "Vendor 01" })).toBeInTheDocument(),
    );

    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    expect(screen.getByText(/page 2 of 2/i)).toBeInTheDocument();

    // Search a term that STILL spans two pages (matches all 30) so the snap-back
    // is driven by the onChange page reset, not by the safePage clamp — removing
    // the reset must make this fail. (#187 review — test-quality reviewer)
    fireEvent.change(screen.getByLabelText(/search vendors/i), {
      target: { value: "Vendor" },
    });
    expect(screen.getByRole("link", { name: "Vendor 01" })).toBeInTheDocument();
    expect(screen.getByText(/page 1 of 2/i)).toBeInTheDocument();
  });

  it("self-heals when the loaded list shrinks below the current page (safePage clamp) (#187)", async () => {
    const many = Array.from({ length: 30 }, (_, i) => makeVendorRow(i + 1));
    server.use(http.get(url("/api/vendors"), () => jsonOk(many)));

    const { queryClient } = renderWithProviders(<VendorsPage />, { auth: authedMe });
    await waitFor(() =>
      expect(screen.getByRole("link", { name: "Vendor 01" })).toBeInTheDocument(),
    );

    fireEvent.click(screen.getByRole("button", { name: /next/i }));
    expect(screen.getByText(/page 2 of 2/i)).toBeInTheDocument();

    // The loaded list shrinks to a single page underneath us (e.g. vendors
    // deleted in another tab → refetch). safePage must re-base to page 1.
    queryClient.setQueryData(["vendors"], [makeVendorRow(1)]);

    await waitFor(() =>
      expect(screen.getByText(/page 1 of 1/i)).toBeInTheDocument(),
    );
    expect(screen.getByRole("link", { name: "Vendor 01" })).toBeInTheDocument();
  });
});

describe("VendorsPage — add-form contact email validation (#369)", () => {
  // This form has guarded the email since FP-076; #369 moved the rule into the shared
  // lib/contact-email predicate so the detail edit form could reuse it. These pin that the
  // extraction preserved the behavior — a silent regression here would re-open the gap
  // from the other side.
  function mount() {
    server.use(http.get(url("/api/vendors"), () => jsonOk([])));
    return renderWithProviders(<VendorsPage />, { auth: authedMe });
  }

  const addButton = () => screen.getByRole("button", { name: /add vendor/i });

  it("blocks Add and explains why when the email is malformed", async () => {
    mount();
    fireEvent.change(await screen.findByLabelText(/^name$/i), { target: { value: "Acme" } });
    const email = screen.getByLabelText(/^contact email$/i);

    fireEvent.change(email, { target: { value: "jane@acme,com" } });

    expect(addButton()).toBeDisabled();
    expect(screen.getByText(/enter a valid contact email address/i)).toBeInTheDocument();
    expect(email).toHaveAttribute("aria-invalid", "true");
  });

  it("allows a blank email — a vendor with no contact email is a valid state", async () => {
    mount();
    fireEvent.change(await screen.findByLabelText(/^name$/i), { target: { value: "Acme" } });
    expect(addButton()).toBeEnabled();
    expect(screen.queryByText(/enter a valid contact email address/i)).toBeNull();
  });

  it("sends the address stripped with the SHARED blank set, not a native trim", async () => {
    // The transport switched from a local `trimmedEmail || null` to the shared
    // trimContactEmail(), and nothing asserted it — the happy-path test discards the body.
    //
    // Padded with NEL (U+0085) and ZWSP (U+200B), NOT spaces and NOT NBSP/BOM. The choice is
    // load-bearing: `String.prototype.trim` DOES strip NBSP and BOM (its WhiteSpace production
    // covers them, unlike JS's `\s`), so padding with those would still pass against a plain
    // `.trim()` and assert nothing. NEL and ZWSP are in the shared BLANK class but survive
    // `.trim()`, so this fails the moment the form stops routing through the shared helper.
    //
    // That drift stores an address no reminder can reach: the per-(org, email) suppression key
    // the Resend webhook writes is trimmed (#340), and a padded address never matches it.
    let sent: unknown = undefined;
    server.use(
      http.get(url("/api/vendors"), () => jsonOk([])),
      http.post(url("/api/vendors"), async ({ request }) => {
        sent = await request.json();
        return jsonOk({ id: "v_new_01" });
      }),
    );
    renderWithProviders(<VendorsPage />, { auth: authedMe });

    fireEvent.change(await screen.findByLabelText(/^name$/i), { target: { value: "Acme" } });
    fireEvent.change(screen.getByLabelText(/^contact email$/i), {
      target: { value: "\u0085ops@new.test\u200B" },
    });

    expect(addButton()).toBeEnabled();
    fireEvent.click(addButton());

    await waitFor(() => expect(sent).toBeDefined());
    expect((sent as { contactEmail: string }).contactEmail).toBe("ops@new.test");
  });

  it("submits a non-ASCII address the shared predicate accepts", async () => {
    // Regression guard for the second-pass finding: this input sat in a real <form> as
    // type="email", so the browser's native constraint validation ran — and its local-part
    // grammar is ASCII-only. `jos\u00E9@empresa.es` is in the shared corpus's ACCEPT list and the
    // detail form saves it happily, but here Add stayed enabled, no error rendered, and the
    // submit handler never fired: a dead end, and the exact form-to-form drift #369 exists to
    // remove. The field is now inputMode="email" (soft-keyboard hint only), so the shared
    // predicate is the only gate. Reverting to type="email" leaves `sent` undefined here.
    let sent: unknown = undefined;
    server.use(
      http.get(url("/api/vendors"), () => jsonOk([])),
      http.post(url("/api/vendors"), async ({ request }) => {
        sent = await request.json();
        return jsonOk({ id: "v_new_02" });
      }),
    );
    renderWithProviders(<VendorsPage />, { auth: authedMe });

    fireEvent.change(await screen.findByLabelText(/^name$/i), { target: { value: "Empresa" } });
    fireEvent.change(screen.getByLabelText(/^contact email$/i), {
      target: { value: "jos\u00E9@empresa.es" },
    });

    expect(screen.queryByText(/enter a valid contact email address/i)).toBeNull();
    expect(addButton()).toBeEnabled();
    fireEvent.click(addButton());

    await waitFor(() => expect(sent).toBeDefined());
    expect((sent as { contactEmail: string }).contactEmail).toBe("jos\u00E9@empresa.es");
  });
});
