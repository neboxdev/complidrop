using System.Collections.Concurrent;
using CompliDrop.Api.Services;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="IBlobStorageService"/> for tests — stores blobs in a dictionary so the
/// upload path works without Azure (the real service builds a BlobServiceClient in its constructor,
/// but no longer makes a network call there — container creation is lazy since #248).
/// </summary>
public sealed class FakeBlobStorageService : IBlobStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    /// <summary>Number of blobs currently stored — lets a test assert orphan-cleanup (e.g. the
    /// idempotency-race loser rolling its blob back, #336).</summary>
    public int BlobCount => _blobs.Count;

    /// <summary>When true, <see cref="UploadAsync"/> throws <see cref="BlobStorageUnavailableException"/>
    /// — simulates a storage outage so the upload endpoints' friendly-503 mapping can be tested (#248).</summary>
    public bool ThrowUnavailableOnUpload { get; set; }

    /// <summary>When true, <see cref="DeleteAsync"/> throws — simulates a storage outage on the delete
    /// path so the sample-clear endpoint's fail-loudly-before-touching-rows behavior can be tested (#238).</summary>
    public bool ThrowOnDelete { get; set; }

    public async Task<BlobUploadResult> UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        if (ThrowUnavailableOnUpload)
            throw new BlobStorageUnavailableException("Simulated storage outage.", new InvalidOperationException("simulated"));
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        _blobs[blobName] = bytes;
        return new BlobUploadResult(blobName, $"memory://{blobName}", bytes.Length, contentType);
    }

    /// <summary>Clears stored blobs and the outage knob between tests (host singleton).</summary>
    public void Reset()
    {
        _blobs.Clear();
        ThrowUnavailableOnUpload = false;
        ThrowOnDelete = false;
    }

    // Honest not-found: null for an unknown name, mirroring the interface contract the real
    // Azure implementation maps its 404 to (#254). Tests that need a document's blob to exist
    // must actually Upload it (see ExtractionWorkerTests.SeedDocAsync).
    public Task<Stream?> DownloadAsync(string blobName, CancellationToken ct) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue(blobName, out var b) ? new MemoryStream(b) : null);

    public Task DeleteAsync(string blobName, CancellationToken ct)
    {
        if (ThrowOnDelete)
            throw new BlobStorageUnavailableException("Simulated storage outage on delete.", new InvalidOperationException("simulated"));
        _blobs.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    public Uri GetBlobUri(string blobName) => new($"memory://{blobName}");
}
