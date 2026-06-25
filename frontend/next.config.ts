import type { NextConfig } from "next";
import { withSentryConfig } from "@sentry/nextjs";
import { sentryBuildOptions } from "./src/lib/sentry/build";

const nextConfig: NextConfig = {
  /* config options here */
};

// Wrap with Sentry's build-time plugin (ADR 0036). This injects the
// instrumentation hooks and, when credentials are present, uploads source maps
// so production stack traces are readable. The token-gated graceful-degradation
// logic lives in `sentryBuildOptions` so it stays unit-testable (build.test.ts).
export default withSentryConfig(nextConfig, sentryBuildOptions());
