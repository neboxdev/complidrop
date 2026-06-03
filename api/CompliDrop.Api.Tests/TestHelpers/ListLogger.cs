using Microsoft.Extensions.Logging;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Minimal in-memory <see cref="ILogger{T}"/> that records every formatted log entry, so a test
/// can assert that code logged a specific warning (e.g. the reminder worker's unverified-recipient
/// dead-letter flag, #184). Replaces <c>NullLogger&lt;T&gt;.Instance</c> where the log itself is
/// the observable contract under test.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries => _entries;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Add(new Entry(logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
