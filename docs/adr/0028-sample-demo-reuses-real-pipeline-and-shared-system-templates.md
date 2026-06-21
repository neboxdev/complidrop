# 0028. The sample-certificate demo reuses the real pipeline; assigned system templates stay shared

- **Status:** accepted
- **Date:** 2026-06-21
- **Deciders:** Ruben G.

## Context

The onboarding cold-start audit ([#237](https://github.com/neboxdev/complidrop/issues/237),
`docs/ux/onboarding-cold-start-2026-06-10.md`) proved that the COI file is the *only* data a cold
"Pat" lacks anywhere in the funnel: she knows her vendor's name, email, and venue type by heart,
but cannot reach a compliance verdict without an insurance document on hand. The instant-value
ticket ([#238](https://github.com/neboxdev/complidrop/issues/238), epic
[#234](https://github.com/neboxdev/complidrop/issues/234)) closes that gap with a one-click "Try it
with a sample certificate" demo, and is feature-class so the decision below stands in for the
`/plan` step (the #237 audit already did the spec-refinement that `/plan` would produce).

Two design questions had to be settled before code:

1. **How does the sample reach a verdict — real pipeline or staged results?** Extraction costs real
   money (Document AI OCR + Gemini). We could either (a) upload a deterministic sample PDF and run
   the *real* upload → OCR → LLM → compliance pipeline, or (b) seed pre-computed extraction fields
   and a pre-set verdict, skipping the pipeline (free, instant, but a stage prop).

2. **What is the system-template-vs-org-clone model?** The audit (§4 → #238) explicitly asked this
   ticket to *decide* the seam it found: a vendor assigned the system "Caterer" checklist evaluates
   correctly, but the `/rules` page says "Your checklists — None yet", because assignment stores a
   foreign-key reference to the shared system template (`Vendor.ComplianceTemplateId`), never a
   per-org copy. Either (a) **clone-on-assign** — copy the system template into an editable org-owned
   checklist whenever it's assigned, so it shows up as "yours"; or (b) **keep assignment shared** and
   let the existing `/rules` "Use this" action be the only thing that mints an editable copy.

## Decision

### 1. The sample runs the REAL pipeline against a deterministic, obviously-fictional PDF

`POST /api/sample` generates a sample COI server-side (`SampleCertificateGenerator`, QuestPDF) and
uploads it through the *same* path a real document takes: blob storage → `Document` row at
`ExtractionStatus.Pending` → the extraction worker claims it → OCR → LLM → compliance evaluation.
There is no special-casing of the sample anywhere in the pipeline. The §7 re-run of the audit
already proved this path produces a clean "Compliant" verdict in ~40–90 s for an equivalent
document, so the epic's "< 2 min sample path" bar is met by reusing infrastructure rather than
building a parallel staged one.

The sample PDF is built to PASS the "Caterer" system checklist
(`ComplianceTemplateSeed.SampleVendorTemplateName`): general-liability each-occurrence $2,000,000
(≥ the $1M rule), workers-comp coverage present, and an expiration date always ~1 year out. The
three graded fields are echoed as plain machine-readable lines so OCR + the LLM extract them
reliably regardless of how the ACORD-style table is parsed.

Trust-building is the reason: a compliance product earns trust by showing its *actual* extraction
on the demo, not a puppet. The honesty also means the demo exercises the real failure modes — if
storage or extraction is down, the seed fails with a friendly error (`storage.unavailable`, the
#248 envelope) and the document surfaces the worker's `ProcessingError`, never the raw
"An unexpected error occurred." dead-end the audit hit (#247).

Because it is fictional customer data, every copy carries fictional insurer/policy identifiers and a
"SAMPLE — NOT A REAL CERTIFICATE OF INSURANCE" banner, watermark, and footer — the legal-compliance
guardrail called out in the ticket.

### 2. System-template assignment stays shared (NO clone-on-assign)

Assigning a system venue-type checklist to a vendor continues to store a foreign-key reference to
the single shared `IsSystemTemplate = true` row. Assignment does **not** mint a per-org copy. The
editable path is the pre-existing `/rules` "Use this" action, which clones a system template into an
org-owned, editable checklist on demand.

Rationale:

- **No silent fan-out / drift.** One shared row assigned to N vendors stays one row; an improvement
  to a system checklist reaches every vendor that references it. Clone-on-assign would scatter a
  frozen copy per vendor (or per assignment), so a later fix to the seeded rules would never reach
  the orgs that already onboarded — the worst outcome for a compliance product.
- **The editability AC is already met.** #238's "applying a template produces an editable, valid
  requirement set" is satisfied by "Use this" → org-owned clone (pinned by a new test). A user who
  wants to *tweak* gets a copy; a user who just wants the standard checklist gets a zero-cost
  reference.
- **Assignment must stay lightweight and tenant-safe.** The FK path is already guarded against
  cross-tenant template poisoning (`TemplateIsAssignable`, #273). Cloning on every assignment would
  add write amplification and a second copy to keep tenant-correct for no user-visible gain.

The visible consequence — `/rules` showing "None yet" while a system checklist is assigned to a
vendor — is a **display** gap, not a data-model one. Healing it belongs to
[#239](https://github.com/neboxdev/complidrop/issues/239) (the audit assigned the `/rules` "acknowledge
assigned system templates" delta there): the page should render assigned system templates as
first-class ("Caterer — used by Brightside Catering Co., suggested checklist"), reading the same
shared rows. This ADR fixes the *model* (shared); #239 fixes the *view*.

## Consequences

### Positive

- The demo is the real product, so its verdict is trustworthy and it exercises the same failure
  handling as a real upload (friendly storage/extraction errors, not raw 500s).
- One sample per org, fully removable: `DELETE /api/sample` soft-deletes the sample document +
  vendor (audit-logged via the interceptor) and deletes the blob, with no orphans. A partial
  unique index (`IX_Documents_OrganizationId_SampleUnique`, filtered to live sample rows) makes a
  concurrent double-click fail loudly at the DB and return the existing sample — true idempotency,
  not just an existence check.
- System checklists stay a single shared source of truth; seeded-rule improvements propagate to
  every assigned vendor with no migration or re-clone.
- The sample document is excluded from the plan document-limit count, so the "see the magic"
  moment is never blocked by a paywall; the per-org monthly cost ceiling still applies at
  extraction time (the sample's ~$0.01 is negligible on the free tier).

### Negative

- The sample costs one real extraction per seed (Document AI + Gemini). Bounded by the one-sample-
  per-org index and the monthly cost ceiling; accepted as the price of an honest demo.
- `/rules` continues to read "None yet" for an org whose only checklist is an *assigned* system
  template until #239 ships the display fix. The seam is now a documented, ticketed view gap rather
  than an undecided model question.
- Extraction is probabilistic: the LLM could in principle misread the sample. Mitigated by a
  deliberately clean, explicitly-labelled PDF and the plain field echo; the verdict-level contract
  ("a doc with these fields + Caterer ⇒ Compliant") is pinned by a unit test on the pure evaluator,
  independent of the LLM.

### Neutral

- Sample artifacts are ordinary tenant rows flagged `IsSample`, so they flow through every existing
  tenant filter, the soft-delete interceptor, audit logging, and the dashboard counts unchanged —
  they are simply *labelled* "Sample" in the UI and removable in one click. No parallel storage or
  lifecycle.

## Alternatives considered

### Seed pre-extracted results instead of running the pipeline (Q1, Option b)

Rejected: free and instant, but a stage prop. It would not exercise OCR/LLM/compliance, would drift
from real extraction output as prompts evolve, and would teach the user nothing true about what the
product does to *their* documents. The real pipeline already clears the < 2 min bar.

### Clone-on-assign (Q2, Option a)

Rejected: scatters frozen per-vendor copies, so seeded-rule improvements never reach already-
onboarded orgs (drift), adds write amplification and a second tenant-correctness surface, and buys
only a display benefit that #239 delivers better by rendering the shared rows as first-class.

## References

- Ticket: [#238](https://github.com/neboxdev/complidrop/issues/238) (epic
  [#234](https://github.com/neboxdev/complidrop/issues/234), track 2 — zero-touch onboarding)
- Source audit: `docs/ux/onboarding-cold-start-2026-06-10.md` §4 (→ #238 gap map)
- Follow-up that heals the `/rules` display seam:
  [#239](https://github.com/neboxdev/complidrop/issues/239) (delta 2)
- Companion patterns: [ADR 0013](0013-account-deletion-is-soft-delete-plus-pii-scrub.md)
  (soft-delete), the #248 friendly-error envelope, and the #273 cross-tenant template-assignment
  guard
- Code: `Endpoints/SampleEndpoints.cs`, `Services/SampleCertificateGenerator.cs`,
  `Data/Seed/ComplianceTemplateSeed.cs`
