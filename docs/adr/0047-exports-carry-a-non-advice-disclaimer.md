# 0047. Every generated export carries one non-advice disclaimer — on by default, not behind a flag (CLM-3)

- **Status:** accepted
- **Date:** 2026-07-31
- **Deciders:** Ruben G. (founder), Claude (implementing #402)

## Context

CompliDrop generates three artifacts a customer hands to someone else:

| Artifact | Route | Builder |
|---|---|---|
| Audit report PDF | `GET /api/export/audit-report` | `ExportService.BuildAuditReportAsync` |
| Vendor compliance package PDF | `GET /api/export/vendor/{id}` | `ExportService.BuildVendorReportAsync` |
| Documents CSV | `GET /api/export/csv` | `ExportService.BuildCsvAsync` |

Each prints a per-document **"Compliant" / "Action needed" / "Expiring soon"** verdict under a
`CompliDrop Audit Report` / `Vendor Compliance Package` masthead. None carried any qualifier. The
audit PDF's footer was the bare attribution `CompliDrop · {org name}`; the vendor package had no
footer at all; the CSV ended at its last data row.

The product's honest framing — *"automated tools … flag whether they appear to meet your
requirements … **not** legal, insurance, or professional advice … we do not guarantee that every
extracted value or compliance result is accurate"* — exists, and is well drafted, but it lives in
`/terms`, which nobody opens. **The export is precisely the surface that leaves the app**: it is
forwarded to an insurer, a broker, an auditor, or opposing counsel, and it is where reliance forms.
A third party reading "Compliant" off a CompliDrop-branded PDF has been given no way to know that
the label is an automated reading of a document, not a coverage verification.

Two facts sharpen this. First, an **ACORD 25 certificate confers no rights and amends nothing** —
the same premise behind [ADR 0043](0043-additional-insured-claim-wording-staged-behind-flag.md)
(CLM-1) — so a verdict computed from the certificate's face cannot speak to what the policy
actually does. Second, several verdicts on the export are already known to be softer than they look:
the never-graded (zero-applicable-rules) document prints "Expiring soon" with an empty checked-set
behind it ([#443](https://github.com/neboxdev/complidrop/issues/443), ADR 0045), and a distrusted
extraction is deliberately **not** demoted at the document level (ADR 0042).

This is **counsel-gate item CLM-3** (`docs/rule-engine/G1-COUNSEL-BRIEF.md` §0, launch-blocking,
pending a licensed **Texas attorney's** sign-off), filed as
[#402](https://github.com/neboxdev/complidrop/issues/402).

## Decision

**Every generated export carries one shared non-advice disclaimer, ON BY DEFAULT.**

### 1. One constant, three artifacts

`ExportService.Disclaimer` is the single source:

> Statuses reflect automated reading of documents as uploaded; certificates do not modify policies.
> Verify current coverage with the issuing carrier.

All three artifacts read that constant. Three hand-copied literals is how the two PDFs drift apart —
and how a counsel-mandated reword lands on two surfaces out of three. Pinned by
`ExportDisclaimerTests`: the sentence appears exactly once in `ExportService.cs`.

Two sentences, and no more than the ticket proposed. It states what the statuses **are** (an
automated reading of the document as uploaded), what a certificate **cannot do** (modify the policy),
and what the reader should do instead (ask the carrier). It deliberately does **not** invent new
legal claims, restate the liability cap, or promise a verification the product does not perform — an
over-reaching disclaimer is its own liability, and the Terms already own the full treatment.

### 2. The PDFs render it from the shared page chrome, in `page.Footer()`

`ApplyPageDefaults(PageDescriptor page, string? attribution)` now applies the footer alongside the
page size, margin and default text style. Both PDF builders call it; a new PDF export cannot pick up
the chrome without the disclaimer, and `ExportDisclaimerTests` pins that every `container.Page`
composition in the file calls through it.

- **`page.Footer()`, not `page.Content()`.** QuestPDF repeats the footer on **every** page, so the
  disclaimer travels with any page of a forwarded export. In the content flow it would print once, at
  the end — on the last page of a multi-page audit report, i.e. the page nobody forwards.
- **Above, never displacing, the attribution.** Each line is its own `Column` item, so the disclaimer
  and `CompliDrop · {org name}` cannot collide, and the longest org name the register endpoint
  accepts (`InputLengths.OrganizationName`, 200) wraps within its own line rather than pushing the
  disclaimer off the page. Pinned by a 120-document, 200-`W` org-name render that must still
  paginate and return a valid PDF (a footer that outgrows its page is a QuestPDF layout exception —
  a 500 on the export, not a cosmetic defect).
- The vendor package passes `attribution: null`: it never loads the `Organization` row, and adding a
  query for a branding line is not what #402 asks for. The disclaimer is not optional; the
  attribution is.

`ExportService.PdfFooterLines(string?)` is an **internal seam**. QuestPDF FlateDecode-compresses the
content stream *and* draws text as subset-font glyph ids, so the rendered words are absent from the
bytes at any setting — a PDF-text library remains a disproportionate test dependency (the #197
judgement). The chrome renders exactly the sequence this seam returns, one `Text` item per element,
so pinning the seam pins what the footer says — the same technique #262 used with
`QueryAuditSliceAsync`.

### 3. The CSV appends it as a trailing row

A single-field record **after** the data. Never a preamble above the header: FP-102 deliberately
shaped row 1 as the header line Excel and pandas both key on, and a note there breaks that. A short
trailing row is unambiguously not a document row, and both parsers accept it — whereas a rectangular
row padded to twelve columns would read as a document *named after the disclaimer*. The sentence
contains no comma, so it is written unquoted and cannot split into extra columns.

### 4. On by default — deliberately NOT behind a feature flag

The repo's counsel-gated corrections (`RuleEngine:Enabled`, `TemplateCorrections:Enabled`,
`ComplianceClaims:CorrectedAdditionalInsuredWording`) are all merged-but-inert, default-OFF. **This
one is not**, and the distinction is the point:

- Those flags stage strings that change **what a verdict asserts**. CLM-1's wording moves
  "Names X as additional insured" ↔ "Certificate indicates X as additional insured"; an unreviewed
  flip alters legal meaning **in either direction**, so OFF is the safe default.
- A disclaimer where none exists is **strictly risk-reducing and one-directional**. It cannot make
  the product worse than today's "no disclaimer at all", which is exactly the defect #402 reports. A
  default-OFF flag would ship the code and leave the reported bug live in production — the failure
  mode ADR 0043's staging exists to avoid, inverted.
- It changes **no verdict and no value**: same statuses, same columns, same rows. It is additive
  copy on the artifact, not a claim about any document.

### 5. The wording is provisional, and refined at the CLM-1 pass

`G1-COUNSEL-BRIEF.md` §0 CLM-3 stays ⬜ (pending attorney confirmation) and now records that the
disclaimer is **shipped and live**, with the exact string to be confirmed or refined in the same pass
as CLM-1's additional-insured sentence — the two highest-leverage strings in the product, reviewed
together. Refinement is a one-line edit to `ExportService.Disclaimer` plus its verbatim test pin: no
flag flip, no runbook, no re-grade. That is the whole reason the constant is single-sourced.

## Consequences

### Positive

- The reliance artifact now qualifies itself. A third party reading a forwarded PDF sees, on every
  page, that the status is an automated reading and that the certificate does not modify the policy.
- Counsel refines one string in one place; the change reaches all three artifacts by construction.
- Structurally durable: a fourth export path either goes through `ApplyPageDefaults` (and carries the
  disclaimer) or fails the pin.
- Partially covers the read-surface overclaims already recorded against the export —
  [#443](https://github.com/neboxdev/complidrop/issues/443)'s never-graded "Expiring soon" and ADR
  0042's undemoted distrusted extraction now at least print under a non-advice qualifier. It does not
  close either; both remain about what the verdict *says*.

### Negative

- The audit PDF's footer grows from one line to three-ish (the disclaimer wraps to two at 8pt on
  Letter), costing ~25pt of content height per page. Accepted: an audit artifact trades a little
  density for the qualifier.
- The CSV gains a trailing row that is not a document. A naive consumer that assumes every row after
  the header is a document sees one extra row with a single populated cell. The trailing position and
  short width are what make that recoverable; the alternative (no disclaimer on the CSV) is worse.
- The wording ships before sign-off. Mitigated by being one-directional (§4) and single-sourced (§5),
  but it *is* an unreviewed legal string in production until CLM-3 clears.

### Neutral

- The document-detail page, the dashboard and the rules page are untouched. They are in-app surfaces
  the customer reads while looking at the product's own framing; #402 is about what leaves the app.
- `SampleCertificateGenerator` (the generated sample COI) is deliberately **not** in scope. It is a
  simulated *vendor document*, not a CompliDrop assertion about anything, and it does not use the
  shared export chrome.
- The email templates are out of scope — a reminder makes no compliance assertion about a document.

## Alternatives considered

### Option A — Stage it behind a default-OFF flag, mirroring ADR 0043
**Rejected.** See §4: a default-OFF flag leaves the reported defect live in prod. The staging pattern
exists for strings whose flip changes a claim's legal meaning in either direction; a disclaimer is
one-directional risk reduction and there is nothing to protect prod from.

### Option B — Link to the Terms instead of restating the qualifier
Print `See complidrop.com/terms` in the footer. **Rejected**: the whole finding is that the
disclaimer lives where nobody looks. A URL on a printed PDF handed to an adjuster relocates the
problem; the qualifier has to be readable on the page it qualifies.

### Option C — PDFs only, skip the CSV
The ticket's own framing ("and ideally the CSV"). **Rejected**: the CSV is exported *by* the same
customers *for* the same readers and carries the same verdict column. Skipping it would leave one of
three artifacts bare and re-open the drift the shared constant exists to prevent.

### Option D — A full disclaimer block (liability cap, no-warranty, subprocessor notice)
**Rejected**: over-reach. The Terms own the full treatment; a long block on the artifact invites the
reader to treat the PDF as the contract, and every additional sentence is another unreviewed legal
claim shipped ahead of CLM-3.

### Option D′ — Adopt `G1-LEGAL-RESEARCH.md` §V.1 verbatim now
§V.1 drafts the conspicuous **"NOT LEGAL ADVICE — NOT A SUBSTITUTE FOR AN ATTORNEY"** notice
(the Tex. Gov't Code §81.101(c) formula) for the *rule engine's* obligation reports, and that
document's §VII item 8 explicitly asks counsel whether the export should adopt it *ahead of* the rule
engine. **Rejected for now, and deliberately left to counsel**: §V.1 speaks about regulatory
obligations and cited sources — a surface today's export does not have — and shipping an unreviewed
attorney-formula notice is exactly the over-reach Option D rejects. The shipped sentence is #402's
own proposal, sized to what the export actually asserts. If counsel answers item 8 "yes", it is a
one-line change to the same constant.

### Option E — Also soften the verdict LABELS on the export ("Compliant" → "Appears compliant")
**Rejected / out of scope**: that is a verdict-semantics change of exactly the kind ADR 0043 stages
behind a flag, it would split the export's vocabulary from every in-app surface
(`DisplayLabels.Compliance`), and the known label overclaims are separately ticketed
([#443](https://github.com/neboxdev/complidrop/issues/443)). #402 adds a qualifier; it does not
re-word verdicts.

## References

- Tickets: [#402](https://github.com/neboxdev/complidrop/issues/402) (bug), [#48](https://github.com/neboxdev/complidrop/issues/48) (rolling bug-fix epic); related [#443](https://github.com/neboxdev/complidrop/issues/443) (never-graded read-surface overclaim)
- Gate: `docs/rule-engine/G1-COUNSEL-BRIEF.md` §0 (CLM-3) + §C; `docs/rule-engine/G1-LEGAL-RESEARCH.md` §V.1 (the rule-engine notice) + §VII item 8 (the open "should the export adopt §V.1 now?" question this leaves to counsel)
- ADRs: [0043](0043-additional-insured-claim-wording-staged-behind-flag.md) (the flag-staging precedent this deliberately does **not** follow, and why), [0042](0042-distrusted-extraction-per-field-gate-and-coverage-exclusion.md) + [0045](0045-canonical-document-type-vocabulary.md) (export verdicts that are softer than they read), [0041](0041-future-effective-not-yet-in-force-reads-pending.md) (the export's future-effective overlay)
- Code: `api/CompliDrop.Api/Services/ExportService.cs` (`Disclaimer`, `PdfFooterLines`, `ApplyPageDefaults`, `BuildCsvAsync`), `api/CompliDrop.Api.Tests/ExportDisclaimerTests.cs`
- Consistent with: `frontend/src/app/terms/page.tsx` ("Automatic reading is a head start, not advice")
