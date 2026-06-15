# 0027. Compliance date-window SQL sites use an exclusive instant upper bound, keeping the date-only deriver as the source of truth

- **Status:** accepted
- **Date:** 2026-06-15
- **Deciders:** Ruben G.

## Context

`ComplianceStatusDeriver.Effective` (added in #257) is the single source of truth for the
date-driven part of a document's compliance verdict. It compares **calendar dates**: a document is
Expired when `exp.Date < today.Date`, and ExpiringSoon when `exp.Date <= today.Date + 30`.

But the SQL read/sweep sites that must agree with that badge — the documents-list status filter
(`DocumentEndpoints`), the dashboard counts and expiry-pipeline buckets (`DashboardEndpoints`), and
the nightly `ComplianceSweepBackgroundService` — compared the **raw `timestamptz` `ExpirationDate`
instant** against `today + 30` at **UTC midnight** (`exp <= today.AddDays(30)`).

`ExpirationDate` is meant to be a face date stored at UTC midnight (`CanonicalDocumentFields.ParseUtcDate`
parses the extraction's `"YYYY-MM-DD"` with `AssumeUniversal` → `00:00:00Z`). When that holds, the
instant comparison and the date comparison agree. But the column is a `timestamptz` and nothing
guarantees midnight: a manual field edit can submit a time-bearing or TZ-offset value, and the type
simply preserves whatever instant it parses to.

When an `ExpirationDate` carries a non-midnight UTC time **and** lands exactly on the 30-day
boundary day, the two comparisons disagree:

- `exp = today+30 at 12:00Z`
- deriver: `exp.Date (today+30) <= today+30` → **ExpiringSoon** (the badge)
- old SQL: `exp (noon) <= today+30 (midnight)` → **false** → not in the ExpiringSoon window; the
  Compliant arm's `exp > today+30` is **true** → counted/filtered as **Compliant**

So the row's badge said ExpiringSoon while the list filter and dashboard bucket said Compliant — the
exact "two answers on one screen" class #257 set out to remove. The practical blast radius today is
narrow (the extraction schema field is type `date`, so values are midnight UTC in practice), but it
is a real latent inconsistency and was filed as #294.

This was deferred from #257 because the obvious fix — canonicalizing the stored date — is entangled
with a deliberate, pinned behavior (`CanonicalDocumentFieldsTests` pins that
`2026-01-15T00:00:00+05:00` stores as the instant `2026-01-14T19:00:00Z`), and because the
naive SQL-side fix (`::date` / `date_trunc` on a `timestamptz`) is session-`TimeZone`-dependent
(ADR 0009-adjacent) and not obviously correct.

## Decision

Keep `ComplianceStatusDeriver` (date-only) as the single source of truth, and make every SQL site
compare against an **exclusive instant upper bound** that is provably equivalent to the deriver's
inclusive date comparison — with no truncation and no `AT TIME ZONE`.

The equivalence: for any instant `exp`,

```
exp.Date <= today.Date + N     ⟺     exp < (today.Date + (N + 1))   [at UTC midnight]
```

Day `N` "noon" is `< today+(N+1) midnight`, so it stays inside the window — matching the badge.
The lower edges need no change, because they are already date-equivalent at UTC midnight:

```
exp.Date <  today.Date         ⟺     exp <  today   (Expired)
exp.Date >= today.Date         ⟺     exp >= today   (not yet expired)
```

So only the **inclusive upper edge** shifts: `exp <= today+N` becomes `exp < today+(N+1)`, and its
complement `exp > today+N` becomes `exp >= today+(N+1)`.

This is centralized in one helper so the SQL sites cannot drift from the deriver:

```csharp
public static DateTime WindowUpperBoundExclusive(DateTime today, int withinDays) =>
    today.Date.AddDays(withinDays + 1);
```

Applied at: the documents-list ExpiringSoon / Compliant / Pending arms and the `expiresWithin`
filter; the dashboard `compliant` / `expiringSoon` counts and the 30/60/90 expiry-pipeline buckets;
and the sweep's ExpiringSoon update. The returned bound is a UTC-midnight instant, so every
comparison stays a plain `timestamptz`-vs-`timestamptz` test.

We explicitly **did not** canonicalize `ExpirationDate` on write (the alternative below).

## Consequences

### Positive
- The badge, the list filter, the dashboard counts, the expiry-pipeline buckets, and the sweep now
  agree for any `ExpirationDate`, midnight or not — the #294 boundary disagreement is closed for
  existing rows too, with no data migration.
- Fully session-`TimeZone`-independent: no `::date`, no `date_trunc`, no `AT TIME ZONE`. The
  `Adr0009EnforcementTests` gate stays green; ADR 0009's concern doesn't arise.
- No change to stored-data semantics and no change to the pinned `CanonicalDocumentFields`
  instant-preservation contract — `ExpirationDate` keeps its full `timestamptz` fidelity.
- Consistent with the existing `ReminderBackgroundService` idiom, which already matches a face date
  against a `timestamptz` via a half-open UTC-day range `[start, start+1)`.
- One helper is the single definition of "the window's instant boundary," so a future SQL site has a
  correct primitive to reach for instead of re-deriving `<= today+N`.

### Negative
- The date window now lives in more than one form: the deriver's date comparison
  (`ComplianceStatusDeriver.Effective`, `exp.Date <= today + N`), the SQL helper's instant-exclusive
  bound (`WindowUpperBoundExclusive`, `exp < today + (N + 1)`), and the write-/eval-time date form in
  `ComplianceCheckService.ComputeOutcome` (`exp.Date <= today + N`, the verdict at evaluation time).
  A future change to the window semantics must keep them aligned. Mitigated three ways: the single
  `ExpiringSoonWindowDays` constant is now the only literal for the day count (all three sites
  reference it — `ComputeOutcome`'s previously-hardcoded `30` was switched to the constant in the
  #294 review); the helper's doc-comment states the date↔instant equivalence; and boundary
  regression tests fail if any site drifts — `ComplianceStatusDeriverTests`
  (`WindowUpperBoundExclusive_is_the_instant_equivalent_of_the_date_window`),
  `ComplianceSweepBackgroundServiceTests` (the day-30 / day-31 sweep tests), and
  `ComplianceVerdictFreshnessTests` (`A_time_bearing_expiry_on_the_30_day_boundary_reads_ExpiringSoon_everywhere`,
  plus the expiry-pipeline boundary test).
- A reviewer must understand the `+1` is intentional (not an off-by-one). The helper name and the
  per-site comments carry that.

### Neutral
- `ExpirationDate` remains a `timestamptz` that *may* hold a non-midnight instant; we made the
  readers robust to that rather than constraining the writer. The reminder path was already robust.

## Alternatives considered

### Option A — Canonicalize `EffectiveDate` / `ExpirationDate` to a calendar date (UTC midnight) on write
Truncate in `CanonicalDocumentFields.ParseUtcDate` (`parsed.Date`) so the column is always midnight
UTC; then every existing instant comparison is already correct and no SQL changes are needed.

Rejected (for now) because:
- It forces a genuine data-semantics decision for TZ-offset inputs (does `2026-01-15T00:00:00+05:00`
  mean the UTC date Jan 14 or the local date Jan 15?) and **changes a deliberately pinned contract**
  (`CanonicalDocumentFieldsTests` instant-preservation). That is a larger, separable decision than
  the read-side boundary bug #294 actually is.
- To be complete it wants a backfill of any legacy time-bearing rows; a UTC-safe SQL truncation runs
  back into the `AT TIME ZONE` / session-TZ question this fix avoids.
- The chosen option fixes existing rows immediately without touching the write path or stored data.

If a future need (e.g. new SQL sites, or wanting the column to *be* a date) makes canonical-on-write
worthwhile, it can supersede this ADR; the deriver + helper stay correct either way.

### Option B — SQL-side date truncation (`exp::date <= today+N` / `date_trunc('day', exp)`)
Compare truncated dates directly in SQL.

Rejected because `timestamptz::date` and `date_trunc('day', timestamptz)` resolve the calendar day
in the **session** `TimeZone`, reintroducing exactly the latent, environment-dependent bug ADR 0009
exists to forbid. Forcing UTC needs `(exp AT TIME ZONE 'UTC')::date`, which trips the ADR 0009
enforcement gate. The exclusive-bound approach gets identical date semantics with a plain
instant comparison and no TZ surface.

## References

- Tickets: [#294](https://github.com/neboxdev/complidrop/issues/294) (this fix),
  [#257](https://github.com/neboxdev/complidrop/issues/257) (the date-overlay cluster that
  introduced the deriver and the SQL sites), [#48](https://github.com/neboxdev/complidrop/issues/48)
  (rolling bug-fix epic)
- ADRs: [0009](0009-no-at-time-zone-on-timestamptz-in-raw-sql.md) (no `AT TIME ZONE` on `timestamptz`
  — the rule that rules option B out), [0007](0007-reminder-log-send-date-is-org-local.md) /
  [0025](0025-reminder-catch-up-window-and-failed-send-retry.md) (the reminder path whose half-open
  UTC-day-range idiom this mirrors)
- Code: `ComplianceStatusDeriver.WindowUpperBoundExclusive`, `ComplianceSweepBackgroundService`,
  `DocumentEndpoints.GetDocuments`, `DashboardEndpoints.Stats` / `.ExpiryPipeline`
