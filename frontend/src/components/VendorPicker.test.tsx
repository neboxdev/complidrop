/**
 * VendorPicker — the type-ahead + inline add-new vendor selector shared by the
 * documents upload staging card and the orphaned-row assign affordance (#186).
 *
 * Pins the four behaviors a regression would silently break:
 *   1. existing vendors render and filter as you type,
 *   2. picking one calls onChange with {id, name},
 *   3. a name that doesn't exist offers "Add new vendor" → POSTs + selects it,
 *   4. a set value collapses to a pill whose Change button clears the selection.
 */
import { describe, it, expect, vi } from "vitest";
import { http } from "msw";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import { VendorPicker } from "./VendorPicker";
import { renderWithProviders, server, url, jsonOk, jsonError, authedMe } from "@/test";

function vendorsHandler(
  vendors: ReadonlyArray<{ id: string; name: string }>,
) {
  return http.get(url("/api/vendors"), () => jsonOk(vendors));
}

describe("VendorPicker (#186)", () => {
  it("lists existing vendors and narrows them as the user types", async () => {
    server.use(
      vendorsHandler([
        { id: "v1", name: "Acme Catering" },
        { id: "v2", name: "Beta Security" },
      ]),
    );

    renderWithProviders(
      <VendorPicker value={null} onChange={vi.fn()} />,
      { auth: authedMe },
    );

    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Acme Catering" })).toBeInTheDocument(),
    );
    expect(screen.getByRole("button", { name: "Beta Security" })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Search vendors"), {
      target: { value: "acme" },
    });

    expect(screen.getByRole("button", { name: "Acme Catering" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Beta Security" })).toBeNull();
  });

  it("selecting an existing vendor calls onChange with its id and name", async () => {
    const onChange = vi.fn();
    server.use(vendorsHandler([{ id: "v1", name: "Acme Catering" }]));

    renderWithProviders(
      <VendorPicker value={null} onChange={onChange} />,
      { auth: authedMe },
    );

    const option = await screen.findByRole("button", { name: "Acme Catering" });
    fireEvent.click(option);

    expect(onChange).toHaveBeenCalledWith({ id: "v1", name: "Acme Catering" });
  });

  it("offers Add-new for an unknown name, creates the vendor, and selects it", async () => {
    const onChange = vi.fn();
    let createBody: unknown = null;
    server.use(
      vendorsHandler([{ id: "v1", name: "Acme Catering" }]),
      http.post(url("/api/vendors"), async ({ request }) => {
        createBody = await request.json();
        return jsonOk({ id: "v_new" });
      }),
    );

    renderWithProviders(
      <VendorPicker value={null} onChange={onChange} />,
      { auth: authedMe },
    );

    await screen.findByRole("button", { name: "Acme Catering" });
    fireEvent.change(screen.getByLabelText("Search vendors"), {
      target: { value: "Northside Tents" },
    });

    const addButton = screen.getByRole("button", { name: /add new vendor/i });
    fireEvent.click(addButton);

    await waitFor(() =>
      expect(onChange).toHaveBeenCalledWith({ id: "v_new", name: "Northside Tents" }),
    );
    expect(createBody).toEqual({ name: "Northside Tents" });
  });

  it("does NOT offer Add-new when the typed name already exists (case-insensitive)", async () => {
    server.use(vendorsHandler([{ id: "v1", name: "Acme Catering" }]));

    renderWithProviders(
      <VendorPicker value={null} onChange={vi.fn()} />,
      { auth: authedMe },
    );

    await screen.findByRole("button", { name: "Acme Catering" });
    fireEvent.change(screen.getByLabelText("Search vendors"), {
      target: { value: "acme catering" },
    });

    expect(screen.queryByRole("button", { name: /add new vendor/i })).toBeNull();
  });

  it("with a value set, shows the selected pill and Change clears it", () => {
    const onChange = vi.fn();
    // useVendors() still fires even in the selected state (hooks run
    // unconditionally) — declare its surface so the run stays warning-free.
    server.use(vendorsHandler([{ id: "v1", name: "Acme Catering" }]));
    renderWithProviders(
      <VendorPicker value={{ id: "v1", name: "Acme Catering" }} onChange={onChange} />,
      { auth: authedMe },
    );

    expect(screen.getByText("Acme Catering")).toBeInTheDocument();
    // The search input is NOT rendered in the selected state.
    expect(screen.queryByLabelText("Search vendors")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /change/i }));
    expect(onChange).toHaveBeenCalledWith(null);
  });

  it("surfaces a friendly message via onCreateError when creating a vendor fails", async () => {
    const onCreateError = vi.fn();
    server.use(
      vendorsHandler([]),
      http.post(url("/api/vendors"), () =>
        jsonError("server.error", "nope", { status: 500 }),
      ),
    );

    renderWithProviders(
      <VendorPicker value={null} onChange={vi.fn()} onCreateError={onCreateError} />,
      { auth: authedMe },
    );

    fireEvent.change(await screen.findByLabelText("Search vendors"), {
      target: { value: "Brand New" },
    });
    fireEvent.click(screen.getByRole("button", { name: /add new vendor/i }));

    await waitFor(() => expect(onCreateError).toHaveBeenCalled());
  });
});
