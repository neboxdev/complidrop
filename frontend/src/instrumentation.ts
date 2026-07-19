// Next.js 16 server/edge instrumentation (App Router). `register` runs once when
// a server instance boots; `onRequestError` forwards Next's server error hook
// (server components, route handlers, server actions) to Sentry. See
// node_modules/next/dist/docs/01-app/03-api-reference/03-file-conventions/
// instrumentation.md.
//
// Both runtimes initialise from the SAME shared, PII-scrubbed options
// (ADR 0037). Next compiles this file once per runtime and resolves
// `@sentry/nextjs` to the matching node / edge build, so one call site serves
// both. commonInitOptions makes init a no-op when NEXT_PUBLIC_SENTRY_DSN is
// unset or NODE_ENV !== production.
import * as Sentry from "@sentry/nextjs";
import { commonInitOptions } from "./lib/sentry/options";

export async function register(): Promise<void> {
  if (
    process.env.NEXT_RUNTIME === "nodejs" ||
    process.env.NEXT_RUNTIME === "edge"
  ) {
    Sentry.init(commonInitOptions());
  }
}

// captureRequestError applies the configured client (and therefore the
// beforeSend scrubber) to server-side request errors.
export const onRequestError = Sentry.captureRequestError;
