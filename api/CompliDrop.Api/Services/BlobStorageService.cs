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

/// <summary>
/// Thrown by <see cref="IBlobStorageService.UploadAsync"/> when the storage backend can't accept the
/// file (misconfiguration, unreachable account, throttling, transient Azure error). Lets the upload
/// endpoints map a storage outage to a friendly 503 instead of the generic unhandled-exception 500
/// (#248) — without referencing the Azure SDK's exception types, mirroring how
/// <see cref="IBlobStorageService.DownloadAsync"/> maps its 404 to <c>null</c> (#254).
/// </summary>
public sealed class BlobStorageUnavailableException(string message, Exception inner) : Exception(message, inner);

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly Func<CancellationToken, Task> _ensureContainerOnce;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private bool _containerEnsured;

    public BlobStorageService(IOptions<AzureStorageSettings> settings)
    {
        // Construction makes NO network call: BlobServiceClient + GetBlobContainerClient only parse
        // config (the ctor throws on an empty/malformed connection string, but the startup
        // AzureStorageSettingsValidator rejects both outside Development, and tests use the fake — so
        // in practice this runs only with a well-formed string). The old ctor's CreateIfNotExists()
        // was a blocking network call inside a lazy singleton, so a transient Azure error at first
        // upload poisoned construction and re-threw an opaque 500 on every subsequent request (#248).
        // Container creation now happens lazily on first upload (EnsureContainerAsync), mappable to 503.
        var cfg = settings.Value;
        var client = new BlobServiceClient(cfg.ConnectionString, BuildClientOptions());
        _container = client.GetBlobContainerClient(cfg.ContainerName);
        _ensureContainerOnce = ct => _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
    }

    /// <summary>
    /// Test-only seam (#248 follow-up): injects the one-time container-create operation so the
    /// retry-after-failure / once-per-process caching of <see cref="EnsureContainerAsync"/> can be
    /// unit-tested without a live Azure account or a loopback HTTP listener. The runtime always uses
    /// the public constructor above, which wires the create to the real container client.
    /// </summary>
    internal BlobStorageService(BlobContainerClient container, Func<CancellationToken, Task> ensureContainerOnce)
    {
        _container = container;
        _ensureContainerOnce = ensureContainerOnce;
    }

    /// <summary>
    /// Creates the container once per process on first use (replaces the ctor's network call, #248).
    /// Guarded by a semaphore so concurrent first uploads don't race; on failure <c>_containerEnsured</c>
    /// stays false so the next upload retries rather than caching a permanently-failed state. Internal
    /// so the regression suite can pin the retry-after-failure + once-per-process semantics directly.
    /// </summary>
    internal async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerEnsured) return;
        await _ensureLock.WaitAsync(ct);
        try
        {
            if (_containerEnsured) return;
            await _ensureContainerOnce(ct);
            _containerEnsured = true;
        }
        finally
        {
            _ensureLock.Release();
        }
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
        try
        {
            await EnsureContainerAsync(ct);

            var blob = _container.GetBlobClient(blobName);
            var headers = new BlobHttpHeaders { ContentType = contentType };
            var options = new BlobUploadOptions { HttpHeaders = headers };

            var initialPosition = content.CanSeek ? content.Position : 0;
            await blob.UploadAsync(content, options, ct);
            var length = content.CanSeek ? content.Position - initialPosition : content.Length;
            return new BlobUploadResult(blobName, blob.Uri.ToString(), length, contentType);
        }
        catch (Exception ex) when (ex is not BlobStorageUnavailableException && !ct.IsCancellationRequested)
        {
            // Any storage failure that ISN'T the caller aborting (Azure RequestFailedException after
            // the fail-fast retries, a transport error, the per-try NetworkTimeout) becomes a
            // mappable domain exception so the upload endpoints answer a friendly 503 rather than the
            // generic 500 (#248). A genuine caller cancellation propagates — there's no one to answer.
            throw new BlobStorageUnavailableException("Blob storage is unavailable.", ex);
        }
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
