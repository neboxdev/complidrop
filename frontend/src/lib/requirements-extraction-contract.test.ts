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

describe("requirement catalog ⊆ extraction prompt (#272)", () => {
  const fieldNames = [...new Set(REQUIREMENT_TYPES.map((t) => t.fieldName))];

  it("sanity: the catalog has fields and the prompt file was found", () => {
    expect(fieldNames.length).toBeGreaterThan(0);
    expect(promptSource).toContain("FIELDS TO EXTRACT WHEN PRESENT");
  });

  it.each(fieldNames)(
    "catalog field '%s' is named in the extraction prompt",
    (fieldName) => {
      expect(
        promptSource,
        `The requirement catalog offers a rule on '${fieldName}', but the extraction ` +
          `prompt never instructs the model to emit it — that requirement can never ` +
          `pass. Add the field to ExtractionPrompts.cs (and bump Version) or remove ` +
          `the menu item.`,
      ).toContain(fieldName);
    },
  );

  it.each(["certificate_holder", "description_of_operations"])(
    "engine fallback field '%s' is named in the extraction prompt",
    (fieldName) => {
      // ComplianceCheckService's additional-insured checkbox fallback reads these two
      // fields — they are part of the rule-vs-prompt contract even though no catalog
      // entry targets them directly.
      expect(promptSource).toContain(fieldName);
    },
  );
});
