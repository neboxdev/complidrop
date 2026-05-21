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
    [InlineData(10, 15)]   // first lock (1× step)
    [InlineData(11, 30)]   // grows per attempt
    [InlineData(12, 45)]
    [InlineData(20, 165)]  // 11× step
    [InlineData(21, 180)]  // cap reached (12× step)
    [InlineData(22, 180)]  // capped — does not keep growing
    [InlineData(100, 180)] // still capped
    public void Locks_with_capped_backoff(int attempts, int expectedMinutes) =>
        AuthLockout.ComputeLockoutDuration(attempts).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
}
