/**
 * Named, typed fixtures shared across every component/hook test.
 *
 * Why fixtures live in one place:
 *   - Every test gets the same shape; a backend contract change is one edit
 *     here, not a search-and-replace across the test tree.
 *   - Tests read intent ("authed admin", "expired portal link") instead of
 *     re-deriving payloads from each route's hook type.
 *   - The shapes are pinned to the real DTOs imported from the data layer,
 *     so a TS rename catches stale fixtures at compile time.
 *
 * Conventions:
 *   - Every exported fixture value is typed `Readonly<…>` / `readonly`-array
 *     so a misbehaving test that mutates `documentsAllStatuses[0]` is a
 *     compile error, not a silent leak into the next test. The README's
 *     "use the factory whenever a test needs to mutate fields" guidance is
 *     thus type-enforced, not just convention.
 *   - Every shared shape has a matching `makeXxx(overrides)` factory that
 *     spreads onto a fresh copy. Tests that need a variant call the factory.
 *   - Dates are absolute ISO-8601 UTC strings with the explicit `Z` suffix
 *     so they parse identically to the backend's `DateTime` (Kind=Utc)
 *     serialization. A date-only string like `"2026-12-31"` parses as UTC
 *     midnight, but a no-Z string like `"2026-12-31T00:00:00"` parses as
 *     LOCAL midnight — the two render different days across the dateline,
 *     so the `Z` matters for test determinism.
 *   - `complianceStatus` values are restricted to what the backend enum
 *     `ComplianceStatus` actually emits — `Pending | Compliant |
 *     NonCompliant | ExpiringSoon | Expired`. Never `"Unknown"`: the
 *     backend never returns it, so a test that depends on that branch is
 *     testing a state production cannot produce.
 */
import type { Me } from "@/hooks/useAuth";
import type {
  DocumentListItem,
  DocumentListResponse,
} from "@/hooks/useDocuments";
import { jsonError, url } from "./helpers";
import { http, type HttpHandler } from "msw";

// -------- Auth --------

/**
 * An admin user on the Pro plan in a UTC org. The default for any test that
 * just needs "someone is logged in" — use `makeMe(overrides)` if the test
 * cares about role / plan / timezone branching.
 */
export const authedMe: Readonly<Me> = {
  userId: "u_owner_01",
  organizationId: "o_acme_01",
  email: "owner@acme.test",
  fullName: "Acme Owner",
  role: "admin",
  plan: "pro",
  organizationName: "Acme Inc",
  timeZone: "UTC",
  // Verified by default so the "confirm your email" banner (#184) stays hidden
  // in the bulk of tests; use makeMe({ emailVerified: false }) to exercise it.
  emailVerified: true,
  // Onboarded by default so the first-run welcome modal (#191) stays closed in
  // the bulk of tests; use makeMe({ hasCompletedOnboarding: false }) to exercise it.
  hasCompletedOnboarding: true,
  // Server feature flags (#416, ADR 0036 Amendment 3): corrected checklists default OFF — the
  // production posture pending the G1 legal sign-off — so the bulk of tests exercise today's
  // visible product; use makeMe({ features: { correctedChecklists: true } }) to exercise the
  // gated liquor add-menu option / additional-insured nudge.
  features: { correctedChecklists: false },
};

export function makeMe(overrides: Partial<Me> = {}): Me {
  return { ...authedMe, ...overrides };
}

// -------- Documents --------

/**
 * The canonical "every extraction status" set used by the documents-list
 * tests. Each entry is a stable id + filename so assertions read clearly.
 *
 * Order matches the documents page's default sort (newest first); status
 * order is Pending → Processing → Completed → Failed so reviewers scanning
 * the fixture can see the full state machine at a glance.
 *
 * The compliance-status field follows the backend enum — Pending while
 * extraction is still in flight or failed, Compliant once the extracted
 * dates are valid. ExpiringSoon / Expired / NonCompliant are exposed via
 * the factory below for tests that need them.
 */
