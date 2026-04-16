# Extraction regression fixtures

Each subdirectory is one fixture: an input document (`input.pdf`) plus a YAML file
(`expected.yaml`) describing the fields an accurate extraction should return.

Phase 3 wires `ExtractionRegressionTests` to this folder. Until then, these files
exist as the slot where real customer-sourced or publicly-available ACORD 25 /
license / permit samples will be dropped. The `input.pdf` files are placeholders
— swap them with real PDFs before running the regression suite against the live
extraction pipeline.

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
