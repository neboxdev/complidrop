using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// #327 / ADR 0033: "latest document per (vendor, type) wins" supersession applied to the EXPIRED
/// liability. A renewed cert's old expired copy must stop inflating the dashboard Expired count, the
/// expiry-pipeline expired bucket, and the deep-linked documents list — all three agreeing — and a
/// superseded doc must not trigger a reminder. The audit export keeps but annotates superseded docs.
/// </summary>
public sealed class DocumentSupersessionTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<Guid> SeedVendorAsync(Guid orgId)
    {
        var vendorId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = $"V-{vendorId:N}",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return vendorId;
    }

    private async Task<Guid> SeedDocAsync(
        Guid orgId, Guid? vendorId, string docType, DateTime expiration, DateTime createdAt)
    {
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = orgId, VendorId = vendorId,
            OriginalFileName = $"doc-{docId:N}.pdf", BlobStorageUrl = "blob://d", FileSizeBytes = 1,
            ContentType = "application/pdf", DocumentType = docType,
            ComplianceStatus = ComplianceStatus.Compliant, ExpirationDate = expiration,
            ExtractionStatus = ExtractionStatus.Completed,
            CreatedAt = createdAt, UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return docId;
    }

    private static async Task<int> ExpiredStat(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats"))
            .GetProperty("data").GetProperty("expired").GetInt32();

    private static async Task<int> ExpiredPipeline(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/dashboard/expiry-pipeline"))
            .GetProperty("data").GetProperty("expired").GetInt32();

    private static async Task<Guid[]> ExpiredListIds(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/documents/?status=Expired"))
            .GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task A_renewed_cert_supersedes_its_old_expired_copy_dashboard_pipeline_and_list_agree()
    {
        // The headline acceptance: an old expired COI renewed by a newer (also-expired) cert must count
        // ONCE (the latest), and the dashboard count must equal the deep-linked list — not "1 vs 2".
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var past = DateTime.UtcNow.Date.AddDays(-2);
        var t0 = DateTime.UtcNow.AddDays(-100);
        var old = await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0);
        var renewed = await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0.AddDays(10));

        (await ExpiredStat(auth.Client)).Should().Be(1, "only the latest cert is a current Expired liability");
        (await ExpiredPipeline(auth.Client)).Should().Be(1);
        var listIds = await ExpiredListIds(auth.Client);
        listIds.Should().BeEquivalentTo(new[] { renewed },
            "the deep-linked Expired list must match the dashboard count exactly — the latest, not the old copy");
        listIds.Should().NotContain(old);
    }

    [Fact]
    public async Task A_renewal_with_a_future_expiry_clears_the_expired_liability_entirely()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var t0 = DateTime.UtcNow.AddDays(-100);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: DateTime.UtcNow.Date.AddDays(-2), createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: DateTime.UtcNow.Date.AddDays(300), createdAt: t0.AddDays(10));

        (await ExpiredStat(auth.Client)).Should().Be(0, "the future-dated renewal covers the requirement — no current expired liability");
        (await ExpiredListIds(auth.Client)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_document_with_no_vendor_is_never_superseded()
    {
        var auth = await RegisterAndLoginAsync();
        var past = DateTime.UtcNow.Date.AddDays(-2);
        var t0 = DateTime.UtcNow.AddDays(-100);
        await SeedDocAsync(auth.OrgId, vendorId: null, "coi", expiration: past, createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId: null, "coi", expiration: past, createdAt: t0.AddDays(10));

        (await ExpiredStat(auth.Client)).Should().Be(2, "no vendor means no requirement group — neither is superseded");
    }

    [Fact]
    public async Task Different_types_for_one_vendor_do_not_supersede_each_other()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var past = DateTime.UtcNow.Date.AddDays(-2);
        var t0 = DateTime.UtcNow.AddDays(-100);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId, "license", expiration: past, createdAt: t0.AddDays(10));

        (await ExpiredStat(auth.Client)).Should().Be(2, "a COI and a license are different requirements — neither supersedes the other");
    }
}
