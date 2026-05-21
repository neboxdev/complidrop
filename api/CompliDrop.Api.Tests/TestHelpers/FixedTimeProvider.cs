namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// A <see cref="TimeProvider"/> that always returns a fixed instant, so date-boundary
/// logic (expiration / expiring-soon) can be tested deterministically without the wall clock.
/// </summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
