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
