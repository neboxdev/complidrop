"use client";

import { useState, useCallback, useEffect, useId } from "react";
import Link from "next/link";
import { useDropzone } from "react-dropzone";
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
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { StaleDataBanner } from "@/components/StaleDataBanner";
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
import { complianceStatusLabel, extractionStatusLabel } from "@/lib/display-labels";
import { cn } from "@/lib/utils";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";
import { isAuthError } from "@/lib/query-client";

const PAGE_SIZE = 25;

// Compliance-status filter options. Labels stay friendly here; #188 introduces
// the app-wide shared status-label map and will reconcile these with it.
const STATUS_FILTERS: ReadonlyArray<{ value: string; label: string }> = [
  { value: "Compliant", label: "Compliant" },
  { value: "NonCompliant", label: "Not compliant" },
  { value: "ExpiringSoon", label: "Expiring soon" },
  { value: "Expired", label: "Expired" },
  { value: "Pending", label: "Pending" },
];

const EXPIRY_FILTERS: ReadonlyArray<{ value: string; label: string }> = [
  { value: "30", label: "Expiring in 30 days" },
  { value: "60", label: "Expiring in 60 days" },
  { value: "90", label: "Expiring in 90 days" },
];

const STATUS_HUE: Record<string, string> = {
  Pending: "bg-slate-100 text-slate-700",
  Processing: "bg-sky-100 text-sky-700 animate-pulse",
  Completed: "bg-emerald-100 text-emerald-700",
  ManualRequired: "bg-amber-100 text-amber-700",
  Failed: "bg-rose-100 text-rose-700",
};

const COMPLIANCE_HUE: Record<string, string> = {
  Pending: "bg-slate-100 text-slate-700",
  Compliant: "bg-emerald-100 text-emerald-700",
  NonCompliant: "bg-rose-100 text-rose-700",
  ExpiringSoon: "bg-amber-100 text-amber-700",
  Expired: "bg-rose-100 text-rose-700",
};

const FILTER_SELECT_CLASS =
  "h-9 rounded-md border border-slate-200 bg-white px-2 text-sm text-slate-700 focus:outline-none focus:ring-2 focus:ring-sky-500/40";

