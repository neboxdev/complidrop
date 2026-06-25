"use client";

import * as Sentry from "@sentry/nextjs";
import { useEffect } from "react";
import { GENERIC_FALLBACK_MESSAGE } from "@/lib/api";

// App Router global error boundary (ADR 0036). Catches render crashes that
// escape every nested boundary — including a crash in the root layout — so it
// must render its own <html>/<body> and cannot depend on the app's CSS having
// loaded (hence inline styles for the critical fallback). Two jobs:
//
//   1. Report the technical error to Sentry — the ONLY place the raw error is
//      allowed to go. The beforeSend scrubber strips any PII before it leaves
//      the browser.
//   2. Show the user friendly, jargon-free copy. Per the #77 / #254 error-copy
//      policy we render GENERIC_FALLBACK_MESSAGE (the single source of truth in
//      lib/api.ts), NEVER `error.message` — a raw React render error ("Cannot
//      read properties of undefined") or HTTP jargon must never reach the
//      screen. Sentry holds the technical detail; the UI stays human.
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  useEffect(() => {
    Sentry.captureException(error);
  }, [error]);

  return (
    <html lang="en">
      <body
        style={{
          margin: 0,
          minHeight: "100vh",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          padding: "1.5rem",
          fontFamily:
            "system-ui, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
          background: "#f8fafc",
          color: "#0f172a",
        }}
      >
        <main style={{ maxWidth: "28rem", textAlign: "center" }}>
          <h1 style={{ fontSize: "1.25rem", fontWeight: 600, margin: "0 0 0.5rem" }}>
            CompliDrop hit a snag
          </h1>
          <p style={{ margin: "0 0 1.25rem", color: "#475569", lineHeight: 1.5 }}>
            {GENERIC_FALLBACK_MESSAGE}
          </p>
          <button
            type="button"
            onClick={() => reset()}
            style={{
              cursor: "pointer",
              borderRadius: "0.5rem",
              border: "none",
              padding: "0.625rem 1.25rem",
              fontSize: "0.9375rem",
              fontWeight: 600,
              color: "#ffffff",
              background: "#0284c7",
            }}
          >
            Try again
          </button>
        </main>
      </body>
    </html>
  );
}
