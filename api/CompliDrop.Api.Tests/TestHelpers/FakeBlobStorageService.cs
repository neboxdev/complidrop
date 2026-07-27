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

    /// <summary>
    /// When set, <see cref="UploadAsync"/> reports this URL instead of the derived <c>memory://</c> one,
    /// while still STORING the blob. Exists so a test can drive a genuine Postgres 22001 on
    /// <c>Document.BlobStorageUrl</c> (<c>varchar(500)</c>) — an insert failure that is NOT an
    /// idempotency-key conflict — and then assert the upload path's unconditional blob cleanup on the
    /// blob store itself rather than merely inferring it from a failed request (#389). The one column on
    /// that insert whose value the SERVER derives, so it is the only lever left once every
    /// request-controlled column is guarded.
    /// </summary>
    public string? UrlOverride { get; set; }

    /// <summary>
    /// Cancelled by <see cref="UploadAsync"/> the instant the blob is stored, simulating a client that
    /// aborts BETWEEN the blob upload and the commit (tab closed, phone loses signal). Installed by
    /// <see cref="ClientAbortStartupFilter"/>, which is what makes the cancelled token the one the
    /// endpoint is holding. Lets a test drive the exact failure path whose cleanup used to no-op (#389
    /// re-review) with no timing race: the cancel happens inside the upload call itself.
    /// </summary>
    public CancellationTokenSource? CancelRequestAfterUpload { get; set; }

    public async Task<BlobUploadResult> UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        if (ThrowUnavailableOnUpload)
            throw new BlobStorageUnavailableException("Simulated storage outage.", new InvalidOperationException("simulated"));
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        _blobs[blobName] = bytes;
        var result = new BlobUploadResult(blobName, UrlOverride ?? $"memory://{blobName}", bytes.Length, contentType);
        // AFTER the blob exists: the orphan this simulates is only possible once storage has taken it.
        if (CancelRequestAfterUpload is { } abort)
            await abort.CancelAsync();
        return result;
    }

    /// <summary>Clears stored blobs and the behavior knobs between tests (host singleton).</summary>
    public void Reset()
    {
        _blobs.Clear();
        ThrowUnavailableOnUpload = false;
        ThrowOnDelete = false;
        UrlOverride = null;
        CancelRequestAfterUpload = null;
    }

    // Honest not-found: null for an unknown name, mirroring the interface contract the real
    // Azure implementation maps its 404 to (#254). Tests that need a document's blob to exist
    // must actually Upload it (see ExtractionWorkerTests.SeedDocAsync).
    public Task<Stream?> DownloadAsync(string blobName, CancellationToken ct) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue(blobName, out var b) ? new MemoryStream(b) : null);

    public Task DeleteAsync(string blobName, CancellationToken ct)
    {
        // FAITHFUL to the real service, and load-bearing for the #389 re-review test: BlobStorageService
        // forwards ct to BlobClient.DeleteIfExistsAsync, which throws the moment it sees an already-
        // cancelled token — it never issues the DELETE. A fake that ignored ct would report a green
        // cleanup for a token Azure would have refused, i.e. it could not detect the orphan-on-abort bug.
        ct.ThrowIfCancellationRequested();
        if (ThrowOnDelete)
            throw new BlobStorageUnavailableException("Simulated storage outage on delete.", new InvalidOperationException("simulated"));
        _blobs.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    public Uri GetBlobUri(string blobName) => new($"memory://{blobName}");
}
