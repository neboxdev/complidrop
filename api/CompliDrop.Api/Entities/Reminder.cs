namespace CompliDrop.Api.Entities;

public class Reminder
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int DaysBefore { get; set; }
    public bool NotifyInternalUser { get; set; } = true;
    public bool NotifyVendor { get; set; } = false;
    public string? EmailSubjectTemplate { get; set; }
    // Dormant: not on the API surface (#264 / FP-095) and never read by the send path;
    // retained for #241 (recipient-aware email rewrite) to honor or drop — see
    // ReminderEndpoints.UpdateReminder. Do NOT remove in a dead-code sweep without #241.
    public string? EmailBodyTemplate { get; set; }
    public bool IsActive { get; set; } = true;

    public Organization Organization { get; set; } = null!;
    public ICollection<ReminderLog> Logs { get; set; } = [];
}

public class ReminderLog
{
    public Guid Id { get; set; }
    // Denormalized from the parent Reminder (#309). ReminderLog is reached via its parent
    // Reminder, but carrying OrganizationId directly lets the org-scoped history query
    // (GET /api/reminders/history) be served by the (OrganizationId, SentAt DESC) index as a
    // range scan instead of a whole-table scan + top-N sort + EXISTS join. Also makes the row
    // eligible for the AppDbContext tenant query filter (defense-in-depth), matching the
    // denormalized-OrgId pattern of AuditLog / IdempotencyRecord (bare column, no FK/nav).
    // Set on insert by ReminderBackgroundService; backfilled for pre-#309 rows by migration.
    public Guid OrganizationId { get; set; }
    public Guid ReminderId { get; set; }
    public Guid DocumentId { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public DateOnly SendDate { get; set; }
    public string? ResendMessageId { get; set; }
    public string Status { get; set; } = ReminderLogStatus.Sent;

    public Reminder Reminder { get; set; } = null!;
}

public class Subscription
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string Plan { get; set; } = "free";
    public string Status { get; set; } = "active";
    public int? DocumentLimit { get; set; } = 5;
    public bool HasVendorPortal { get; set; } = false;
    public decimal ExtractionSpendThisMonthUsd { get; set; } = 0m;
    // UTC month the spend counter belongs to (first of month). A row anchored to any OTHER
    // month carries a stale counter that counts as zero — the lazy monthly reset of #256.
    // DateOnly.MinValue (also the column default, incl. for pre-#256 rows) is always stale,
    // so existing lifetime counters were forgiven on deploy and a fresh row starts at zero.
    public DateOnly SpendMonthStart { get; set; } = DateOnly.MinValue;
    public DateTime? CurrentPeriodEnd { get; set; }
    // Stripe `cancel_at_period_end` (#323): an ACTIVE subscription set to cancel at the period end —
    // Stripe only flips Status to "canceled" at the actual period end, so until then the billing card
    // must say "Ends on {CurrentPeriodEnd}" (won't renew) rather than "Renews on". Persisted from the
    // customer.subscription.updated/.created webhook; additive, defaults false.
    public bool CancelAtPeriodEnd { get; set; } = false;
    // Order-resilience fence (#275, ADR 0023): the as-of moment of the newest applied
    // subscription state — the Stripe `created` of the newest applied webhook event, or the
    // live subscription's EndedAt when a checkout applied already-terminal live truth.
    // Handlers skip state application for events strictly older than this fence, so a
    // failed-then-retried event (up to Stripe's ~3-day retry window, ADR 0020) cannot
    // overwrite newer state. Null = no event applied yet (fence open). Ties apply: `created`
    // is second-granularity, and equal-timestamp re-application is what keeps ADR 0020's
    // crash-window re-apply benign.
    public DateTime? LastStripeEventAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}

public class WaitlistEntry
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Industry { get; set; }
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
}
