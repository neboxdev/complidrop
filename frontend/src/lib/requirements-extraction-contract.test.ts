/**
 * Rule-vs-prompt contract (#272): every field the requirement catalog can put on a
 * compliance rule must be a field the extraction prompt instructs the model to emit —
 * otherwise the requirement can NEVER pass and every honest certificate gets flagged
 * (the "Professional liability (E&O)" door: the menu offered
 * `professional_liability_limit` while the prompt never asked for it, so `min_value`
 * failed on every document).
 *
 * The Gemini/Anthropic structured-output schema is an open name/value array — the
 * prompt's field list IS the schema for field names — so this test reads the C# prompt
 * source directly and asserts catalog ⊆ prompt. It imports the live catalog (not a
 * copy), so adding a menu item with a never-extracted field fails CI here.
 *
 * CI note: `.github/workflows/frontend-ci.yml` includes ExtractionPrompts.cs in its
 * path filter so a prompt-side edit (e.g. removing a field) also runs this test.
 */

import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

import { REQUIREMENT_TYPES } from "@/lib/requirements";

const promptPath = resolve(
  __dirname,
  "../../..",
  "api",
  "CompliDrop.Api",
  "Services",
  "Extraction",
  "ExtractionPrompts.cs",
);
const promptSource = readFileSync(promptPath, "utf8");

// The assertion surface is the FIELDS TO EXTRACT section ONLY — not the whole C# file.
// A field whose only remaining occurrence is a FORMATTING RULES bullet, a C# comment, or
// the Version slug has been dropped from the schema-defining list, and the test must
// fail (the review caught exactly that partial-edit door: every #272 field appears in
// both the list and a formatting bullet).
const FIELDS_HEADER = "FIELDS TO EXTRACT WHEN PRESENT";
const FIELDS_END = "FORMATTING RULES";
const fieldListSection = promptSource.slice(
  promptSource.indexOf(FIELDS_HEADER),
  promptSource.indexOf(FIELDS_END),
);

/** Word-boundary containment: 'liability_limit' must NOT pass via 'general_liability_limit'. */
function sectionNamesField(fieldName: string): boolean {
  return new RegExp(`(^|[^a-z0-9_])${fieldName}([^a-z0-9_]|$)`, "im").test(fieldListSection);
}

describe("requirement catalog ⊆ extraction prompt (#272)", () => {
  const fieldNames = [...new Set(REQUIREMENT_TYPES.map((t) => t.fieldName))];

  it("sanity: the catalog has fields and the prompt's field list was found", () => {
    expect(fieldNames.length).toBeGreaterThan(0);
    expect(promptSource.indexOf(FIELDS_HEADER)).toBeGreaterThan(-1);
    expect(promptSource.indexOf(FIELDS_END)).toBeGreaterThan(promptSource.indexOf(FIELDS_HEADER));
  });

  it.each(fieldNames)(
    "catalog field '%s' is in the prompt's FIELDS TO EXTRACT list",
    (fieldName) => {
      expect(
        sectionNamesField(fieldName),
        `The requirement catalog offers a rule on '${fieldName}', but the extraction ` +
          `prompt's FIELDS TO EXTRACT list never instructs the model to emit it — that ` +
          `requirement can never pass. Add the field to ExtractionPrompts.cs (and bump ` +
          `Version) or remove the menu item.`,
    ).toBe(true);
    },
  );

  it.each(["certificate_holder", "description_of_operations"])(
    "engine fallback field '%s' is in the prompt's FIELDS TO EXTRACT list",
    (fieldName) => {
      // ComplianceCheckService's additional-insured checkbox fallback reads these two
      // fields — they are part of the rule-vs-prompt contract even though no catalog
      // entry targets them directly.
      expect(sectionNamesField(fieldName)).toBe(true);
    },
  );
});
