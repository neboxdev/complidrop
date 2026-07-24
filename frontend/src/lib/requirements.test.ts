/**
 * Vendor-requirements catalog + sentence mapping (#192) — the translation layer
 * between plain-English requirements and the engine's rule shape.
 */
import { describe, it, expect } from "vitest";
import {
  formatMoney,
  parseMoneyInput,
  isSuspiciouslyLowMoney,
  requirementSentence,
  requirementErrorMessage,
  findRequirementType,
  REQUIREMENT_TYPES,
  REQUIREMENT_GROUPS,
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

  it("truncates a pasted decimal to whole dollars (never concatenates the cents)", () => {
    // "$1,000,000.50" must NOT become 100000050 — coverage limits are whole dollars.
    expect(parseMoneyInput("$1,000,000.50")).toBe(1_000_000);
    expect(parseMoneyInput("12.99")).toBe(12);
    expect(parseMoneyInput(".50")).toBeNull(); // no whole-dollar part
  });

  it("honors k/m suffixes so '2M' isn't truncated to $2 (#319 FP-084)", () => {
    expect(parseMoneyInput("2M")).toBe(2_000_000);
    expect(parseMoneyInput("2m")).toBe(2_000_000);
    expect(parseMoneyInput("1.5M")).toBe(1_500_000);
    expect(parseMoneyInput("500k")).toBe(500_000);
    expect(parseMoneyInput("$2 m")).toBe(2_000_000);
    // No suffix still parses as whole dollars; a plain "$2" stays $2 (the form warns).
    expect(parseMoneyInput("$2")).toBe(2);
    // Trailing junk after the unit does NOT read as millions — falls through to whole dollars.
    expect(parseMoneyInput("2MB")).toBe(2);
  });

  it("flags a suspiciously-low coverage amount (#319 FP-084)", () => {
    expect(isSuspiciouslyLowMoney(2)).toBe(true);
    expect(isSuspiciouslyLowMoney(9_999)).toBe(true);
    expect(isSuspiciouslyLowMoney(10_000)).toBe(false);
    expect(isSuspiciouslyLowMoney(1_000_000)).toBe(false);
    expect(isSuspiciouslyLowMoney(0)).toBe(false);
    expect(isSuspiciouslyLowMoney(null)).toBe(false);
  });
});

