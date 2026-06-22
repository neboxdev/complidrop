/**
 * session-expiry — the open-redirect guard + the per-tab expiry flag (#318 FP-045).
 */
import { describe, it, expect, beforeEach } from "vitest";
import { safeReturnTo, markSessionExpired, consumeSessionExpired } from "./session-expiry";

describe("safeReturnTo — open-redirect guard", () => {
  it("accepts same-origin relative paths (with query/fragment)", () => {
    expect(safeReturnTo("/documents")).toBe("/documents");
    expect(safeReturnTo("/vendors/123?tab=docs")).toBe("/vendors/123?tab=docs");
    expect(safeReturnTo("/")).toBe("/");
  });

  it("rejects absolute URLs", () => {
    expect(safeReturnTo("https://evil.com")).toBeNull();
    expect(safeReturnTo("http://evil.com/phish")).toBeNull();
    expect(safeReturnTo("evil.com")).toBeNull();
    expect(safeReturnTo("javascript:alert(1)")).toBeNull();
  });

  it("rejects protocol-relative and backslash off-origin tricks", () => {
    expect(safeReturnTo("//evil.com")).toBeNull();
    expect(safeReturnTo("/\\evil.com")).toBeNull();
    expect(safeReturnTo("/\tevil.com")).toBeNull(); // leading control char after the slash
    expect(safeReturnTo("/foo\\bar")).toBeNull(); // backslash anywhere
  });

  it("rejects empty / nullish / non-string input", () => {
    expect(safeReturnTo("")).toBeNull();
    expect(safeReturnTo(null)).toBeNull();
    expect(safeReturnTo(undefined)).toBeNull();
  });
});

describe("session-expiry flag", () => {
  beforeEach(() => window.sessionStorage.clear());

  it("is false until marked, then consumed exactly once (read-and-clear)", () => {
    expect(consumeSessionExpired()).toBe(false);
    markSessionExpired();
    expect(consumeSessionExpired()).toBe(true);
    expect(consumeSessionExpired()).toBe(false);
  });
});
