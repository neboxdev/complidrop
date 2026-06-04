/**
 * DocumentTypeSelect — native <select> over the canonical document-type
 * vocabulary, shared by the upload staging card and the detail-page type editor
 * (#186). Pins that human labels render, the incoming value resolves
 * case-insensitively, onChange emits the canonical value, and a legacy/unknown
 * value is preserved (shown) rather than silently collapsed.
 */
import { describe, it, expect, vi } from "vitest";
import { fireEvent, screen } from "@testing-library/react";
import { DocumentTypeSelect } from "./DocumentTypeSelect";
import { renderWithProviders } from "@/test";

describe("DocumentTypeSelect (#186)", () => {
  it("renders the canonical types with human labels", () => {
    renderWithProviders(<DocumentTypeSelect value="coi" onChange={vi.fn()} aria-label="Type" />);

    const select = screen.getByRole("combobox", { name: "Type" }) as HTMLSelectElement;
    expect(select.value).toBe("coi");
    expect(screen.getByRole("option", { name: "Certificate of Insurance" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Business License" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Permit" })).toBeInTheDocument();
  });

  it("resolves the incoming value case-insensitively (legacy upper-case)", () => {
    renderWithProviders(<DocumentTypeSelect value="COI" onChange={vi.fn()} aria-label="Type" />);
    expect((screen.getByRole("combobox", { name: "Type" }) as HTMLSelectElement).value).toBe("coi");
  });

  it("emits the canonical value on change", () => {
    const onChange = vi.fn();
    renderWithProviders(<DocumentTypeSelect value="coi" onChange={onChange} aria-label="Type" />);

    fireEvent.change(screen.getByRole("combobox", { name: "Type" }), {
      target: { value: "permit" },
    });
    expect(onChange).toHaveBeenCalledWith("permit");
  });

  it("preserves an unknown stored type as a displayed option instead of hiding it", () => {
    renderWithProviders(<DocumentTypeSelect value="weird_legacy" onChange={vi.fn()} aria-label="Type" />);
    // The unknown value is surfaced (humanized fallthrough returns it verbatim),
    // so the user sees what's actually stored rather than a wrong "coi".
    expect(screen.getByRole("option", { name: "weird_legacy" })).toBeInTheDocument();
  });
});
