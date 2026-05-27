/**
 * Pins the dropzone-helper contracts added in #84.
 *
 * Four invariants matter for downstream tests:
 *   1. `dropFilesIn` throws a HELPFUL error when container is
 *      undefined — the most common foot-gun (forgot to destructure
 *      `({ container } = renderWithProviders(...))`).
 *   2. `dropFilesIn` throws a HELPFUL error when no `<input
 *      type="file">` exists in the container — the second-most-common
 *      foot-gun (forgot to render a dropzone-bearing component).
 *   3. `dropFilesIn` is container-scoped — a file input in a sibling
 *      tree (or in `document.body` outside the container) is invisible
 *      to it. This is the whole point of the lift.
 *   4. `makeFile` produces a File of the requested size — the 11 MB
 *      oversize test in `portal/[token]/page.test.tsx` silently
 *      assumes `sizeBytes: 11 * 1024 * 1024` actually yields a file
 *      whose `.size` reports as 11 MB. A regression that swapped
 *      `'x'.repeat` for a fixed-size buffer would silently regress
 *      the maxSize-rejection test to false-green.
 */
import { describe, it, expect } from "vitest";
import { dropFilesIn, makeFile } from "./dropzone";

describe("dropFilesIn — container guard contract (#84)", () => {
  it("throws with a helpful 'did you forget to destructure?' message when container is undefined", () => {
    expect(() =>
      dropFilesIn(undefined as unknown as HTMLElement, [makeFile("a.pdf")]),
    ).toThrow(/container was undefined/i);
    expect(() =>
      dropFilesIn(undefined as unknown as HTMLElement, [makeFile("a.pdf")]),
    ).toThrow(/renderWithProviders/);
  });

  it("throws with a helpful 'no <input type=file> found' message when container has no file input", () => {
    const empty = document.createElement("div");
    expect(() => dropFilesIn(empty, [makeFile("a.pdf")])).toThrow(
      /no <input type='file'> found/i,
    );
  });

  it("is container-scoped — a file input outside the container is invisible to the helper", () => {
    // Build a container with NO file input, and ALSO inject a file
    // input directly into document.body. The pre-lift global helper
    // would have picked up the body-level input; the container-scoped
    // helper must NOT.
    const container = document.createElement("div");
    document.body.appendChild(container);
    const sibling = document.createElement("input");
    sibling.type = "file";
    document.body.appendChild(sibling);

    try {
      expect(() => dropFilesIn(container, [makeFile("a.pdf")])).toThrow(
        /no <input type='file'> found/i,
      );
    } finally {
      document.body.removeChild(container);
      document.body.removeChild(sibling);
    }
  });

  it("fires a change event against the input inside the container when found", () => {
    // Smoke-pin the happy path: build a container with a file input,
    // call dropFilesIn, assert the input's files property was set
    // (the change event itself can't fire onDrop without a real
    // react-dropzone subtree, but the file-attach step is what we
    // own and the rest is integration-tested in portal/[token]).
    const container = document.createElement("div");
    const input = document.createElement("input");
    input.type = "file";
    container.appendChild(input);
    document.body.appendChild(container);

    try {
      const file = makeFile("a.pdf");
      dropFilesIn(container, [file]);
      // Object.defineProperty wrote the file list onto the input.
      expect(input.files).not.toBeNull();
      expect(input.files?.length).toBe(1);
      expect(input.files?.[0]).toBe(file);
    } finally {
      document.body.removeChild(container);
    }
  });
});

describe("makeFile — size + type contract (#84)", () => {
  it("defaults to 1 KiB application/pdf when only the name is provided", () => {
    const f = makeFile("a.pdf");
    expect(f.name).toBe("a.pdf");
    expect(f.type).toBe("application/pdf");
    expect(f.size).toBe(1024);
  });

  it("honours explicit sizeBytes — pins the maxSize-rejection test against a future refactor of the size shape", () => {
    // The 11 MB oversize test in portal/[token]/page.test.tsx
    // depends on this contract. If makeFile ever switched from
    // `'x'.repeat(N)` to a fixed-size buffer (or to a Blob whose
    // size reports differently), the boundary test would silently
    // regress.
    const oversize = makeFile("huge.pdf", "application/pdf", 11 * 1024 * 1024);
    expect(oversize.size).toBe(11 * 1024 * 1024);
    expect(oversize.size).toBeGreaterThan(10 * 1024 * 1024);
  });

  it("respects the type argument", () => {
    const malware = makeFile("malware.exe", "application/octet-stream");
    expect(malware.type).toBe("application/octet-stream");
    expect(malware.name).toBe("malware.exe");
  });
});
