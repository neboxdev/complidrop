import { describe, it, expect } from "vitest";
import { isAuthError } from "./query-client";
import { ApiError } from "./api";

describe("isAuthError", () => {
  it("is true for session-expiry codes (the global handler redirects on these)", () => {
    expect(isAuthError(new ApiError("auth.unauthorized", "x", 401))).toBe(true);
    expect(isAuthError(new ApiError("auth.token_expired", "x", 401))).toBe(true);
  });

  it("is FALSE for a bad-login 401 — a failed sign-in must not be mistaken for an expired session", () => {
    // Critical distinction: keyed on the error CODE, not the raw 401 status.
    // If this regressed to `status === 401`, every wrong-password attempt
    // would null the me-cache and bounce the user, and the login form's own
    // error toast would be suppressed.
    expect(isAuthError(new ApiError("auth.invalid_credentials", "x", 401))).toBe(false);
    expect(isAuthError(new ApiError("auth.locked", "x", 423))).toBe(false);
  });

  it("is false for non-auth errors and non-ApiError values", () => {
    expect(isAuthError(new ApiError("server.error", "x", 500))).toBe(false);
    expect(isAuthError(new ApiError("validation.name", "x", 400))).toBe(false);
    expect(isAuthError(new Error("boom"))).toBe(false);
    expect(isAuthError(null)).toBe(false);
    expect(isAuthError(undefined)).toBe(false);
  });
});