export const documentsAllStatuses: ReadonlyArray<Readonly<DocumentListItem>> = [
  {
    id: "d_pending_01",
    originalFileName: "coi-pending.pdf",
    documentType: "COI",
    vendorName: "Pending Vendor",
    vendorId: "v_pending_01",
    extractionStatus: "Pending",
    extractionConfidence: null,
    complianceStatus: "Pending",
    effectiveDate: null,
    expirationDate: null,
    daysUntilExpiry: null,
    isSample: false,
    createdAt: "2026-05-26T12:00:00Z",
  },
  {
    id: "d_processing_01",
    originalFileName: "license-processing.pdf",
    documentType: "License",
    vendorName: "Processing Vendor",
    vendorId: "v_processing_01",
    extractionStatus: "Processing",
    extractionConfidence: null,
    complianceStatus: "Pending",
    effectiveDate: null,
    expirationDate: null,
    daysUntilExpiry: null,
    isSample: false,
    createdAt: "2026-05-26T11:50:00Z",
  },
  {
    id: "d_completed_01",
    originalFileName: "coi-completed.pdf",
    documentType: "COI",
    vendorName: "Completed Vendor",
    vendorId: "v_completed_01",
    extractionStatus: "Completed",
    extractionConfidence: 0.94,
    complianceStatus: "Compliant",
    effectiveDate: "2026-01-01T00:00:00Z",
    expirationDate: "2026-12-31T00:00:00Z",
    daysUntilExpiry: 219,
    isSample: false,
    createdAt: "2026-05-25T09:30:00Z",
  },
  {
    id: "d_failed_01",
    originalFileName: "permit-failed.pdf",
    documentType: "Permit",
    vendorName: null,
    vendorId: null,
    extractionStatus: "Failed",
    extractionConfidence: null,
    complianceStatus: "Pending",
    effectiveDate: null,
    expirationDate: null,
    daysUntilExpiry: null,
    isSample: false,
    createdAt: "2026-05-24T16:00:00Z",
  },
];

// -------- Document detail --------

/**
 * Mirror of the inline `DocDetail` type from
 * `frontend/src/app/(dashboard)/documents/[id]/page.tsx`. The detail
 * page hand-rolls the type inline (its own `useQuery` on a per-id key
 * lives next to the page), so this fixture's shape DOES drift if the
 * page renames a field — that's intentional. Keeping the fixture here
 * lets portal extraction-error tests in #37 reuse the same shape
 * without re-deriving it from a page-private type.
 */
export type DocumentDetailField = {
  id: string;
  fieldName: string;
  fieldValue: string | null;
  fieldType: string | null;
  confidence: number;
  isManuallyEdited: boolean;
  originalValue: string | null;
};

/**
 * Mirror of the backend `ComplianceCheckDto` (camelCased over JSON), as the
 * detail page consumes it from `DocumentDetail.complianceChecks`. (#193)
 */
export type ComplianceCheckFixture = {
  id: string;
  complianceRuleId: string;
  ruleFieldName: string | null;
  ruleOperator: string | null;
  ruleExpectedValue: string | null;
  ruleErrorMessage: string | null;
  actualValue: string | null;
  isPassed: boolean;
  notes: string | null;
  checkedAt: string;
};

const COMPLIANCE_CHECK_BASE: Readonly<ComplianceCheckFixture> = {
  id: "chk_gl_01",
  complianceRuleId: "rule_gl_01",
  ruleFieldName: "general_liability_limit",
  ruleOperator: "min_value",
  ruleExpectedValue: "1000000",
  ruleErrorMessage: "General liability must be at least $1,000,000",
  actualValue: "500000",
  isPassed: false,
  notes: "Value 500000 below required minimum 1000000.",
  checkedAt: "2026-05-26T12:00:00Z",
};

/** A failed-by-default compliance check; pass `{ isPassed: true }` for a met one. */
export function makeComplianceCheck(
  overrides: Partial<ComplianceCheckFixture> = {},
): ComplianceCheckFixture {
  return { ...COMPLIANCE_CHECK_BASE, ...overrides };
}

export type DocumentDetailFixture = {
  id: string;
  originalFileName: string;
  documentType: string;
  documentSubType: string | null;
  vendorName: string | null;
  vendorContactEmail: string | null;
  vendorId: string | null;
  extractionStatus: string;
  extractionConfidence: number | null;
  complianceStatus: string;
  effectiveDate: string | null;
  expirationDate: string | null;
  daysUntilExpiry: number | null;
  isManuallyVerified: boolean;
  uploadedBy: string | null;
  isSample: boolean;
  generalLiabilityLimit: number | null;
  fields: DocumentDetailField[];
  complianceChecks: ComplianceCheckFixture[];
  unreadableFields: string[];
  extractionFields: unknown;
  extractionPromptVersion: string | null;
  processingError: string | null;
  createdAt: string;
  updatedAt: string;
};

const DOCUMENT_DETAIL_BASE: Readonly<DocumentDetailFixture> = {
  id: "d_completed_01",
  originalFileName: "coi.pdf",
  documentType: "COI",
  documentSubType: null,
  vendorName: null,
  vendorContactEmail: null,
  vendorId: null,
  extractionStatus: "Pending",
  extractionConfidence: null,
  complianceStatus: "Pending",
  effectiveDate: null,
  expirationDate: null,
  daysUntilExpiry: null,
  isManuallyVerified: false,
  uploadedBy: null,
  isSample: false,
  generalLiabilityLimit: null,
  fields: [],
  complianceChecks: [],
  unreadableFields: [],
  extractionFields: null,
  extractionPromptVersion: null,
  processingError: null,
  createdAt: "2026-05-26T12:00:00Z",
  updatedAt: "2026-05-26T12:00:00Z",
};

