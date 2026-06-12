import type { Accept, FileRejection } from "react-dropzone";

/**
 * Map react-dropzone's machine-readable rejection codes to human copy, shared by every
 * upload dropzone (vendor portal + dashboard documents page, #265). Silent rejection is
 * hostile UX on any upload surface — the portal solved it first (#196); the dashboard
 * swallowed rejections until #265. Keep the strings short and jargon-free: both audiences
 * are non-technical, and one of them lands here exactly once.
 */
export function rejectionCopy(rejections: FileRejection[]): string | null {
  if (rejections.length === 0) return null;
  const first = rejections[0].errors[0];
  switch (first?.code) {
    case "file-invalid-type":
      // HEIC/HEIF (the iPhone camera default) is accepted and transcoded to JPEG
      // server-side (#220), so the old "switch to Most Compatible" workaround is gone.
      // This now only fires for genuinely unsupported types (a Word doc, a video, a
      // .zip) — point at the formats that do work.
      return "We can't read that file type. Please upload a PDF or a photo (JPEG, PNG, or HEIC).";
    case "file-too-large":
      // Drop the desktop "split/compress" language — on a phone-photo surface the
      // actionable fix is to reshoot from further back or send a PDF. (#196 review)
      return "That file is over the 10 MB limit. If it's a photo, try taking it again from a bit further back, or upload a PDF.";
    case "file-too-small":
      return "That file is empty.";
    case "too-many-files":
      return "Please drop one file at a time.";
    default:
      return first?.message ?? "That file couldn't be accepted.";
  }
}

/**
 * The accept map every CompliDrop dropzone uses — PDF + the photo formats the backend's
 * magic-byte validation admits (HEIC/HEIF transcode to JPEG on ingest, #220 / ADR 0018).
 * Shared so the dashboard and portal cannot drift apart again (#265: the dashboard
 * rejected the iPhone default format the portal and backend already accepted).
 */
export const UPLOAD_ACCEPT: Accept = {
  "application/pdf": [".pdf"],
  "image/jpeg": [".jpg", ".jpeg"],
  "image/png": [".png"],
  "image/heic": [".heic"],
  "image/heif": [".heif"],
};

/** Mirrors the backend's Kestrel/request cap — see CLAUDE.md "10 MB cap at Kestrel". */
export const UPLOAD_MAX_BYTES = 10 * 1024 * 1024;
