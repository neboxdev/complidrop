/**
 * Vendors list page — state matrix + new-vendor mutation (#36).
 */
import { describe, it, expect, vi, beforeEach } from "vitest";
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
} from "@/test";

const { toastSuccess, toastError } = vi.hoisted(() => ({
  toastSuccess: vi.fn(),
  toastError: vi.fn(),
}));
vi.mock("sonner", () => ({
  toast: { success: toastSuccess, error: toastError },
  Toaster: () => null,
}));

beforeEach(() => {
  toastSuccess.mockClear();
  toastError.mockClear();
});

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

  it("error: page chrome renders, vendor list falls back to the empty fallback", async () => {
    server.use(
      http.get(url("/api/vendors"), () =>
        jsonError("server.error", "vendor index down", { status: 500 }),
      ),
    );

    renderWithProviders(<VendorsPage />, { auth: authedMe });

    // Wait for the query to settle (the Loading row disappears) before
    // checking the empty fallback — the in-flight Loading row would
    // otherwise race the assertion.
    await waitFor(() =>
      expect(screen.queryByText(/^loading…$/i)).toBeNull(),
    );
    expect(
      screen.getByRole("heading", { name: /^vendors$/i }),
    ).toBeInTheDocument();
    // The page's branching is `vendors.isLoading || empty || .map`.
    // On error, isLoading is false and data is undefined → `vendors.data ?? []`
    // is empty → the "No vendors yet" row renders.
    expect(screen.getByText(/no vendors yet/i)).toBeInTheDocument();
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

    const inputs = document.querySelectorAll("input");
    fireEvent.input(inputs[0], { target: { value: "New Sub LLC" } });
    fireEvent.input(inputs[1], { target: { value: "ops@new.test" } });
    fireEvent.click(screen.getByRole("button", { name: /add vendor/i }));

    await waitFor(() =>
      expect(toastSuccess).toHaveBeenCalledWith("Vendor added"),
    );
    // Invalidation re-reads the list.
    await waitFor(() => expect(listCalls).toBe(2));
    // Form cleared.
    const refreshedInputs = document.querySelectorAll("input");
    expect((refreshedInputs[0] as HTMLInputElement).value).toBe("");
    expect((refreshedInputs[1] as HTMLInputElement).value).toBe("");
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

    const inputs = document.querySelectorAll("input");
    fireEvent.input(inputs[0], { target: { value: "Acme Subcontractor" } });
    fireEvent.click(screen.getByRole("button", { name: /add vendor/i }));

    await waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        "A vendor with that name already exists.",
      ),
    );
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});
