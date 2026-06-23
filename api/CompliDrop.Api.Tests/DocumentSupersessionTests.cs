using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
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
        Guid orgId, Guid? vendorId, string docType, DateTime? expiration, DateTime createdAt, DateTime? deletedAt = null)
    {
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = orgId, VendorId = vendorId,
            OriginalFileName = $"doc-{docId:N}.pdf", BlobStorageUrl = "blob://d", FileSizeBytes = 1,
            ContentType = "application/pdf", DocumentType = docType,
            // A null expiry models a still-processing / no-expiry-extracted upload: ExpirationDate unset and
            // ExtractionStatus Pending so the row is realistic (the supersession predicate keys off expiry).
            ComplianceStatus = ComplianceStatus.Compliant, ExpirationDate = expiration,
            ExtractionStatus = expiration is null ? ExtractionStatus.Pending : ExtractionStatus.Completed,
            CreatedAt = createdAt, UpdatedAt = createdAt, DeletedAt = deletedAt,
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

    private static async Task<int> ExpiringSoonStat(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats"))
            .GetProperty("data").GetProperty("expiringSoon").GetInt32();

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
    public async Task A_doc_superseded_only_by_a_soft_deleted_newer_doc_is_current_again()
    {
        // Load-bearing correctness: supersession runs over the SOFT-DELETE-filtered document set, so a
        // newer cert that was deleted must NOT keep de-counting the old one. (ADR 0033: "a deleted doc
        // never counts as the superseder.")
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var past = DateTime.UtcNow.Date.AddDays(-2);
        var t0 = DateTime.UtcNow.AddDays(-100);
        var old = await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0.AddDays(10),
            deletedAt: DateTime.UtcNow); // the only would-be superseder is deleted

        (await ExpiredStat(auth.Client)).Should().Be(1, "its only superseder is soft-deleted — the old cert is current again");
        (await ExpiredListIds(auth.Client)).Should().BeEquivalentTo(new[] { old });
    }

    [Fact]
    public async Task A_tie_on_created_at_supersedes_neither_both_count()
    {
        // Strict `CreatedAt >` — two certs uploaded at the SAME instant don't supersede each other, so
        // both count. Pins the strict-greater semantic (a future switch to >= would silently drop one).
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var past = DateTime.UtcNow.Date.AddDays(-2);
        var t = DateTime.UtcNow.AddDays(-50);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t);

        (await ExpiredStat(auth.Client)).Should().Be(2, "an exact CreatedAt tie supersedes neither");
    }

    [Fact]
    public async Task With_three_certs_in_a_group_only_the_newest_is_current()
    {
        // Not just the immediately-prior cert — EVERY older cert in the group is superseded by the newest.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var past = DateTime.UtcNow.Date.AddDays(-2);
        var t0 = DateTime.UtcNow.AddDays(-100);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0.AddDays(10));
        var newest = await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: past, createdAt: t0.AddDays(20));

        (await ExpiredStat(auth.Client)).Should().Be(1, "only the newest of three certs is a current liability");
        (await ExpiredListIds(auth.Client)).Should().BeEquivalentTo(new[] { newest });
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

    [Fact]
    public async Task A_still_processing_renewal_does_not_supersede_an_expired_cert()
    {
        // COMPLIANCE-SAFETY (#327 re-review / ADR 0033 Amendment 1): a vendor re-uploads after their COI
        // expired, but the new file is still extracting (ExpirationDate null). It must NOT supersede the old
        // expired cert — otherwise a genuinely-unmet liability would silently vanish from the dashboard
        // during the extraction window (or forever, if the upload never yields an expiry).
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var t0 = DateTime.UtcNow.AddDays(-100);
        var old = await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: DateTime.UtcNow.Date.AddDays(-2), createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: null, createdAt: t0.AddDays(10)); // still processing

        (await ExpiredStat(auth.Client)).Should().Be(1, "a still-processing renewal has no expiry, so it can't supersede a real expired liability");
        (await ExpiredPipeline(auth.Client)).Should().Be(1);
        (await ExpiredListIds(auth.Client)).Should().BeEquivalentTo(new[] { old },
            "the deep-linked list must still show the expired cert — the dashboard count and the list agree on showing it");
    }

    [Fact]
    public async Task A_newer_upload_that_expires_earlier_does_not_supersede_the_old_cert()
    {
        // Coverage-extension guard: a later upload that expires EARLIER than the cert it would replace
        // extends no coverage, so it can't de-count the old expired cert. Both remain current liabilities.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var t0 = DateTime.UtcNow.AddDays(-100);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: DateTime.UtcNow.Date.AddDays(-2), createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: DateTime.UtcNow.Date.AddDays(-30), createdAt: t0.AddDays(10));

        (await ExpiredStat(auth.Client)).Should().Be(2, "the later upload expires earlier — it extends no coverage and supersedes nothing");
    }

    [Fact]
    public async Task The_export_supersession_mirror_matches_the_db_predicate()
    {
        // The audit export computes supersession in memory (ExportService.SupersededIds) while the live
        // surfaces use the DB EXISTS (DocumentSupersession.IsSuperseded). They MUST flag the same docs or
        // the CSV/PDF annotation would disagree with the dashboard. This seed stresses every branch: a
        // future renewal, a still-processing (null) upload, an earlier→later-expiry license pair, a
        // CreatedAt tie, a soft-deleted would-be superseder, and a no-vendor doc.
        var auth = await RegisterAndLoginAsync();
        var v1 = await SeedVendorAsync(auth.OrgId);
        var v2 = await SeedVendorAsync(auth.OrgId);
        var t0 = DateTime.UtcNow.AddDays(-100);
        var pastA = DateTime.UtcNow.Date.AddDays(-20);
        var pastB = DateTime.UtcNow.Date.AddDays(-5);
        var future = DateTime.UtcNow.Date.AddDays(200);

        await SeedDocAsync(auth.OrgId, v1, "coi", expiration: pastA, createdAt: t0);                // superseded by the future renewal
        await SeedDocAsync(auth.OrgId, v1, "coi", expiration: future, createdAt: t0.AddDays(10));   // current (latest, extends coverage)
        await SeedDocAsync(auth.OrgId, v1, "coi", expiration: null, createdAt: t0.AddDays(20));     // still-processing — supersedes nothing, never superseded
        await SeedDocAsync(auth.OrgId, v1, "license", expiration: pastA, createdAt: t0);            // superseded by the later, later-expiry license
        await SeedDocAsync(auth.OrgId, v1, "license", expiration: pastB, createdAt: t0.AddDays(5)); // current
        await SeedDocAsync(auth.OrgId, v2, "coi", expiration: pastA, createdAt: t0);                // tie pair — neither supersedes
        await SeedDocAsync(auth.OrgId, v2, "coi", expiration: pastA, createdAt: t0);
        await SeedDocAsync(auth.OrgId, v2, "coi", expiration: pastB, createdAt: t0.AddDays(10), deletedAt: DateTime.UtcNow); // deleted would-be superseder
        await SeedDocAsync(auth.OrgId, vendorId: null, "coi", expiration: pastA, createdAt: t0.AddDays(30)); // no vendor — never superseded

        await using var db = CreateSystemDb();
        var loaded = await db.Documents
            .Where(d => d.OrganizationId == auth.OrgId && d.DeletedAt == null)
            .ToListAsync();
        var memory = ExportService.SupersededIds(loaded);
        var dbSet = await db.Documents
            .Where(d => d.OrganizationId == auth.OrgId)
            .Where(DocumentSupersession.IsSuperseded(db.Documents))
            .Select(d => d.Id)
            .ToListAsync();

        memory.Should().BeEquivalentTo(dbSet,
            "the in-memory export mirror must flag exactly the docs the DB predicate flags — a soft-deleted superseder counts in neither");
        memory.Should().NotBeEmpty("the seed includes genuinely superseded docs, so the agreement isn't vacuously true");
    }

    [Fact]
    public async Task Expiring_soon_is_not_de_superseded_unlike_the_expired_bucket()
    {
        // ADR 0033 scopes supersession to the EXPIRED liability ONLY. The expiringSoon tally (and the future
        // pipeline buckets) deliberately keep every doc — a renew-early vendor legitimately shows both the
        // soon-expiring and the far cert. Two coverage-extending certs both within 30 days must count as
        // TWO; if supersession were (wrongly) applied here, the older would be de-counted to one.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorAsync(auth.OrgId);
        var t0 = DateTime.UtcNow.AddDays(-100);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: DateTime.UtcNow.Date.AddDays(5), createdAt: t0);
        await SeedDocAsync(auth.OrgId, vendorId, "coi", expiration: DateTime.UtcNow.Date.AddDays(20), createdAt: t0.AddDays(10));

        (await ExpiringSoonStat(auth.Client)).Should().Be(2, "expiringSoon is informational and not de-superseded — both certs show");
        (await ExpiredStat(auth.Client)).Should().Be(0, "neither cert is expired");
    }
}
