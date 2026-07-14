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

import { REQUIREMENT_TYPES, findRequirementType } from "@/lib/requirements";

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

const seedPath = resolve(
  __dirname,
  "../../..",
  "api",
  "CompliDrop.Api",
  "Data",
  "Seed",
  "ComplianceTemplateSeed.cs",
);
const seedSource = readFileSync(seedPath, "utf8");

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
  // Escaped so a future fieldName with a regex metacharacter fails loudly instead of
  // silently matching something else ('policy.number' must not match 'policy_number').
  const escaped = fieldName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`(^|[^a-z0-9_])${escaped}([^a-z0-9_]|$)`, "im").test(fieldListSection);
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

// ---------------------------------------------------------------------------
// Reverse direction (#400): every SEEDED compliance rule has a catalog home.
//
// The catalog⊆prompt test above only guards one direction. Nothing caught the #400 defect
// class: a rule added to a SYSTEM template's seed (e.g. the Caterer `liquor_liability_limit`
// min_value rule) with NO matching REQUIREMENT_TYPES entry renders as a context-free raw-token
// fallback on /rules (no curated sentence, no Edit pencil, no re-add path) — the same FP-085
// orphan the Security Service cert rule hit. This asserts seed ⊆ catalog.
//
// The seeded (documentType, fieldName, operator) triples are DERIVED from the C# seed source,
// not hand-mirrored: the seed file itself avoids "brittle hand-mirror constants" (see its
// TemplateCount doc), and this test already reads C# source for the prompt direction. Each
// RuleSeed is a rigid positional literal — `new("<type>", "<field>"|null, "<operator>", …)` —
// so an anchored regex parses them deterministically; a `null` FieldName (allowed by the record)
// is skipped since a rule with no field has no catalog entry to match. A sanity floor fails the
// suite loudly if the parse ever matches nothing, rather than passing vacuously.
// ---------------------------------------------------------------------------

type SeededTriple = { documentType: string; fieldName: string; operator: string };

function parseSeededTriples(source: string): SeededTriple[] {
  // RuleSeed(string DocumentType, string? FieldName, string Operator, …): capture the first
  // three positional args. Args 1 & 3 are always quoted \w+ tokens; arg 2 is a quoted \w+ or the
  // literal `null`. The 3rd-arg-quoted requirement excludes the TemplateSeed `new("Name", "desc",
  // [ … ])` literals (their 3rd arg is a collection expression, never a quoted word).
  const re = /new\(\s*"(\w+)"\s*,\s*(?:"(\w+)"|null)\s*,\s*"(\w+)"/g;
  const seen = new Set<string>();
  const triples: SeededTriple[] = [];
  for (const m of source.matchAll(re)) {
    const [, documentType, fieldName, operator] = m;
    if (!fieldName) continue; // null FieldName → no field to grade → no catalog entry expected
    const key = `${documentType}|${fieldName}|${operator}`;
    if (seen.has(key)) continue;
    seen.add(key);
    triples.push({ documentType, fieldName, operator });
  }
  return triples;
}

describe("seeded rule fields ⊆ requirement catalog (#400)", () => {
  const seededTriples = parseSeededTriples(seedSource);

  it("sanity: the seed parser found the seeded rules (never a vacuous pass)", () => {
    // The seed file now carries BOTH gated rule sets (#416, ADR 0036 Amendment 3): the corrected
    // §4 set (7 distinct (type, field, operator) triples) plus the flag-off LegacyTemplates set,
    // whose three pre-#416 rules the correction removed (Security certification-expiry, Transport
    // license_type == CDL, Photographer E&O) re-enter the union — 10 distinct triples total. BOTH
    // sets are graded in production (the flag selects which), so both rightly feed the per-triple
    // catalog-home check below. A parse that suddenly finds far fewer means the RuleSeed literal
    // shape changed and this test's derivation must be revisited — fail loudly, don't pass empty.
    // (When the legacy set is retired after the sign-off + flip, this floor drops back to 7.)
    expect(seededTriples.length).toBeGreaterThanOrEqual(10);
  });

  it.each(seededTriples.map((t) => [`${t.documentType}/${t.fieldName}/${t.operator}`, t] as const))(
    "seeded rule '%s' has a matching REQUIREMENT_TYPES catalog entry",
    (_label, triple) => {
      expect(
        findRequirementType({
          documentType: triple.documentType,
          fieldName: triple.fieldName,
          operator: triple.operator,
        }),
        `The system seed installs a compliance rule on '${triple.fieldName}' ` +
          `(${triple.documentType} / ${triple.operator}), but REQUIREMENT_TYPES has no matching ` +
          `entry — it will render on /rules as a context-free raw-token fallback with no Edit or ` +
          `re-add path (the FP-085 / #400 defect). Add a catalog entry in requirements.ts.`,
      ).toBeDefined();
    },
  );
});
