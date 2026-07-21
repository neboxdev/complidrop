"use client";

import { useState, useCallback, useEffect, useId, useMemo, useRef } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useDropzone, type FileRejection } from "react-dropzone";
import { toast } from "sonner";
import {
  UploadCloud,
  FileText,
  Trash2,
  AlertTriangle,
  RotateCw,
  X,
  Search,
  ChevronLeft,
  ChevronRight,
  Loader2,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { StaleDataBanner } from "@/components/StaleDataBanner";
import { ComplianceBadge, ExtractionBadge } from "@/components/StatusBadges";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { VendorPicker, type VendorOption } from "@/components/VendorPicker";
import { DocumentTypeSelect } from "@/components/DocumentTypeSelect";
import {
  useDocuments,
  useUploadDocument,
  useDeleteDocument,
  useUpdateDocument,
  type DocumentListParams,
} from "@/hooks/useDocuments";
import { DOCUMENT_TYPES, documentTypeLabel } from "@/lib/document-types";
import { complianceStatusLabel } from "@/lib/display-labels";
import { formatCalendarDate } from "@/lib/dates";
import { cn } from "@/lib/utils";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import {
  rejectionCopy,
  UPLOAD_ACCEPT,
  UPLOAD_MAX_BYTES,
  UPLOAD_PICKER_ACCEPT,
} from "@/lib/upload-policy";
import { isAuthError } from "@/lib/query-client";
import { PageTip } from "@/components/onboarding/PageTip";
import { TIP_IDS } from "@/lib/onboarding";

const PAGE_SIZE = 25;

/**
 * Search-box debounce. Exported because a regression test has to arm a timer on
 * the SAME deadline to reproduce the one-scheduler-tick window where a keystroke
 * can be overwritten by the write's own echo — duplicating the literal there
 * would let this constant drift and silently close the window under test.
 */
export const SEARCH_DEBOUNCE_MS = 300;

// Compliance-status filter values. The <option> labels come from the shared
// complianceStatusLabel map (#188) so the dropdown and the row badges speak the
// SAME words ("Action needed", "Awaiting review", …); the value stays the raw
// enum the server's ?status= filter expects.
const STATUS_FILTER_VALUES: ReadonlyArray<string> = [
  "Compliant",
  "NonCompliant",
  "ExpiringSoon",
  "Expired",
  "Pending",
];

const EXPIRY_FILTERS: ReadonlyArray<{ value: string; label: string }> = [
  { value: "30", label: "Expiring in 30 days" },
  { value: "60", label: "Expiring in 60 days" },
  { value: "90", label: "Expiring in 90 days" },
];

const FILTER_SELECT_CLASS =
  "h-9 rounded-md border border-input bg-white px-2 text-sm text-slate-700 focus:outline-none focus:ring-2 focus:ring-ring";

export default function DocumentsPage() {
  // --- list controls (#187) ---
  const searchParams = useSearchParams();
  // Filters are URL-addressable (FP-041), and the URL is their SINGLE SOURCE OF
  // TRUTH (#370): a deep link lands pre-filtered (the dashboard's
  // "Non-compliant" card -> ?status=NonCompliant, a vendor's "Docs N" ->
  // ?vendor=<id>), the view is shareable, and Back re-seeds it.
  //
  // These used to be four `useState` cells mirrored back into the URL by an
  // effect. That was a feedback loop — the effect's input (`searchParams`)
  // lagged its own output by a commit — with two live defects:
  //   A. Clear reset state and replaced to "/documents", but the effect re-ran
  //      against the pre-navigation snapshot and re-added `?vendor=`, so the
  //      vendor filter survived the click meant to clear it.
  //   B. Clicking "Documents" in the sidebar while filtered is a same-route
  //      nav: no remount, so the `useState` initializers didn't re-run. The
  //      list stayed filtered under a bare URL, and the next filter change
  //      wrote that stale residue back.
  // Deriving instead of mirroring makes both structurally impossible — there
  // is no second copy to fall out of sync, and nothing writes state->URL
  // except an explicit user action.
  const [page, setPage] = useState(1);

  // WHICH of the two URL copies is current depends on HOW the URL moved, so
  // neither can be preferred unconditionally. Getting this backwards is what
  // broke the two previous passes of this ticket, in opposite directions:
  //
  //   History-API write (our own `writeFilters`)  window.location  leads
  //                                               useSearchParams() lags
  //   Router navigation (deep link, sidebar)      useSearchParams() leads
  //                                               window.location  lags
  //
  // Verified in `next/dist/client/components/app-router.js`: `searchParams` is
  // derived from `canonicalUrl` in a RENDER-phase `useMemo`, while
  // `window.location` is moved afterwards in `HistoryUpdater`'s
  // `useInsertionEffect` — the COMMIT phase. That internal write carries
  // `__NA`, so the patched `replaceState` short-circuits and dispatches
  // nothing: no follow-up render ever corrects a component that read
  // `window.location` during a navigation render. Preferring it therefore
  // renders the PREVIOUS route's query, permanently, on every deep link — the
  // dashboard's "Non-compliant" card would land on ?status=NonCompliant
  // showing an unfiltered list.
  //
  // So the HOOK is the base truth: it is correct for every change we did not
  // make. Our own writes — the one case where the hook lags — are overlaid on
  // top until the router echoes them back. The overlay is what stops a filter
  // pick flashing backwards for a frame, which is the symptom that pushed the
  // earlier pass onto `window.location` in the first place.
  const routerQuery = searchParams.toString();
  // Our own writes the router hasn't echoed back yet, oldest first; the newest
  // is what the controls render. A QUEUE rather than one value because two
  // writes can be in flight at once (each `replaceState` schedules its own
  // transition), and when the first lands it must not drag the controls back
  // to it while the second is still pending.
  const [pendingWrites, setPendingWrites] = useState<string[]>([]);
  // Reconcile only when the router side actually MOVED. Without this guard an
  // unrelated re-render (a page change, a refetch) would compare a still-old
  // `routerQuery` against the overlay and discard it.
  const [lastRouterQuery, setLastRouterQuery] = useState(routerQuery);
  if (routerQuery !== lastRouterQuery) {
    setLastRouterQuery(routerQuery);
    const landed = pendingWrites.indexOf(routerQuery);
    // One of ours landing retires it and everything queued before it. Anything
    // else is an external navigation (Back, a deep link, the sidebar click of
    // scenario B), which supersedes the overlay entirely.
    setPendingWrites(landed === -1 ? [] : pendingWrites.slice(landed + 1));
  } else if (pendingWrites.length > 0 && pendingWrites.at(-1) === routerQuery) {
    // The overlay is REDUNDANT: what we last wrote is already what the router
    // reports, so it can contribute nothing. Retire it.
    //
    // The drain above only runs when `routerQuery` MOVES, which leaves a hole:
    // a pair of writes that nets back to the URL the router already holds (pick
    // a filter, immediately undo it) may never move it. Next derives
    // `searchParams` from `canonicalUrl`, so an unchanged canonical URL is a
    // byte-identical string and no change is observed — and `dispatchAction`
    // marks a pending ACTION_RESTORE discarded when a second lands before the
    // first resolves, so the intermediate need never commit at all. The entries
    // would then sit here for the component's lifetime, and a LATER same-route
    // navigation to a value still in that stale queue would match `indexOf`
    // against it and leave a residue that supersedes the real URL.
    //
    // This branch cannot fire in the genuine two-in-flight case: there `at(-1)`
    // is the NEWER write, which by definition the router has not reached yet.
    setPendingWrites([]);
  }
  const effectiveQuery = pendingWrites.at(-1) ?? routerQuery;
  const query = useMemo(() => new URLSearchParams(effectiveQuery), [effectiveQuery]);
  // Mirrored for `writeFilters`, which must compose on this value but must not
  // close over it (see there). Assigned during render rather than in an effect
  // because a write can be dispatched before an effect for the current render
  // has run — a debounce firing in the same tick as a router commit — and an
  // effect-updated mirror would then compose on the previous URL. The
  // assignment is idempotent and derived purely from committed state, so a
  // discarded concurrent render cannot leave it wrong: the render that commits
  // is also the one whose value the next event handler reads.
  const effectiveQueryRef = useRef(effectiveQuery);
  effectiveQueryRef.current = effectiveQuery;

  const search = query.get("search") ?? "";
  const status = query.get("status") ?? "";
  const typeFilter = query.get("type") ?? "";
  const expiresWithin = query.get("expiresWithin") ?? "";
  // ?vendor=<id> is honored for deep-linking (FP-071's "Documents from {vendor}");
  // there's no vendor dropdown, so it's only ever set by an inbound link.
  const vendorId = query.get("vendor") || undefined;

  // The ONE writer, shared by every control.
  //
  // `window.history.replaceState`, NOT `router.replace`: Next's App Router
  // integrates the native History API, updating the history entry and syncing
  // `usePathname`/`useSearchParams` with NO route navigation and no RSC fetch.
  // The docs name list filtering as its purpose, and on this page that removes
  // a Next-server round-trip from the front of every filter interaction (the
  // dropdown looks frozen meanwhile, since there is no loading.tsx under
  // (dashboard)).
  //
  // It patches the query string rather than rebuilding it, so a param this
  // page doesn't model survives an unrelated filter change — `?vendor=` is
  // exactly that from a dropdown's perspective.
  //
  // Composed on `effectiveQuery`, NOT on `window.location.search` and not on
  // the raw hook — it is the only expression that is current under BOTH
  // orderings above. Composing on `window.location` drops a deep link's params
  // on the first filter touch (the address bar is still the previous route's
  // during a navigation render); composing on the raw hook drops the earlier of
  // two writes made inside one transition window.
  //
  // Read through a ref so the callback can close over nothing and stay
  // referentially stable: an unstable `writeFilters` re-arms the 300ms search
  // debounce below on every unrelated filter change.
  const writeFilters = useCallback((patch: Record<string, string>) => {
    const sp = new URLSearchParams(effectiveQueryRef.current);
    for (const [key, value] of Object.entries(patch)) {
      if (value) sp.set(key, value);
      else sp.delete(key);
    }
    const qs = sp.toString();
    window.history.replaceState(null, "", qs ? `/documents?${qs}` : "/documents");
    // Render the new value NOW. Next syncs `useSearchParams()` through a
    // transition, so without the overlay the control the user just touched
    // renders its OLD value until that lands.
    setPendingWrites((q) => [...q, qs]);
  }, []);

  // Clear every filter at once, including params with no control of their own.
  const clearFilters = useCallback(() => {
    window.history.replaceState(null, "", "/documents");
    setPendingWrites((q) => [...q, ""]);
  }, []);

  // The search box is the one filter with local state: it must echo every
  // keystroke, but only the debounced value belongs in the URL (and in a
  // request). `searchInput` is therefore a draft of `search`, not a second
  // source of truth.
  const [searchInput, setSearchInput] = useState(search);
  // Re-seed the box whenever the URL's search changes underneath us — a
  // same-route sidebar click, Back, Clear, a deep link. Adjusting state during
  // render (rather than in an effect) is React's supported path for "derive
  // from a changing input": it re-renders before paint, so the box never
  // flashes the stale text.
  //
  // Guarded against the page's OWN echo, but only for one comparison.
  //
  // The debounce writes ?search=acme; you keep typing " roofing"; the echo
  // lands mid-word and truncates you back to "acme". The window is real: the
  // debounce's `setPendingWrites` is a DefaultLane update flushed in a later
  // scheduler macrotask, while a keystroke in that gap renders at SyncLane
  // FIRST — so the later render re-seeds over characters typed after the write.
  //
  // An earlier pass removed this guard on the theory that `search` changes in
  // the same task as the write. It does not, for the lane reason above.
  //
  // But the ORIGINAL guard was a ref holding the last dispatched value
  // indefinitely, and that went stale: across an external clear it swallowed
  // the re-seed on the following Back, leaving the box empty and letting the
  // debounce write the restored filter back OUT of the URL. Retiring the echo
  // after a single comparison fixes both — it covers exactly the one re-seed
  // its own write provokes, and never outlives it.
  const [searchEcho, setSearchEcho] = useState<string | null>(null);
  const [lastUrlSearch, setLastUrlSearch] = useState(search);
  if (search !== lastUrlSearch) {
    setLastUrlSearch(search);
    setSearchEcho(null);
    if (search !== searchEcho) setSearchInput(search);
  }

  // Debounce the search box so we don't fire a request per keystroke, then
  // write THROUGH to the URL. The `searchInput === search` bail is what keeps
  // this from looping: the write updates `search`, which re-runs the effect,
  // which then does nothing — and on mount, where the two already agree, it
  // means no write at all (so a deep link is a pure read).
  useEffect(() => {
    if (searchInput === search) return;
    const t = setTimeout(() => {
      // Announce the echo in the SAME batch as the write, so the re-seed check
      // above sees it on the render where `search` becomes this value.
      setSearchEcho(searchInput);
      writeFilters({ search: searchInput });
    }, SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [searchInput, search, writeFilters]);

  const params: DocumentListParams = {
    page,
    pageSize: PAGE_SIZE,
    search: search || undefined,
    status: status || undefined,
    type: typeFilter || undefined,
    vendorId,
    expiresWithin: expiresWithin ? Number(expiresWithin) : undefined,
  };

  const docs = useDocuments(params);
  const upload = useUploadDocument();
  const del = useDeleteDocument();
  const updateDoc = useUpdateDocument();
  const [isUploading, setIsUploading] = useState(false);

  const total = docs.data?.total ?? 0;
  const serverPageSize = docs.data?.pageSize ?? PAGE_SIZE;
  const totalPages = Math.max(1, Math.ceil(total / serverPageSize));

  // Any filter change returns to page 1 so the new results are visible. This
  // keys off the filter values themselves rather than living in each dropdown's
  // onChange (#370): filters now arrive from the URL, so they also change on a
  // Back, a deep link, or a same-route nav — routes the handler-based reset
  // never saw, which stranded the user on a page that no longer exists in the
  // new result set.
  // JSON.stringify, not join(sep): `search` is free text, so any separator
  // character could appear inside a value and let two different filter sets
  // produce the same key (search "a b" vs search "a" plus a "b" filter).
  const filterKey = JSON.stringify([search, status, typeFilter, expiresWithin, vendorId ?? ""]);
  const [lastFilterKey, setLastFilterKey] = useState(filterKey);
  const filtersJustChanged = filterKey !== lastFilterKey;
  if (filtersJustChanged) {
    setLastFilterKey(filterKey);
    setPage(1);
  }
  // Self-heal if `page` fell out of range — e.g. another tab/user (or a backend
  // mutation) shrank the result set while we sat on the last page. Setting state
  // during render is React's supported "adjust state when derived data changes"
  // path: it re-renders immediately with the corrected value, so the user never
  // sees a stranded empty out-of-range page (and the request re-bases). This
  // converges — once page == totalPages the guard is false. The delete onSuccess
  // below still decrements as a same-session fast path so we skip the empty
  // fetch. (#187 review — correctness reviewer)
  //
  // `else if` so the two adjustments can't fight over one render: on a filter
  // change `totalPages` is still derived from the PREVIOUS filter's response,
  // and letting it queue a second setPage after the reset would clamp the user
  // to the old result set's last page instead of page 1. (#370)
  else if (page > totalPages) setPage(totalPages);

  const hasActiveFilters = Boolean(search || status || typeFilter || expiresWithin || vendorId);

  // Drop stages the files; the vendor + document-type step below must be
  // completed before they're actually uploaded — so every document lands
  // associated with a vendor instead of orphaned-and-Pending-forever (#186).
  const [staged, setStaged] = useState<File[]>([]);
  const [stagedVendor, setStagedVendor] = useState<VendorOption | null>(null);
  const [stagedType, setStagedType] = useState<string>("coi");

  // Which orphaned row currently has its inline "assign a vendor" picker open.
  const [assigningId, setAssigningId] = useState<string | null>(null);

  const vendorInputId = useId();
  const typeSelectId = useId();

  const onDrop = useCallback((accepted: File[], rejected: FileRejection[]) => {
    // Surface why nothing happened for rejected files (wrong type, over 10 MB) —
    // the dropzone used to swallow them silently and the page just sat there,
    // which reads as "the product is broken" in a first session (#265). Toast is
    // this page's idiom for transient upload feedback; accepted files in the same
    // drop still stage below.
    const rejectionMessage = rejectionCopy(rejected);
    if (rejectionMessage) toast.error(rejectionMessage);
    if (accepted.length === 0) return;
    // Append rather than replace so a second drop adds to the batch instead of
    // discarding the first; the staging card lets the user remove any file.
    setStaged((prev) => [...prev, ...accepted]);
  }, []);

  const { getRootProps, getInputProps, isDragActive } = useDropzone({
    onDrop,
    // Shared accept list + size cap (@/lib/upload-policy): PDF + the photo formats
    // the backend admits, incl. HEIC/HEIF (iPhone "High Efficiency" photos, transcoded
    // to JPEG server-side on ingest, #220) — the dashboard used to reject the iPhone
    // default format the portal and backend already accepted (#265).
    accept: UPLOAD_ACCEPT,
    maxSize: UPLOAD_MAX_BYTES,
  });

  async function handleUpload() {
    if (!stagedVendor || staged.length === 0) return;
    const vendorId = stagedVendor.id;
    const documentType = stagedType;
    setIsUploading(true);
    try {
      // Snapshot the batch, then drop each file from `staged` as soon as it
      // uploads. A mid-batch failure therefore leaves ONLY the files that
      // didn't succeed staged — clicking Upload again retries just those,
      // instead of re-sending (and re-creating, since each upload mints a fresh
      // idempotency key) the documents that already landed. (#186 review)
      const batch = [...staged];
      for (const file of batch) {
        await upload.mutateAsync({ file, vendorId, documentType });
        setStaged((prev) => prev.filter((f) => f !== file));
        toast.success(`Uploaded ${file.name}`);
      }
      // Whole batch succeeded — reset the vendor/type selection for the next one.
      setStagedVendor(null);
      setStagedType("coi");
      // A just-uploaded doc can be invisible behind an active status/expiry/vendor
      // filter — the success toast then reads as a lie ("uploaded" but the list is
      // empty). Tell the user where it went. (FP-054)
      if (hasActiveFilters) {
        toast.info("Heads up — an active filter may be hiding your new upload. Clear filters to see everything.");
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Upload failed.");
    } finally {
      setIsUploading(false);
    }
  }

  function cancelStaging() {
    setStaged([]);
    setStagedVendor(null);
    setStagedType("coi");
  }

  // Memoized so the live-region effect's dep is stable across renders (a fresh
  // `?? []` literal would otherwise re-run it every render).
  const items = useMemo(() => docs.data?.items ?? [], [docs.data]);

  // Announce when a document finishes reading on a polling refetch
  // (Pending/Processing → a terminal state) so screen-reader users hear it
  // without watching the badge. We write textContent directly on the live
  // region (rather than via state) — that's the canonical aria-live update and
  // avoids a render-coupled setState. (#189)
  const liveRef = useRef<HTMLDivElement>(null);
  const prevExtraction = useRef<Record<string, string>>({});
  useEffect(() => {
    const finished: string[] = [];
    for (const d of items) {
      const prev = prevExtraction.current[d.id];
      const wasInFlight = prev === "Pending" || prev === "Processing";
      const isTerminal =
        d.extractionStatus === "Completed" ||
        d.extractionStatus === "Failed" ||
        d.extractionStatus === "ManualRequired";
      if (wasInFlight && isTerminal) finished.push(d.originalFileName);
      prevExtraction.current[d.id] = d.extractionStatus;
    }
    if (finished.length && liveRef.current) {
      liveRef.current.textContent = finished
        .map((name) => `${name} finished processing.`)
        .join(" ");
    }
  }, [items]);

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-6">
      {/* aria-live (not role="status") so it announces without colliding with
          the StaleDataBanner's role="status" on the same page. */}
      <div ref={liveRef} aria-live="polite" aria-atomic="true" className="sr-only" />
      <header className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Documents</h1>
          <p className="text-slate-500">COIs, licenses, permits — dropped once, tracked forever.</p>
        </div>
        <p className="text-xs text-slate-500">{docs.data?.total ?? 0} total</p>
      </header>

      <PageTip id={TIP_IDS.documents} title="This is where documents land">
        Drag a COI, license, or permit here — or send a vendor an upload link from their page.
        We read each one and check it against that vendor&apos;s requirements automatically.
      </PageTip>

      <Card>
        <CardContent className="p-6">
          <div
            {...getRootProps()}
            className={cn(
              "border-2 border-dashed rounded-lg p-10 text-center transition cursor-pointer",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
              isDragActive ? "border-sky-500 bg-sky-50" : "border-slate-200 hover:border-sky-300 hover:bg-sky-50/50",
            )}
          >
            {/* `accept` MUST stay AFTER the `{...getInputProps()}` spread — react-dropzone
                injects its own narrower `accept` and last-prop-wins is what lets the
                shared picker override win (see UPLOAD_PICKER_ACCEPT's doc). Mirrors the
                portal dropzone (#265). */}
            <input {...getInputProps()} accept={UPLOAD_PICKER_ACCEPT} />
            <UploadCloud className="w-10 h-10 mx-auto text-sky-500" />
            <p className="mt-3 text-sm font-medium text-slate-700">
              {isDragActive ? "Drop to add…" : "Drag a file here or click to browse"}
            </p>
            <p className="text-xs text-slate-500">PDF, JPEG, PNG, or iPhone photo (HEIC) · 10 MB max</p>
          </div>
        </CardContent>
      </Card>

      {staged.length > 0 && (
        <Card>
          <CardContent className="p-6 space-y-4">
            <div>
              <h2 className="text-sm font-semibold text-slate-800">
                Add details before uploading
              </h2>
              <p className="text-xs text-slate-500">
                {staged.length === 1
                  ? "Pick the vendor this is for so we can check it against their requirements."
                  : `Pick the vendor these ${staged.length} files are for — all of them will be assigned to it and checked against its requirements.`}
              </p>
            </div>

            <ul className="space-y-1.5">
              {staged.map((file, i) => (
                <li
                  key={`${file.name}-${file.size}-${i}`}
                  className="flex items-center justify-between rounded-md bg-slate-50 px-3 py-2 text-sm"
                >
                  <span className="flex items-center gap-2 text-slate-700">
                    <FileText className="h-4 w-4 text-slate-400" /> {file.name}
                  </span>
                  {/* Per-file feedback while the batch uploads — each file drops
                      from the list as it lands, so a spinner on the rest reads as
                      "in progress" instead of a frozen Remove button. (FP-055) */}
                  {isUploading ? (
                    <span className="flex items-center gap-1 text-xs text-sky-600">
                      <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden /> Uploading…
                    </span>
                  ) : (
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      aria-label={`Remove ${file.name} from this upload`}
                      onClick={() => setStaged((prev) => prev.filter((_, idx) => idx !== i))}
                    >
                      <X className="h-4 w-4 text-slate-500 hover:text-rose-600" />
                    </Button>
                  )}
                </li>
              ))}
            </ul>

            <div className="grid gap-4 sm:grid-cols-2">
              <div className="space-y-1.5">
                <label htmlFor={vendorInputId} className="text-xs font-medium text-slate-600">
                  Vendor
                </label>
                <VendorPicker
                  inputId={vendorInputId}
                  value={stagedVendor}
                  onChange={setStagedVendor}
                  disabled={isUploading}
                  onCreateError={(message) => toast.error(message)}
                />
              </div>
              <div className="space-y-1.5">
                <label htmlFor={typeSelectId} className="text-xs font-medium text-slate-600">
                  Document type
                </label>
                <div>
                  <DocumentTypeSelect
                    id={typeSelectId}
                    value={stagedType}
                    onChange={setStagedType}
                    disabled={isUploading}
                    className="w-full"
                  />
                </div>
              </div>
            </div>

            <div className="flex items-center gap-3">
              <Button type="button" onClick={handleUpload} disabled={!stagedVendor || isUploading}>
                {isUploading
                  ? "Uploading…"
                  : `Upload ${staged.length} file${staged.length === 1 ? "" : "s"}`}
              </Button>
              <Button type="button" variant="ghost" onClick={cancelStaging} disabled={isUploading}>
                Cancel
              </Button>
              {!stagedVendor && (
                <span className="text-xs text-slate-500">Choose a vendor to continue.</span>
              )}
            </div>
          </CardContent>
        </Card>
      )}

      {docs.isError && items.length > 0 && (
        // Discreet "couldn't refresh" indicator when the polling
        // refetch failed but the cached list is still rendered below.
        // Gated on `items.length > 0` (symmetric with the full-page
        // error card's `length === 0` gate) — a populated list keeps
        // the user reading the stale data; the banner signals it may
        // not reflect the latest state. `useDocuments`'s
        // refetchInterval short-circuits on error so the backend isn't
        // hammered while the banner is visible — once the user clicks
        // Try again (or any subsequent refetch lands a 200), isError
        // flips back to false and the banner disappears. (#97)
        <StaleDataBanner
          message={docs.error?.message}
          onRetry={() => docs.refetch()}
          isRetrying={docs.isFetching}
          noun="documents"
        />
      )}

      <div className="flex flex-wrap items-center gap-2">
        <div className="relative flex-1 min-w-[12rem]">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <Input
            aria-label="Search documents"
            placeholder="Search by file or vendor name…"
            value={searchInput}
            // No setPage(1) here: the filter-signature reset below owns that
            // now (#370), and it fires when `search` actually reaches the URL.
            // Resetting on every keystroke instead re-based the list to page 1
            // 300ms BEFORE the search term landed, costing one extra unfiltered
            // request per search.
            onChange={(e) => setSearchInput(e.target.value)}
            className="pl-8"
          />
        </div>
        <select
          aria-label="Filter by compliance status"
          className={FILTER_SELECT_CLASS}
          value={status}
          onChange={(e) => writeFilters({ status: e.target.value })}
        >
          <option value="">All statuses</option>
          {STATUS_FILTER_VALUES.map((value) => (
            <option key={value} value={value}>
              {complianceStatusLabel(value)}
            </option>
          ))}
        </select>
        <select
          aria-label="Filter by document type"
          className={FILTER_SELECT_CLASS}
          value={typeFilter}
          onChange={(e) => writeFilters({ type: e.target.value })}
        >
          <option value="">All types</option>
          {DOCUMENT_TYPES.map((t) => (
            <option key={t.value} value={t.value}>
              {t.label}
            </option>
          ))}
        </select>
        <select
          aria-label="Filter by expiry"
          className={FILTER_SELECT_CLASS}
          value={expiresWithin}
          onChange={(e) => writeFilters({ expiresWithin: e.target.value })}
        >
          <option value="">Any expiry</option>
          {EXPIRY_FILTERS.map((x) => (
            <option key={x.value} value={x.value}>
              {x.label}
            </option>
          ))}
        </select>
        {hasActiveFilters && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              // Dropping the whole query string clears every filter at once,
              // including the `?vendor=` deep link that has no dropdown of its
              // own (#317 review). Page and the derived filters follow from the
              // now-empty URL; nothing re-adds a param afterwards, which is the
              // #370 fix — the old mirror effect used to re-run against the
              // pre-navigation snapshot and resurrect `?vendor=`.
              //
              // Goes through `clearFilters` (a History-API write) rather than
              // `router.replace` so `window.location` is bare IMMEDIATELY: a
              // dropdown touched before the router transition commits composes
              // on the CLEARED url, not on the stale filters.
              clearFilters();
              // The search DRAFT is the one piece of state the URL can't clear:
              // if the user typed within the last 300ms, `search` is still ""
              // (the debounce hasn't written yet), so the URL-sync above sees no
              // change and the box would keep its text — with the pending timer
              // then writing it straight back.
              setSearchInput("");
            }}
          >
            Clear
          </Button>
        )}
      </div>

      <Card>
        <CardContent className="p-0 overflow-x-auto">
          <table className="stacked-table w-full text-sm">
            <thead className="bg-slate-50 text-xs uppercase text-slate-500">
              <tr>
                <th className="px-4 py-3 text-left">File</th>
                <th className="px-4 py-3 text-left">Type</th>
                <th className="px-4 py-3 text-left">Vendor</th>
                <th className="px-4 py-3 text-left">Reading</th>
                <th className="px-4 py-3 text-left">Compliance</th>
                <th className="px-4 py-3 text-left">Expires</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {docs.isLoading ? (
                <tr>
                  <td colSpan={7} className="px-4 py-8 text-center text-sm text-slate-500">
                    Loading documents…
                  </td>
                </tr>
              ) : docs.isError && items.length === 0 && !isAuthError(docs.error) ? (
                // Error state distinct from empty so a backend outage is
                // not mistaken for a brand-new org with zero documents
                // (#80). The `!isAuthError` guard keeps an EXPIRED-SESSION
                // 401 out of this card — that's handled globally by nulling
                // the me-cache → the dashboard layout redirects to /login
                // (lib/query-client.ts), so a logged-out user is sent to
                // sign in rather than shown a scary "couldn't load" error.
                // Gate on `items.length === 0` so a polling
                // failure on a populated list does NOT clobber the
                // rows the user is reading — the cached data stays
                // visible and `useDocuments.refetchInterval` short-
                // circuits on error so the backend isn't hammered.
                // The polling-failure-UX-banner is deferred to #97;
                // this gate is the minimum that prevents the
                // regression from shipping per the #80 followup review.
                //
                // `err.message` is the human server message from the
                // ApiError envelope; api.ts's GENERIC_FALLBACK_MESSAGE
                // kicks in when the body is non-JSON or fetch rejected
                // (#77), so `?? GENERIC_FALLBACK_MESSAGE` here only
                // covers the unreachable `error: null` defensive path.
                //
                // role="alert" gets the error announced by assistive
                // tech the moment isError flips true, matching the
                // convention in frontend/src/test/example.test.tsx.
                <tr>
                  <td colSpan={7} className="px-4 py-12 text-center" role="alert">
                    <AlertTriangle className="w-8 h-8 mx-auto text-rose-500" />
                    <p className="mt-2 text-sm font-medium text-slate-800">
                      Couldn&apos;t load documents.
                    </p>
                    <p className="text-xs text-slate-500">
                      {docs.error?.message?.trim() || GENERIC_FALLBACK_MESSAGE}
                    </p>
                    <Button
                      variant="outline"
                      size="sm"
                      className="mt-3"
                      onClick={() => docs.refetch()}
                      disabled={docs.isFetching}
                    >
                      <RotateCw
                        className={cn(
                          "w-3.5 h-3.5 mr-1",
                          docs.isFetching && "animate-spin",
                        )}
                      />
                      Retry
                    </Button>
                  </td>
                </tr>
              ) : items.length === 0 ? (
                <tr>
                  <td colSpan={7} className="px-4 py-12 text-center">
                    <FileText className="w-8 h-8 mx-auto text-slate-300" />
                    <p className="mt-2 text-sm text-slate-500">
                      {hasActiveFilters
                        ? "No documents match your filters."
                        : "No documents yet — drop a COI, license, or permit above and we'll read it and track its expiry for you."}
                    </p>
                  </td>
                </tr>
              ) : (
                items.map((d) => (
                  <tr key={d.id} className="border-t border-slate-100 hover:bg-slate-50/80">
                    <td className="px-4 py-3">
                      <Link href={`/documents/${d.id}`} className="text-sky-700 hover:underline font-medium">
                        {d.originalFileName}
                      </Link>
                      {d.isSample && (
                        <span className="ml-2 inline-flex items-center rounded-full bg-sky-100 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-sky-700">
                          Sample
                        </span>
                      )}
                    </td>
                    <td data-label="Type" className="px-4 py-3 text-slate-600 text-xs">{documentTypeLabel(d.documentType)}</td>
                    <td data-label="Vendor" className="px-4 py-3 text-slate-600">
                      {d.vendorName ? (
                        d.vendorName
                      ) : assigningId === d.id ? (
                        <div className="min-w-[15rem]">
                          <VendorPicker
                            autoFocus
                            value={null}
                            disabled={updateDoc.isPending}
                            onChange={(vendor) => {
                              if (!vendor) {
                                setAssigningId(null);
                                return;
                              }
                              updateDoc.mutate(
                                { id: d.id, vendorId: vendor.id },
                                {
                                  onSuccess: () => {
                                    toast.success(`Assigned to ${vendor.name}`);
                                    setAssigningId(null);
                                  },
                                  onError: (err) =>
                                    toast.error(
                                      err instanceof Error ? err.message : "Couldn't assign vendor.",
                                    ),
                                },
                              );
                            }}
                            onCreateError={(message) => toast.error(message)}
                          />
                          <button
                            type="button"
                            className="mt-1 text-xs text-slate-500 hover:underline"
                            onClick={() => setAssigningId(null)}
                          >
                            Cancel
                          </button>
                        </div>
                      ) : (
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          onClick={() => setAssigningId(d.id)}
                        >
                          Assign vendor
                        </Button>
                      )}
                    </td>
                    <td data-label="Reading" className="px-4 py-3">
                      <ExtractionBadge status={d.extractionStatus} />
                    </td>
                    <td data-label="Compliance" className="px-4 py-3">
                      <ComplianceBadge status={d.complianceStatus} />
                    </td>
                    <td data-label="Expires" className="px-4 py-3 text-slate-600">
                      {formatCalendarDate(d.expirationDate)}
                      {d.daysUntilExpiry != null && (
                        <span className={cn("ml-2 text-xs", d.daysUntilExpiry < 30 ? "text-rose-600" : "text-slate-500")}>
                          {d.daysUntilExpiry < 0
                            ? `expired ${-d.daysUntilExpiry} day${-d.daysUntilExpiry === 1 ? "" : "s"} ago`
                            : `in ${d.daysUntilExpiry} day${d.daysUntilExpiry === 1 ? "" : "s"}`}
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <ConfirmDialog
                        title={`Remove ${d.originalFileName}?`}
                        description="This removes the document from your records and can't be undone."
                        confirmLabel="Remove"
                        destructive
                        onConfirm={() =>
                          del.mutate(d.id, {
                            onSuccess: () => {
                              toast.success("Document removed");
                              // If that was the last row on a page past the
                              // first, step back so we don't strand the user
                              // on a now-empty page.
                              if (items.length === 1 && page > 1) setPage((p) => p - 1);
                            },
                            onError: (err) => toast.error(err instanceof Error ? err.message : "Remove failed"),
                          })
                        }
                        trigger={
                          <Button
                            variant="ghost"
                            size="sm"
                            aria-label={`Remove ${d.originalFileName}`}
                            disabled={del.isPending}
                          >
                            <Trash2 className="w-4 h-4 text-slate-500 hover:text-rose-600" />
                          </Button>
                        }
                      />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </CardContent>
      </Card>

      {total > 0 && (
        <div className="flex items-center justify-between text-sm text-slate-600">
          <span>
            Page {page} of {totalPages} · {total} document{total === 1 ? "" : "s"}
          </span>
          <div className="flex items-center gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={page <= 1}
              onClick={() => setPage((p) => Math.max(1, p - 1))}
            >
              <ChevronLeft className="mr-1 h-4 w-4" /> Prev
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={page >= totalPages}
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
            >
              Next <ChevronRight className="ml-1 h-4 w-4" />
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
