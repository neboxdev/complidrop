using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CompliDrop.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services;

public interface IBlobStorageService
{
    Task<BlobUploadResult> UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct);
    Task<Stream> DownloadAsync(string blobName, CancellationToken ct);
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
        var client = new BlobServiceClient(cfg.ConnectionString);
        _container = client.GetBlobContainerClient(cfg.ContainerName);
        _container.CreateIfNotExists(PublicAccessType.None);
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

    public async Task<Stream> DownloadAsync(string blobName, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(blobName);
        var result = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return result.Value.Content;
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(blobName);
        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    public Uri GetBlobUri(string blobName) =>
        _container.GetBlobClient(blobName).Uri;
}
