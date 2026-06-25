import type { NextConfig } from "next";
import { withSentryConfig } from "@sentry/nextjs";

const nextConfig: NextConfig = {
  /* config options here */
};

// Wrap with Sentry's build-time plugin (ADR 0036). This injects the
// instrumentation hooks and, when credentials are present, uploads source maps
// so production stack traces are readable.
//
// GRACEFUL DEGRADATION: source-map upload is gated on SENTRY_AUTH_TOKEN. Local
// builds and any CI job without the secret (frontend-ci's build step sets only
// NEXT_PUBLIC_API_URL) skip the upload and still succeed — a missing token must
// never fail the build. Org/project/token all also fall back to the matching
// SENTRY_* env vars.
export default withSentryConfig(nextConfig, {
  org: process.env.SENTRY_ORG,
  project: process.env.SENTRY_PROJECT,
  authToken: process.env.SENTRY_AUTH_TOKEN,
  sourcemaps: { disable: !process.env.SENTRY_AUTH_TOKEN },
  // Quiet plugin logs when there's nothing to upload; show them on a real
  // (token-bearing) production build.
  silent: !process.env.SENTRY_AUTH_TOKEN,
  // Don't phone build telemetry home from a compliance product's CI.
  telemetry: false,
  // Tree-shake the Sentry SDK debug logger out of the client bundle.
  disableLogger: true,
  // Upload a wider set of client bundles so minified frames symbolicate
  // (no-op when source-map upload is disabled).
  widenClientFileUpload: true,
});
