using System.Collections.Concurrent;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="IBlobStorageService"/> for tests — stores blobs in a dictionary so the
/// upload path works without Azure (the real service connects to Azure in its constructor).
/// </summary>
public sealed class FakeBlobStorageService : IBlobStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    /// <summary>
    /// One-shot: the next <see cref="DownloadAsync"/> throws this instead of returning a
    /// stream, then the toggle clears. Lets tests simulate the real Azure client's
    /// <c>RequestFailedException</c> (e.g. 404 BlobNotFound) — the in-memory default of
    /// returning an empty stream can't exercise those endpoint catch paths. Reset between
    /// tests by <see cref="IntegrationTestFixture.ResetAsync"/>.
    /// </summary>
    public Exception? NextDownloadThrows { get; set; }

    public void Reset() => NextDownloadThrows = null;

    public async Task<BlobUploadResult> UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        _blobs[blobName] = bytes;
        return new BlobUploadResult(blobName, $"memory://{blobName}", bytes.Length, contentType);
    }

    public Task<Stream> DownloadAsync(string blobName, CancellationToken ct)
    {
        if (NextDownloadThrows is { } ex)
        {
            NextDownloadThrows = null;
            throw ex;
        }
        return Task.FromResult<Stream>(new MemoryStream(_blobs.TryGetValue(blobName, out var b) ? b : []));
    }

    public Task DeleteAsync(string blobName, CancellationToken ct)
    {
        _blobs.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    public Uri GetBlobUri(string blobName) => new($"memory://{blobName}");
}
