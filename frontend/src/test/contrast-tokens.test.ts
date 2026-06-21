import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { describe, expect, it } from "vitest";

/**
 * WCAG-contrast regression guard for the brand color tokens (#315 / FP-005,
 * FP-001, FP-002). The final UX audit found every primary CTA at 2.77:1 and the
 * input borders at 1.33:1 — both failing AA. Rather than assert class strings
 * (which drift), this pins the actual perceptual property: the `:root` tokens
 * must clear their WCAG threshold against the surface they sit on (white).
 *
 * If someone reverts `--primary` to sky-500 (#0EA5E9) or `--input` to sky-200
 * (#BAE6FD), the computed ratio drops below the floor and this test fails.
 */

const SRC = path.dirname(fileURLToPath(import.meta.url));
const read = (rel: string) => readFileSync(path.resolve(SRC, rel), "utf8");

const globalsCss = read("../app/globals.css");

// Isolate the light-theme `:root { ... }` block (the `.dark` block uses oklch()).
// Anchored on the block's own closing `\n}` so a stray brace can't truncate it.
const rootMatch = globalsCss.match(/:root\s*\{([\s\S]*?)\n\}/);
if (!rootMatch) throw new Error(":root block not found in globals.css");
const rootBlock = rootMatch[1];

function token(name: string): string {
  const m = rootBlock.match(new RegExp(`\\n\\s*--${name}:\\s*(#[0-9a-fA-F]{6})`));
  if (!m) throw new Error(`token --${name} not found as a 6-digit hex in :root`);
  return m[1];
}

function srgbToLinear(channel: number): number {
  const c = channel / 255;
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

function relativeLuminance(hex: string): number {
  const h = hex.replace("#", "");
  const r = parseInt(h.slice(0, 2), 16);
  const g = parseInt(h.slice(2, 4), 16);
  const b = parseInt(h.slice(4, 6), 16);
  return 0.2126 * srgbToLinear(r) + 0.7152 * srgbToLinear(g) + 0.0722 * srgbToLinear(b);
}

function contrast(a: string, b: string): number {
  const la = relativeLuminance(a);
  const lb = relativeLuminance(b);
  const [hi, lo] = la > lb ? [la, lb] : [lb, la];
  return (hi + 0.05) / (lo + 0.05);
}

const WHITE = "#ffffff";

describe("globals.css brand tokens clear WCAG contrast (FP-005/001/002)", () => {
  it("--primary holds white text/fills at AA (>= 4.5:1)", () => {
    // bg-primary buttons + text-primary links both pair with white/light.
    expect(contrast(token("primary"), WHITE)).toBeGreaterThanOrEqual(4.5);
  });

  it("--primary-foreground stays white so the AA pairing above is the real one", () => {
    expect(token("primary-foreground").toLowerCase()).toBe("#ffffff");
  });

  it("--accent holds white CTA text at AA (>= 4.5:1)", () => {
    expect(contrast(token("accent"), WHITE)).toBeGreaterThanOrEqual(4.5);
  });

  it("--accent-foreground stays white so the accent AA claim is anchored to the real fg", () => {
    expect(token("accent-foreground").toLowerCase()).toBe("#ffffff");
  });

  it("--input boundary is visible on white at >= 3:1 (WCAG 1.4.11)", () => {
    expect(contrast(token("input"), WHITE)).toBeGreaterThanOrEqual(3);
  });

  it("--ring focus indicator is visible on white at >= 3:1 (WCAG 1.4.11)", () => {
    expect(contrast(token("ring"), WHITE)).toBeGreaterThanOrEqual(3);
  });
});

/**
 * The contrast guard above only knows the token VALUE. The other half of FP-002
 * is that the primitives render the ring at FULL opacity — a re-added `/50`
 * modifier would halve the effective contrast (below the 3:1 floor) yet leave
 * the token test green. Pin the application here, on the central primitives.
 */
describe("focus rings apply the --ring token at full opacity (FP-002)", () => {
  for (const file of ["../components/ui/button.tsx", "../components/ui/input.tsx", "../components/ui/badge.tsx"]) {
    it(`${path.basename(file)} drives the focus ring off ring-ring with no fractional opacity`, () => {
      const src = read(file);
      expect(src).toMatch(/focus-visible:ring-ring(?![/\w-])/);
      // No fractional-opacity ring (e.g. ring-ring/50) anywhere in the primitive.
      expect(src).not.toMatch(/ring-ring\/\d/);
    });
  }
});
