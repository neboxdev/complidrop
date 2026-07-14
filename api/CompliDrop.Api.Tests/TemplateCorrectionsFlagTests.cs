using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// End-to-end proof of the FLAG-OFF (production-default) posture of the #416 template-corrections
/// gate (ADR 0036 Amendment 3): a host booted with <c>TemplateCorrections:Enabled=false</c> seeds
/// the LEGACY (pre-#416) checklist set and reports <c>features.correctedChecklists=false</c> on
/// every me-shaped payload, so the SPA keeps the gated UI hidden.
///
/// Runs against its OWN container (pattern: <see cref="DatabaseMigratorStartupTests"/>), NOT the
/// shared <see cref="IntegrationTestFixture"/>: the shared test hosts pin the flag ON, and booting
/// a flag-OFF host against the shared database would converge its system templates back to the
/// legacy set (the flag is reversible by design) and corrupt every later seed-dependent test. The
/// flag-ON value of the me feature is pinned by AuthEndpointsTests on the shared host.
/// </summary>
public sealed class TemplateCorrectionsFlagTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Flag_off_boot_seeds_the_legacy_set_and_reports_the_feature_off_on_me()
    {
        // Override the test-host default (CustomWebApplicationFactory pins the flag ON for the
        // shared-fixture world) back to the PRODUCTION default: OFF. Boot auto-migrates the fresh
        // container and runs the seed with the legacy set selected.
        await using var factory = new CustomWebApplicationFactory(
            _container.GetConnectionString(),
            new Dictionary<string, string?> { ["TemplateCorrections:Enabled"] = "false" });
        using var client = factory.CreateClient();

        // Register: the register payload is me-shaped and the SPA writes it straight into its
        // session cache, so it must carry the same features object as /me — off here.
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"flag-off-{Guid.NewGuid():N}@x.com",
            password = "Password1234",
            fullName = "Flag Off",
            companyName = "Flag Off Venue",
        });
        reg.StatusCode.Should().Be(HttpStatusCode.OK);
        var regBody = await reg.Content.ReadFromJsonAsync<JsonElement>();
        regBody.GetProperty("data").GetProperty("features").GetProperty("correctedChecklists").GetBoolean()
            .Should().BeFalse("the register payload must hide the gated UI while the flag is off");

        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>();
        meBody.GetProperty("data").GetProperty("features").GetProperty("correctedChecklists").GetBoolean()
            .Should().BeFalse("features.correctedChecklists must reflect TemplateCorrections:Enabled=false");

        // And the boot-seeded system templates are the LEGACY set — a flag-off deploy is
        // behaviorally identical to pre-#416 production: no liquor rule anywhere, the old $500k
        // Photographer GL floor, and the old Transport CDL rule still present.
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        (await ScalarAsync(conn,
            "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
            "WHERE ct.\"IsSystemTemplate\" = true AND cr.\"FieldName\" = 'liquor_liability_limit'"))
            .Should().Be(0, "the legacy Caterer checklist has no liquor-liability rule");
        (await ScalarStringAsync(conn,
            "SELECT cr.\"ExpectedValue\" FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
            "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Photographer / Videographer' AND cr.\"FieldName\" = 'general_liability_limit'"))
            .Should().Be("500000", "the legacy Photographer GL floor is $500k");
        (await ScalarAsync(conn,
            "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
            "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Transportation / Shuttle' " +
            "AND cr.\"FieldName\" = 'license_type' AND cr.\"Operator\" = 'equals'"))
            .Should().Be(1, "the legacy Transport checklist still carries the CDL rule the corrected set removed");
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync()) as string;
    }
}
