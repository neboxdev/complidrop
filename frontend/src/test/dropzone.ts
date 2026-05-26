/**
 * Dropzone test helpers — `dropFilesIn` and `makeFile`.
 *
 * `react-dropzone` v15+ evaluates `accept` + `maxSize` INSIDE its
 * `onDrop` callback (not at the browser-level input filter), which
 * jsdom can't enforce. The canonical jsdom-friendly path is therefore
 * to populate the hidden file input directly and fire a `change`
 * event — `fireEvent.drop` on the dropzone root works too but the
 * input path is what real users hit via the click-to-select
 * affordance.
 *
 * The previous inline helper in `portal/[token]/page.test.tsx` used
 * `document.querySelector('input[type="file"]')` — a global lookup
 * that two simultaneous file inputs (e.g. a portal preview rendered
 * alongside a documents-page modal in a future composite test) would
 * silently collide on. `dropFilesIn(container, files)` scopes the
 * input lookup to the rendered container from `renderWithProviders`
 * so that hazard is impossible.
 */
import { fireEvent } from "@testing-library/react";

/**
 * Populate the file `<input type="file">` inside `container` and fire
 * a synthetic `change` event so `react-dropzone`'s `onDrop` callback
 * runs against the supplied files. Throws if no file input is found —
 * a missing input is always a programming error in the test setup,
 * never an expected branch.
 *
 * @param container the rendered container (e.g. from `renderWithProviders`)
 * @param files     the File objects to attach
 */
export function dropFilesIn(container: HTMLElement, files: File[]): void {
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
