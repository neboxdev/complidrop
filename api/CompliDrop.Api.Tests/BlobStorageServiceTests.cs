using Azure.Core;
using Azure.Storage.Blobs;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the fail-fast blob retry policy (#259, problem 5). The Azure SDK default (many retries with
/// long back-off) made an unreachable-storage upload hang ~25s on the product's most important
/// request. Without this test a revert to the SDK defaults — the exact regression — would pass the
/// rest of the suite, since every blob-touching test uses the in-memory FakeBlobStorageService and
/// never exercises the real client's retry options. Pure unit test: BuildClientOptions is static, so
/// no connection or network is needed.
/// </summary>
public sealed class BlobStorageServiceTests
{
    [Fact]
    public void Client_options_use_a_fail_fast_retry_policy()
    {
        var options = BlobStorageService.BuildClientOptions();

        // The retry-storm bound is what actually fixed the ~25s hang: few retries, short back-off.
        options.Retry.MaxRetries.Should().Be(2, "the SDK default retry count caused the ~25s hang");
        options.Retry.Mode.Should().Be(RetryMode.Exponential);
        options.Retry.Delay.Should().Be(TimeSpan.FromMilliseconds(500));
        options.Retry.MaxDelay.Should().Be(TimeSpan.FromSeconds(2));

        // The per-try transfer bound stays generous on purpose — it must fit a legitimately slow
        // 10 MB upload (the cap), so it is NOT the fail-fast lever and must not be tightened to it.
        options.Retry.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    // A well-formed connection string whose blob endpoint points at a closed local port, so any
    // network call fails fast with "connection refused" — no external network, deterministic (#248).
    private const string UnreachableConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:1/devstoreaccount1;";

    private static BlobStorageService Build(string connectionString) =>
        new(Options.Create(new AzureStorageSettings { ConnectionString = connectionString, ContainerName = "documents" }));

    [Fact]
    public void Constructor_makes_no_network_call_and_does_not_throw_on_an_unreachable_account()
    {
        // #248: the ctor must be pure (BlobServiceClient + GetBlobContainerClient parse only) — it
        // must NOT connect or throw here, even pointed at an unreachable account. (The old ctor's
        // CreateIfNotExists network call poisoned construction and 500ed every upload.)
        var act = () => Build(UnreachableConnectionString);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task UploadAsync_maps_a_storage_failure_to_BlobStorageUnavailableException()
    {
        // The container-ensure + upload hit the closed port; the wrapper must surface the domain
        // exception (so the endpoints can answer a friendly 503), not leak the Azure SDK type (#248).
        var sut = Build(UnreachableConnectionString);
        using var content = new MemoryStream([1, 2, 3, 4]);
        // Bound the test in case a pathological CI blackholes the SYN to 127.0.0.1:1 instead of
        // refusing it (loopback normally RSTs immediately). A genuine cancellation propagates as OCE
        // (UploadAsync deliberately doesn't wrap caller-cancellation), so a timeout fails loudly
        // rather than hanging the suite — but the expected, near-instant path is connection-refused.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var act = async () => await sut.UploadAsync("org/2026-06/doc.pdf", content, "application/pdf", cts.Token);

        await act.Should().ThrowAsync<BlobStorageUnavailableException>();
    }

    [Fact]
    public async Task EnsureContainerAsync_retries_after_a_failure_then_caches_the_success()
    {
        // #248 follow-up: the lazy container-create must NOT cache a failure (else every later upload
        // would 500 forever) and MUST cache a success (so it isn't re-attempted per upload). Drive the
        // injected create op directly via the internal test seam — no live Azure / loopback listener.
        var calls = 0;
        var failFirst = true;
        // Parse-only container client (no network); EnsureContainerAsync only uses the injected op.
        var container = new BlobServiceClient(UnreachableConnectionString).GetBlobContainerClient("documents");
        var sut = new BlobStorageService(container, _ =>
        {
            calls++;
            if (failFirst) { failFirst = false; throw new InvalidOperationException("simulated create failure"); }
            return Task.CompletedTask;
        });

        // First attempt fails and must leave the state uncached so the next upload can retry.
        var first = async () => await sut.EnsureContainerAsync(CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);

        // Second attempt runs the create AGAIN (retry-after-failure) and succeeds → caches.
        await sut.EnsureContainerAsync(CancellationToken.None);
        calls.Should().Be(2);

        // Third attempt is a no-op (once-per-process caching): the create is not invoked again.
        await sut.EnsureContainerAsync(CancellationToken.None);
        calls.Should().Be(2, "a successful ensure caches and is not re-attempted");
    }
}
