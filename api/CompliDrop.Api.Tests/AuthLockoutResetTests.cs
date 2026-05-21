using System.Net;
using System.Net.Http.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration test for the one lockout behavior that lives in the login endpoint rather than
/// the pure <see cref="CompliDrop.Api.Auth.AuthLockout"/> helper: the failed-attempt counter
/// accumulates on bad logins and resets after a successful login (acceptance criterion of #4).
/// The broader register/login/refresh HTTP flow is covered by #5.
/// </summary>
public sealed class AuthLockoutResetTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Failed_login_attempts_accumulate_then_reset_after_a_successful_login()
    {
        const string email = "lockout@example.com";
        const string password = "Password1234";
        await RegisterAndLoginAsync(email: email, password: password);

        var client = CreateClient();

        // Three bad logins increment the counter (rate limiting is disabled in the test host).
        for (var i = 0; i < 3; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "definitely-wrong-1" });
            bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        await using (var db = CreateSystemDb())
        {
            (await db.Users.Where(u => u.Email == email).Select(u => u.FailedLoginAttempts).FirstAsync())
                .Should().Be(3);
        }

        // A successful login resets the counter and clears any lock.
        (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).EnsureSuccessStatusCode();

        await using (var db = CreateSystemDb())
        {
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.FailedLoginAttempts.Should().Be(0);
            user.LockedUntil.Should().BeNull();
        }
    }
}
