namespace CompliDrop.Api.Entities;

/// <summary>
/// The <see cref="ReminderLog.Status"/> vocabulary — single definition site for a string set
/// that is load-bearing in three places: the reminder worker writes <see cref="Sent"/> /
/// <see cref="Failed"/> and branches its retry dedupe on <see cref="Failed"/> (ADR 0025), the
/// inbound Resend webhook advances accepted mail through the five delivery statuses under
/// <c>ReminderStatusPrecedence</c>'s ordering rules, and the schema defaults the column to
/// <see cref="Sent"/>.
/// <para/>
/// <see cref="Failed"/> means Resend never accepted the mail — the row carries no
/// <c>ResendMessageId</c>, the recipient was not served, and a later tick the same org-local
/// day retries the send in place (ADR 0025). Every other value describes mail Resend accepted
/// and counts as "served" for dedupe; in particular <see cref="Bounced"/> and
/// <see cref="Complained"/> must never auto-resend.
/// </summary>
internal static class ReminderLogStatus
{
    internal const string Sent = "sent";
    internal const string Failed = "failed";
    internal const string Delivered = "delivered";
    internal const string Opened = "opened";
    internal const string Clicked = "clicked";
    internal const string Bounced = "bounced";
    internal const string Complained = "complained";
}
