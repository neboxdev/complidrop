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
      expect(screen.getByRole("option", { name: "Acme Catering" })).toBeInTheDocument(),
    );
    expect(screen.getByRole("option", { name: "Beta Security" })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Search vendors"), {
      target: { value: "acme" },
    });

    expect(screen.getByRole("option", { name: "Acme Catering" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Beta Security" })).toBeNull();
  });

  it("selecting an existing vendor calls onChange with its id and name", async () => {
    const onChange = vi.fn();
    server.use(vendorsHandler([{ id: "v1", name: "Acme Catering" }]));

    renderWithProviders(
      <VendorPicker value={null} onChange={onChange} />,
      { auth: authedMe },
    );

    const option = await screen.findByRole("option", { name: "Acme Catering" });
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

    await screen.findByRole("option", { name: "Acme Catering" });
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

    await screen.findByRole("option", { name: "Acme Catering" });
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

  // FP-130 (a11y): the picker is a real ARIA combobox now, not a bare input with a
  // permanently-true aria-expanded. These pin the semantics + keyboard model so a
  // regression that drops them (and re-mutes the picker for screen readers) fails loudly.
  describe("combobox a11y (FP-130)", () => {
    it("exposes combobox + listbox/option roles with a meaningful aria-expanded", async () => {
      server.use(vendorsHandler([{ id: "v1", name: "Acme Catering" }]));
      renderWithProviders(<VendorPicker value={null} onChange={vi.fn()} />, { auth: authedMe });

      // Wait for the option first so the vendors query has settled (avoids reading aria-expanded
      // mid-load when matches is still empty).
      const option = await screen.findByRole("option", { name: "Acme Catering" });
      expect(option).toBeInTheDocument();
      expect(screen.getByRole("listbox", { name: "Vendors" })).toBeInTheDocument();
      const combobox = screen.getByRole("combobox", { name: "Search vendors" });
      // An option is present → expanded is true (not the old permanently-bare attribute).
      expect(combobox).toHaveAttribute("aria-expanded", "true");
    });

    it("collapses aria-expanded to false when there is nothing to pick or create", async () => {
      server.use(vendorsHandler([])); // empty book, empty query → no options, nothing to create yet
      renderWithProviders(<VendorPicker value={null} onChange={vi.fn()} />, { auth: authedMe });

      const combobox = await screen.findByRole("combobox", { name: "Search vendors" });
      await waitFor(() => expect(combobox).toHaveAttribute("aria-expanded", "false"));

      // Typing a brand-new name gives a create affordance → expanded flips true (genuinely dynamic).
      fireEvent.change(combobox, { target: { value: "Northside Tents" } });
      expect(combobox).toHaveAttribute("aria-expanded", "true");
    });

    it("Arrow keys highlight an option (aria-activedescendant) and Enter selects it", async () => {
      const onChange = vi.fn();
      server.use(
        vendorsHandler([
          { id: "v1", name: "Acme Catering" },
          { id: "v2", name: "Beta Security" },
        ]),
      );
      renderWithProviders(<VendorPicker value={null} onChange={onChange} />, { auth: authedMe });

      // Wait for the vendors to load before driving the keyboard (matches is empty mid-load).
      await screen.findByRole("option", { name: "Acme Catering" });
      const combobox = screen.getByRole("combobox", { name: "Search vendors" });
      fireEvent.keyDown(combobox, { key: "ArrowDown" }); // highlight first
      fireEvent.keyDown(combobox, { key: "ArrowDown" }); // highlight second

      const second = screen.getByRole("option", { name: "Beta Security" });
      expect(second).toHaveAttribute("aria-selected", "true");
      expect(combobox).toHaveAttribute("aria-activedescendant", second.id);

      fireEvent.keyDown(combobox, { key: "Enter" });
      expect(onChange).toHaveBeenCalledWith({ id: "v2", name: "Beta Security" });
    });

    it("announces the filtered result count in a polite live region", async () => {
      server.use(
        vendorsHandler([
          { id: "v1", name: "Acme Catering" },
          { id: "v2", name: "Acme Security" },
        ]),
      );
      renderWithProviders(<VendorPicker value={null} onChange={vi.fn()} />, { auth: authedMe });

      // Wait for the vendors to load so the filter has data to count.
      await screen.findByRole("option", { name: "Acme Catering" });
      const combobox = screen.getByRole("combobox", { name: "Search vendors" });
      // Empty query → silent (no chatter on mount).
      expect(screen.getByRole("status").textContent).toBe("");

      fireEvent.change(combobox, { target: { value: "acme" } });
      expect(screen.getByRole("status")).toHaveTextContent("2 vendors found");

      fireEvent.change(combobox, { target: { value: "Acme Catering" } });
      expect(screen.getByRole("status")).toHaveTextContent("1 vendor found");
    });

    it("Enter with nothing highlighted creates the typed vendor", async () => {
      const onChange = vi.fn();
      server.use(
        vendorsHandler([{ id: "v1", name: "Acme Catering" }]),
        http.post(url("/api/vendors"), () => jsonOk({ id: "v_new" })),
      );
      renderWithProviders(<VendorPicker value={null} onChange={onChange} />, { auth: authedMe });

      const combobox = await screen.findByRole("combobox", { name: "Search vendors" });
      fireEvent.change(combobox, { target: { value: "Northside Tents" } });
      fireEvent.keyDown(combobox, { key: "Enter" });

      await waitFor(() =>
        expect(onChange).toHaveBeenCalledWith({ id: "v_new", name: "Northside Tents" }),
      );
    });

    it("Escape clears the typed query", async () => {
      server.use(vendorsHandler([{ id: "v1", name: "Acme Catering" }]));
      renderWithProviders(<VendorPicker value={null} onChange={vi.fn()} />, { auth: authedMe });

      const combobox = await screen.findByRole<HTMLInputElement>("combobox", { name: "Search vendors" });
      fireEvent.change(combobox, { target: { value: "Beta" } });
      expect(combobox.value).toBe("Beta");

      fireEvent.keyDown(combobox, { key: "Escape" });
      expect(combobox.value).toBe("");
    });

    it("ArrowUp from the cold state highlights the LAST option (idiomatic APG)", async () => {
      server.use(
        vendorsHandler([
          { id: "v1", name: "Acme Catering" },
          { id: "v2", name: "Beta Security" },
        ]),
      );
      renderWithProviders(<VendorPicker value={null} onChange={vi.fn()} />, { auth: authedMe });

      await screen.findByRole("option", { name: "Acme Catering" });
      const combobox = screen.getByRole("combobox", { name: "Search vendors" });
      fireEvent.keyDown(combobox, { key: "ArrowUp" }); // from -1 → last

      const last = screen.getByRole("option", { name: "Beta Security" });
      expect(last).toHaveAttribute("aria-selected", "true");
      expect(screen.getByRole("option", { name: "Acme Catering" })).toHaveAttribute(
        "aria-selected",
        "false",
      );
    });

    it("hovering an option tracks the highlight (aria-selected follows the pointer)", async () => {
      server.use(
        vendorsHandler([
          { id: "v1", name: "Acme Catering" },
          { id: "v2", name: "Beta Security" },
        ]),
      );
      renderWithProviders(<VendorPicker value={null} onChange={vi.fn()} />, { auth: authedMe });

      const second = await screen.findByRole("option", { name: "Beta Security" });
      fireEvent.mouseEnter(second);
      expect(second).toHaveAttribute("aria-selected", "true");
    });
  });
});
