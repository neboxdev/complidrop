using System.Net;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Unit tests for the REAL <see cref="StripeService.CancelSubscriptionAsync"/> error
/// discrimination (#255). The integration suite exercises the IStripeService seam via
/// FakeStripeService; the absorb-terminal-states branchwork (resource_missing → success,
/// cancel-failed-but-live-status-canceled → success, verify-failure → original error)
/// lives BELOW that seam, so it gets the StubHttpMessageHandler treatment instead — the
/// same convention as the Gemini/Anthropic client tests. The Stripe SDK is pointed at the
/// stub via the internal ClientOverride seam; with the override set the global
/// StripeConfiguration is neither read nor written, so these tests are parallel-safe.
/// </summary>
public sealed class StripeServiceCancelTests
{
    private const string SubId = "sub_unit_test_1";

    private static StripeService NewService(StubHttpMessageHandler stub)
    {
        var service = new StripeService(
            // CancelSubscriptionAsync never touches the DbContext; the options just need to exist.
            new SystemDbContext(new DbContextOptionsBuilder<SystemDbContext>()
                .UseNpgsql("Host=unused;Database=unused").Options),
            Options.Create(new StripeSettings { SecretKey = "sk_test_unit" }),
            NullLogger<StripeService>.Instance)
        {
            ClientOverride = new StripeClient(
                "sk_test_unit",
                httpClient: new SystemNetHttpClient(new HttpClient(stub))),
        };
        return service;
    }

    private static string SubscriptionJson(string status) =>
        $"{{\"id\":\"{SubId}\",\"object\":\"subscription\",\"status\":\"{status}\"}}";

    private static string ErrorJson(string message, string? code = null) =>
        code is null
            ? $"{{\"error\":{{\"type\":\"invalid_request_error\",\"message\":\"{message}\"}}}}"
            : $"{{\"error\":{{\"type\":\"invalid_request_error\",\"code\":\"{code}\",\"message\":\"{message}\"}}}}";

    [Fact]
    public async Task Successful_cancel_returns_without_throwing()
    {
        var stub = new StubHttpMessageHandler(HttpStatusCode.OK, SubscriptionJson("canceled"));
        var service = NewService(stub);

        await service.CancelSubscriptionAsync(SubId, CancellationToken.None);

        stub.CallCount.Should().Be(1);
        stub.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        stub.LastRequest.RequestUri!.AbsolutePath.Should().EndWith($"/v1/subscriptions/{SubId}");
    }

    [Fact]
    public async Task Resource_missing_is_absorbed_as_already_gone()
    {
        var stub = new StubHttpMessageHandler(
            HttpStatusCode.NotFound, ErrorJson("No such subscription.", code: "resource_missing"));
        var service = NewService(stub);

        var act = () => service.CancelSubscriptionAsync(SubId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        stub.CallCount.Should().Be(1, "already-gone needs no verification GET");
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("incomplete_expired")]
    public async Task Cancel_failure_with_terminal_live_status_is_absorbed(string terminalStatus)
    {
        // The stale-local-row case: our DB says active-ish, Stripe says the sub is already
        // terminal. Cancel errors (non-missing), the verification GET sees the terminal
        // status, and the call absorbs — otherwise account deletion would wedge forever.
        var stub = new StubHttpMessageHandler((req, _) =>
            req.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(ErrorJson("This subscription has been canceled."), System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SubscriptionJson(terminalStatus), System.Text.Encoding.UTF8, "application/json"),
                });
        var service = NewService(stub);

        var act = () => service.CancelSubscriptionAsync(SubId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        stub.CallCount.Should().Be(2, "the failure triggers exactly one verification GET");
    }

    [Fact]
    public async Task Cancel_failure_with_a_still_live_subscription_rethrows_the_original_error()
    {
        var stub = new StubHttpMessageHandler((req, _) =>
            req.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.PaymentRequired)
                {
                    Content = new StringContent(ErrorJson("Original cancel failure."), System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SubscriptionJson("active"), System.Text.Encoding.UTF8, "application/json"),
                });
        var service = NewService(stub);

        var act = () => service.CancelSubscriptionAsync(SubId, CancellationToken.None);

        (await act.Should().ThrowAsync<StripeException>())
            .Which.Message.Should().Contain("Original cancel failure",
                "the ORIGINAL cancel error must surface, not the verification result");
    }

    [Fact]
    public async Task Verification_failure_surfaces_the_original_cancel_error()
    {
        var stub = new StubHttpMessageHandler((req, _) =>
            req.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(ErrorJson("Original cancel failure."), System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(ErrorJson("Verification also failed."), System.Text.Encoding.UTF8, "application/json"),
                });
        var service = NewService(stub);

        var act = () => service.CancelSubscriptionAsync(SubId, CancellationToken.None);

        (await act.Should().ThrowAsync<StripeException>())
            .Which.Message.Should().Contain("Original cancel failure");
    }
}
