using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the #250 boot guard: <c>Frontend:BaseUrl</c> must be a real public origin outside
/// Development (it mints portal/verify/reset/checkout links sent to real users), so a misconfigured
/// prod fails fast at boot instead of mailing dead <c>http://localhost:3000</c> links. Pure unit
/// tests over the validator — no host.
/// </summary>
public sealed class FrontendSettingsValidatorTests
{
    private static ValidateOptionsResultAssertion Validate(string env, string? baseUrl)
    {
        var settings = new FrontendSettings();
        if (baseUrl is not null) settings.BaseUrl = baseUrl;
        var result = new FrontendSettingsValidator(new FakeEnv(env)).Validate(null, settings);
        return new ValidateOptionsResultAssertion(result);
    }

    [Fact]
    public void Development_allows_the_localhost_default()
    {
        Validate("Development", null).Succeeds();
        Validate("Development", "http://localhost:3000").Succeeds();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Non_development_rejects_the_localhost_default(string env)
    {
        Validate(env, "http://localhost:3000").FailsWith("localhost");
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("https://localhost")]
    [InlineData("http://[::1]:3000")]
    // #301: trailing-dot (FQDN-rooted) and wildcard bind addresses bypassed the old Uri.IsLoopback /
    // exact-"localhost" check and booted fine in prod, minting dead links. Pin them as rejected.
    [InlineData("http://localhost.:3000")]
    [InlineData("http://127.0.0.1.:3000")]
    [InlineData("http://0.0.0.0:3000")]
    [InlineData("http://[::]:3000")]
    public void Non_development_rejects_loopback_hosts(string url)
    {
        Validate("Production", url).Fails();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Non_development_rejects_empty(string url)
    {
        Validate("Production", url).Fails();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")] // missing scheme
    public void Non_development_rejects_non_http_or_relative(string url)
    {
        Validate("Production", url).Fails();
    }

    [Theory]
    [InlineData("https://www.complidrop.com")]
    [InlineData("https://www.complidrop.com/")]
    [InlineData("https://app.complidrop.com:8443")]
    public void Non_development_accepts_a_real_public_origin(string url)
    {
        Validate("Production", url).Succeeds();
    }

    private sealed class FakeEnv(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ValidateOptionsResultAssertion(ValidateOptionsResult result)
    {
        public void Succeeds() => result.Succeeded.Should().BeTrue(result.FailureMessage);
        public void Fails() => result.Failed.Should().BeTrue("the value must be rejected");
        public void FailsWith(string fragment)
        {
            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain(fragment);
        }
    }
}

/// <summary>
/// End-to-end pin for #250 AC2: with a configured (non-default) <c>Frontend:BaseUrl</c>, the portal
/// link and the email-verification link both come back on that public origin — not the localhost
/// default. Boots a second host with the override against the shared test database.
/// </summary>
public sealed class FrontendBaseUrlLinkTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Origin = "https://links.example.test";

    [Fact]
    public void Production_boot_fails_fast_on_the_localhost_BaseUrl_default()
    {
        // Pins the WIRING (registration + ValidateOnStart), not just the validator logic: booting in
        // Production with the unset/localhost default must abort startup with a clear message. A
        // regression that dropped .ValidateOnStart() or the IValidateOptions registration would make
        // this throw nothing (#250 AC1).
        using var factory = new CustomWebApplicationFactory(
                Fixture.ConnectionString,
                new Dictionary<string, string?>
                {
                    // Valid Azure config so the FrontendSettings guard is the ONLY validator that fails;
                    // otherwise the #248 AzureStorage guard also fires and *localhost* would be matching
                    // an aggregated message. Mirrors the #248 boot-fail test's isolation discipline.
                    ["AzureStorage:ConnectionString"] = TestConfig.WellFormedAzureConnectionString,
                    ["AzureStorage:ContainerName"] = "documents",
                })
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var act = () => factory.CreateClient(); // builds + starts the host → runs ValidateOnStart

        act.Should().Throw<OptionsValidationException>().WithMessage("*localhost*");
    }

    [Fact]
    public async Task Portal_and_verify_links_use_the_configured_origin_not_localhost()
    {
        await using var factory = new CustomWebApplicationFactory(
            Fixture.ConnectionString,
            new Dictionary<string, string?> { ["Frontend:BaseUrl"] = Origin });
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        var email = (FakeEmailService)factory.Services.GetRequiredService<CompliDrop.Api.Services.IEmailService>();
        email.Reset();

        // Register — this sends the email-verification link and logs the client in.
        var userEmail = $"baseurl-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = userEmail, password = "Password1234", fullName = "Test User",
            companyName = "Test Co", industry = (string?)null, companySize = (string?)null,
            timeZone = "America/New_York",
        });
        reg.EnsureSuccessStatusCode();
        var orgId = (await reg.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("organizationId").GetGuid();

        // Verify-email link uses the configured origin.
        email.Sends.Should().ContainSingle(s => s.HtmlBody.Contains("/verify-email?token="));
        email.Sends.Single(s => s.HtmlBody.Contains("/verify-email?token=")).HtmlBody
            .Should().Contain($"{Origin}/verify-email?token=")
            .And.NotContain("localhost");

        // Grant the portal entitlement (#261), then mint a portal link and assert its origin.
        await SetPortalEntitlementAsync(orgId, on: true, documentLimit: null);
        var vendorResp = await client.PostAsJsonAsync("/api/vendors", new
        {
            name = "Acme", contactEmail = (string?)null, contactPhone = (string?)null,
            category = (string?)null, complianceTemplateId = (Guid?)null,
        });
        vendorResp.EnsureSuccessStatusCode();
        var vendorId = (await vendorResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();

        var linkResp = await client.PostAsync($"/api/vendors/{vendorId}/portal-link", content: null);
        linkResp.EnsureSuccessStatusCode();
        var url = (await linkResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("url").GetString();

        url.Should().StartWith($"{Origin}/portal/").And.NotContain("localhost");

        // Password-reset is the other email-borne link minted from BaseUrl (#250 AC2). The AC asked
        // for "at least portal + verify", but reset shares the same single-origin design, so pin it
        // too so a regression that hard-coded a different origin in the reset path is caught.
        // forgot-password does its work (incl. the send) on a DETACHED background scope so response
        // time can't leak account existence (#183), so the reset email lands AFTER the 200 — poll for it.
        var forgot = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = userEmail });
        forgot.EnsureSuccessStatusCode();
        for (var i = 0; i < 500 && !email.Sends.Any(s => s.HtmlBody.Contains("/reset-password?token=")); i++)
            await Task.Delay(20);
        email.Sends.Single(s => s.HtmlBody.Contains("/reset-password?token=")).HtmlBody
            .Should().Contain($"{Origin}/reset-password?token=")
            .And.NotContain("localhost");
    }
}
