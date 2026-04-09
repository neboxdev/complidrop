namespace CompliDrop.Api.Entities;

public class Reminder
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int DaysBefore { get; set; }
    public bool NotifyInternalUser { get; set; } = true;
    public bool NotifyVendor { get; set; } = false;
    public string? EmailSubjectTemplate { get; set; }
    public string? EmailBodyTemplate { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation
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
    public string? ResendMessageId { get; set; }
    public string Status { get; set; } = "sent";

    // Navigation
    public Reminder Reminder { get; set; } = null!;
}

public class Subscription
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string StripeCustomerId { get; set; } = string.Empty;
    public string? StripeSubscriptionId { get; set; }
    public string Plan { get; set; } = "free";
    public string Status { get; set; } = "active";
    public int DocumentLimit { get; set; } = 5;
    public bool HasVendorPortal { get; set; } = false;
    public DateTime? CurrentPeriodEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
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