export const documentDetail = DOCUMENT_DETAIL_BASE;

/**
 * Build a fresh `DocumentDetailFixture` deep-copying every nested field
 * so the caller can mutate freely without affecting the shared base.
 * Test files use this to construct the polling-transition fixtures
 * (Pending → Completed, Processing → Failed) and the failed-path
 * extraction-error card.
 */
export function makeDocumentDetail(
  overrides: Partial<DocumentDetailFixture> = {},
): DocumentDetailFixture {
  return {
    ...DOCUMENT_DETAIL_BASE,
    fields: DOCUMENT_DETAIL_BASE.fields.map((f) => ({ ...f })),
    complianceChecks: DOCUMENT_DETAIL_BASE.complianceChecks.map((c) => ({ ...c })),
    ...overrides,
  };
}

/**
 * Build a fresh `DocumentListResponse`, deep-copying every entry so the
 * caller gets a value totally disconnected from the shared array. Override
 * any top-level field (e.g. `total`, `page`) or pass a fresh `items` list
 * built from `makeDocument(...)` for variants the canonical set doesn't
 * carry.
 *
 * Prefer this factory in tests that want isolation. The exported
 * `documentsAllStatusesResponse` const is a stable snapshot for read-only
 * uses (MSW handlers that `JSON.stringify` the value, deep-equality
 * assertions).
 */
export function makeDocumentsResponse(
  overrides: Partial<DocumentListResponse> = {},
): DocumentListResponse {
  return {
    items: documentsAllStatuses.map((d) => ({ ...d })),
    total: documentsAllStatuses.length,
    page: 1,
    pageSize: 50,
    ...overrides,
  };
}

export const documentsAllStatusesResponse: Readonly<DocumentListResponse> =
  makeDocumentsResponse();

/**
 * Build a single document with whatever fields a test wants to vary. Use
 * for ExpiringSoon / Expired / NonCompliant rows that the canonical set
 * deliberately doesn't carry.
 */
export function makeDocument(
  overrides: Partial<DocumentListItem> = {},
): DocumentListItem {
  return { ...documentsAllStatuses[2], ...overrides };
}

// -------- Vendor portal --------

/**
 * Mirrors the inline PortalInfo type in `frontend/src/app/portal/[token]/page.tsx`.
 * That route hand-rolls its fetch (it can't use the cookie-based api client),
 * so the type lives in the page file — duplicate the shape here rather than
 * exporting it from the route, which would force the route into the test
 * compilation graph just for a type.
 *
 * NOTE: the portal page parses the envelope INLINE — it doesn't go through
 * `lib/api.ts`. MSW handlers still match on URL, but `ApiError` is never
 * thrown for portal responses; the page's own `try/catch` maps `body.error`
 * to a string. Portal tests assert on inline error-string state, not
 * `ApiError` instances.
 */
export type PortalInfoFixture = {
  vendorName: string;
  orgName: string;
  instructions: string;
  isActive: boolean;
  uploadCount: number;
  maxUploads: number;
};

/**
 * Healthy portal link: active, under quota, with simple instructions.
 */
export const portalInfo: Readonly<PortalInfoFixture> = {
  vendorName: "Beachfront Janitorial",
  orgName: "Acme Inc",
  instructions:
    "Please upload your current COI and any state license. PDF / JPEG / PNG, 10 MB max.",
  isActive: true,
  uploadCount: 0,
  maxUploads: 5,
};

export function makePortalInfo(
  overrides: Partial<PortalInfoFixture> = {},
): PortalInfoFixture {
  return { ...portalInfo, ...overrides };
}

/**
 * MSW handler factory for an expired/revoked portal link. Composes with the
 * shared `jsonError` helper so the response shape stays in lockstep with
 * every other 4xx in the harness — no hand-rolled `new Response(...)`.
 *
 * Pass the EXACT token the test will request, e.g.:
 *
 *     server.use(expiredPortalLinkHandler("abc"));
 *     renderWithProviders(<Portal />, { params: { token: "abc" } });
 *
 * The default `":token"` is MSW's path-param syntax — it matches any token
 * value on `/api/portal/*`. Use it when the test doesn't care which token
 * the page fetches, only that the response is the expired-link envelope.
 *
 *     server.use(expiredPortalLinkHandler()); // matches any /api/portal/*
 */
export function expiredPortalLinkHandler(token = ":token"): HttpHandler {
  return http.get(url(`/api/portal/${token}`), () =>
    jsonError("portal.expired", "This link is no longer available.", {
      status: 404,
    }),
  );
}

/**
 * The raw envelope shape an expired-link response carries. Useful for tests
 * that want to inspect the literal payload (e.g. assert the page renders
 * the backend's error message verbatim).
 */
export const expiredLink404 = {
  status: 404,
  envelope: {
    data: null,
    error: {
      code: "portal.expired",
      message: "This link is no longer available.",
    },
  },
} as const;
