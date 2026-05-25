namespace CompliDrop.Api.Webhooks;

/// <summary>
/// Pure precedence rule for how an inbound Resend delivery-status event maps onto a
/// <see cref="Entities.ReminderLog"/>'s current <c>Status</c>. Resend delivers webhooks via Svix,
/// which does NOT guarantee event ordering and may redeliver older events; the handler must
/// therefore reject incoming events that would roll the displayed status back to an
/// earlier-lifecycle or less-severe state. Ranks are local to this helper — they are NOT
/// persisted, NOT exposed on the entity, and exist solely to compare two status strings.
/// <para/>
/// Rules:
/// <list type="bullet">
///   <item>Negative/terminal events (<c>bounced</c>, <c>complained</c>) ALWAYS apply (negative
///         wins over any positive, and over a <em>different</em> negative). Equal-to-equal is a
///         no-op.</item>
///   <item>Positive events (<c>delivered</c> &lt; <c>opened</c> &lt; <c>clicked</c>, with
///         <c>sent</c> below all three) apply only when their rank is strictly greater than the
///         current status's rank.</item>
///   <item>Once a negative is stored, positive events are ignored (no roll-back).</item>
///   <item>Unknown current status (incl. <c>failed</c>, null, or any value not in the table) is
///         treated as rank −1 — strictly below <c>sent</c>. Any real positive event can therefore
///         advance an unknown state. In practice <c>failed</c> carries no <c>ResendMessageId</c>
///         so the webhook never matches such a log, but the function must be defined for every
///         input.</item>
/// </list>
/// </summary>
internal static class ReminderStatusPrecedence
{
    internal const string Sent = "sent";
    internal const string Delivered = "delivered";
    internal const string Opened = "opened";
    internal const string Clicked = "clicked";
    internal const string Bounced = "bounced";
    internal const string Complained = "complained";

    /// <summary>
    /// Returns true when <paramref name="incoming"/> should overwrite <paramref name="current"/>.
    /// Caller still owns the actual mutation and SaveChanges.
    /// </summary>
    public static bool ShouldApply(string? current, string incoming)
    {
        // Same status (incl. redelivered duplicate negative): no DB write needed.
        if (string.Equals(current, incoming, StringComparison.Ordinal))
            return false;

        // Bounce/complaint wins over any other state — including a different negative.
        if (IsNegative(incoming))
            return true;

        // Once locked into a negative, positive events are ignored.
        if (IsNegative(current))
            return false;

        // Both positive: strictly-higher rank wins. Unknown current is rank −1 so any positive
        // advances it (covers the "failed"/null/unrecognized edge).
        return Rank(incoming) > Rank(current);
    }

    private static bool IsNegative(string? status) =>
        status is Bounced or Complained;

    private static int Rank(string? status) => status switch
    {
        Sent => 0,
        Delivered => 1,
        Opened => 2,
        Clicked => 3,
        _ => -1
    };
}
