using Azure.Storage.Blobs;
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

        // "absent OR MALFORMED" (the ticket's words): a non-empty-but-garbled connection string would
        // pass the checks above, then throw FormatException in BlobServiceClient's constructor on
        // FIRST UPLOAD — outside UploadAsync's try/catch, so it would surface as the generic 500 this
        // ticket exists to remove. Parse it here (the BlobServiceClient ctor parses with NO network
        // call) so a malformed string also fails the boot. The message names the key, never the value.
        try
        {
            _ = new BlobServiceClient(options.ConnectionString);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return ValidateOptionsResult.Fail(
                "AzureStorage:ConnectionString is malformed and could not be parsed. Check the storage "
                + "account connection string.");
        }

        return ValidateOptionsResult.Success;
    }
}
