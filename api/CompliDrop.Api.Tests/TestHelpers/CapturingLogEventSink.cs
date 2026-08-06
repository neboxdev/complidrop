using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Serilog sink that records every emitted event; thread-safe for the host's loggers. Registered as an
/// <see cref="ILogEventSink"/> on a per-test host (<c>WithWebHostBuilder</c> + <c>ConfigureTestServices</c>)
/// so a test can read what the application actually logged — including EF Core's command log, which is the
/// only place the COLUMNS an UPDATE carried are observable.
/// <para/>
/// Shared rather than hand-copied per suite: three suites read this log, two of them for the same
/// "which columns did that write emit" pin that reviewers.md names in two places (the ADR 0052
/// <c>Marking_verified_still_emits_trust_WITHOUT_forcing_the_status_it_read</c> bullet and the ADR
/// 0030 Amendment 2 <c>The_persist_emits_what_it_extracted_...</c> one). One implementation keeps those
/// pins reading the log the same way (#460 review round 2, S4).
/// </summary>
public sealed class CapturingLogEventSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    /// <summary>The scalar value of one Serilog property, unquoted, or null when it is absent.</summary>
    public static string? Scalar(LogEvent e, string property) =>
        e.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: { } v }
            ? v.ToString()
            : null;

    /// <summary>
    /// The level of SERILOG's own request-completion line for <paramref name="path"/> — the one
    /// <c>UseSerilogRequestLogging</c> emits — or null when there is none. Deliberately keyed on
    /// <c>SourceContext</c>: the FRAMEWORK emits a second completion line for the same request
    /// (<c>Microsoft.AspNetCore.Hosting.Diagnostics</c>) at its own level, and #390's whole point is that
    /// the two are different records with different fates (<c>ClientAbortLoggingTests</c>).
    /// <para/>
    /// Exists because <c>Program.cs</c> replaces Serilog's default <c>GetLevel</c> with an inline lambda
    /// to demote ONE case (a client abort), and the two arms it restates by hand — "an exception or a
    /// 5xx is Error, everything else Information" — govern the level of every request the API serves.
    /// A hand-copied default is a default that can be lost.
    /// </summary>
    public LogEventLevel? SerilogRequestLevel(string path) => _events
        .Where(e => Scalar(e, "SourceContext") == "Serilog.AspNetCore.RequestLoggingMiddleware"
            && Scalar(e, "RequestPath") == path)
        .Select(e => (LogEventLevel?)e.Level)
        .LastOrDefault();

    /// <summary>
    /// The most recently logged message containing every one of <paramref name="fragments"/>, or null when
    /// there is none. Serilog renders the command text as a quoted string, so the SQL's own quotes arrive
    /// escaped — they are unescaped here once, rather than at each call site.
    /// </summary>
    public string? LastMessageContaining(params string[] fragments) => _events
        .Select(e => e.RenderMessage().Replace("\\\"", "\"", StringComparison.Ordinal))
        .LastOrDefault(m => fragments.All(f => m.Contains(f, StringComparison.Ordinal)));

    /// <summary>
    /// The SET clause of the most recent <c>UPDATE "<paramref name="table"/>"</c> that also contains every
    /// one of <paramref name="fragments"/>, or null when there is none. Two reasons for both halves:
    /// <list type="bullet">
    /// <item>EF batches several statements into ONE logged command, and "does this write carry column X" is
    /// a question about one of them — so the answer is sliced at the statement's <c>WHERE</c>;</item>
    /// <item>a writer usually has more than one UPDATE against a table across a test (a request's, a
    /// worker's, a failure-bookkeeping one), so the caller passes a column only the statement under test
    /// writes. Without that discriminator an assertion about absent columns can silently read someone
    /// else's statement and pass for the wrong reason.</item>
    /// </list>
    /// </summary>
    public string? LastUpdateSetClause(string table, params string[] fragments)
    {
        var marker = $"UPDATE \"{table}\" SET ";
        var message = LastMessageContaining([marker, .. fragments]);
        if (message is null) return null;

        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.IndexOf("WHERE", start, StringComparison.Ordinal);
        return end < 0 ? message[start..] : message[start..end];
    }
}