export default function DocumentsPage() {
  // --- list controls (#187) ---
  const [page, setPage] = useState(1);
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState(""); // debounced value sent to the server
  const [status, setStatus] = useState("");
  const [typeFilter, setTypeFilter] = useState("");
  const [expiresWithin, setExpiresWithin] = useState("");

  // Debounce the search box so we don't fire a request per keystroke. The
  // page-1 reset lives in the input's onChange (an event handler) — NOT here —
  // so this effect only ever syncs the debounced value. Resetting page in this
  // effect would also fire on mount (~300ms later) and clobber any page the user
  // had navigated to in the meantime. (#187 review — test-quality reviewer)
  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput), 300);
    return () => clearTimeout(t);
  }, [searchInput]);

  const params: DocumentListParams = {
    page,
    pageSize: PAGE_SIZE,
    search: search || undefined,
    status: status || undefined,
    type: typeFilter || undefined,
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

  // Self-heal if `page` fell out of range — e.g. another tab/user (or a backend
  // mutation) shrank the result set while we sat on the last page. Setting state
  // during render is React's supported "adjust state when derived data changes"
  // path: it re-renders immediately with the corrected value, so the user never
  // sees a stranded empty out-of-range page (and the request re-bases). This
  // converges — once page == totalPages the guard is false. The delete onSuccess
  // below still decrements as a same-session fast path so we skip the empty
  // fetch. (#187 review — correctness reviewer)
  if (page > totalPages) setPage(totalPages);

  // Any filter dropdown change returns to page 1 so the new results are visible.
  const onFilterChange = (setter: (v: string) => void) => (value: string) => {
    setter(value);
    setPage(1);
  };
  const hasActiveFilters = Boolean(search || status || typeFilter || expiresWithin);

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

  const onDrop = useCallback((accepted: File[]) => {
    if (accepted.length === 0) return;
    // Append rather than replace so a second drop adds to the batch instead of
    // discarding the first; the staging card lets the user remove any file.
    setStaged((prev) => [...prev, ...accepted]);
  }, []);

  const { getRootProps, getInputProps, isDragActive } = useDropzone({
    onDrop,
    accept: {
      "application/pdf": [".pdf"],
      "image/jpeg": [".jpg", ".jpeg"],
      "image/png": [".png"],
    },
    maxSize: 10 * 1024 * 1024,
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

  const items = docs.data?.items ?? [];

  return (
    <div className="max-w-6xl mx-auto px-6 py-8 space-y-6">
      <header className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-sky-900">Documents</h1>
          <p className="text-slate-500">COIs, licenses, permits — dropped once, tracked forever.</p>
        </div>
        <p className="text-xs text-slate-500">{docs.data?.total ?? 0} total</p>
      </header>

      <Card>
        <CardContent className="p-6">
          <div
            {...getRootProps()}
            className={cn(
              "border-2 border-dashed rounded-lg p-10 text-center transition cursor-pointer",
              isDragActive ? "border-sky-500 bg-sky-50" : "border-slate-200 hover:border-sky-300 hover:bg-sky-50/50",
            )}
          >
            <input {...getInputProps()} />
            <UploadCloud className="w-10 h-10 mx-auto text-sky-500" />
            <p className="mt-3 text-sm font-medium text-slate-700">
              {isDragActive ? "Drop to add…" : "Drag a file here or click to browse"}
            </p>
            <p className="text-xs text-slate-500">PDF, JPEG, PNG · 10 MB max</p>
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
                Pick the vendor this is for so we can check it against their requirements.
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
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    aria-label={`Remove ${file.name} from this upload`}
                    disabled={isUploading}
                    onClick={() => setStaged((prev) => prev.filter((_, idx) => idx !== i))}
                  >
                    <X className="h-4 w-4 text-slate-400 hover:text-rose-600" />
                  </Button>
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
            onChange={(e) => {
              setSearchInput(e.target.value);
              setPage(1); // reset immediately on type so matches aren't hidden on a later page
            }}
            className="pl-8"
          />
        </div>
        <select
          aria-label="Filter by compliance status"
          className={FILTER_SELECT_CLASS}
          value={status}
          onChange={(e) => onFilterChange(setStatus)(e.target.value)}
        >
          <option value="">All statuses</option>
          {STATUS_FILTERS.map((s) => (
            <option key={s.value} value={s.value}>
              {s.label}
            </option>
          ))}
        </select>
        <select
          aria-label="Filter by document type"
          className={FILTER_SELECT_CLASS}
          value={typeFilter}
          onChange={(e) => onFilterChange(setTypeFilter)(e.target.value)}
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
          onChange={(e) => onFilterChange(setExpiresWithin)(e.target.value)}
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
              setSearchInput("");
              setSearch("");
              setStatus("");
              setTypeFilter("");
              setExpiresWithin("");
              setPage(1);
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
                <th className="px-4 py-3 text-left">Extraction</th>
                <th className="px-4 py-3 text-left">Compliance</th>
                <th className="px-4 py-3 text-left">Expires</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {docs.isLoading ? (
                <tr>
                  <td colSpan={7} className="px-4 py-8 text-center text-sm text-slate-400">
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
                    <td data-label="Extraction" className="px-4 py-3">
                      <Badge className={cn("border-transparent font-medium", STATUS_HUE[d.extractionStatus] ?? STATUS_HUE.Pending)}>
                        {extractionStatusLabel(d.extractionStatus)}
                      </Badge>
                    </td>
                    <td data-label="Compliance" className="px-4 py-3">
                      <Badge className={cn("border-transparent", COMPLIANCE_HUE[d.complianceStatus] ?? COMPLIANCE_HUE.Pending)}>
                        {complianceStatusLabel(d.complianceStatus)}
                      </Badge>
                    </td>
                    <td data-label="Expires" className="px-4 py-3 text-slate-600">
                      {d.expirationDate ? new Date(d.expirationDate).toLocaleDateString() : "—"}
                      {d.daysUntilExpiry != null && (
                        <span className={cn("ml-2 text-xs", d.daysUntilExpiry < 30 ? "text-rose-600" : "text-slate-400")}>
                          {d.daysUntilExpiry < 0 ? `${-d.daysUntilExpiry}d ago` : `in ${d.daysUntilExpiry}d`}
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-right">
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Remove ${d.originalFileName}`}
                        disabled={del.isPending}
                        onClick={() => {
                          if (confirm(`Remove ${d.originalFileName}?`)) {
                            del.mutate(d.id, {
                              onSuccess: () => {
                                toast.success("Document removed");
                                // If that was the last row on a page past the
                                // first, step back so we don't strand the user
                                // on a now-empty page.
                                if (items.length === 1 && page > 1) setPage((p) => p - 1);
                              },
                              onError: (err) => toast.error(err instanceof Error ? err.message : "Remove failed"),
                            });
                          }
                        }}
                      >
                        <Trash2 className="w-4 h-4 text-slate-400 hover:text-rose-600" />
                      </Button>
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
