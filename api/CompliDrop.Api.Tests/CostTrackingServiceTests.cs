using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for <see cref="CostTrackingService"/>'s lazy monthly reset (#256): the
/// "monthly" ceiling was a LIFETIME cap before — the counter only ever incremented, so an org
/// that ever crossed it had extraction killed forever. The reset is evaluated at read time
/// against the UTC-month anchor in <c>Subscription.SpendMonthStart</c>; no background job.
/// A fixed clock pins the month boundary deterministically.
/// </summary>
public sealed class CostTrackingServiceTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static readonly DateTimeOffset June15 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly JuneStart = new(2026, 6, 1);
    private static readonly DateOnly MayStart = new(2026, 5, 1);

    private CostTrackingService CreateService(Data.SystemDbContext db, DateTimeOffset now) =>
        new(db, Options.Create(new CostCeilings()), new FixedTimeProvider(now));

    private async Task<Guid> SeedOrgWithSubscriptionAsync(decimal spend, DateOnly anchor, string plan = "free")
    {
        var orgId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Plan = plan,
            Status = "active",
            ExtractionSpendThisMonthUsd = spend,
            SpendMonthStart = anchor,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    [Fact]
    public async Task Last_months_spend_does_not_count_against_this_months_ceiling()
    {
        // THE #256 regression: $100 spent in May (over both ceilings) must not block June.
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 100m, anchor: MayStart);

        await using var db = CreateSystemDb();
        (await CreateService(db, June15).CanSpendAsync(orgId, 0.01m, default)).Should().BeTrue(
            "a stale-month counter counts as zero — the ceiling is monthly, not lifetime");
    }

    [Fact]
    public async Task Pre_reset_lifetime_counters_are_forgiven()
    {
        // Rows written before #256 keep their lifetime total but get the always-stale
        // DateOnly.MinValue anchor from the migration default — extraction revives on deploy.
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 999m, anchor: DateOnly.MinValue);

        await using var db = CreateSystemDb();
        (await CreateService(db, June15).CanSpendAsync(orgId, 0.01m, default)).Should().BeTrue();
    }

    [Fact]
    public async Task Current_month_spend_counts_and_the_exact_ceiling_boundary_is_allowed()
    {
        // Free ceiling is $5: 4.99 + 0.01 == 5.00 → allowed (<=); 5.00 + 0.01 → blocked.
        var atBoundary = await SeedOrgWithSubscriptionAsync(spend: 4.99m, anchor: JuneStart);
        var overBoundary = await SeedOrgWithSubscriptionAsync(spend: 5.00m, anchor: JuneStart);

        await using var db = CreateSystemDb();
        var svc = CreateService(db, June15);
        (await svc.CanSpendAsync(atBoundary, 0.01m, default)).Should().BeTrue();
        (await svc.CanSpendAsync(overBoundary, 0.01m, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Paid_plan_uses_the_paid_ceiling()
    {
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 49.99m, anchor: JuneStart, plan: "pro");
        var overId = await SeedOrgWithSubscriptionAsync(spend: 50.00m, anchor: JuneStart, plan: "pro");

        await using var db = CreateSystemDb();
        var svc = CreateService(db, June15);
        (await svc.CanSpendAsync(orgId, 0.01m, default)).Should().BeTrue();
        (await svc.CanSpendAsync(overId, 0.01m, default)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordSpend_within_the_anchored_month_increments()
    {
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 1.00m, anchor: JuneStart);

        await using (var db = CreateSystemDb())
            await CreateService(db, June15).RecordSpendAsync(orgId, 0.25m, default);

        await using var verify = CreateSystemDb();
        var sub = await verify.Subscriptions.SingleAsync(s => s.OrganizationId == orgId);
        sub.ExtractionSpendThisMonthUsd.Should().Be(1.25m);
        sub.SpendMonthStart.Should().Be(JuneStart);
    }

    [Fact]
    public async Task RecordSpend_in_a_new_month_resets_the_counter_and_reanchors()
    {
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 4.99m, anchor: MayStart);

        await using (var db = CreateSystemDb())
            await CreateService(db, June15).RecordSpendAsync(orgId, 0.25m, default);

        await using var verify = CreateSystemDb();
        var sub = await verify.Subscriptions.SingleAsync(s => s.OrganizationId == orgId);
        sub.ExtractionSpendThisMonthUsd.Should().Be(0.25m, "May's spend is gone, June starts fresh");
        sub.SpendMonthStart.Should().Be(JuneStart);
    }

    [Fact]
    public async Task Concurrent_RecordSpends_do_not_lose_increments()
    {
        // The old read-modify-write save could drop an increment when workers raced; the
        // conditional ExecuteUpdate is a single server-side CASE WHEN, so all land. Ten
        // overlapping writers (each on its own connection) so a read-modify-write regression
        // loses at least one update with near-certainty instead of passing flakily.
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 0m, anchor: JuneStart);

        var contexts = Enumerable.Range(0, 10).Select(_ => CreateSystemDb()).ToList();
        try
        {
            await Task.WhenAll(contexts.Select(db =>
                CreateService(db, June15).RecordSpendAsync(orgId, 0.10m, default)));
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }

        await using var verify = CreateSystemDb();
        (await verify.Subscriptions.SingleAsync(s => s.OrganizationId == orgId))
            .ExtractionSpendThisMonthUsd.Should().Be(1.00m);
    }

    [Fact]
    public async Task A_stale_stamped_writer_cannot_roll_the_anchor_backwards()
    {
        // Month-boundary race (#256 review): a RecordSpend stamped just before the UTC month
        // flip that commits AFTER another instance re-anchored the row to the new month must
        // increment the new month's counter — not overwrite it and regress the anchor, which
        // would wipe counted spend and read as effective $0.
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 2.00m, anchor: JuneStart);

        var lateMayStamp = new DateTimeOffset(2026, 5, 31, 23, 59, 59, TimeSpan.Zero);
        await using (var db = CreateSystemDb())
            await CreateService(db, lateMayStamp).RecordSpendAsync(orgId, 0.25m, default);

        await using var verify = CreateSystemDb();
        var sub = await verify.Subscriptions.SingleAsync(s => s.OrganizationId == orgId);
        sub.SpendMonthStart.Should().Be(JuneStart, "the anchor is monotonic — never moves backwards");
        sub.ExtractionSpendThisMonthUsd.Should().Be(2.25m,
            "the boundary-straddling laggard's cents land in the newer month (safe direction)");
    }

    [Fact]
    public async Task Anchor_equality_is_year_aware()
    {
        // Kills the month-number-only mutant: an anchor from the SAME calendar month of a
        // PRIOR year is stale — it must not count against the ceiling, and a new spend must
        // reset rather than increment it.
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 100m, anchor: new DateOnly(2025, 6, 1));

        await using (var db = CreateSystemDb())
        {
            var svc = CreateService(db, June15);
            (await svc.CanSpendAsync(orgId, 0.01m, default)).Should().BeTrue(
                "June 2025 spend is not June 2026 spend");
            await svc.RecordSpendAsync(orgId, 0.25m, default);
        }

        await using var verify = CreateSystemDb();
        var sub = await verify.Subscriptions.SingleAsync(s => s.OrganizationId == orgId);
        sub.ExtractionSpendThisMonthUsd.Should().Be(0.25m);
        sub.SpendMonthStart.Should().Be(JuneStart);
    }

    [Fact]
    public async Task An_org_without_a_subscription_row_is_denied_and_RecordSpend_is_a_noop()
    {
        // CanSpend fails CLOSED on a missing row (the worker comment relies on it), and the
        // ExecuteUpdate-on-empty-set RecordSpend neither throws nor conjures a row.
        var orgId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var seed = CreateSystemDb())
        {
            seed.Organizations.Add(new Organization { Id = orgId, Name = "No-sub Org", CreatedAt = now, UpdatedAt = now });
            await seed.SaveChangesAsync();
        }

        await using var db = CreateSystemDb();
        var svc = CreateService(db, June15);
        (await svc.CanSpendAsync(orgId, 0.01m, default)).Should().BeFalse("no subscription row → fail closed");
        await svc.RecordSpendAsync(orgId, 0.25m, default);

        await using var verify = CreateSystemDb();
        (await verify.Subscriptions.AnyAsync(s => s.OrganizationId == orgId)).Should().BeFalse();
    }

    [Fact]
    public async Task Billing_endpoint_reports_effective_spend_not_the_raw_counter()
    {
        // The Settings tile is labeled "this month" — a stale-month counter must read as $0
        // there too, or the tile would show lifetime spend the gate no longer enforces.
        var auth = await RegisterAndLoginAsync();
        // The endpoint runs on the real clock, so derive "current" and "stale" from it.
        var currentMonthStart = CostTrackingService.MonthStart(DateOnly.FromDateTime(DateTime.UtcNow));
        var staleMonthStart = currentMonthStart.AddMonths(-1);

        await using (var db = CreateSystemDb())
            await db.Subscriptions.Where(s => s.OrganizationId == auth.OrgId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.ExtractionSpendThisMonthUsd, 7.77m)
                    .SetProperty(x => x.SpendMonthStart, staleMonthStart));

        var stale = await auth.Client.GetFromJsonAsync<JsonElement>("/api/billing/subscription");
        stale.GetProperty("data").GetProperty("extractionSpend").GetDecimal().Should().Be(0m,
            "a stale-month counter is not this month's spend");

        await using (var db = CreateSystemDb())
            await db.Subscriptions.Where(s => s.OrganizationId == auth.OrgId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SpendMonthStart, currentMonthStart));

        var current = await auth.Client.GetFromJsonAsync<JsonElement>("/api/billing/subscription");
        current.GetProperty("data").GetProperty("extractionSpend").GetDecimal().Should().Be(7.77m);
    }
}
