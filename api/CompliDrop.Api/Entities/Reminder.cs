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
    public Guid ReminderId { get; set; }
    public Guid DocumentId { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public DateOnly SendDate { get; set; }
    public string? ResendMessageId { get; set; }
    public string Status { get; set; } = "sent";

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
    public DateTime? CurrentPeriodEnd { get; set; }
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
