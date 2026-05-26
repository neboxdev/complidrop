/**
 * Vendors list page — state matrix + new-vendor mutation (#36).
 */
import { describe, it, expect } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
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
  },
];

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

    expect(screen.getByText(/^loading…$/i)).toBeInTheDocument();
  });

  it("empty: renders the no-vendors-yet copy", async () => {
    server.use(http.get(url("/api/vendors"), () => jsonOk([])));

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    await waitFor(() =>
      expect(screen.getByText(/no vendors yet/i)).toBeInTheDocument(),
    );
  });

  it("error: a 5xx surfaces an error card with the server message and a Retry affordance, NOT the empty fallback (#80)", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonError("server.error", "vendor index down", { status: 500 }),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    // Wait for the query to settle (the Loading row disappears) before
    // asserting the error state. After #80, the page branches into a
    // distinct error row instead of falling through to "No vendors yet"
    // — a backend outage must NOT read like a brand-new org.
    await waitFor(() =>
      expect(screen.queryByText(/^loading…$/i)).toBeNull(),
    );
    expect(
      screen.getByRole("heading", { name: /^vendors$/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/couldn't load vendors/i),
    ).toBeInTheDocument();
    expect(screen.getByText("vendor index down")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /retry/i }),
    ).toBeInTheDocument();
    // Negative: the empty-state copy must NOT appear on error.
    expect(screen.queryByText(/no vendors yet/i)).toBeNull();
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
    expect(screen.queryByText(/couldn't load vendors/i)).toBeNull();
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
      screen.getByPlaceholderText(/mike's electrical/i),
      { target: { value: "New Sub LLC" } },
    );
    fireEvent.input(
      screen.getByPlaceholderText(/mike@acme\.com/i),
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
      (screen.getByPlaceholderText(/mike's electrical/i) as HTMLInputElement).value,
    ).toBe("");
    expect(
      (screen.getByPlaceholderText(/mike@acme\.com/i) as HTMLInputElement).value,
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
      screen.getByPlaceholderText(/mike's electrical/i),
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
