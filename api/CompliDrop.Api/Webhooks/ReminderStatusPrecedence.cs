using static CompliDrop.Api.Entities.ReminderLogStatus;

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
/// <para/>
/// The handler enforces this rule atomically in the database via <see cref="CurrentStatusesToIgnore"/>
/// — see that method's remarks for the SQL representation and how concurrent webhook deliveries
/// are serialized.
/// </summary>
internal static class ReminderStatusPrecedence
{
    // Status strings come from the shared ReminderLogStatus vocabulary (using static above) —
    // the worker's retry dedupe, this precedence table, and the schema default all read from
    // the same definition site.

    /// <summary>
    /// Returns true when <paramref name="incoming"/> should overwrite <paramref name="current"/>.
    /// This is the human-readable form of the rule and the source of truth that
    /// <see cref="CurrentStatusesToIgnore"/> must agree with for every non-null current value.
    /// </summary>
    internal static bool ShouldApply(string? current, string incoming)
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

    /// <summary>
    /// Returns the set of <em>current</em> Status values for which an <paramref name="incoming"/>
    /// event should be IGNORED — i.e., the SQL block list. The handler issues a single
    /// <c>ExecuteUpdateAsync</c> with <c>WHERE Status NOT IN (block list)</c>, so:
    /// <list type="bullet">
    ///   <item>two concurrent webhook deliveries for the same <c>ResendMessageId</c> can't both
    ///         pass an in-memory check and race the write — Postgres serializes their UPDATEs on
    ///         the row lock and re-evaluates the second WHERE clause against the first's
    ///         committed value (Read Committed semantics, per Postgres docs);</item>
    ///   <item>equal-status redeliveries are no-ops (the block list always contains the
    ///         <paramref name="incoming"/> status itself);</item>
    ///   <item>positive-after-negative is rejected (block list always contains both negatives
    ///         when <paramref name="incoming"/> is positive);</item>
    ///   <item>negative-over-anything wins (block list for a negative contains only itself, so
    ///         every other status — including the other negative — matches the WHERE).</item>
    /// </list>
    /// Must agree with <see cref="ShouldApply"/> for every reachable current value; unit tests
    /// exhaustively cross-check this. The single divergence is <c>current = null</c>: SQL
    /// <c>NULL NOT IN (...)</c> evaluates to NULL (not true), so the row would NOT be selected —
    /// but <c>ReminderLog.Status</c> is non-nullable in the schema, so this case is unreachable
    /// at runtime.
    /// </summary>
    /// <param name="incoming">One of the five lowercase statuses the webhook's <c>type switch</c>
    /// produces (<c>delivered</c>, <c>opened</c>, <c>clicked</c>, <c>bounced</c>,
    /// <c>complained</c>).</param>
    /// <exception cref="ArgumentOutOfRangeException">If a status outside the five-item whitelist
    /// is passed — would mean the handler's mapping switch added a case without updating this
    /// table.</exception>
    internal static IReadOnlyList<string> CurrentStatusesToIgnore(string incoming) => incoming switch
    {
        Delivered => [Delivered, Opened, Clicked, Bounced, Complained],
        Opened => [Opened, Clicked, Bounced, Complained],
        Clicked => [Clicked, Bounced, Complained],
        Bounced => [Bounced],
        Complained => [Complained],
        _ => throw new ArgumentOutOfRangeException(
            nameof(incoming),
            incoming,
            "Unknown incoming status. The webhook handler's type switch only produces the five whitelisted lowercase strings.")
    };

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
