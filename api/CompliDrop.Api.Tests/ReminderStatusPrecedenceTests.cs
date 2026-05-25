using CompliDrop.Api.Webhooks;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Unit tests for the pure precedence function that gates ReminderLog.Status writes in the
/// Resend webhook handler. Tests cover the full lifecycle table — positive ascent, negative
/// override, negative lock-out, and the unknown/failed current-status edge — so the integration
/// tests in <see cref="ResendWebhookTests"/> can focus on the wired-up behavior end-to-end
/// without re-asserting every combination.
/// </summary>
public class ReminderStatusPrecedenceTests
{
    // -- Positive ascent: strictly-higher rank wins. -----------------------------------------

    [Theory]
    [InlineData("sent", "delivered")]
    [InlineData("sent", "opened")]
    [InlineData("sent", "clicked")]
    [InlineData("delivered", "opened")]
    [InlineData("delivered", "clicked")]
    [InlineData("opened", "clicked")]
    public void Positive_event_applies_when_strictly_higher_than_current(string current, string incoming) =>
        ReminderStatusPrecedence.ShouldApply(current, incoming).Should().BeTrue();

    [Theory]
    [InlineData("delivered", "sent")]
    [InlineData("opened", "sent")]
    [InlineData("opened", "delivered")]
    [InlineData("clicked", "sent")]
    [InlineData("clicked", "delivered")]
    [InlineData("clicked", "opened")]
    public void Positive_event_ignored_when_not_strictly_higher_than_current(string current, string incoming) =>
        ReminderStatusPrecedence.ShouldApply(current, incoming).Should().BeFalse();

    // -- Same-status redelivery: idempotent no-op. -------------------------------------------

    [Theory]
    [InlineData("sent")]
    [InlineData("delivered")]
    [InlineData("opened")]
    [InlineData("clicked")]
    [InlineData("bounced")]
    [InlineData("complained")]
    public void Equal_current_and_incoming_is_no_op(string status) =>
        ReminderStatusPrecedence.ShouldApply(status, status).Should().BeFalse();

    // -- Negative always wins (incl. over a different negative). -----------------------------

    [Theory]
    [InlineData("sent", "bounced")]
    [InlineData("delivered", "bounced")]
    [InlineData("opened", "bounced")]
    [InlineData("clicked", "bounced")]
    [InlineData("complained", "bounced")] // different negative — still applies, per the ticket rule
    [InlineData("sent", "complained")]
    [InlineData("delivered", "complained")]
    [InlineData("opened", "complained")]
    [InlineData("clicked", "complained")]
    [InlineData("bounced", "complained")]
    public void Negative_event_always_applies(string current, string incoming) =>
        ReminderStatusPrecedence.ShouldApply(current, incoming).Should().BeTrue();

    // -- Negative locks out subsequent positives. --------------------------------------------

    [Theory]
    [InlineData("bounced", "delivered")]
    [InlineData("bounced", "opened")]
    [InlineData("bounced", "clicked")]
    [InlineData("bounced", "sent")]
    [InlineData("complained", "delivered")]
    [InlineData("complained", "opened")]
    [InlineData("complained", "clicked")]
    [InlineData("complained", "sent")]
    public void Positive_event_ignored_once_negative_is_stored(string current, string incoming) =>
        ReminderStatusPrecedence.ShouldApply(current, incoming).Should().BeFalse();

    // -- Unknown/failed/null current status: treated as rank -1; any positive advances it. ----

    [Theory]
    [InlineData(null, "delivered")]
    [InlineData(null, "opened")]
    [InlineData(null, "clicked")]
    [InlineData(null, "bounced")]
    [InlineData(null, "complained")]
    [InlineData("failed", "delivered")]
    [InlineData("failed", "opened")]
    [InlineData("failed", "clicked")]
    [InlineData("failed", "bounced")]
    [InlineData("failed", "complained")]
    [InlineData("garbage-status", "delivered")]
    [InlineData("", "delivered")]
    public void Unknown_current_status_is_below_sent_and_any_event_applies(
        string? current, string incoming) =>
        ReminderStatusPrecedence.ShouldApply(current, incoming).Should().BeTrue();

    // -- Case sensitivity: status strings are compared ordinally. ----------------------------

    [Fact]
    public void Status_comparison_is_case_sensitive_so_unexpected_casing_is_treated_as_unknown()
    {
        // Documents the contract: producers (ReminderBackgroundService, the handler's own mapping)
        // always write lowercase, so "Delivered" is treated as unknown rank -1, and an incoming
        // positive will overwrite it. This pins the behavior so a future case-insensitivity tweak
        // is a deliberate decision, not an accident.
        ReminderStatusPrecedence.ShouldApply("Delivered", "delivered").Should().BeTrue();
    }

    // -- Agreement between ShouldApply (human-readable) and CurrentStatusesToIgnore (SQL-block) --

    /// <summary>
    /// The handler issues an atomic <c>ExecuteUpdateAsync</c> with
    /// <c>WHERE Status NOT IN (CurrentStatusesToIgnore(incoming))</c>. For that to enforce the
    /// same rule the unit-tested <see cref="ReminderStatusPrecedence.ShouldApply"/> defines, the
    /// block list must contain exactly those current values for which ShouldApply returns false.
    /// This cross-check pins that invariant for every reachable (current, incoming) pair so a
    /// future change to either method without the other will fail loudly.
    /// </summary>
    [Theory]
    [InlineData("delivered")]
    [InlineData("opened")]
    [InlineData("clicked")]
    [InlineData("bounced")]
    [InlineData("complained")]
    public void CurrentStatusesToIgnore_agrees_with_ShouldApply_for_every_reachable_current(string incoming)
    {
        // ReminderLog.Status is NOT NULL in the schema, so we don't test current=null here — SQL
        // `NULL NOT IN (...)` evaluates to NULL (not true), which would diverge from
        // ShouldApply(null, ...). This is documented on CurrentStatusesToIgnore and unreachable.
        var blockList = ReminderStatusPrecedence.CurrentStatusesToIgnore(incoming).ToHashSet(StringComparer.Ordinal);

        var reachableCurrents = new[]
        {
            "sent", "delivered", "opened", "clicked", "bounced", "complained",
            "failed",          // produced by ReminderBackgroundService when Resend returns no id
            "garbage-status",  // any unrecognized value — rank -1 path
            ""                 // pathological but well-defined under string.Equals/Ordinal
        };
        foreach (var current in reachableCurrents)
        {
            var blocked = blockList.Contains(current);
            var shouldApply = ReminderStatusPrecedence.ShouldApply(current, incoming);
            blocked.Should().Be(!shouldApply,
                $"CurrentStatusesToIgnore and ShouldApply must agree on (current='{current}', incoming='{incoming}')");
        }
    }

    [Fact]
    public void CurrentStatusesToIgnore_throws_for_a_status_outside_the_webhook_whitelist()
    {
        // The webhook handler's `type switch` produces exactly these five strings or null
        // (returned early). If a sixth event type is ever added to that switch without updating
        // CurrentStatusesToIgnore, this guard makes the omission loud rather than silently
        // sending the row through an empty block list (which would let any current overwrite).
        var act = () => ReminderStatusPrecedence.CurrentStatusesToIgnore("sent");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
