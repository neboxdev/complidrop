# Extraction regression fixtures

Each subdirectory is one fixture: an input document (`input.pdf`) plus a YAML file
(`expected.yaml`) describing the fields an accurate extraction should return.

`expected.yaml` has **two consumers**, and stays canonical for both:

1. **Host-free extraction-client suite** (`ExtractionFixtureHarness` + the
   Gemini/Anthropic client tests, active today). It mocks the HTTP boundary and
   *synthesizes* a canned provider response from `expected.yaml`, then asserts the
   client maps it back — proving request shaping + response parsing. It does **not**
   read `input.pdf`, and the synthesized round-trip is not a measure of model
   accuracy (that is an explicit non-goal).
2. **Live `ExtractionRegressionTests`** (future, Phase 3). It reads `input.pdf`,
   runs the real OCR + LLM pipeline, and compares the live extraction to
   `expected.yaml` honoring each field's `tolerance`.

The `input.pdf` files are placeholders for consumer (2) — swap them with real PDFs
(real customer-sourced or publicly-available ACORD 25 / license / permit samples)
before running the regression suite against the live extraction pipeline. Editing an
`expected.yaml` affects both suites.

## Fixture layout

```
ExtractionFixtures/
  01_coi_general_liability/
    input.pdf         (placeholder — replace with real COI)
    expected.yaml
  02_coi_workers_comp/
  03_license_contractor/
  04_permit_construction/
  05_certification_safety/
```

## Sourcing real documents

- ACORD 25 blank forms: acord.org (free samples)
- Contractor license sample: most state contractor boards publish sample licenses
- Safety certification sample: OSHA 10/30 templates

To replace: overwrite `input.pdf` in place. `expected.yaml` stays canonical.
