/**
 * Pins the shared Sentry init-option gating (ADR 0037): Sentry must be a
 * no-op in Development and whenever the DSN is unset, sample rates must stay
 * conservative + env-tunable, and beforeSend must actually run the scrubber and
 * the correlationId tagger.
 */
import { afterEach, describe, it, expect, vi } from "vitest";
import type { ErrorEvent, Event, EventHint } from "@sentry/nextjs";
import {
  commonInitOptions,
  getDsn,
  sampleRate,
  sentryEnabled,
} from "./options";

const DSN = "https://abc123@o1.ingest.sentry.io/42";

describe("getDsn", () => {
  it("returns the trimmed DSN when set", () => {
    expect(getDsn({ NEXT_PUBLIC_SENTRY_DSN: `  ${DSN}  ` })).toBe(DSN);
  });

  it("returns undefined when unset or blank", () => {
    expect(getDsn({})).toBeUndefined();
    expect(getDsn({ NEXT_PUBLIC_SENTRY_DSN: "   " })).toBeUndefined();
  });
});

describe("sentryEnabled — production AND DSN required", () => {
  it("is true only with a DSN in production", () => {
    expect(sentryEnabled({ NEXT_PUBLIC_SENTRY_DSN: DSN, NODE_ENV: "production" })).toBe(true);
  });

  it("is false in development even with a DSN", () => {
    expect(sentryEnabled({ NEXT_PUBLIC_SENTRY_DSN: DSN, NODE_ENV: "development" })).toBe(false);
  });

  it("is false in production without a DSN", () => {
    expect(sentryEnabled({ NODE_ENV: "production" })).toBe(false);
  });

  it("is false when nothing is configured", () => {
    expect(sentryEnabled({})).toBe(false);
  });
});

describe("sampleRate", () => {
  it("parses an in-range value", () => {
    expect(sampleRate("0.25", 0)).toBe(0.25);
  });

  it("falls back for unset / blank / non-numeric / out-of-range input", () => {
    expect(sampleRate(undefined, 0.1)).toBe(0.1);
    expect(sampleRate("", 0.1)).toBe(0.1);
    expect(sampleRate("abc", 0.05)).toBe(0.05);
    expect(sampleRate("2", 0)).toBe(0); // > 1
    expect(sampleRate("-1", 0.2)).toBe(0.2); // < 0
  });
});

describe("commonInitOptions", () => {
  it("is a no-op in Development (enabled === false) even with a DSN", () => {
    const opts = commonInitOptions({ NEXT_PUBLIC_SENTRY_DSN: DSN, NODE_ENV: "development" });
    expect(opts.enabled).toBe(false);
  });

  it("is a no-op without a DSN (enabled === false, dsn === undefined)", () => {
    const opts = commonInitOptions({ NODE_ENV: "production" });
    expect(opts.enabled).toBe(false);
    expect(opts.dsn).toBeUndefined();
  });

  it("is enabled with a DSN in production", () => {
    const opts = commonInitOptions({ NEXT_PUBLIC_SENTRY_DSN: DSN, NODE_ENV: "production" });
    expect(opts.enabled).toBe(true);
    expect(opts.dsn).toBe(DSN);
  });

  it("never sends default PII and defaults traces to 0 (errors-only)", () => {
    const opts = commonInitOptions({ NEXT_PUBLIC_SENTRY_DSN: DSN, NODE_ENV: "production" });
    expect(opts.sendDefaultPii).toBe(false);
    expect(opts.tracesSampleRate).toBe(0);
  });

  it("honours the traces sample-rate env knob", () => {
    const opts = commonInitOptions({
      NEXT_PUBLIC_SENTRY_DSN: DSN,
      NODE_ENV: "production",
      NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE: "0.3",
    });
    expect(opts.tracesSampleRate).toBe(0.3);
  });

  it("derives environment from the explicit override, else NODE_ENV", () => {
    expect(commonInitOptions({ NODE_ENV: "production" }).environment).toBe("production");
    expect(
      commonInitOptions({ NODE_ENV: "production", NEXT_PUBLIC_SENTRY_ENVIRONMENT: "staging" })
        .environment,
    ).toBe("staging");
  });

  it("beforeSend scrubs the event AND tags the correlationId", () => {
    const opts = commonInitOptions({ NEXT_PUBLIC_SENTRY_DSN: DSN, NODE_ENV: "production" });
    const event = {
      request: { cookies: { cd_session: "secret" }, headers: { cookie: "cd_session=secret" } },
    } as unknown as ErrorEvent;
    const hint = { originalException: { correlationId: "trace-xyz" } } as EventHint;

    const result = opts.beforeSend(event, hint) as ErrorEvent;

    expect(result.request?.cookies).toBeUndefined();
    expect(result.request?.headers?.cookie).toBeUndefined();
    expect(result.tags?.correlation_id).toBe("trace-xyz");
  });

  it("beforeSendTransaction scrubs the transaction", () => {
    const opts = commonInitOptions({ NEXT_PUBLIC_SENTRY_DSN: DSN, NODE_ENV: "production" });
    const txn = {
      type: "transaction",
      request: { cookies: { cd_session: "secret" } },
    } as unknown as Event;

    const result = opts.beforeSendTransaction(txn) as Event;
    expect(result.request?.cookies).toBeUndefined();
  });
});

describe("no-arg default env (RUNTIME_ENV) — #356", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.resetModules();
  });

  it("captures the env at module load (literal-read snapshot), not at call time", async () => {
    // Next inlines only LITERAL `process.env.NEXT_PUBLIC_*` member expressions
    // into the client bundle; an aliased `env = process.env` default parameter
    // ships un-inlined and undefined to the browser (#356). The no-arg default
    // must therefore be a module-scoped snapshot of literal reads. Pin the
    // snapshot semantic: stub the env, load a fresh module copy, UNSTUB, then
    // call — the load-time values must still be in effect. (The inlining itself
    // is a build-time property, proven by grepping the built client chunks —
    // it cannot be asserted from vitest.)
    vi.stubEnv("NEXT_PUBLIC_SENTRY_DSN", DSN);
    vi.stubEnv("NODE_ENV", "production");
    vi.resetModules();
    const fresh = await import("./options");
    vi.unstubAllEnvs();

    expect(fresh.getDsn()).toBe(DSN);
    expect(fresh.sentryEnabled()).toBe(true);
    const opts = fresh.commonInitOptions();
    expect(opts.dsn).toBe(DSN);
    expect(opts.enabled).toBe(true);
    expect(opts.environment).toBe("production");
  });
});
