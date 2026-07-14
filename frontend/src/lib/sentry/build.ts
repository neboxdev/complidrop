type Env = Record<string, string | undefined>;

/**
 * Build-time options for `withSentryConfig` (ADR 0037).
 *
 * GRACEFUL DEGRADATION: source-map upload (and the plugin's logs) are gated on
 * `SENTRY_AUTH_TOKEN`. Local builds and any CI job without the secret (the
 * frontend-ci build step sets only `NEXT_PUBLIC_API_URL`) skip the upload and
 * still succeed — a missing token must never fail the build.
 *
 * Pure + env-injectable so the degradation contract is unit-testable;
 * `next.config.ts` itself can't be unit-tested, so the token-gating lives here.
 *
 * The aliased `env = process.env` default is fine HERE (unlike `./options.ts`,
 * which must read literally for Next's client-bundle inlining, see its
 * RUNTIME_ENV): SENTRY_AUTH_TOKEN / SENTRY_ORG / SENTRY_PROJECT are not
 * `NEXT_PUBLIC_*` vars — they're consumed only by `next.config.ts` inside the
 * real Node build process and never bundled for the browser.
 */
export function sentryBuildOptions(env: Env = process.env) {
  const hasAuthToken = Boolean(env.SENTRY_AUTH_TOKEN);
  return {
    org: env.SENTRY_ORG,
    project: env.SENTRY_PROJECT,
    authToken: env.SENTRY_AUTH_TOKEN,
    // No token ⇒ disable upload (and silence plugin logs); a real prod build with
    // a token uploads and logs.
    sourcemaps: { disable: !hasAuthToken },
    silent: !hasAuthToken,
    // Don't phone build telemetry home from a compliance product's CI.
    telemetry: false,
    // Upload a wider set of client bundles so minified frames symbolicate
    // (no-op when source-map upload is disabled).
    widenClientFileUpload: true,
    // NB: the deprecated `disableLogger` (and the webpack `treeshake.*` options)
    // are intentionally omitted — Next 16 builds with Turbopack, which doesn't
    // support them, and the SDK debug logger is inert unless `debug: true` (never
    // set). Keeping `disableLogger` only emitted a deprecation warning on every
    // prod build for no effect.
  };
}
