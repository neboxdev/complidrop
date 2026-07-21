# 0039. The documents URL is the source of truth, read through a pending-write overlay

- **Status:** accepted
- **Date:** 2026-07-21
- **Deciders:** Ruben G. (via autonomous backlog worker)

## Context

The documents list page's filters (`search`, `status`, `type`, `expiresWithin`,
plus the deep-link-only `vendor`) used to live in four `useState` cells that an
effect mirrored back into the URL. That was a feedback loop — the effect's input
(`searchParams`) lagged its own output by a commit — and it produced two live
defects ([#370](https://github.com/neboxdev/complidrop/issues/370)): **Clear**
could resurrect `?vendor=`, and a same-route sidebar click left the list
filtered under a bare URL.

Deriving the filters from the URL instead removes the second copy. But it raises
a question the ticket then got wrong **twice, in opposite directions**: *which*
copy of the URL do you read?

There are two, and they update at different times:

| | leads | lags |
|---|---|---|
| History-API write (`replaceState` the page calls) | `window.location` | `useSearchParams()` |
| Router navigation (`<Link>`, deep link, sidebar) | `useSearchParams()` | `window.location` |

Verified in `next/dist/client/components/app-router.js` (Next 16.2.3):

- `Router` derives `searchParams` from `canonicalUrl` in a **render-phase**
  `useMemo`, so on a router navigation the hook is already correct while the
  page renders.
- `HistoryUpdater` moves `window.location` afterwards, in a **`useInsertionEffect`**
  (commit phase), and that internal write carries `__NA: true`.
- The patched `pushState`/`replaceState` short-circuit on `data?.__NA`, so the
  internal write dispatches nothing. **No follow-up render ever corrects a
  component that read `window.location` during a navigation render.**

The first pass read the hook and every filter pick flashed backwards for a frame
(React re-rendered the stale value onto a controlled `<select>`). The second
pass "fixed" that by preferring `window.location` unconditionally — which makes
every deep link render the *previous* route's query, permanently: the dashboard's
"Non-compliant" card would land on `?status=NonCompliant` showing an unfiltered
list. Both passes shipped a fully green documents suite, because the test harness
modelled the ordering wrongly in each direction in turn.

## Decision

**`useSearchParams()` is the base truth**, because it is correct for every change
the page did not make. **The page's own writes are overlaid on top until the
router echoes them back**, because that is the single case where the hook lags.

The pending writes are held as a **queue**, not one value. Two writes can be in
flight at once (each `replaceState` schedules its own transition). When the first
lands, the router snapshot equals neither the pre-write base nor what the user
last picked — a single-value overlay must read that as an external navigation and
discard the second write, snapping a dropdown back mid-interaction. With a queue,
a router value found in the queue retires that entry and everything before it;
anything *not* in the queue is a genuine external navigation and supersedes the
overlay entirely.

Two supporting rules:

1. **Reconcile only when the router query actually moved**, so an unrelated
   re-render can't compare a still-old value against the overlay and discard it.
   Plus a redundancy drain: if the newest pending write already equals what the
   router reports, the overlay contributes nothing and is retired. Without that
   drain, a pair of writes that nets back to the current URL can strand entries
   permanently (Next's `dispatchAction` marks a pending `ACTION_RESTORE` discarded
   when a second lands first, so the intermediate URL need never commit), and a
   later same-route navigation would match `indexOf` against the stale entry.
2. **`writeFilters` composes on the overlay-resolved query**, not on
   `window.location` and not on the raw hook — the only expression current under
   both orderings. It reads through a ref so the callback stays referentially
   stable; an unstable `writeFilters` re-arms the search debounce on every
   unrelated filter change.

The test harness (`frontend/src/test/navigation.ts`) models **both** orderings
deliberately, and `setNavigationCommitDelay` staggers commit latency so
two-in-flight interleavings are expressible at all.

## Consequences

### Positive
- Deep links, Back, and same-route navigation all render correctly, and a filter
  pick never flashes backwards — the two goals that were previously in tension.
- One source of truth. There is no state cell that can drift from the URL, so
  #370's scenarios A and B are structurally impossible rather than patched.
- The harness now encodes the ordering as an executable spec, so the next change
  in this area fails loudly instead of shipping green.

### Negative
- The overlay is genuinely subtle, and its correctness depends on Next internals
  that are not public API. A Next upgrade that changes when `canonicalUrl` or
  `window.location` moves could invalidate it. The mitigation is the harness plus
  the real-browser probes described below — not the comments.
- `pendingWrites` is unbounded in principle. In practice every entry is retired
  by a router echo, an external navigation, or the redundancy drain.

### Neutral
- The harness cannot currently express Next's `ACTION_RESTORE` coalescing (it
  commits every History-API write), so the redundancy drain in rule 1 is
  defensive hardening rather than a test-pinned fix. Tracked separately.

## Alternatives considered

### Option A — read `window.location` unconditionally
Rejected: breaks every deep link, permanently. This was the state the branch was
stopped in, and it is the reason this ADR exists.

### Option B — read `useSearchParams()` unconditionally
Rejected: correct for navigation, but every filter pick visibly reverts for a
frame while the transition catches up. The pre-#370 page held local state and
never flickered, so this would ship as a UX regression.

### Option C — `useSyncExternalStore` over `window.location`
Rejected: it does not address the problem. A store over `window.location` is
still stale during a router-navigation render, so it re-introduces Option A's bug
with more machinery.

### Option D — keep local state, sync harder
Rejected: this is the pre-#370 design. Two copies of the filters is what produced
the original feedback loop; making the mirroring smarter does not remove the
second copy.
