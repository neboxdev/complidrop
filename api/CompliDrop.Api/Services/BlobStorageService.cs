using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services;

public interface IBlobStorageService
{
    Task<BlobUploadResult> UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct);
    /// <summary>
    /// Opens a read stream for the blob, or <c>null</c> when it does not exist. Not-found is
    /// part of THIS contract (not a leaked vendor exception) so callers never need to know the
    /// storage SDK's error surface — the Azure implementation maps its 404 internally (#254).
    /// </summary>
    Task<Stream?> DownloadAsync(string blobName, CancellationToken ct);
    Task DeleteAsync(string blobName, CancellationToken ct);
    Uri GetBlobUri(string blobName);
}

public record BlobUploadResult(string BlobName, string Url, long Size, string ContentType);

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;

    public BlobStorageService(IOptions<AzureStorageSettings> settings)
    {
        var cfg = settings.Value;
        var client = new BlobServiceClient(cfg.ConnectionString, BuildClientOptions());
        _container = client.GetBlobContainerClient(cfg.ContainerName);
        _container.CreateIfNotExists(PublicAccessType.None);
    }

    /// <summary>
    /// Fail-fast retry policy. The Azure SDK default (many retries with long back-off) makes an
    /// unreachable-storage upload hang ~25s before surfacing — unacceptable on the interactive upload
    /// path, the product's most important request (#259, problem 5). The retry-storm bound (the
    /// actual cause of the ~25s hang) is <see cref="Azure.Core.RetryOptions.MaxRetries"/> = 2 with a
    /// short exponential back-off. NetworkTimeout is the PER-TRY transfer bound and is kept generous
    /// (30s) on purpose: it must accommodate a legitimately-slow upload of a 10 MB file (the upload
    /// cap) on a modest connection, so it is NOT used as the fail-fast lever. The overall request is
    /// additionally bounded by the caller's CancellationToken. Internal so the regression suite can
    /// pin these values against a silent revert to the SDK defaults.
    /// </summary>
    internal static BlobClientOptions BuildClientOptions()
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2;
        options.Retry.Mode = Azure.Core.RetryMode.Exponential;
        options.Retry.Delay = TimeSpan.FromMilliseconds(500);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(2);
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        return options;
    }

    public async Task<BlobUploadResult> UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(blobName);
        var headers = new BlobHttpHeaders { ContentType = contentType };
        var options = new BlobUploadOptions { HttpHeaders = headers };

        var initialPosition = content.CanSeek ? content.Position : 0;
        await blob.UploadAsync(content, options, ct);
        var length = content.CanSeek ? content.Position - initialPosition : content.Length;
        return new BlobUploadResult(blobName, blob.Uri.ToString(), length, contentType);
    }

    public async Task<Stream?> DownloadAsync(string blobName, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(blobName);
        try
        {
            var result = await blob.DownloadStreamingAsync(cancellationToken: ct);
            return result.Value.Content;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // BlobNotFound / ContainerNotFound — surface as the interface's null, so no
            // caller has to reference the Azure SDK's exception types.
            return null;
        }
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(blobName);
        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    public Uri GetBlobUri(string blobName) =>
        _container.GetBlobClient(blobName).Uri;
}
