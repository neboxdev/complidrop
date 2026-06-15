namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Shared test constants. Centralizes the well-formed Azure Blob connection string so the boot-fail
/// isolation tests (#250) and the validator "accepted config" cases (#248) don't each re-paste a
/// base64 AccountKey — addressing the cross-ticket duplication the #248/#250 audit flagged.
/// </summary>
internal static class TestConfig
{
    /// <summary>
    /// A well-formed connection string with a valid base64 AccountKey (the Azure SDK validates the key
    /// format when it parses, with NO network call). Use it as a valid <c>AzureStorage</c> override so
    /// a boot-fail test isolates the validator under test, and as the validator's "accepted config" case.
    /// </summary>
    public const string WellFormedAzureConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=acct;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "EndpointSuffix=core.windows.net";
}
