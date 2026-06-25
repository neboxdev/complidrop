import type { ErrorEvent, Event, EventHint } from "@sentry/nextjs";
import { scrubEvent, tagCorrelationId } from "./scrub";

/**
 * Shared `Sentry.init` options for the browser, Node-server, and Edge runtimes
 * (ADR 0036). One source of truth so the PII scrubber, the dev/no-DSN no-op
 * gating, and the conservative sample rates can never drift between the three
 * `Sentry.init` call sites (`instrumentation-client.ts` + `instrumentation.ts`).
 *
 * Pure and env-injectable so `options.test.ts` can pin the gating without
 * touching the ambient process env.
 */

type Env = Record<string, string | undefined>;

/**
 * The public DSN, or `undefined` when unset/blank. DSNs are not secrets (they
 * ship in the client bundle by design); absence is the signal to no-op.
 */
export function getDsn(env: Env = process.env): string | undefined {
  const dsn = env.NEXT_PUBLIC_SENTRY_DSN?.trim();
  return dsn ? dsn : undefined;
}

/**
 * Sentry is live ONLY in production AND only when a DSN is configured. This
 * mirrors the dev-isolation posture of #271 (Resend left unset → email-silent;
 * PostHog gated on its key): a Development build, or a production build with no
 * DSN, captures nothing. Both `enabled: false` and `dsn: undefined` enforce it.
 */
export function sentryEnabled(env: Env = process.env): boolean {
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
export function commonInitOptions(env: Env = process.env) {
  return {
    dsn: getDsn(env),
    enabled: sentryEnabled(env),
    environment:
      env.NEXT_PUBLIC_SENTRY_ENVIRONMENT?.trim() || env.NODE_ENV || "production",
    // Never let the SDK attach IP addresses, cookies, or request bodies; the
    // beforeSend scrubber is the second line of defence on top of this.
    sendDefaultPii: false as const,
    tracesSampleRate: sampleRate(env.NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE, 0),
    beforeSend,
    beforeSendTransaction,
  };
}
