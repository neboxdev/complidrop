using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// End-to-end HTTP tests for the future-effective coverage-gap fix (#362 / ADR 0041): a certificate that
/// is not yet in force (EffectiveDate a date strictly after today) must NOT read Compliant/ExpiringSoon
/// today — it reads Pending on every surface (detail badge, list filter + badge, dashboard counts, vendor
/// rollup) so the product never asserts present-tense coverage that isn't in force. Expired still wins and
/// a hard fail is never masked. Docs are seeded with a stored verdict + a future EffectiveDate so the
/// read-overlay demotion is what's under test.
/// </summary>
public sealed class FutureEffectiveCoverageGapTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<Guid> SeedDocAsync(
        Guid orgId, ComplianceStatus stored, DateTime? expiration, DateTime? effective,
        Guid? vendorId = null, string docType = "coi", DateTime? createdAt = null)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = $"doc-{docId:N}.pdf",
            BlobStorageUrl = "blob://d",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = docType,
            ComplianceStatus = stored,
            ExpirationDate = expiration,
            EffectiveDate = effective,
            ExtractionStatus = ExtractionStatus.Completed,
            CreatedAt = createdAt ?? now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return docId;
    }

    // Seeds a vendor on a checklist that requires a COI, so ListVendors rolls up coverage.
    private async Task<Guid> SeedVendorRequiringCoiAsync(Guid orgId)
    {
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = orgId, Name = "T", CreatedAt = now });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "1000000", SortOrder = 0
        });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = "V", ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return vendorId;
    }

    private static async Task<string> DetailStatusAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/documents/{id}"))
            .GetProperty("data").GetProperty("complianceStatus").GetString()!;

    private static async Task<Guid[]> ListIdsAsync(HttpClient client, string status) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/documents/?status={status}"))
            .GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private static async Task<JsonElement> StatsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/dashboard/stats")).GetProperty("data");

    private static DateTime FarFuture => DateTime.UtcNow.Date.AddDays(300);
    private static DateTime NextMonth => DateTime.UtcNow.Date.AddDays(30);

    [Fact]
    public async Task A_standalone_future_effective_compliant_doc_reads_Pending_today()
    {
        // AC (a): the narrow standalone case the owner comment flags — a vendor's FIRST-ever policy starts
        // next month. It passed every rule (stored Compliant) but is not in force today, so it reads Pending
        // on the detail badge, appears under ?status=Pending (not ?status=Compliant), and the dashboard
        // counts it in NEITHER compliant NOR expiringSoon.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture, effective: NextMonth);

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Pending",
            "a future-effective compliant doc is not yet in force — it reads Pending, not Compliant");
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(id);
        (await ListIdsAsync(auth.Client, "Compliant")).Should().NotContain(id);

        var stats = await StatsAsync(auth.Client);
        stats.GetProperty("compliant").GetInt32().Should().Be(0, "no coverage is in force today");
        stats.GetProperty("expiringSoon").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task The_verdict_self_heals_the_day_the_policy_takes_effect()
    {
        // AC (f), end to end across the effective boundary: two docs with the SAME stored Compliant verdict,
        // one effective yesterday (in force → reads Compliant) and one effective tomorrow (not in force →
        // reads Pending). The demotion is a pure read overlay driven by today, so the doc self-heals the
        // instant the calendar reaches its EffectiveDate — no re-evaluation needed.
        var auth = await RegisterAndLoginAsync();
        var today = DateTime.UtcNow.Date;
        var inForce = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture,
            effective: today.AddDays(-1));
        var notYet = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture,
            effective: today.AddDays(1));

        // Read BOTH badges, then guard the ±1-day effective boundary against a UTC-midnight rollover: the
        // handlers read DateTime.UtcNow.Date, so if the day advanced mid-test the notYet doc (effective
        // today+1) would flip to in-force and spuriously fail. The deterministic self-heal boundary is
        // pinned by the pure deriver test; this end-to-end straddle just skips the ppm rollover window.
        var inForceStatus = await DetailStatusAsync(auth.Client, inForce);
        var notYetStatus = await DetailStatusAsync(auth.Client, notYet);
        if (DateTime.UtcNow.Date != today) return;

        inForceStatus.Should().Be("Compliant", "effective yesterday → in force");
        notYetStatus.Should().Be("Pending", "effective tomorrow → not yet in force");
    }

    [Fact]
    public async Task A_future_effective_doc_that_fails_its_rules_stays_NonCompliant()
    {
        // AC (d): a not-yet-active deficient cert is accurately not-compliant — never masked to Pending.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.NonCompliant, expiration: FarFuture, effective: NextMonth);

        (await DetailStatusAsync(auth.Client, id)).Should().Be("NonCompliant");
        (await ListIdsAsync(auth.Client, "NonCompliant")).Should().Contain(id);
        (await ListIdsAsync(auth.Client, "Pending")).Should().NotContain(id);
        (await StatsAsync(auth.Client)).GetProperty("nonCompliant").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Expired_wins_for_a_malformed_future_effective_but_already_expired_doc()
    {
        // AC (e): a malformed cert (EffectiveDate after today AND ExpirationDate before today). Expired is
        // top precedence — it must read Expired, never Pending.
        var auth = await RegisterAndLoginAsync();
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant,
            expiration: DateTime.UtcNow.Date.AddDays(-1), effective: NextMonth);

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Expired");
        (await ListIdsAsync(auth.Client, "Expired")).Should().Contain(id);
        (await ListIdsAsync(auth.Client, "Pending")).Should().NotContain(id);
    }

    [Fact]
    public async Task Dashboard_compliant_count_equals_the_deep_linked_list_with_a_future_effective_doc()
    {
        // AC (g): the headline consistency contract (dashboard count == deep-linked ?status= list),
        // extended to the future-effective case. One in-force compliant + one future-effective compliant:
        // the dashboard shows compliant == 1, the ?status=Compliant list returns exactly that one doc, and
        // the future-effective doc lands under ?status=Pending — count and list never disagree.
        var auth = await RegisterAndLoginAsync();
        var inForce = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture, effective: null);
        var future = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture, effective: NextMonth);

        var stats = await StatsAsync(auth.Client);
        var compliantList = await ListIdsAsync(auth.Client, "Compliant");

        stats.GetProperty("compliant").GetInt32().Should().Be(compliantList.Length,
            "the dashboard compliant count must equal the deep-linked Compliant list length");
        compliantList.Should().BeEquivalentTo(new[] { inForce });
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(future).And.NotContain(inForce);
    }

    [Fact]
    public async Task The_compliance_rate_excludes_a_future_effective_doc_from_the_denominator()
    {
        // A future-effective doc reads Pending, so — like any Pending doc (#318) — it is excluded from the
        // compliance-rate denominator, not counted as a graded non-compliant one. One in-force Compliant +
        // one future-effective Compliant → 100% (1 of 1 graded), never a misleading 50%.
        var auth = await RegisterAndLoginAsync();
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture, effective: null);
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture, effective: NextMonth);

        (await StatsAsync(auth.Client)).GetProperty("complianceRate").GetDouble().Should().Be(100.0,
            "the not-yet-in-force doc is treated as Pending, excluded from the rate denominator");
    }

    [Fact]
    public async Task A_vendor_whose_only_cert_is_future_effective_is_not_Covered()
    {
        // Vendor coverage rollup: a required COI whose latest doc is not yet in force provides no coverage
        // today, so the vendor reads ActionNeeded, not Covered.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorRequiringCoiAsync(auth.OrgId);
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: FarFuture, effective: NextMonth, vendorId: vendorId);

        var vendors = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/vendors"))
            .GetProperty("data").EnumerateArray().ToArray();
        var vendor = vendors.Single(v => v.GetProperty("id").GetGuid() == vendorId);
        vendor.GetProperty("coverage").GetProperty("status").GetString().Should().Be("ActionNeeded",
            "a future-effective cert is not coverage in force — the vendor needs action");
    }

    [Fact]
    public async Task A_vendor_covered_by_an_in_force_cert_stays_Covered_when_a_future_effective_renewal_is_pre_uploaded()
    {
        // #362 review (CONFIRMED BUG): the coverage rollup must judge a required type by its best
        // CURRENTLY-IN-FORCE cert, not strictly the newest upload. A vendor covered today by an in-force
        // earlier COI (A) who PRE-UPLOADS next year's renewal (B, effective the day A lapses) must stay
        // Covered — B reads Pending (not yet in force, ADR 0041), but A still provides coverage today.
        // Basing coverage on the newest upload alone (B → Pending) wrongly flipped this vendor to
        // ActionNeeded. The textbook insurance-renewal flow must never downgrade a still-covered vendor.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorRequiringCoiAsync(auth.OrgId);
        var today = DateTime.UtcNow.Date;

        // A: in force ~a year, expires in 31 days (just OUTSIDE the 30-day window → reads plain Compliant),
        // uploaded first (earlier CreatedAt).
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: today.AddDays(31),
            effective: today.AddDays(-365), vendorId: vendorId, createdAt: DateTime.UtcNow.AddMinutes(-10));
        // B: renewal effective the day A lapses (future → reads Pending), expires far out, uploaded LAST
        // (later CreatedAt) — so the pre-fix "newest upload only" rollup consulted B and returned ActionNeeded.
        await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant, expiration: today.AddDays(396),
            effective: today.AddDays(31), vendorId: vendorId, createdAt: DateTime.UtcNow);

        var vendors = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/vendors"))
            .GetProperty("data").EnumerateArray().ToArray();
        var coverage = vendors.Single(v => v.GetProperty("id").GetGuid() == vendorId).GetProperty("coverage");
        coverage.GetProperty("status").GetString().Should().Be("Covered",
            "the in-force earlier cert still covers the vendor today — a pre-uploaded future renewal must not downgrade it");
        // Honest horizon: covered THROUGH the in-force cert's expiry (day 31), never the not-yet-in-force
        // renewal's far-future date — a doc that isn't in force can't extend the coverage horizon.
        coverage.GetProperty("coveredThrough").GetDateTime().Date.Should().Be(today.AddDays(31));
    }

    [Fact]
    public async Task A_future_effective_doc_expiring_within_the_window_is_excluded_from_ExpiringSoon_everywhere()
    {
        // #362 review S1: a future-effective cert whose expiry falls INSIDE the 30-day ExpiringSoon window
        // (effective in 5 days, expiring in 20) reads Pending — not yet in force — so it must be excluded
        // from the dashboard expiringSoon count AND the ?status=ExpiringSoon list, and instead surface under
        // ?status=Pending with a Pending detail badge. This is the ExpiringSoon mirror of the pinned
        // Compliant surface: it exercises the effective-date exclusion on DashboardEndpoints (the
        // expiringSoon count) and DocumentEndpoints (the ExpiringSoon list arm) end-to-end. Dropping that
        // clause is exactly the #294-class count-vs-badge split reviewers.md calls a real finding.
        var auth = await RegisterAndLoginAsync();
        var today = DateTime.UtcNow.Date;
        var id = await SeedDocAsync(auth.OrgId, ComplianceStatus.Compliant,
            expiration: today.AddDays(20), effective: today.AddDays(5));

        (await DetailStatusAsync(auth.Client, id)).Should().Be("Pending",
            "a not-yet-in-force cert can't assert 'about to lapse' — it reads Pending, not ExpiringSoon");
        (await ListIdsAsync(auth.Client, "ExpiringSoon")).Should().NotContain(id,
            "the ?status=ExpiringSoon list must exclude a not-yet-in-force doc");
        (await ListIdsAsync(auth.Client, "Pending")).Should().Contain(id,
            "the future-effective doc surfaces under ?status=Pending instead");

        (await StatsAsync(auth.Client)).GetProperty("expiringSoon").GetInt32().Should().Be(0,
            "the future-effective doc is not yet in force — it must not inflate the ExpiringSoon count");
    }
}
