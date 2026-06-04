/**
 * display-labels — the shared humanization maps applied across the UI + (via
 * the C# mirror) the export. Pins the curated labels AND the fallbacks,
 * especially the actionLabel camelCase fix that #188 was filed for. (#188)
 */
import { describe, it, expect } from "vitest";
import {
  complianceStatusLabel,
  extractionStatusLabel,
  fieldLabel,
  actionLabel,
  operatorLabel,
  deliveryStatusLabel,
} from "./display-labels";

describe("complianceStatusLabel (#188)", () => {
  it("humanizes the raw enums", () => {
    expect(complianceStatusLabel("NonCompliant")).toBe("Action needed");
    expect(complianceStatusLabel("ExpiringSoon")).toBe("Expiring soon");
    expect(complianceStatusLabel("Compliant")).toBe("Compliant");
    expect(complianceStatusLabel("Expired")).toBe("Expired");
    expect(complianceStatusLabel("Pending")).toBe("Awaiting review");
  });
  it("never returns the raw camelCase enum for a known value", () => {
    expect(complianceStatusLabel("NonCompliant")).not.toMatch(/NonCompliant/);
  });
  it("falls back to the raw value for an unknown status, and to Awaiting review for empty", () => {
    expect(complianceStatusLabel("Whatever")).toBe("Whatever");
    expect(complianceStatusLabel(null)).toBe("Awaiting review");
    expect(complianceStatusLabel(undefined)).toBe("Awaiting review");
  });
});

describe("extractionStatusLabel (#188)", () => {
  it("humanizes the machine states", () => {
    expect(extractionStatusLabel("Processing")).toBe("Reading…");
    expect(extractionStatusLabel("ManualRequired")).toBe("Needs your review");
    expect(extractionStatusLabel("Completed")).toBe("Read");
    expect(extractionStatusLabel("Failed")).toBe("Couldn't read");
    expect(extractionStatusLabel("Pending")).toBe("Waiting to read");
  });
  it("never leaks the ManualRequired camelCase enum", () => {
    expect(extractionStatusLabel("ManualRequired")).not.toMatch(/ManualRequired/);
  });
  it("falls back to the raw value for unknown, and to Waiting to read for empty", () => {
    expect(extractionStatusLabel("Whatever")).toBe("Whatever");
    expect(extractionStatusLabel(null)).toBe("Waiting to read");
    expect(extractionStatusLabel(undefined)).toBe("Waiting to read");
  });
});

describe("fieldLabel (#188)", () => {
  it("uses curated labels for known snake_case fields", () => {
    expect(fieldLabel("general_liability_limit")).toBe("General liability limit");
    expect(fieldLabel("policy_number")).toBe("Policy number");
    expect(fieldLabel("expiration_date")).toBe("Expiration date");
  });
  it("is case-insensitive on the lookup", () => {
    expect(fieldLabel("General_Liability_Limit")).toBe("General liability limit");
  });
  it("falls back to a sentence-cased de-snaked form for unknown fields", () => {
    expect(fieldLabel("some_new_field")).toBe("Some new field");
  });
  it("falls back to a de-camelCased form (never a raw camelCase token)", () => {
    expect(fieldLabel("PolicyNumber")).toBe("Policy Number");
  });
});

describe("actionLabel (#188)", () => {
  it("maps known audit actions to plain English", () => {
    expect(actionLabel("document.uploaded")).toBe("Document uploaded");
    expect(actionLabel("vendor.created")).toBe("Vendor added");
  });
  it("maps the real login + explicit camelCase actions (not the dead user.login key)", () => {
    expect(actionLabel("user.logged_in")).toBe("Signed in");
    expect(actionLabel("complianceRule.upserted")).toBe("Requirement saved");
    expect(actionLabel("vendorPortalLink.revoked")).toBe("Portal link revoked");
  });
  it("fixes the all-lowercase entity garble the old prettyAction produced", () => {
    // Old prettyAction → "Compliancetemplate · Created"; the map gives English.
    expect(actionLabel("compliancetemplate.created")).toBe("Requirement set created");
    expect(actionLabel("compliancetemplate.created")).not.toMatch(/Compliancetemplate/);
  });
  it("falls back to a de-dotted, de-camelCased, title-cased form for unknown actions", () => {
    expect(actionLabel("fooBar.created")).toBe("Foo Bar · Created");
    expect(actionLabel("weird.action")).toBe("Weird · Action");
  });
});

describe("operatorLabel (#188)", () => {
  it("humanizes compliance-rule operators", () => {
    expect(operatorLabel("min_value")).toBe("Must be at least");
    expect(operatorLabel("required")).toBe("Must be present");
    expect(operatorLabel("contains")).toBe("Must contain");
  });
  it("falls back to the raw operator for unknown / empty", () => {
    expect(operatorLabel("weird_op")).toBe("weird_op");
    expect(operatorLabel(null)).toBe("");
  });
});

describe("deliveryStatusLabel (#188)", () => {
  it("humanizes reminder delivery statuses (incl. the engagement ones)", () => {
    expect(deliveryStatusLabel("delivered")).toBe("Delivered");
    expect(deliveryStatusLabel("bounced")).toBe("Bounced — bad address");
    expect(deliveryStatusLabel("complained")).toBe("Marked as spam");
    // opened/clicked are real ReminderLog.Status values (ranked above
    // "delivered") that previously leaked raw.
    expect(deliveryStatusLabel("opened")).toBe("Opened");
    expect(deliveryStatusLabel("clicked")).toBe("Clicked");
  });
  it("sentence-cases an unknown token instead of leaking it raw", () => {
    expect(deliveryStatusLabel("some_future_status")).toBe("Some_future_status");
    expect(deliveryStatusLabel("queued")).toBe("Queued");
  });
});

// Drift / coverage guard (#188 review): every audit action the backend
// actually emits must resolve to a CURATED label, never the regex fallback
// (which inserts " · "). Mirrored by DisplayLabelsTests.Action_curates_every_
// emitted_action on the C# side so a one-sided map edit is caught.
const REAL_AUDIT_ACTIONS = [
  "document.created", "document.uploaded", "document.updated", "document.deleted",
  "document.verified", "document.fields_edited", "document.reextract_queued",
  "documentfield.created", "documentfield.updated",
  "vendor.created", "vendor.updated", "vendor.deleted",
  "vendorPortalLink.created", "vendorPortalLink.revoked",
  "complianceTemplate.created", "complianceTemplate.updated", "complianceTemplate.deleted",
  "complianceRule.created", "complianceRule.updated", "complianceRule.upserted", "complianceRule.deleted",
  "user.registered", "user.logged_in", "user.login_failed", "user.password_changed",
  "user.password_reset", "user.password_reset_requested", "user.email_verified",
  "user.email_changed", "user.email_change_requested", "user.account_deleted",
];

describe("actionLabel — every emitted audit action is curated (#188)", () => {
  it.each(REAL_AUDIT_ACTIONS)("%s resolves to a curated label (no raw fallback)", (action) => {
    const label = actionLabel(action);
    expect(label).not.toContain(" · "); // the fallback's dot-join marker
    expect(label).not.toContain("_");
    expect(label[0]).toBe(label[0].toUpperCase());
  });
});
