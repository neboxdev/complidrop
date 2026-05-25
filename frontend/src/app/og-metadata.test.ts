/**
 * Pins the dimensions + content type exported by the Next 16 file-convention
 * metadata routes. The actual `ImageResponse` PNGs are verified at build time
 * (and visually-inspected once), but a regression here would silently break
 * the auto-emitted `<meta property="og:image:width">` / apple-touch-icon
 * `sizes="180x180"` attributes — which clients use to decide rendering.
 */
import { describe, it, expect } from "vitest";
import * as og from "./opengraph-image";
import * as apple from "./apple-icon";

describe("opengraph-image route exports", () => {
  it("declares 1200×630 — the canonical OG card size", () => {
    expect(og.size).toEqual({ width: 1200, height: 630 });
  });

  it("emits image/png", () => {
    expect(og.contentType).toBe("image/png");
  });

  it("supplies the CompliDrop tagline as alt text", () => {
    expect(og.alt).toBe("CompliDrop — Stop Chasing Paper. Start Dropping Docs.");
  });
});

describe("apple-icon route exports", () => {
  it("declares 180×180 — the iOS home-screen / apple-touch-icon size", () => {
    expect(apple.size).toEqual({ width: 180, height: 180 });
  });

  it("emits image/png", () => {
    expect(apple.contentType).toBe("image/png");
  });
});
