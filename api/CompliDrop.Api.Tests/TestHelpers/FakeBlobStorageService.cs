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

    public async Task<BlobUploadResult> UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        _blobs[blobName] = bytes;
        return new BlobUploadResult(blobName, $"memory://{blobName}", bytes.Length, contentType);
    }

    // Honest not-found: null for an unknown name, mirroring the interface contract the real
    // Azure implementation maps its 404 to (#254). Tests that need a document's blob to exist
    // must actually Upload it (see ExtractionWorkerTests.SeedDocAsync).
    public Task<Stream?> DownloadAsync(string blobName, CancellationToken ct) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue(blobName, out var b) ? new MemoryStream(b) : null);

    public Task DeleteAsync(string blobName, CancellationToken ct)
    {
        _blobs.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    public Uri GetBlobUri(string blobName) => new($"memory://{blobName}");
}
