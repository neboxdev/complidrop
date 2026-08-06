using System.Collections.Concurrent;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="IEmailService"/> for tests — records every send in order so the
/// reminder worker can be exercised without hitting Resend. Always returns a deterministic
/// <c>resend_*</c> id (no transport failures) so callers see the "send succeeded" path; flip
/// <see cref="IsEnabled"/> to false to exercise the "email disabled" skip.
/// </summary>
public sealed class FakeEmailService : IEmailService
{
    private readonly ConcurrentQueue<Sent> _sends = new();
    private int _counter;

    public bool IsEnabled { get; set; } = true;

    /// <summary>When true, the next <see cref="SendAsync"/> call records the attempt and returns
    /// null (simulating Resend non-2xx). Resets to false after one use, so tests can exercise the
    /// "messageId is null → log status 'failed'" branch without leaking into later sends.</summary>
    public bool NextSendReturnsNull { get; set; }

    /// <summary>When true, EVERY <see cref="SendAsync"/> call records the attempt and returns null —
    /// the shape of a revoked <c>Resend:ApiKey</c>, where nothing the tick sends is ever accepted.
    /// Unlike <see cref="NextSendReturnsNull"/> it does not reset itself; <see cref="Reset"/> clears it.
    /// </summary>
    public bool AllSendsReturnNull { get; set; }

    /// <summary>Every <see cref="SendFailureReporting"/> value this fake was called with, in order —
    /// so a test can pin that a BULK caller told the service not to raise its own metered event.</summary>
    public IReadOnlyList<SendFailureReporting> FailureReportingModes => _failureReporting.ToArray();

    private readonly ConcurrentQueue<SendFailureReporting> _failureReporting = new();

    /// <summary>When set, the next <see cref="SendAsync"/> call faults with this exception instead
    /// of sending — simulating a transport failure or the 30s "resend" HttpClient timeout
    /// (TaskCanceledException). Resets after one use. Lets tests exercise an endpoint's
    /// catch-the-throw path (e.g. VendorEndpoints' email → 502).</summary>
    public Exception? NextSendThrows { get; set; }

    /// <summary>When set, EVERY <see cref="SendAsync"/> call faults — the shape of an unreachable
    /// Resend, where the reminder worker's own per-recipient catch is what fires. Unlike
    /// <see cref="NextSendThrows"/> it does not reset itself; <see cref="Reset"/> clears it.
    /// <para/>
    /// A FACTORY, not an instance, and that matters: a real failing HTTP call raises a fresh exception
    ///each time, and Sentry's default <c>DeduplicateMode</c> includes <c>SameExceptionInstance</c> — so a
    /// reused instance would collapse a whole day of events into one and make a quota test pass for a
    /// reason production does not enjoy.</summary>
    public Func<Exception>? AllSendsThrow { get; set; }

    public IReadOnlyList<Sent> Sends => _sends.ToArray();

    /// <summary>Clears captured sends and restores <see cref="IsEnabled"/> to its default true.
    /// Callers should not have to remember a finally — any test that mutates IsEnabled is freshly
    /// reset before the next test runs.</summary>
    public void Reset()
    {
        _sends.Clear();
        _failureReporting.Clear();
        Interlocked.Exchange(ref _counter, 0);
        IsEnabled = true;
        NextSendReturnsNull = false;
        AllSendsReturnNull = false;
        NextSendThrows = null;
        AllSendsThrow = null;
    }

    public Task<string?> SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct,
        string? idempotencyKey = null,
        SendFailureReporting failureReporting = SendFailureReporting.PerSend)
    {
        _failureReporting.Enqueue(failureReporting);

        if (!IsEnabled) return Task.FromResult<string?>(null);

        if (NextSendThrows is { } boom)
        {
            NextSendThrows = null;
            return Task.FromException<string?>(boom);
        }

        if (AllSendsThrow is { } always) return Task.FromException<string?>(always());

        if (NextSendReturnsNull || AllSendsReturnNull)
        {
            NextSendReturnsNull = false;
            _sends.Enqueue(new Sent(toEmail, subject, htmlBody, MessageId: null, idempotencyKey));
            return Task.FromResult<string?>(null);
        }

        var id = $"resend_test_{Interlocked.Increment(ref _counter):D6}";
        _sends.Enqueue(new Sent(toEmail, subject, htmlBody, id, idempotencyKey));
        return Task.FromResult<string?>(id);
    }

    public sealed record Sent(string ToEmail, string Subject, string HtmlBody, string? MessageId, string? IdempotencyKey = null);
}
