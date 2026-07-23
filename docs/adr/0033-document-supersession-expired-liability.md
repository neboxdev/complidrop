# 0033. Document supersession — latest cert per (vendor, type) for the Expired liability

- **Status:** accepted (amended 2026-06-23 — see [Amendment 1](#amendment-1-2026-06-23--superseder-must-extend-coverage); amended 2026-07-23 — see [Amendment 2](#amendment-2-2026-07-23--superseder-must-be-continuous-effective-date))
- **Date:** 2026-06-23
- **Deciders:** Ruben G. (founder), Claude (implementing #327)

## Context

The dashboard "Expired" count (and the expiry-pipeline "Expired" bucket) counted **every** document whose stored expiration date is in the past — **including** documents already superseded by a renewed certificate for the same vendor + document type. So a vendor who uploads a renewal leaves their old expired COI inflating the count: the dashboard says "2 expired" when only one requirement is actually unmet (or none, if the renewal is current). FP-042 (`docs/ux/final-pass-2026-06-10.md`) proposed bucketing by latest-document-per-(vendor, type); its clean half (excluding not-yet-graded docs from the compliance-rate denominator) shipped in #318, and this supersession half was deferred (#327) because changing what "Expired" means is a cross-surface data-semantics decision: the rule must apply **consistently** across the dashboard stats, the expiry-pipeline buckets, the documents list (`/documents?status=Expired`, deep-linked from the dashboard cards), the reminder windows, and the audit export — otherwise the dashboard would say "1 expired" and the list it links to would show 2. There is also no first-class "supersede / archive" concept in the data model.

## Decision

**A document is _superseded_ when a newer document (later `CreatedAt`) exists for the same `(VendorId, DocumentType)`, with `VendorId` non-null** — i.e. it is not the latest cert for that requirement. Superseded documents are **de-counted** (computed, set-based) from the live Expired liability — **not hidden, not archived**: no new column or state, the old cert stays fully visible in the documents list and the audit export. "Latest" is by `CreatedAt` (most recently uploaded), matching the vendor coverage rollup (FP-074). A document with no vendor belongs to no requirement group and is never superseded.

The rule is a single shared predicate, `DocumentSupersession.IsCurrent` / `.IsSuperseded` — a set-based correlated `EXISTS` over the same tenant-/soft-delete-scoped `db.Documents` the caller reads from (so a deleted or cross-org doc never counts as the superseder; no per-document round trips).

**Where it applies (the Expired liability surfaces):**
- Dashboard `expired` count.
- Expiry-pipeline `expired` bucket.
- Documents list `?status=Expired` — so the deep-linked list matches the dashboard count **exactly** (the headline consistency contract, pinned by a test).
- Reminder windows — a superseded cert never generates a reminder (the vendor already renewed; don't pester them).
- Audit export (CSV + PDF audit report + vendor package): superseded documents are **annotated** ("Superseded" column / "(superseded)" marker), **not** removed — the export must keep the full history (an auditor wants to see the expired old cert AND its renewal), with the annotation making the supersession explicit.

**Where it deliberately does NOT apply (scoped out per the ticket's "Expired" framing):**
- The dashboard `compliant` / `nonCompliant` / `expiringSoon` counts and the list's non-Expired filters — these are verdict/informational tallies, not the Expired liability the ticket scopes; leaving them avoids disturbing the well-tested #257 mutual-exclusivity and #294 boundary invariants.
- The **future** expiry-pipeline buckets (30 / 60 / 90 / beyond) — a not-yet-expired cert is informational ("when does each doc expire"), not yet a missed liability, and a vendor commonly renews early so both the old (soon) and new (far) cert legitimately appear in the upcoming view.

The shared `DocumentSupersession` predicate is the single source of truth, so extending the model to ExpiringSoon or the future buckets, if a later ticket decides to, is a one-line change.

## Consequences

### Positive
- The dashboard Expired count, the expiry-pipeline expired bucket, and the deep-linked documents list now **agree** — a renewed COI's old expired copy stops inflating any of them. Pinned by a test that seeds two certs for one (vendor, type) and asserts all three show one.
- Renewed vendors aren't pestered: a superseded cert in the reminder window generates no reminder.
- The audit export stays a complete record but makes supersession explicit, so an auditor reading "old COI — Expired" next to "new COI — Compliant" sees the old one flagged Superseded.
- No schema change, no new state to keep in sync — supersession is always computed from the current document set, so it can never go stale.

### Negative
- Each Expired count/filter gains a correlated `EXISTS` subquery. A dedicated `IX_Documents_Supersession` on `(VendorId, DocumentType, CreatedAt)` serves it directly — load-bearing specifically for the **reminder worker**, which runs on `SystemDbContext` (no tenant filter), so its `EXISTS` has no `OrganizationId` predicate and the `(OrganizationId, VendorId)` index (org-leading) can't seek the inner `VendorId` lookup; without a `VendorId`-leading index it would seq-scan the whole (cross-org, never-pruned) `Documents` table per candidate, hourly. The index is non-partial so it fully covers the `Vendor` FK — EF therefore drops the now-redundant single-column `IX_Documents_VendorId` (a clean consolidation, not a net new index). (#327 review.)
- The export gains a `Superseded` column (inserted after `Compliance`, before the trailing GUID) — an additive format change; the GUID stays the last column (FP-102).
- The scoping (Expired-only, not ExpiringSoon) means a renewed-early cert can still appear as a future expiry in the pipeline / `expiringSoon` count while not generating a reminder — a deliberate, documented asymmetry (the dashboard's upcoming view is informational; the reminder is an action that a renewal cancels).

### Neutral
- "Latest" is by `CreatedAt`, not `ExpirationDate`. A vendor who uploads a stale (earlier-expiry) cert *later* would have it treated as current — rare, and consistent with the vendor coverage rollup's "most recently uploaded = current intent."

## Alternatives considered

### Option A — Hide or archive superseded documents (a first-class `IsSuperseded`/`Archived` column)
Add persisted supersession state and hide/archive old certs. **Rejected**: a schema change + a synchronization burden (the flag must update on every upload/delete), and hiding the old cert guts the audit trail. Computed de-counting needs no state and can never go stale; the export keeps (and annotates) the history.

### Option B — Apply supersession to ALL dashboard counts and pipeline buckets
Make every requirement contribute at most one document everywhere (compliant, expiringSoon, the future buckets). **Rejected for this ticket**: the ticket scopes "Expired"; touching the verdict tallies risks the #257/#294 invariants, and the future-expiry view is legitimately informational (renew-early certs). The shared predicate keeps this a trivial future extension.

### Option C — De-count superseded docs from the export too (exclude them)
**Rejected**: the audit export is a per-document compliance record; dropping a superseded doc would hide that the vendor had an expired cert and renewed it. Annotation preserves the record while applying the rule.

## Amendment 1 (2026-06-23) — superseder must extend coverage

A post-merge re-review of #327 found that the original predicate (`a newer doc by CreatedAt exists`) de-counted an older **expired** cert whenever **any** newer doc existed for the same `(vendor, type)`, **with no check that the newer doc was actually valid coverage**. So a "renewal" that was still processing (`ExpirationDate` null until extraction), had no expiry, or expired *earlier* than the cert it replaced would make a **genuinely-unmet expired liability silently disappear** from the dashboard, the deep-linked list, and the reminder windows. For a compliance product that is the exact failure mode we must not ship: a missed certificate must never be hidden by an invalid renewal.

**Revised rule:** a document `d` is superseded by `o` (same `VendorId` non-null, same `DocumentType`) only when `o.CreatedAt > d.CreatedAt` **AND** `o.ExpirationDate` is non-null and `o.ExpirationDate >= d.ExpirationDate` — i.e. the newer upload must **extend coverage** (expire no earlier than the cert it replaces).

- **Still-processing / no-expiry renewal** (`o.ExpirationDate` null): `NULL >= date` is false in SQL → does **not** supersede → the old expired cert stays counted and keeps reminding until a real covering cert lands. (Closes the unsafe window.)
- **Future-dated or equal-expiry renewal** (a genuine renewal, or a duplicate re-upload of the same cert): still supersedes → the old copy never double-counts. The original anti-inflation goal and every original test are preserved (they seed same- or later-expiry superseders).
- **Coverage-extending but non-compliant renewal**: still supersedes the *Expired* liability, but the new cert surfaces under the **NonCompliant** tally — nothing is hidden, only re-classified (accurate: the cert isn't expired, it's deficient).
- **Earlier-expiry doc uploaded later** (the "stale correction" case the original Neutral note flagged): no longer treated as the current cert — both show as expired, the conservative-safe outcome.

This narrows supersession (it now de-counts strictly fewer docs), so it can only ever **show more** liability, never less — a safe direction for a compliance tool. The `DocumentSupersession` predicate and the export's in-memory `SupersededIds` mirror were updated together and are pinned equal by a test. The `IX_Documents_Supersession (VendorId, DocumentType, CreatedAt)` index still serves the seek; `ExpirationDate` is a cheap residual filter on the already-narrow per-(vendor, type) row set.

## Amendment 2 (2026-07-23) — superseder must be continuous (effective-date door)

A deep domain review (filed as [#362](https://github.com/neboxdev/complidrop/issues/362)) found that Amendment 1 closed the failure through the **expiration-date** door but left the **effective-date** door open. The revised predicate de-counted an old expired cert whenever a newer cert **extended coverage** (`o.ExpirationDate >= d.ExpirationDate`) — with **no check on when the newer cert becomes effective**. So a renewal effective *in the future* — bought after the old cert already lapsed, starting next month — still superseded the old expired cert and made it vanish from the dashboard Expired count, the deep-linked list, and the reminder windows, even though the vendor has **no coverage in force** for the gap between now and the new policy's effective date. That is the exact "a genuinely-unmet expired liability silently disappearing" failure Amendment 1 exists to prevent, reached through the other date.

**Revised rule:** a document `d` is superseded by `o` (same `VendorId` non-null, same `DocumentType`) only when `o.CreatedAt > d.CreatedAt` **AND** `o.ExpirationDate` is non-null and `o.ExpirationDate >= d.ExpirationDate` (Amendment 1, extends coverage) **AND** `o.EffectiveDate` is null OR `o.EffectiveDate <= d.ExpirationDate` (Amendment 2, continuous — the renewal picks up on or before the old cert lapses, leaving no gap).

- **Future-effective renewal** (`o.EffectiveDate > d.ExpirationDate`): opens a live coverage gap → does **not** supersede → the old expired cert stays counted and keeps reminding until real in-force coverage lands. (Closes the effective-date door.)
- **Continuous renewal** (`o.EffectiveDate <= d.ExpirationDate`): no gap → still supersedes, exactly as before Amendment 2.
- **Unknown effective date** (`o.EffectiveDate` null): treated as continuous — the Amendment 1 coverage-extension clause still gates on a real (non-null) expiry, so a still-processing upload with no expiry is already excluded; a doc with an expiry but no effective date is not held back on the effective-date axis.
- The boundary is **date-adjacency-conservative**: a renewal effective *exactly on* the old cert's expiry date supersedes (continuous); one effective *the day after* does not (a one-day gap). Erring toward showing the gap is the safe direction for a compliance tool.

Both `EffectiveDate` and `ExpirationDate` are face dates stored at UTC midnight (`CanonicalDocumentFields.ParseUtcDate`), so `o.EffectiveDate <= d.ExpirationDate` is a plain `timestamptz`-vs-`timestamptz` comparison that reproduces a calendar-date comparison with no `AT TIME ZONE` (ADR 0009 / 0027). Like Amendment 1 this **narrows** supersession (de-counts strictly fewer docs → shows more liability, never less), needs **no migration** (`EffectiveDate` already exists), and is a cheap residual filter on the already-narrow per-(vendor, type) row set served by `IX_Documents_Supersession`. The `DocumentSupersession` predicate and the export's `SupersededIds` mirror were updated together and stay pinned equal by a test (extended with future-effective gap/continuity cases). See [ADR 0041](0041-future-effective-not-yet-in-force-reads-pending.md), the sibling that closes the same effective-date failure on the **verdict** side (a standalone future-effective cert grading Compliant with no prior cert to supersede).

## References

- Tickets: [#327](https://github.com/neboxdev/complidrop/issues/327), [#318](https://github.com/neboxdev/complidrop/issues/318) (FP-042 clean half), [#241](https://github.com/neboxdev/complidrop/issues/241), [#150](https://github.com/neboxdev/complidrop/issues/150)
- ADRs: [0027](0027-compliance-date-window-boundaries.md) (the date-window deriver the Expired predicate uses)
- Code: `Services/DocumentSupersession.cs`, `Endpoints/DashboardEndpoints.cs`, `Endpoints/DocumentEndpoints.cs` (`ListDocuments`), `BackgroundServices/ReminderBackgroundService.cs`, `Services/ExportService.cs`
- FP-042 in `docs/ux/final-pass-2026-06-10.md`
