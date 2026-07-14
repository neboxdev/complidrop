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
  complianceFailureReason,
  formatCheckValue,
  processingErrorMessage,
  relativeTime,
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
    // #400: liquor liability is a graded field now — it gets a curated label.
    expect(fieldLabel("liquor_liability_limit")).toBe("Liquor liability limit");
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
  it("maps the #318 FP-043 feed actions (portal upload, link email, processed)", () => {
    expect(actionLabel("vendorPortalLink.upload_processed")).toBe("Vendor sent a document");
    expect(actionLabel("vendorPortalLink.emailed")).toBe("Upload link emailed");
    expect(actionLabel("document.processed")).toBe("Document read");
    // #340 suppression feed event — pin the exact curated copy, not just "not the raw fallback".
    expect(actionLabel("reminder.recipient_suppressed")).toBe("Reminders paused — bad email");
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

describe("relativeTime (#318 FP-049)", () => {
  const now = new Date("2026-06-22T12:00:00Z").getTime();
  it("renders recent times relatively", () => {
    expect(relativeTime(new Date(now - 5_000).toISOString(), now)).toBe("just now");
    expect(relativeTime(new Date(now - 5 * 60_000).toISOString(), now)).toBe("5m ago");
    expect(relativeTime(new Date(now - 3 * 3_600_000).toISOString(), now)).toBe("3h ago");
    expect(relativeTime(new Date(now - 2 * 86_400_000).toISOString(), now)).toBe("2d ago");
  });
  it("falls back to a date for anything older than ~a week", () => {
    const old = new Date(now - 30 * 86_400_000).toISOString();
    const out = relativeTime(old, now);
    expect(out).not.toMatch(/ago/);
    expect(out).toBe(new Date(now - 30 * 86_400_000).toLocaleDateString());
  });
  it("treats a future / clock-skewed timestamp as 'just now', never negative", () => {
    expect(relativeTime(new Date(now + 60_000).toISOString(), now)).toBe("just now");
  });
  it("returns empty for nullish / unparseable input", () => {
    expect(relativeTime(null, now)).toBe("");
    expect(relativeTime("not-a-date", now)).toBe("");
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
  "document.verified", "document.fields_edited", "document.reextract_queued", "document.processed",
  "documentfield.created", "documentfield.updated",
  "vendor.created", "vendor.updated", "vendor.deleted",
  "vendorPortalLink.created", "vendorPortalLink.revoked", "vendorPortalLink.emailed",
  "vendorPortalLink.upload_processed",
  "complianceTemplate.created", "complianceTemplate.updated", "complianceTemplate.deleted",
  "complianceRule.created", "complianceRule.updated", "complianceRule.upserted", "complianceRule.deleted",
  "reminder.recipient_suppressed",
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

describe("formatCheckValue (#193)", () => {
  it("formats money-ish fields as whole-dollar USD", () => {
    expect(formatCheckValue("general_liability_limit", "1000000")).toBe("$1,000,000");
    expect(formatCheckValue("auto_liability_limit", "500000")).toBe("$500,000");
    // #400: the MONEY_FIELD regex matches "liquor_liability_limit" (liabilit|limit), so
    // its value formats as currency automatically — no separate rule needed.
    expect(formatCheckValue("liquor_liability_limit", "1000000")).toBe("$1,000,000");
    // Already-formatted input is normalized, not double-formatted.
    expect(formatCheckValue("umbrella_limit", "$1,000,000")).toBe("$1,000,000");
  });
  it("leaves non-money fields and non-numeric values verbatim", () => {
    expect(formatCheckValue("certificate_holder", "Acme LLC")).toBe("Acme LLC");
    expect(formatCheckValue("general_liability_limit", "see attached")).toBe("see attached");
  });
  it("returns null for an empty / whitespace value so callers can say 'missing'", () => {
    expect(formatCheckValue("general_liability_limit", "")).toBeNull();
    expect(formatCheckValue("general_liability_limit", "   ")).toBeNull();
    expect(formatCheckValue("general_liability_limit", null)).toBeNull();
  });
});

describe("complianceFailureReason (#193)", () => {
  it("prefers the owner-authored requirement text and appends what the document shows", () => {
    const reason = complianceFailureReason({
      isPassed: false,
      ruleErrorMessage: "General liability must be at least $1,000,000",
      ruleFieldName: "general_liability_limit",
      ruleOperator: "min_value",
      ruleExpectedValue: "1000000",
      actualValue: "500000",
    });
    expect(reason).toBe(
      "General liability must be at least $1,000,000 — this document shows $500,000.",
    );
  });
  it("synthesizes a plain-English reason when no owner message is set — never a raw operator or snake_case field", () => {
    const reason = complianceFailureReason({
      isPassed: false,
      ruleErrorMessage: null,
      ruleFieldName: "general_liability_limit",
      ruleOperator: "min_value",
      ruleExpectedValue: "1000000",
      actualValue: "500000",
    });
    expect(reason).toContain("General liability limit");
    expect(reason).toContain("must be at least");
    expect(reason).toContain("$1,000,000");
    expect(reason).toContain("$500,000");
    expect(reason).not.toContain("min_value");
    expect(reason).not.toContain("general_liability_limit");
  });
  it("says the value couldn't be found when the document has no value for the field", () => {
    const reason = complianceFailureReason({
      isPassed: false,
      ruleErrorMessage: "A workers' comp certificate is required",
      ruleFieldName: "workers_comp_limit",
      ruleOperator: "required",
      ruleExpectedValue: null,
      actualValue: null,
    });
    expect(reason).toBe(
      "A workers' comp certificate is required — we couldn't find this on the document.",
    );
  });
});

describe("processingErrorMessage (#193)", () => {
  it("maps known codes to plain-English copy, never echoing the raw code", () => {
    const tooMany = processingErrorMessage(
      "extraction.too_many_attempts: Exceeded 5 attempts (6 so far).",
    );
    expect(tooMany).toMatch(/tried several times/i);
    expect(tooMany).not.toMatch(/extraction\.too_many_attempts/);
    expect(tooMany).not.toMatch(/Exceeded 5 attempts/);

    const ceiling = processingErrorMessage(
      "extraction.cost_ceiling_hit: Monthly extraction cost ceiling reached.",
    );
    expect(ceiling).toMatch(/monthly processing limit/i);
    // #256: the limit now genuinely resets monthly, but an already-failed document is
    // NOT re-read automatically — the copy must point at "Read again", never promise
    // the old "it resumes next cycle" auto-recovery.
    expect(ceiling).toMatch(/resets at the start of next month/i);
    expect(ceiling).toMatch(/read again/i);
    expect(ceiling).not.toMatch(/resumes next cycle/i);
    expect(processingErrorMessage("extraction.failed: System.Exception boom"))
      .toMatch(/something went wrong/i);
  });
  it("parses a bare code with no ': detail' suffix", () => {
    // split(':', 1) on a colon-less string yields the whole string — the code
    // still resolves. Pins that boundary of the parser.
    expect(processingErrorMessage("extraction.too_many_attempts")).toMatch(
      /tried several times/i,
    );
  });
  it("falls back to a generic line for unknown codes and bare exception messages", () => {
    expect(processingErrorMessage("System.InvalidOperationException: Document has no blob path."))
      .toMatch(/weren't able to read this file/i);
    expect(processingErrorMessage(null)).toMatch(/weren't able to read this file/i);
    // Never leaks the raw exception text.
    expect(processingErrorMessage("System.InvalidOperationException: Document has no blob path."))
      .not.toMatch(/InvalidOperationException/);
  });
});
