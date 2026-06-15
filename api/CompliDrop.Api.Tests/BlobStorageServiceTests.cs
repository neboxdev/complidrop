using Azure.Core;
using CompliDrop.Api.Services;
using FluentAssertions;

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
}
