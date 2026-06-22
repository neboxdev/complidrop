/**
 * The shared rejection-code → human-copy map and the shared accept list (#265).
 * Behavior contracts pinned here; the page-level wiring (toast on the dashboard,
 * inline error on the portal) is pinned in each page's own test file.
 */
import { describe, expect, it } from "vitest";
import type { FileRejection } from "react-dropzone";

import { rejectionCopy, UPLOAD_ACCEPT, UPLOAD_MAX_BYTES } from "@/lib/upload-policy";

function rejection(code: string, message = "machine message"): FileRejection {
  return {
    file: new File(["x"], "f.bin", { type: "application/octet-stream" }),
    errors: [{ code, message }],
  } as FileRejection;
}

describe("rejectionCopy (#265)", () => {
  it("returns null for no rejections", () => {
    expect(rejectionCopy([])).toBeNull();
  });

  it("maps file-invalid-type to the formats-that-work copy", () => {
    // FP-125: copy names the accepted FORMATS ("file format" + an explicit list) rather than
    // "upload … a photo" — a vendor whose WebP/GIF was rejected DID send a photo.
    expect(rejectionCopy([rejection("file-invalid-type")])).toMatch(
      /can't read that file format.*PDF, JPEG, PNG, or HEIC/i,
    );
  });

  it("maps file-too-large to the 10 MB copy with a phone-friendly fix", () => {
    expect(rejectionCopy([rejection("file-too-large")])).toMatch(/over the 10 MB limit/i);
  });

  it("maps file-too-small and too-many-files", () => {
    expect(rejectionCopy([rejection("file-too-small")])).toBe("That file is empty.");
    expect(rejectionCopy([rejection("too-many-files")])).toBe("Please drop one file at a time.");
  });

  it("unknown codes fall back to the rejection's own message, then a generic line", () => {
    expect(rejectionCopy([rejection("future-code", "Custom reason.")])).toBe("Custom reason.");
    const noErrors = { file: new File(["x"], "f"), errors: [] } as unknown as FileRejection;
    expect(rejectionCopy([noErrors])).toBe("That file couldn't be accepted.");
  });

  it("uses the FIRST rejection's first error when several files were rejected", () => {
    expect(
      rejectionCopy([rejection("file-too-large"), rejection("file-invalid-type")]),
    ).toMatch(/over the 10 MB limit/i);
  });
});

describe("UPLOAD_ACCEPT / UPLOAD_MAX_BYTES (#265)", () => {
  it("accepts EXACTLY the formats the backend's magic-byte validation admits", () => {
    // Exact set, not arrayContaining: an addition (say image/gif) would pass the
    // client but fail magic-byte validation at upload — on BOTH surfaces at once now
    // that the map is shared — re-creating the silent-failure class #265 kills.
    expect(Object.keys(UPLOAD_ACCEPT).sort()).toEqual(
      ["application/pdf", "image/heic", "image/heif", "image/jpeg", "image/png"].sort(),
    );
  });

  it("caps at the backend's 10 MB Kestrel limit", () => {
    expect(UPLOAD_MAX_BYTES).toBe(10 * 1024 * 1024);
  });
});
