/**
 * Pins the assertNotInDom contract added in #85.
 *
 * Five invariants matter:
 *   1. Passes silently when the value is absent.
 *   2. Catches a textContent-only leak (visible-copy / aria-label
 *      that contributes to textContent).
 *   3. Catches an innerHTML-only leak (a hidden node's
 *      `data-token` attribute that NEVER appears in textContent).
 *   4. Catches an HTML-entity-escaped leak — the helper must scan
 *      both the literal `value` AND its escaped form, otherwise a
 *      token containing `<`, `>`, `&`, or `"` would round-trip
 *      through DOM serialization undetected.
 *   5. Error messages SANITIZE the sensitive value (length + prefix
 *      + suffix sentinel) — they must NOT echo the full token to
 *      CI logs, because that inverts the helper's whole contract.
 */
import { describe, it, expect } from "vitest";
import { assertNotInDom } from "./security";

describe("assertNotInDom — basic contract (#85)", () => {
  it("passes silently when the value is absent from the container", () => {
    const container = document.createElement("div");
    container.innerHTML = "<p>Hi there, nothing sensitive here.</p>";
    expect(() => assertNotInDom("token-not-present", container)).not.toThrow();
  });

  it("catches a textContent leak (value rendered as visible copy)", () => {
    const container = document.createElement("div");
    container.innerHTML = "<p>Your token is sup3r-secret-1234567890.</p>";
    expect(() =>
      assertNotInDom("sup3r-secret-1234567890", container),
    ).toThrow(/appeared in root\.textContent/i);
  });

  it("catches an innerHTML-only leak (value in a data-* attribute, NOT in textContent)", () => {
    // A regression where production code rendered `<input
    // data-token={token} type="hidden">` would silently pass a
    // textContent-only scan: the attribute value is part of innerHTML
    // but contributes nothing to textContent. The two-scan shape is
    // the entire reason for this helper's existence.
    const container = document.createElement("div");
    container.innerHTML =
      '<input type="hidden" data-token="leaked-via-attr-xyz" />';
    expect(() =>
      assertNotInDom("leaked-via-attr-xyz", container),
    ).toThrow(/appeared in root\.innerHTML but NOT in root\.textContent/i);
  });

  it("defaults to document.body when no root is provided", () => {
    document.body.innerHTML = "<p>some-public-content</p>";
    try {
      expect(() => assertNotInDom("not-present")).not.toThrow();
      expect(() => assertNotInDom("some-public-content")).toThrow(
        /appeared in root\.textContent/i,
      );
    } finally {
      document.body.innerHTML = "";
    }
  });
});

describe("assertNotInDom — HTML-entity-escape edge case (#85)", () => {
  it("catches a leak where the token contains `&` and gets serialized to `&amp;`", () => {
    // HTML5 attribute serialization escapes `&` (and the surrounding
    // quote char) in attribute values; it does NOT escape `<` / `>`.
    // So `&` is the realistic char that round-trips through DOM
    // serialization as an entity reference. A naive
    // `.includes(rawValue)` would miss this; the helper scans both
    // the literal AND the escaped form so the leak surfaces.
    const container = document.createElement("div");
    container.innerHTML = '<span data-x="r&amp;d-secret-token">visible</span>';
    expect(() =>
      assertNotInDom("r&d-secret-token", container),
    ).toThrow(/HTML-entity-escaped form found/i);
  });

  it("catches a leak where the token contains `<` (which jsdom serializes literally in attribute values, so the literal scan picks it up)", () => {
    // Sanity-check the inverse case: `<` is NOT escaped in attribute
    // values per HTML5 (only `&` and the quote char are). So the
    // literal `.includes(rawValue)` scan finds it without needing
    // the escaped form. Pinned here so a future jsdom version that
    // started escaping `<` in attributes wouldn't silently regress
    // the literal-scan path.
    const container = document.createElement("div");
    container.innerHTML = '<span title="abc<leak>xyz">visible</span>';
    expect(() =>
      assertNotInDom("abc<leak>xyz", container),
    ).toThrow(/appeared in root\.innerHTML/i);
  });
});

describe("assertNotInDom — error-message sanitization (#85)", () => {
  it("does NOT echo the full sensitive value into the error message", () => {
    const container = document.createElement("div");
    const realisticToken = "vendor-portal-token-9f3d2c1a7b6e5d4c";
    container.innerHTML = `<p>${realisticToken}</p>`;
    let caught: Error | null = null;
    try {
      assertNotInDom(realisticToken, container);
    } catch (e) {
      caught = e as Error;
    }
    expect(caught).not.toBeNull();
    // The full token MUST NOT appear in the error message.
    expect(caught!.message).not.toContain(realisticToken);
    // Length and prefix/suffix sentinels should appear instead.
    expect(caught!.message).toMatch(/length 36/);
    expect(caught!.message).toContain('"vend"');
    expect(caught!.message).toContain('"5d4c"');
  });

  it("short values (<= 8 chars) report only the length, not a near-full prefix+suffix disclosure", () => {
    // A 6-char value like "abc123" would have a 4-char prefix
    // overlapping with its 4-char suffix — together those would
    // effectively disclose the entire value. For short values the
    // summarizer reports length only.
    const container = document.createElement("div");
    container.innerHTML = "<p>abc123</p>";
    let caught: Error | null = null;
    try {
      assertNotInDom("abc123", container);
    } catch (e) {
      caught = e as Error;
    }
    expect(caught).not.toBeNull();
    expect(caught!.message).not.toContain("abc123");
    expect(caught!.message).toMatch(/length 6/);
  });
});