describe("Certifications catalog group (#319 FP-085)", () => {
  it("exposes a Certifications group and matches the seeded cert rule", () => {
    expect(REQUIREMENT_GROUPS).toContain("Certifications");
    expect(REQUIREMENT_TYPES.some((t) => t.group === "Certifications")).toBe(true);
    // The seeded "Security Service" rule (certification / expiration_date / required) now has a
    // real catalog home, so it renders a curated sentence + gets an Edit/re-add path.
    const match = findRequirementType({
      documentType: "certification",
      fieldName: "expiration_date",
      operator: "required",
    });
    expect(match?.group).toBe("Certifications");
    expect(requirementSentence({
      documentType: "certification",
      fieldName: "expiration_date",
      operator: "required",
      expectedValue: null,
    })).toMatch(/certification has not expired/i);
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

  it("renders the seeded liquor-liability rule as a curated money sentence (#400)", () => {
    // The Caterer system checklist seeds a graded `liquor_liability_limit` min_value rule
    // (#400). Without a catalog home it rendered the raw-number fallback ("Liquor liability
    // limit must be at least 1000000") — this pins the curated "$1,000,000" sentence, exactly
    // as general_liability above (the FP-085 defect class, fixed for the seeded cert rule too).
    expect(
      requirementSentence({
        documentType: "coi",
        fieldName: "liquor_liability_limit",
        operator: "min_value",
        expectedValue: "1000000",
      }),
    ).toBe("Carries at least $1,000,000 in liquor liability coverage");
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

  it("interpolates the typed value for text requirements (additional insured / license type)", () => {
    expect(
      requirementSentence({
        documentType: "coi",
        fieldName: "additional_insured",
        operator: "contains",
        expectedValue: "Riverside Event Hall",
      }),
    ).toMatch(/riverside event hall.*additional insured/i);
    expect(
      requirementSentence({
        documentType: "license",
        fieldName: "license_type",
        operator: "equals",
        expectedValue: "CDL",
      }),
    ).toMatch(/holds a .cdl. license/i);
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

  it("never leaks a raw operator token for a future/unknown operator", () => {
    const s = requirementSentence({
      documentType: "coi",
      fieldName: "general_liability_limit",
      operator: "in_range", // not an engine operator the catalog knows
      expectedValue: "1-5",
    });
    expect(s).not.toMatch(/in_range/);
    expect(s).toMatch(/custom rule/i);
  });
});

describe("additional-insured claim copy — CLM-1 flag (#396)", () => {
  const aiRule = {
    documentType: "coi",
    fieldName: "additional_insured",
    operator: "contains",
    expectedValue: "Riverside Event Hall",
  };
  const aiType = findRequirementType(aiRule)!;

  // The two authoritative copy strings (TRR §3). Kept as literals so a drift in either the legacy
  // (prod-default) or the researched-corrected wording goes red.
  const LEGACY_SENTENCE = "Names “Riverside Event Hall” as additional insured";
  const CORRECTED_SENTENCE = "Certificate indicates “Riverside Event Hall” as additional insured";
  const LEGACY_ERROR = "“Riverside Event Hall” was not found as an additional insured.";
  const CORRECTED_ERROR = "The certificate does not indicate “Riverside Event Hall” as an additional insured.";

  it("requirementSentence: flag off / absent / undefined = today's EXACT copy (prod-identical)", () => {
    expect(requirementSentence(aiRule)).toBe(LEGACY_SENTENCE);
    expect(requirementSentence(aiRule, {})).toBe(LEGACY_SENTENCE);
    expect(requirementSentence(aiRule, { correctedAdditionalInsuredWording: false })).toBe(LEGACY_SENTENCE);
    // empty name → "your company", still the legacy verb.
    expect(requirementSentence({ ...aiRule, expectedValue: null })).toBe(
      "Names “your company” as additional insured",
    );
  });

  it("requirementSentence: flag on = the researched 'certificate indicates…' copy", () => {
    expect(requirementSentence(aiRule, { correctedAdditionalInsuredWording: true })).toBe(CORRECTED_SENTENCE);
    expect(
      requirementSentence({ ...aiRule, expectedValue: null }, { correctedAdditionalInsuredWording: true }),
    ).toBe("Certificate indicates “your company” as additional insured");
  });

  it("the catalog sentence/errorMessage stay byte-identical to pre-#396 (the flag-off default source)", () => {
    expect(aiType.sentence("Riverside Event Hall")).toBe(LEGACY_SENTENCE);
    expect(aiType.sentence(null)).toBe("Names “your company” as additional insured");
    expect(aiType.errorMessage("Riverside Event Hall")).toBe(LEGACY_ERROR);
    expect(aiType.errorMessage(null)).toBe("“Your company” was not found as an additional insured.");
  });

  it("requirementErrorMessage: flag off = legacy 'not found', flag on = 'does not indicate'", () => {
    expect(requirementErrorMessage(aiType, "Riverside Event Hall")).toBe(LEGACY_ERROR);
    expect(requirementErrorMessage(aiType, "Riverside Event Hall", { correctedAdditionalInsuredWording: false })).toBe(
      LEGACY_ERROR,
    );
    expect(requirementErrorMessage(aiType, "Riverside Event Hall", { correctedAdditionalInsuredWording: true })).toBe(
      CORRECTED_ERROR,
    );
    expect(requirementErrorMessage(aiType, null, { correctedAdditionalInsuredWording: true })).toBe(
      "The certificate does not indicate “Your company” as an additional insured.",
    );
  });

  it("the flag is scoped to additional insured — every other requirement is unchanged when it's on", () => {
    const glRule = {
      documentType: "coi",
      fieldName: "general_liability_limit",
      operator: "min_value",
      expectedValue: "1000000",
    };
    const glType = findRequirementType(glRule)!;
    expect(requirementSentence(glRule, { correctedAdditionalInsuredWording: true })).toBe(
      "Carries at least $1,000,000 in general liability insurance",
    );
    expect(requirementErrorMessage(glType, "1000000", { correctedAdditionalInsuredWording: true })).toBe(
      glType.errorMessage("1000000"),
    );
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
    // Text requirements resolve too (so the Edit affordance can pre-fill them).
    expect(
      findRequirementType({ documentType: "coi", fieldName: "additional_insured", operator: "contains" })?.key,
    ).toBe("additional_insured");
    expect(
      findRequirementType({ documentType: "license", fieldName: "license_type", operator: "equals" })?.key,
    ).toBe("license_type");
    // A different documentType for the same field is a DIFFERENT requirement (no match here).
    expect(
      findRequirementType({ documentType: "license", fieldName: "general_liability_limit", operator: "min_value" }),
    ).toBeUndefined();
  });
});
