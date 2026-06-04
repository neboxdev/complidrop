using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the authenticated vendor management endpoints
/// (<see cref="CompliDrop.Api.Endpoints.VendorEndpoints"/>), focused on the #190
/// "email the upload link to the vendor" path. The send goes through the in-memory
/// <see cref="FakeEmailService"/> (the real Resend service self-disables with no API key),
/// so each test resets the fake AFTER arrangement to drop the registration verification
/// email and isolate the portal-invite send.
/// </summary>
public sealed class VendorEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeEmailService Email =>
        (FakeEmailService)Fixture.Factory.Services.GetRequiredService<IEmailService>();

    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private static async Task<string?> ErrorCode(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString();

    private static async Task<Guid> CreateVendorAsync(HttpClient client, string name, string? contactEmail)
    {
        var resp = await client.PostAsJsonAsync("/api/vendors", new
        {
            name,
            contactEmail,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });
        resp.EnsureSuccessStatusCode();
        return (await Data(resp)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid LinkId, string Token, string Url)> GenerateLinkAsync(HttpClient client, Guid vendorId)
    {
        var resp = await client.PostAsync($"/api/vendors/{vendorId}/portal-link", null);
        resp.EnsureSuccessStatusCode();
        var data = await Data(resp);
        return (data.GetProperty("id").GetGuid(), data.GetProperty("token").GetString()!, data.GetProperty("url").GetString()!);
    }

    [Fact]
    public async Task Email_portal_link_sends_to_contact_email_and_returns_recipient()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", "ops@acme.test");
        var (linkId, _, linkUrl) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset(); // drop the registration verification email so we assert only the portal send

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(resp)).GetProperty("sentTo").GetString().Should().Be("ops@acme.test");

        Email.Sends.Should().ContainSingle();
        var sent = Email.Sends.Single();
        sent.ToEmail.Should().Be("ops@acme.test");
        sent.HtmlBody.Should().Contain(linkUrl, "the actual portal upload link must be in the email body");
        // Org name personalises the subject; RegisterAndLoginAsync registers companyName "Test Co".
        sent.Subject.Should().Contain("Test Co");
    }

    [Fact]
    public async Task Email_portal_link_without_contact_email_is_400_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "No Contact LLC", contactEmail: null);
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("vendor.no_contact_email");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_with_unknown_link_is_404_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        Email.Reset();

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{Guid.NewGuid()}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendorPortalLink.not_found");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_when_delivery_returns_null_is_502()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();
        Email.NextSendReturnsNull = true; // simulate Resend non-2xx → SendAsync returns null

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ErrorCode(resp)).Should().Be("email.send_failed");
    }

    [Fact]
    public async Task Email_portal_link_when_send_throws_timeout_is_502_not_500()
    {
        // The "resend" HttpClient has a 30s timeout → a hung send throws TaskCanceledException
        // (an OperationCanceledException whose token is the client's internal timeout, NOT the
        // request ct). The endpoint must catch it and return the friendly 502, not let it escape
        // as an unhandled 500. Gating the catch on `!ct.IsCancellationRequested` (rather than
        // `ex is not OperationCanceledException`) is what makes this pass.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();
        Email.NextSendThrows = new TaskCanceledException("simulated 30s Resend timeout");

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ErrorCode(resp)).Should().Be("email.send_failed");
    }

    [Fact]
    public async Task Email_portal_link_when_email_not_configured_is_503_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();
        Email.IsEnabled = false; // Resend not configured (no API key / from-email)

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorCode(resp)).Should().Be("email.not_configured");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_for_revoked_link_is_400_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        (await auth.Client.DeleteAsync($"/api/vendors/{vendorId}/portal-link/{linkId}")).EnsureSuccessStatusCode();
        Email.Reset();

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("vendorPortalLink.inactive");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_is_tenant_scoped()
    {
        // Org A owns the vendor + link; Org B must not be able to email it.
        var orgA = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(orgA.Client, "Acme", "ops@acme.test");
        var (linkId, _, _) = await GenerateLinkAsync(orgA.Client, vendorId);

        var orgB = await RegisterAndLoginAsync();
        Email.Reset();

        var resp = await orgB.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        // Org B can't see Org A's vendor through the tenant filter → 404, nothing sent.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendor.not_found");
        Email.Sends.Should().BeEmpty();
    }
}
