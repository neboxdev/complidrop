/**
 * Vendor-requirements catalog + sentence mapping (#192) — the translation layer
 * between plain-English requirements and the engine's rule shape.
 */
import { describe, it, expect } from "vitest";
import {
  formatMoney,
  parseMoneyInput,
  requirementSentence,
  findRequirementType,
  REQUIREMENT_TYPES,
} from "./requirements";

describe("money helpers (#192)", () => {
  it("formats integers as $ with commas", () => {
    expect(formatMoney(1_000_000)).toBe("$1,000,000");
    expect(formatMoney(500_000)).toBe("$500,000");
    expect(formatMoney(0)).toBe("$0");
  });

  it("parses formatted or raw money to a bare integer; blank/non-numeric → null", () => {
    expect(parseMoneyInput("$1,000,000")).toBe(1_000_000);
    expect(parseMoneyInput("1000000")).toBe(1_000_000);
    expect(parseMoneyInput("  $2,000,000 ")).toBe(2_000_000);
    expect(parseMoneyInput("")).toBeNull();
    expect(parseMoneyInput("abc")).toBeNull();
  });
});

describe("requirementSentence (#192)", () => {
  it("renders a known min_value money rule as a plain sentence", () => {
    expect(
      requirementSentence({
        documentType: "coi",
        fieldName: "general_liability_limit",
        operator: "min_value",
        expectedValue: "1000000",
      }),
    ).toBe("Carries at least $1,000,000 in general liability insurance");
  });

  it("renders the not-expired rule honestly (no future-date promise)", () => {
    expect(
      requirementSentence({
        documentType: "coi",
        fieldName: "expiration_date",
        operator: "required",
        expectedValue: null,
      }),
    ).toBe("Insurance has not expired");
  });

  it("distinguishes coi vs license expiration by documentType", () => {
    expect(
      requirementSentence({
        documentType: "license",
        fieldName: "expiration_date",
        operator: "required",
        expectedValue: null,
      }),
    ).toBe("License has not expired");
  });

  it("falls back to a readable sentence for an unknown rule — never a raw token", () => {
    const s = requirementSentence({
      documentType: "coi",
      fieldName: "some_new_field",
      operator: "equals",
      expectedValue: "X",
    });
    expect(s).not.toMatch(/some_new_field/);
    expect(s).toMatch(/some new field/i);
  });
});

describe("REQUIREMENT_TYPES → engine mapping (#192)", () => {
  it("every catalog item maps to a valid engine rule shape", () => {
    for (const t of REQUIREMENT_TYPES) {
      expect(t.documentType).toBeTruthy();
      expect(t.fieldName).toBeTruthy();
      expect(["required", "equals", "contains", "min_value"]).toContain(t.operator);
      expect(t.errorMessage(t.valueKind === "money" ? "1000000" : "x")).toBeTruthy();
    }
  });

  it("findRequirementType matches on documentType + fieldName + operator", () => {
    expect(
      findRequirementType({ documentType: "coi", fieldName: "general_liability_limit", operator: "min_value" })?.key,
    ).toBe("general_liability");
    // A different documentType for the same field is a DIFFERENT requirement (no match here).
    expect(
      findRequirementType({ documentType: "license", fieldName: "general_liability_limit", operator: "min_value" }),
    ).toBeUndefined();
  });
});
