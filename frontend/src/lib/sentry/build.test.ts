/**
 * Pins the source-map graceful-degradation contract (ADR 0036): a build with no
 * SENTRY_AUTH_TOKEN must disable upload and still succeed; a build with the token
 * uploads. next.config.ts isn't unit-testable, so the token-gating lives in this
 * pure helper.
 */
import { describe, it, expect } from "vitest";
import { sentryBuildOptions } from "./build";

describe("sentryBuildOptions", () => {
  it("disables source-map upload + silences logs when no auth token (build still succeeds)", () => {
    const o = sentryBuildOptions({});
    expect(o.sourcemaps.disable).toBe(true);
    expect(o.silent).toBe(true);
    expect(o.authToken).toBeUndefined();
    expect(o.org).toBeUndefined();
    expect(o.project).toBeUndefined();
  });

  it("enables upload to the configured org/project when the auth token is present", () => {
    const o = sentryBuildOptions({
      SENTRY_AUTH_TOKEN: "sntrys_token",
      SENTRY_ORG: "complidrop",
      SENTRY_PROJECT: "frontend",
    });
    expect(o.sourcemaps.disable).toBe(false);
    expect(o.silent).toBe(false);
    expect(o.authToken).toBe("sntrys_token");
    expect(o.org).toBe("complidrop");
    expect(o.project).toBe("frontend");
  });

  it("never phones build telemetry home", () => {
    expect(sentryBuildOptions({}).telemetry).toBe(false);
  });
});
