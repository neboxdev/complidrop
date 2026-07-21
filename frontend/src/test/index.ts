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
 *       expiredPortalLinkHandler,
 *     } from "@/test";
 *
 * Each leaf module owns its own responsibility and stays cheap to import
 * directly (e.g. `import { url } from "@/test/helpers"` doesn't drag in
 * React Testing Library or MSW). This index is the convenience surface;
 * the leaf paths are the precise surface.
 */
export {
  renderWithProviders,
  createTestQueryClient,
  createTestWrapper,
  type RenderWithProvidersOptions,
} from "./render";
export { server } from "./server";
export * from "./helpers";
export * from "./fixtures";
export {
  navState,
  resetNavigation,
  setNavigationState,
  setNavigationCommitDelay,
  subscribeNavigation,
} from "./navigation";
export type { NavigationState, RouterMock } from "./navigation";
export {
  toastSuccess,
  toastError,
  toastInfo,
  toastWarning,
  toastLoading,
  toastDismiss,
  toastMessage,
  toastPromise,
  resetSonner,
} from "./sonner";
export { sequencedJsonOk, sequencedResponses } from "./polling";
export { dropFilesIn, makeFile } from "./dropzone";
export { assertNotInDom } from "./security";
export { fillByLabel, submitFormIn } from "./forms";
