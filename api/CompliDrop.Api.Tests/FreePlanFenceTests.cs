using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CompliDrop.Api.Tests.TestHelpers.UploadFixtures;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the #261 free-plan fences end to end. The decision of record (issue #261 comment,
/// 2026-06-12): the vendor portal is a paid entitlement gated on the
/// <c>Subscription.HasVendorPortal</c> FLAG (never the plan string, so a founder comp keeps
/// working); a lapsed plan kills existing links with the NEUTRAL revoked-link message (no
/// billing-status leak to vendors) and re-subscribing revives them untouched; the portal
/// upload enforces the org's <c>DocumentLimit</c> (mirroring the dashboard path) with
/// vendor-appropriate copy; <c>GET /status</c> stays ungated so accepted uploads remain
/// pollable.
///
/// Org-side gates (authenticated, friendly upgrade message) and portal-side gates (public,
/// neutral message) are both covered here so the whole fence lives in one reviewable file.
/// </summary>
public sealed class FreePlanFenceTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeEmailService Email =>
        (FakeEmailService)Fixture.Factory.Services.GetRequiredService<IEmailService>();

    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private static async Task<JsonElement> Error(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");

    private static async Task<Guid> CreateVendorAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/vendors", new
        {
            name,
            contactEmail = "ops@vendor.test",
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });
        resp.EnsureSuccessStatusCode();
        return (await Data(resp)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string token) =>
        client.PostAsync($"/api/portal/{token}/upload", UploadForm(PdfBytes(), "coi.pdf", "application/pdf"));

    /// <summary>Seeds an active document directly (the portal cap counts non-deleted docs).</summary>
    private async Task SeedDocumentAsync(Guid orgId, Guid? vendorId = null, DateTime? deletedAt = null)
    {
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "seed.pdf",
            BlobStorageUrl = "blob://seed",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync();
    }

    // ============================================================================================
    // Org-side gate: link generation + emailing require the portal entitlement
    // ============================================================================================

    [Fact]
    public async Task Free_org_cannot_generate_a_portal_link()
    {
        // The live defect this ticket exists for: a Free org generated and used a working
        // portal link while its Settings tile read "Vendor portal: Off".
        var auth = await RegisterAndLoginAsync(); // registers as Free: HasVendorPortal=false
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering");

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await Error(resp);
        error.GetProperty("code").GetString().Should().Be("plan.portal_not_included");
        // The message is the org-side friendly upgrade copy (decision of record) — it must
        // read like product copy, not HTTP jargon.
        error.GetProperty("message").GetString().Should().Contain("Pro").And.Contain("Upgrade");

        await using var db = CreateSystemDb();
        (await db.VendorPortalLinks.CountAsync(l => l.VendorId == vendorId))
            .Should().Be(0, "a refused generation must not mint a link row");
    }

    [Fact]
    public async Task Lapsed_org_cannot_email_a_link_minted_while_paid()
    {
        // Mint while entitled, then lapse (what StripeService's subscription-deleted handler
        // does): emailing the now-dead link must be refused — it would hand the vendor a link
        // the portal rejects.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering");
        await SetPortalEntitlementAsync(auth.OrgId, on: true);
        var mint = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link", null);
        mint.EnsureSuccessStatusCode();
        var linkId = (await Data(mint)).GetProperty("id").GetGuid();
        await SetPortalEntitlementAsync(auth.OrgId, on: false, documentLimit: 5);
        Email.Reset();

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Error(resp)).GetProperty("code").GetString().Should().Be("plan.portal_not_included");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Entitled_org_can_generate_and_email_a_portal_link()
    {
        // The gate must point the right way: granting the flag (what checkout does) unlocks
        // both halves. Guards against an inverted condition that would lock out paying orgs.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering");
        await SetPortalEntitlementAsync(auth.OrgId, on: true);
        Email.Reset();

        var mint = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link", null);
        mint.StatusCode.Should().Be(HttpStatusCode.OK);
        var linkId = (await Data(mint)).GetProperty("id").GetGuid();

        var email = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);
        email.StatusCode.Should().Be(HttpStatusCode.OK);
        Email.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task Cross_org_probe_by_a_free_org_still_answers_404_not_403()
    {
        // Ordering pin: the plan gate runs AFTER the vendor lookup, so a Free org probing
        // another org's vendor id gets the identical 404 it always did — the 403 must never
        // become a cross-tenant existence oracle.
        var orgA = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(orgA.Client, "Org A's vendor");
        var orgB = await RegisterAndLoginAsync(); // Free, no entitlement

        var resp = await orgB.Client.PostAsync($"/api/vendors/{vendorId}/portal-link", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Error(resp)).GetProperty("code").GetString().Should().Be("vendor.not_found");
    }

    // ============================================================================================
    // Portal-side gate: lapsed plans kill existing links, neutrally; revival on re-subscribe
    // ============================================================================================

    [Fact]
    public async Task Lapsed_plan_makes_existing_links_answer_the_neutral_revoked_message()
    {
        // Decision of record: feature off = links off, but the vendor sees exactly what a
        // revoked link shows — never "this business stopped paying".
        var seeded = await SeedLinkAsync(hasVendorPortal: false, documentLimit: 5);
        var client = CreateClient();

        var info = await client.GetAsync($"/api/portal/{seeded.Token}");
        info.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var infoError = await Error(info);
        infoError.GetProperty("code").GetString().Should().Be("vendor.portal_token_invalid");
        infoError.GetProperty("message").GetString().Should().Be("This upload link is no longer active.",
            "the lapsed-plan message must be byte-identical to the revoked-link message — any " +
            "divergence is a billing-status leak to vendors");
        var raw = await info.Content.ReadAsStringAsync();
        raw.Should().NotContainAny("plan", "billing", "subscription", "upgrade", "Pro");

        var upload = await UploadAsync(client, seeded.Token);
        upload.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Error(upload)).GetProperty("code").GetString().Should().Be("vendor.portal_token_invalid");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == seeded.OrgId))
            .Should().Be(0, "a lapsed org must not keep accumulating documents");
        (await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId)).UploadCount
            .Should().Be(0, "a refused upload must not consume link quota");
    }

    [Fact]
    public async Task Resubscribing_revives_the_same_links_untouched()
    {
        // The portal gate reads the flag at request time and never mutates link rows, so
        // flipping the flag back (what checkout does) revives the exact same token.
        var seeded = await SeedLinkAsync(hasVendorPortal: false, documentLimit: 5);
        var client = CreateClient();
        (await client.GetAsync($"/api/portal/{seeded.Token}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "sanity: gated while lapsed");

        await SetPortalEntitlementAsync(seeded.OrgId, on: true);

        var revived = await client.GetAsync($"/api/portal/{seeded.Token}");
        revived.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(revived)).GetProperty("vendorName").GetString().Should().Be(seeded.VendorName);

        (await UploadAsync(client, seeded.Token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lapsed_plan_with_an_expired_link_answers_404_not_410()
    {
        // Same ordering rationale as the dead-tenant guard: a 410 would acknowledge the token
        // was once valid for an org that no longer has the feature.
        var seeded = await SeedLinkAsync(
            hasVendorPortal: false, documentLimit: 5, expiresAt: DateTime.UtcNow.AddDays(-1));

        var info = await CreateClient().GetAsync($"/api/portal/{seeded.Token}");

        info.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Error(info)).GetProperty("code").GetString().Should().Be("vendor.portal_token_invalid");
    }

    [Fact]
    public async Task Missing_subscription_row_fails_closed_on_the_portal_path()
    {
        // Every org gets a Subscription row at registration; a link whose org somehow lacks
        // one is corrupt state and must not pass a pricing fence (fail-closed, not NRE).
        var seeded = await SeedLinkAsync(seedSubscription: false);
        var client = CreateClient();

        (await client.GetAsync($"/api/portal/{seeded.Token}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await UploadAsync(client, seeded.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Status_of_an_accepted_upload_stays_pollable_after_the_plan_lapses()
    {
        // The carve-out of record: the fence stops new intake, not status reads — a vendor who
        // uploaded minutes before the lapse must still see "we got it".
        var seeded = await SeedLinkAsync();
        var client = CreateClient();
        var uploadId = (await Data(await UploadAsync(client, seeded.Token))).GetProperty("uploadId").GetGuid();

        await SetPortalEntitlementAsync(seeded.OrgId, on: false, documentLimit: 5);

        (await client.GetAsync($"/api/portal/{seeded.Token}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "sanity: info is gated after the lapse");

        var status = await client.GetAsync($"/api/portal/{seeded.Token}/status/{uploadId}");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(status)).GetProperty("extractionStatus").GetString().Should().Be("Pending");
    }

    // ============================================================================================
    // Portal-side document cap: vendor uploads must not sail past the org's DocumentLimit
    // ============================================================================================

    [Fact]
    public async Task Portal_upload_at_the_document_cap_is_rejected_with_vendor_facing_copy()
    {
        // The second fence of #261: the dashboard 403s at the cap; the portal must too. The
        // copy faces the VENDOR (who can't upgrade anything) — it names the org and suggests
        // contacting them, never "upgrade your plan".
        var seeded = await SeedLinkAsync(hasVendorPortal: true, documentLimit: 2);
        await SeedDocumentAsync(seeded.OrgId);
        await SeedDocumentAsync(seeded.OrgId);
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await Error(resp);
        error.GetProperty("code").GetString().Should().Be("vendor.portal_document_limit_reached");
        error.GetProperty("message").GetString().Should()
            .Contain(seeded.OrgName, "the vendor needs to know WHO to contact")
            .And.NotContainAny("upgrade", "Upgrade", "plan", "Pro");

        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == seeded.OrgId))
            .Should().Be(2, "the capped-out upload must not persist a document");
        (await db.VendorPortalLinks.SingleAsync(l => l.Id == seeded.LinkId)).UploadCount
            .Should().Be(0, "the capped-out upload must not consume link quota");
    }

    [Fact]
    public async Task Portal_upload_under_the_document_cap_succeeds()
    {
        var seeded = await SeedLinkAsync(hasVendorPortal: true, documentLimit: 2);
        await SeedDocumentAsync(seeded.OrgId);
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Documents.IgnoreQueryFilters().CountAsync(
                d => d.OrganizationId == seeded.OrgId && d.DeletedAt == null))
            .Should().Be(2);
    }

    [Fact]
    public async Task Soft_deleted_documents_do_not_count_toward_the_portal_cap()
    {
        // Mirrors the dashboard's count (DeletedAt == null): deleting a document frees a slot
        // for vendor uploads exactly as it does for dashboard uploads.
        var seeded = await SeedLinkAsync(hasVendorPortal: true, documentLimit: 1);
        await SeedDocumentAsync(seeded.OrgId, deletedAt: DateTime.UtcNow);
        var client = CreateClient();

        var resp = await UploadAsync(client, seeded.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Comped_org_with_portal_but_a_document_cap_is_capped_not_gated()
    {
        // The two fences are independent: HasVendorPortal=true with a finite DocumentLimit
        // (a comp or a future capped tier) serves the portal but still enforces the cap —
        // pins that neither check shadows the other.
        var seeded = await SeedLinkAsync(hasVendorPortal: true, documentLimit: 1);
        var client = CreateClient();

        (await client.GetAsync($"/api/portal/{seeded.Token}")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the portal itself is entitled");

        (await UploadAsync(client, seeded.Token)).StatusCode
            .Should().Be(HttpStatusCode.OK, "one slot is free");

        var second = await UploadAsync(client, seeded.Token);
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Error(second)).GetProperty("code").GetString().Should().Be("vendor.portal_document_limit_reached");
    }
}
