import type { ErrorEvent, Event, EventHint } from "@sentry/nextjs";
import { scrubEvent, tagCorrelationId } from "./scrub";

/**
 * Shared `Sentry.init` options for the browser, Node-server, and Edge runtimes
 * (ADR 0037). One source of truth so the PII scrubber, the dev/no-DSN no-op
 * gating, and the conservative sample rates can never drift between the three
 * `Sentry.init` call sites (`instrumentation-client.ts` + `instrumentation.ts`).
 *
 * Pure and env-injectable so `options.test.ts` can pin the gating without
 * touching the ambient process env. The injectable parameter defaults to
 * {@link RUNTIME_ENV}, NOT to `process.env` — see the comment there.
 */

type Env = Record<string, string | undefined>;

// Default env for the no-arg call sites (the three real `Sentry.init`s).
//
// Next.js inlines `NEXT_PUBLIC_*` vars into the BROWSER bundle only for
// LITERAL `process.env.X` member expressions; an aliased read (`const env =
// process.env; env.NEXT_PUBLIC_X` — which is exactly what a `env: Env =
// process.env` default parameter is) is the documented "will NOT be inlined"
// case (node_modules/next/dist/docs/01-app/02-guides/environment-variables.md,
// "Bundling Environment Variables for the Browser"). With an aliased default,
// every value below reached the client as `undefined`, so the browser
// `Sentry.init` ran `{ dsn: undefined, enabled: false }` and captured nothing
// in production even with the DSN configured (#356). Server/edge were
// unaffected (their `process.env` is a real runtime object). So: every env var
// is read here ONCE, at module scope, as a literal member expression — never
// alias `process.env` for a `NEXT_PUBLIC_*` (or `NODE_ENV`) read in
// client-reachable code.
//
// (`./build.ts` keeps its aliased `process.env` default deliberately:
// SENTRY_AUTH_TOKEN / SENTRY_ORG / SENTRY_PROJECT are build-time-only vars
// read by `next.config.ts` inside the real Node build process and are never
// bundled for the browser.)
const RUNTIME_ENV: Env = {
  NEXT_PUBLIC_SENTRY_DSN: process.env.NEXT_PUBLIC_SENTRY_DSN,
  NEXT_PUBLIC_SENTRY_ENVIRONMENT: process.env.NEXT_PUBLIC_SENTRY_ENVIRONMENT,
  NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE:
    process.env.NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE,
  NODE_ENV: process.env.NODE_ENV,
};

/**
 * The public DSN, or `undefined` when unset/blank. DSNs are not secrets (they
 * ship in the client bundle by design); absence is the signal to no-op.
 */
export function getDsn(env: Env = RUNTIME_ENV): string | undefined {
  const dsn = env.NEXT_PUBLIC_SENTRY_DSN?.trim();
  return dsn ? dsn : undefined;
}

/**
 * Sentry is live ONLY in production AND only when a DSN is configured. This
 * mirrors the dev-isolation posture of #271 (Resend left unset → email-silent;
 * PostHog gated on its key): a Development build, or a production build with no
 * DSN, captures nothing. Both `enabled: false` and `dsn: undefined` enforce it.
 */
export function sentryEnabled(env: Env = RUNTIME_ENV): boolean {
  return Boolean(getDsn(env)) && env.NODE_ENV === "production";
}

/**
 * Parse a `[0,1]` sample-rate env var, falling back to `fallback` for unset /
 * blank / non-numeric / out-of-range input. Keeps a typo (`"0.1.0"`, `"50"`)
 * from silently sending 100% of traces at $49/mo scale.
 */
export function sampleRate(raw: string | undefined, fallback: number): number {
  if (raw === undefined || raw.trim() === "") return fallback;
  const n = Number(raw);
  return Number.isFinite(n) && n >= 0 && n <= 1 ? n : fallback;
}

function beforeSend(event: ErrorEvent, hint: EventHint): ErrorEvent | null {
  const scrubbed = scrubEvent(event);
  tagCorrelationId(scrubbed, hint);
  return scrubbed;
}

// Generic over the concrete event type (TransactionEvent isn't surfaced at the
// @sentry/nextjs top level; reaching into the transitive @sentry/core would
// trip the knip unlisted-dependency gate). Instantiates to the option's
// expected signature at the Sentry.init call site.
function beforeSendTransaction<T extends Event>(event: T): T | null {
  return scrubEvent(event);
}

/**
 * Build the runtime-agnostic init options. Spread into each runtime's
 * `Sentry.init`.
 *
 * Defaults are deliberately conservative for a cost-sensitive SMB product:
 * `tracesSampleRate` is 0 (pure error monitoring — errors are captured
 * regardless of the trace rate), tunable up via
 * `NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE`. Session Replay is NOT enabled here
 * (a COI on screen must never be recorded).
 */
export function commonInitOptions(env: Env = RUNTIME_ENV) {
  return {
    dsn: getDsn(env),
    enabled: sentryEnabled(env),
    environment:
      env.NEXT_PUBLIC_SENTRY_ENVIRONMENT?.trim() || env.NODE_ENV || "production",
    // Never let the SDK attach IP addresses, cookies, or request bodies; the
    // beforeSend scrubber is the second line of defence on top of this.
    sendDefaultPii: false as const,
    // SDK-level cap on long string fields (the SDK sets no default). Bounds
    // payload size and is a second line of defence behind the scrubber's own
    // per-string cap for the beforeSend regex passes (ADR 0037).
    maxValueLength: 8192,
    tracesSampleRate: sampleRate(env.NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE, 0),
    beforeSend,
    beforeSendTransaction,
  };
}
