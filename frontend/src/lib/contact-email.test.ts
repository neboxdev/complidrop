/**
 * The shared vendor contact-email predicate (#369).
 *
 * Behavior contract pinned here; each form's wiring (Save blocked + inline message) is
 * pinned in that page's own test file. The server mirror lives in
 * `Services/ContactEmail.cs` and is pinned by `VendorEndpointsTests` — the two MUST agree,
 * so the literals below are deliberately the same ones asserted on the backend.
 */
import { describe, expect, it } from "vitest";

import {
  CONTACT_EMAIL_MAX_LENGTH,
  isMalformedContactEmail,
  trimContactEmail,
} from "@/lib/contact-email";

describe("isMalformedContactEmail (#369)", () => {
  it("accepts blank, whitespace, null and undefined — a vendor with no contact email is valid", () => {
    // Load-bearing: if blank were "malformed", Save would be permanently disabled on every
    // vendor that has no contact email, which is a supported and common state.
    for (const blank of ["", "   ", null, undefined]) {
      expect(isMalformedContactEmail(blank)).toBe(false);
    }
  });

  it("rejects the two typos from the #369 failure scenario", () => {
    // A comma instead of a dot — the domain has no dot at all.
    expect(isMalformedContactEmail("jane@acme,com")).toBe(true);
    // A pasted mail-client display-name form. Note System.Net.Mail.MailAddress ACCEPTS this,
    // which is exactly why the server mirror doesn't use it.
    expect(isMalformedContactEmail("Jane Smith <jane@acme.com>")).toBe(true);
  });

  it("rejects other structurally-broken addresses", () => {
    for (const bad of [
      "jane",              // no @ at all
      "jane@",             // no domain
      "@acme.com",         // no local part
      "jane@acme",         // undotted domain
      "jane@@acme.com",    // second @ leaves an empty label
      "jane doe@acme.com", // whitespace in the local part
      "jane@acme .com",    // whitespace in the domain
    ]) {
      expect(isMalformedContactEmail(bad), `${bad} must be rejected`).toBe(true);
    }
  });

  it("accepts ordinary addresses, including the seeded sample-demo one", () => {
    for (const good of [
      "ops@acmecatering.com",
      "jane.doe+coi@sub.domain.co.uk",
      "o'brien@acme.com",
      // #238 seeds this on the sample vendor — if the predicate rejected it, the sample
      // demo would be unsaveable through the form it ships in.
      "sample-vendor@example.com",
    ]) {
      expect(isMalformedContactEmail(good), `${good} must be accepted`).toBe(false);
    }
  });

  it("evaluates the trimmed value, so padding alone is not malformed", () => {
    expect(isMalformedContactEmail("  ops@acme.com  ")).toBe(false);
  });

  it("rejects an address longer than the varchar(256) column", () => {
    // Npgsql does NOT truncate — without this cap the write 500s instead of 400ing.
    const domain = "@acme.com";
    const atLimit = "a".repeat(CONTACT_EMAIL_MAX_LENGTH - domain.length) + domain;
    expect(atLimit).toHaveLength(CONTACT_EMAIL_MAX_LENGTH);
    expect(isMalformedContactEmail(atLimit)).toBe(false);
    expect(isMalformedContactEmail("a" + atLimit)).toBe(true);
  });
});

describe("trimContactEmail (#369)", () => {
  it("trims, and collapses blank to null", () => {
    expect(trimContactEmail("  ops@acme.com  ")).toBe("ops@acme.com");
    for (const blank of ["", "   ", null, undefined]) {
      expect(trimContactEmail(blank)).toBeNull();
    }
  });
});
