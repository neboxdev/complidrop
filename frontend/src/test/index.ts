/**
 * Single import surface for component/hook tests:
 *
 *     import {
 *       renderWithProviders,
 *       server,
 *       url,
 *       jsonOk,
 *       jsonError,
 *       authedMe,
 *       documentsAllStatusesResponse,
 *       portalInfo,
 *       expiredLink404,
 *     } from "@/test";
 *
 * Keep this file a thin re-export. New harness primitives go in their own
 * module first (so they can be tree-shaken / read in isolation) and only
 * surface here once they're part of the documented template.
 */
export * from "./render";
