/**
 * Dropzone test helpers — `dropFilesIn` and `makeFile`.
 *
 * `react-dropzone` v15+ evaluates `accept` + `maxSize` INSIDE its
 * `onDrop` callback (not at the browser-level `<input accept="…">`
 * filter, which jsdom ignores). The canonical jsdom-friendly path is
 * therefore to populate the hidden file input directly and fire a
 * `change` event — `fireEvent.drop` on the dropzone root works too
 * but requires building a `DataTransfer` object jsdom doesn't fully
 * support, and the input path is what real users hit via the click-
 * to-select affordance anyway.
 *
 * The previous inline helper in `portal/[token]/page.test.tsx` used
 * `document.querySelector('input[type="file"]')` — a global lookup
 * that two simultaneous file inputs (e.g. a portal preview rendered
 * alongside a documents-page modal in a future composite test) would
 * silently collide on. `dropFilesIn(container, files)` scopes the
 * input lookup to the rendered container from `renderWithProviders`
 * so that hazard is impossible.
 *
 * ## Upstream pins (what could silently break this helper)
 *
 * - **react-dropzone v15**: relies on the contract that `accept` and
 *   `maxSize` are validated inside the `onDrop` callback, NOT only on
 *   the browser-level `<input accept>` attribute. A v16+ release that
 *   moved validation entirely to the input's `accept` attribute would
 *   silently false-pass every accept/maxSize rejection test under
 *   jsdom (which ignores `<input accept>`). The escape hatch is to
 *   switch to `fireEvent.drop` on the dropzone root with a constructed
 *   `DataTransfer`.
 * - **jsdom permissive `HTMLInputElement.files`**: real browsers reject
 *   direct property assignment to `.files`; jsdom allows it via the
 *   `Object.defineProperty` workaround used below (the `configurable:
 *   true` flag is critical — without it, a second call on the same
 *   input would throw). A jsdom major-version upgrade that tightened
 *   `.files` to match browser semantics would also break this helper.
 *
 * Both pins are exercised in `dropzone.test.ts`.
 */
import { fireEvent } from "@testing-library/react";

/**
 * Populate the file `<input type="file">` inside `container` and fire
 * a synthetic `change` event so `react-dropzone`'s `onDrop` callback
 * runs against the supplied files. Throws if container is undefined
 * or if no file input is found — both are always programming errors
 * in test setup, never expected branches.
 *
 * @param container the rendered container (e.g. from `renderWithProviders`)
 * @param files     the File objects to attach
 */
export function dropFilesIn(container: HTMLElement, files: File[]): void {
  // Guard for the first-test-or-unassigned case. The portal-page test
  // captures `container` via a module-level `let container: HTMLElement`
  // that's destructured per-`it` — if a contributor adds a new test
  // and forgets the destructure, `container` is undefined and the
  // bare `container.querySelector(...)` would throw
  // `TypeError: Cannot read properties of undefined (reading
  // 'querySelector')` pointing at this file, not at the missing
  // assignment. The explicit guard names the actual root cause.
  if (!container) {
    throw new Error(
      "dropFilesIn: container was undefined — did you forget " +
        "`({ container } = renderWithProviders(...))` in this test?",
    );
  }
  const input = container.querySelector(
    'input[type="file"]',
  ) as HTMLInputElement | null;
  if (!input) {
    throw new Error(
      "dropFilesIn: no <input type='file'> found inside container",
    );
  }
  Object.defineProperty(input, "files", {
    value: files,
    configurable: true,
  });
  fireEvent.change(input);
}

/**
 * Factory for a `File` of a given name, MIME type, and (padded) size.
 * Matches the previous inline helper used by the portal test:
 * defaults to a 1 KiB PDF.
 */
export function makeFile(
  name: string,
  type = "application/pdf",
  sizeBytes = 1024,
): File {
  return new File(["x".repeat(sizeBytes)], name, { type });
}
