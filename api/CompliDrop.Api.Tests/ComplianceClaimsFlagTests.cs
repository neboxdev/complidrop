using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Testcontainers.PostgreSql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// End-to-end proof that the /api/auth/me <c>features.correctedAdditionalInsuredWording</c> flag
/// (#396, CLM-1, ADR 0042) mirrors <c>ComplianceClaims:CorrectedAdditionalInsuredWording</c> in BOTH
/// postures, on every me-shaped payload the SPA caches (register + /me).
///
/// Runs against its OWN container (pattern: <see cref="TemplateCorrectionsFlagTests"/>). Unlike the
/// template-corrections flag, this one is COPY-ONLY — it touches no seed and never converges the
/// system templates — so booting a flag-ON and a flag-OFF host against the SAME isolated container is
/// harmless; each case still boots its own host so the config value under test is unambiguous. The
/// shared-fixture default (OFF) is separately pinned by AuthEndpointsTests.
/// </summary>
public sealed class ComplianceClaimsFlagTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task Register_and_me_report_the_configured_flag_value(string configValue, bool expected)
    {
        await using var factory = new CustomWebApplicationFactory(
            _container.GetConnectionString(),
            new Dictionary<string, string?>
            {
                ["ComplianceClaims:CorrectedAdditionalInsuredWording"] = configValue,
            });
        using var client = factory.CreateClient();

        // Register is me-shaped and the SPA writes it straight into its session cache, so it must carry
        // the same features object as /me — the flag has to be present on BOTH.
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"clm1-{Guid.NewGuid():N}@x.com",
            password = "Password1234",
            fullName = "Clm One",
            companyName = "Claims Venue",
        });
        reg.StatusCode.Should().Be(HttpStatusCode.OK);
        var regBody = await reg.Content.ReadFromJsonAsync<JsonElement>();
        regBody.GetProperty("data").GetProperty("features").GetProperty("correctedAdditionalInsuredWording").GetBoolean()
            .Should().Be(expected, "the register payload must carry the flag the SPA gates the additional-insured copy on");

        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>();
        meBody.GetProperty("data").GetProperty("features").GetProperty("correctedAdditionalInsuredWording").GetBoolean()
            .Should().Be(expected, "features.correctedAdditionalInsuredWording must mirror ComplianceClaims:CorrectedAdditionalInsuredWording");
    }
}
