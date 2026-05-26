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
 *   - Every exported fixture is a `const` declared `as const`-safe value, OR
 *     a factory that takes optional `overrides` and `Object.assign`s onto a
 *     fresh copy. Use the factory whenever a test needs to mutate fields.
 *   - Dates are absolute ISO-8601 UTC strings so a test running on Mar-31
 *     doesn't render a different "days until expiry" than one running on
 *     Apr-01. Pin `Date.now` separately if you want deterministic relative
 *     copy assertions.
 */
import type { Me } from "@/hooks/useAuth";
import type {
  DocumentListItem,
  DocumentListResponse,
} from "@/hooks/useDocuments";

// -------- Auth --------

/**
 * An admin user on the Pro plan in a UTC org. The default for any test that
 * just needs "someone is logged in" — use a factory call below if your test
 * cares about role / plan / timezone branching.
 */
export const authedMe: Me = {
  userId: "u_owner_01",
  organizationId: "o_acme_01",
  email: "owner@acme.test",
  fullName: "Acme Owner",
  role: "admin",
  plan: "pro",
  organizationName: "Acme Inc",
  timeZone: "UTC",
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
 */
export const documentsAllStatuses: DocumentListItem[] = [
  {
    id: "d_pending_01",
    originalFileName: "coi-pending.pdf",
    documentType: "COI",
    vendorName: "Pending Vendor",
    vendorId: "v_pending_01",
    extractionStatus: "Pending",
    extractionConfidence: null,
    complianceStatus: "Unknown",
    effectiveDate: null,
    expirationDate: null,
    daysUntilExpiry: null,
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
    complianceStatus: "Unknown",
    effectiveDate: null,
    expirationDate: null,
    daysUntilExpiry: null,
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
    effectiveDate: "2026-01-01",
    expirationDate: "2026-12-31",
    daysUntilExpiry: 219,
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
    complianceStatus: "Unknown",
    effectiveDate: null,
    expirationDate: null,
    daysUntilExpiry: null,
    createdAt: "2026-05-24T16:00:00Z",
  },
];

export const documentsAllStatusesResponse: DocumentListResponse = {
  items: documentsAllStatuses,
  total: documentsAllStatuses.length,
  page: 1,
  pageSize: 50,
};

// -------- Vendor portal --------

/**
 * Mirrors the inline PortalInfo type in `frontend/src/app/portal/[token]/page.tsx`.
 * That route hand-rolls its fetch (it can't use the cookie-based api client),
 * so the type lives in the page file — duplicate the shape here rather than
 * exporting it from the route, which would force the route into the test
 * compilation graph just for a type.
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
export const portalInfo: PortalInfoFixture = {
  vendorName: "Beachfront Janitorial",
  orgName: "Acme Inc",
  instructions:
    "Please upload your current COI and any state license. PDF / JPEG / PNG, 10 MB max.",
  isActive: true,
  uploadCount: 0,
  maxUploads: 5,
};

/**
 * The body the API returns for an expired or revoked portal link — same
 * envelope `lib/api.ts` parses for any 4xx, status 404. Tests assert the
 * portal page renders the "no longer available" UI on this response.
 */
export const expiredLink404 = {
  status: 404,
  body: {
    data: null,
    error: {
      code: "portal.expired",
      message: "This link is no longer available.",
    },
  },
} as const;
