using CompliDrop.Api.Auth;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>Pure unit tests for the account-lockout backoff policy (<see cref="AuthLockout"/>).</summary>
public class AuthLockoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)] // one below the threshold — still no lock
    public void No_lock_below_threshold(int attempts) =>
        AuthLockout.ComputeLockoutDuration(attempts).Should().BeNull();

    [Theory]
    [InlineData(10, 15)]     // first lock: 15 min
    [InlineData(11, 30)]     // doubles each subsequent attempt
    [InlineData(12, 60)]
    [InlineData(13, 120)]
    [InlineData(14, 240)]
    [InlineData(16, 960)]
    [InlineData(17, 1440)]   // cap reached (24h)
    [InlineData(18, 1440)]   // capped — does not keep doubling
    [InlineData(100, 1440)]  // still capped, overflow-safe
    public void Locks_with_exponential_backoff_capped_at_24h(int attempts, int expectedMinutes) =>
        AuthLockout.ComputeLockoutDuration(attempts).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
}
