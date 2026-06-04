/**
 * E2E-side fixture shapes (#38).
 *
 * Mirrors a subset of `frontend/src/test/fixtures.ts` (the Vitest
 * harness's typed fixtures), pinned to what smoke E2E will need in
 * #39. Duplicated rather than shared so the E2E suite can run from
 * an independent build artifact without dragging the Vitest tree
 * into Playwright's compilation.
 *
 * All identifiers are obviously synthetic (`@acme.test` per RFC 6761,
 * `u_`/`o_` opaque ids). Backend ExtractionFixtures
 * (api/CompliDrop.Api.Tests/ExtractionFixtures/) are confirmed
 * synthetic per their README — this file does NOT import them.
 */
export const authedMe = {
  userId: "u_owner_01",
  organizationId: "o_acme_01",
  email: "owner@acme.test",
  fullName: "Acme Owner",
  role: "admin",
  plan: "pro",
  organizationName: "Acme Inc",
  timeZone: "UTC",
} as const;

export const anonMe = null;

export const portalInfo = {
  vendorName: "Beachfront Janitorial",
  orgName: "Acme Inc",
  instructions: "Please upload your current COI and any state license.",
  isActive: true,
  uploadCount: 0,
  maxUploads: 5,
} as const;

/**
 * Build a tiny but PDF-magic-byte-valid Buffer for `setInputFiles`.
 * The leading `%PDF-1.7` is the canonical magic byte sequence the
 * backend's file validator looks for (CLAUDE.md: "File validation
 * must use magic bytes, not Content-Type"). The trailing `%%EOF` is
 * cosmetic — a real PDF would have a xref table between them, but
 * the project's validator only inspects the prefix.
 *
 * Returns the shape `setInputFiles` accepts directly.
 */
export function makeFakePdf(name: string): {
  name: string;
  mimeType: string;
  buffer: Buffer;
} {
  return {
    name,
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.7\nsynthetic-e2e-fixture\n%%EOF"),
  };
}

// --- Shared route entries ---------------------------------------
//
// Re-usable route table entries for the most-repeated mocks. Specs
// spread these into their own route arrays so the handler shape lives
// in ONE place. #40 (rules / billing / export / reminders) will add
// more flows that need authed/anon Me handlers; lifting them now
// avoids each new spec re-declaring the same shape.
//
// Imported here (not from `./mock-api` at this file's top) to keep
// the file's import surface stable — these are co-located conveniences,
// not the harness's primary API.
import { jsonOk, jsonError } from "./mock-api";

/**
 * `GET /api/auth/me` returning 401 — the logged-out baseline. Use in
 * specs whose user-state is anonymous.
 */
export const anonMeRoute = {
  method: "GET" as const,
  path: "/api/auth/me",
  handler: jsonError("auth.unauthorized", "Not authenticated", 401),
};

/**
 * `GET /api/auth/me` returning the standard `authedMe` envelope. Use
 * in specs whose user-state is authenticated.
 */
export const authedMeRoute = {
  method: "GET" as const,
  path: "/api/auth/me",
  handler: jsonOk(authedMe),
};

/**
 * The three dashboard-page endpoints that fire on `/dashboard` mount.
 * Returning empty/zero payloads keeps a flow that navigates through
 * the dashboard quiet — no `test.no_mock` 404 spam in trace files.
 * Each spec can override individual entries if it cares about a
 * specific dashboard cell.
 */
export const emptyDashboardRoutes = [
  {
    method: "GET" as const,
    path: "/api/dashboard/stats",
    handler: jsonOk({
      totalDocuments: 0,
      compliant: 0,
      nonCompliant: 0,
      expiringSoon: 0,
      expired: 0,
      pendingExtraction: 0,
      totalVendors: 0,
      complianceRate: 0,
    }),
  },
  {
    method: "GET" as const,
    path: "/api/dashboard/expiry-pipeline",
    handler: jsonOk({
      expired: 0,
      bucket30: 0,
      bucket60: 0,
      bucket90: 0,
      beyond: 0,
    }),
  },
  {
    method: "GET" as const,
    path: "/api/dashboard/recent-activity",
    handler: jsonOk([]),
  },
];

// --- Document-detail factory ------------------------------------
//
// Mirror of `frontend/src/test/fixtures.ts:makeDocumentDetail`. ADR
// 0010 says the E2E equivalent lives here so the Vitest tree isn't
// dragged into the Playwright compile graph. Shape kept in sync at
// the field-name level; drift surfaces at runtime via mock-response
// assertions.

export type DocumentDetailFixtureE2E = {
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
  blobStorageUrl: string | null;
  generalLiabilityLimit: number | null;
  fields: Array<{
    id: string;
    fieldName: string;
    fieldValue: string | null;
    fieldType: string | null;
    confidence: number;
    isManuallyEdited: boolean;
    originalValue: string | null;
  }>;
  complianceChecks: Array<{
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
  }>;
  extractionFields: unknown;
  extractionPromptVersion: string | null;
  processingError: string | null;
  createdAt: string;
  updatedAt: string;
};

const DOCUMENT_DETAIL_BASE: DocumentDetailFixtureE2E = {
  id: "d_smoke_01",
  originalFileName: "smoke.pdf",
  documentType: "COI",
  documentSubType: null,
  vendorName: null,
  vendorContactEmail: null,
  vendorId: null,
  extractionStatus: "Pending",
  extractionConfidence: null,
  complianceStatus: "NonCompliant",
  effectiveDate: null,
  expirationDate: null,
  daysUntilExpiry: null,
  isManuallyVerified: false,
  uploadedBy: null,
  blobStorageUrl: null,
  generalLiabilityLimit: null,
  fields: [],
  complianceChecks: [],
  extractionFields: null,
  extractionPromptVersion: null,
  processingError: null,
  createdAt: "2026-05-26T12:00:00Z",
  updatedAt: "2026-05-26T12:00:00Z",
};

export function makeDocumentDetail(
  overrides: Partial<DocumentDetailFixtureE2E> = {},
): DocumentDetailFixtureE2E {
  return {
    ...DOCUMENT_DETAIL_BASE,
    fields: DOCUMENT_DETAIL_BASE.fields.map((f) => ({ ...f })),
    ...overrides,
  };
}
