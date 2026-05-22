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

    public IReadOnlyList<Sent> Sends => _sends.ToArray();

    public void Reset()
    {
        _sends.Clear();
        Interlocked.Exchange(ref _counter, 0);
    }

    public Task<string?> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        if (!IsEnabled) return Task.FromResult<string?>(null);
        var id = $"resend_test_{Interlocked.Increment(ref _counter):D6}";
        _sends.Enqueue(new Sent(toEmail, subject, htmlBody, id));
        return Task.FromResult<string?>(id);
    }

    public sealed record Sent(string ToEmail, string Subject, string HtmlBody, string MessageId);
}
