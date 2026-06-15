using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Configuration;

public class AzureStorageSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "documents";
}

/// <summary>
/// Fails the boot outside Development if Azure Blob storage is misconfigured (#248). The blob client
/// is a lazy singleton, so a missing/empty <c>ConnectionString</c> used to surface only on FIRST
/// UPLOAD — the product's most important request — as an opaque, re-thrown 500 with no log line
/// naming the misconfiguration. Validating at startup (ValidateOnStart) turns that into a loud,
/// immediate boot failure instead. In Development (and tests) the blob service is faked / self-disables,
/// so the localhost-style empty default is allowed there — mirroring <see cref="FrontendSettingsValidator"/>.
/// </summary>
public sealed class AzureStorageSettingsValidator(IHostEnvironment env) : IValidateOptions<AzureStorageSettings>
{
    public ValidateOptionsResult Validate(string? name, AzureStorageSettings options)
    {
        if (env.IsDevelopment()) return ValidateOptionsResult.Success;

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            return ValidateOptionsResult.Fail(
                "AzureStorage:ConnectionString must be set outside Development — file upload (the "
                + "product's core path) cannot work without it. Set it to the storage account's "
                + "connection string.");

        if (string.IsNullOrWhiteSpace(options.ContainerName))
            return ValidateOptionsResult.Fail("AzureStorage:ContainerName must be set outside Development.");

        return ValidateOptionsResult.Success;
    }
}
