using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the #248 startup guard for Azure Blob config: outside Development, an unset
/// ConnectionString/ContainerName must fail the boot (file upload — the product's core path — can't
/// work without it), rather than 500ing on first upload. Pure unit tests over the validator.
/// </summary>
public sealed class AzureStorageSettingsValidatorTests
{
    private static ValidateOptionsResult Validate(string env, string connectionString, string containerName) =>
        new AzureStorageSettingsValidator(new FakeEnv(env))
            .Validate(null, new AzureStorageSettings { ConnectionString = connectionString, ContainerName = containerName });

    [Fact]
    public void Development_allows_empty_config()
    {
        Validate("Development", "", "").Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Non_development_rejects_an_empty_connection_string(string env)
    {
        var result = Validate(env, "", "documents");
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ConnectionString");
    }

    // Well-formed connection string with a valid base64 AccountKey (the SDK validates the key when it
    // parses) — used for the "container name empty" and "accepted" cases so they reach/pass the parse
    // check rather than tripping it. Centralized in TestConfig so the #250 boot-isolation test reuses
    // the same value instead of re-pasting the key.
    private const string WellFormed = TestConfig.WellFormedAzureConnectionString;

    [Fact]
    public void Non_development_rejects_an_empty_container_name()
    {
        var result = Validate("Production", WellFormed, "");
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ContainerName");
    }

    [Fact]
    public void Non_development_rejects_a_malformed_connection_string()
    {
        // "absent OR malformed" (AC1): a non-empty garbage string passes the non-empty check but must
        // fail the parse check at boot, not throw a generic 500 on first upload.
        var result = Validate("Production", "this-is-not-a-valid-connection-string", "documents");
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("malformed");
    }

    [Fact]
    public void Non_development_accepts_a_populated_config()
    {
        Validate("Production", WellFormed, "documents").Succeeded.Should().BeTrue();
    }

    private sealed class FakeEnv(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// Integration pins for #248: a Production boot with no Azure config aborts startup, and an upload
/// during a storage outage returns a friendly 503 (dashboard + the external-persona portal) instead
/// of the generic unhandled-exception 500.
/// </summary>
public sealed class BlobStorageFailureTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeBlobStorageService Blobs =>
        (FakeBlobStorageService)Fixture.Factory.Services.GetRequiredService<IBlobStorageService>();

    [Fact]
    public void Production_boot_fails_fast_when_AzureStorage_is_unconfigured()
    {
        // Valid Frontend:BaseUrl override so the only thing wrong is Azure (the #250 guard would
        // otherwise also fire); AzureStorage:ConnectionString is unset → boot must abort (#248).
        using var factory = new CustomWebApplicationFactory(
                Fixture.ConnectionString,
                new Dictionary<string, string?> { ["Frontend:BaseUrl"] = "https://links.example.test" })
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>().WithMessage("*ConnectionString*");
    }

    [Fact]
    public async Task Dashboard_upload_returns_a_friendly_503_when_storage_is_unavailable()
    {
        var auth = await RegisterAndLoginAsync();
        Blobs.ThrowUnavailableOnUpload = true;

        var resp = await auth.Client.PostAsync("/api/documents/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("storage.unavailable");
        var message = body.GetProperty("error").GetProperty("message").GetString();
        message.Should().NotContainEquivalentOf("unexpected error", "the friendly copy must replace the generic 500");
        // Positively pin the dashboard-specific wording (distinct from the portal's "save your file"),
        // matching the portal test's assertion strength so a regression that swapped the copy is caught.
        message.Should().Contain("store your file");
    }

    [Fact]
    public async Task Portal_upload_returns_a_friendly_503_for_the_external_persona_when_storage_is_unavailable()
    {
        var seeded = await SeedLinkAsync(); // entitled portal link (#261 default)
        Blobs.ThrowUnavailableOnUpload = true;

        var resp = await CreateClient().PostAsync(
            $"/api/portal/{seeded.Token}/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("storage.unavailable");
        var message = body.GetProperty("error").GetProperty("message").GetString();
        message.Should().NotContainEquivalentOf("unexpected error");
        // Pin the external-persona (vendor) copy specifically — distinct from the dashboard's "store"
        // wording — so a regression that swapped in the internal copy is caught.
        message.Should().Contain("save your file");
    }
}
