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
        // The old read-modify-write save could drop an increment when two workers raced; the
        // conditional ExecuteUpdate is a single server-side CASE WHEN, so both land.
        var orgId = await SeedOrgWithSubscriptionAsync(spend: 0m, anchor: JuneStart);

        await using var dbA = CreateSystemDb();
        await using var dbB = CreateSystemDb();
        await Task.WhenAll(
            CreateService(dbA, June15).RecordSpendAsync(orgId, 0.10m, default),
            CreateService(dbB, June15).RecordSpendAsync(orgId, 0.15m, default));

        await using var verify = CreateSystemDb();
        (await verify.Subscriptions.SingleAsync(s => s.OrganizationId == orgId))
            .ExtractionSpendThisMonthUsd.Should().Be(0.25m);
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
