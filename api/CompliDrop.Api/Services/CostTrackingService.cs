using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services;

public interface ICostTrackingService
{
    Task<bool> CanSpendAsync(Guid organizationId, decimal plannedUsd, CancellationToken ct);
    Task RecordSpendAsync(Guid organizationId, decimal usd, CancellationToken ct);
}

/// <summary>
/// Per-org extraction budget with a LAZY monthly reset (#256): the spend counter is valid only
/// for the UTC calendar month anchored in <see cref="Subscription.SpendMonthStart"/> — a row
/// anchored to any other month carries a stale counter that counts as zero. No background job
/// flips anything; the boundary is evaluated at read time and the counter re-anchors on the
/// first spend of a new month. UTC (not org-local) months on purpose: the ceiling protects the
/// company's AI bill, it is not a per-tenant billing promise, and one unambiguous boundary
/// beats 40 time-zone-shifted ones.
/// </summary>
public class CostTrackingService(
    SystemDbContext db,
    IOptions<CostCeilings> ceilings,
    TimeProvider timeProvider) : ICostTrackingService
{
    /// <summary>First day of the UTC month <paramref name="utcToday"/> falls in.</summary>
    public static DateOnly MonthStart(DateOnly utcToday) => new(utcToday.Year, utcToday.Month, 1);

    /// <summary>
    /// The spend that actually counts against this month's ceiling: the stored counter unless it
    /// is anchored to a PAST month (stale — including the lifetime totals accumulated before
    /// #256), which reads as zero. A future-anchored counter (possible only under clock skew
    /// between instances) still counts: over-enforcing the ceiling is the safe direction for a
    /// spend control. Shared with the billing endpoint so the Settings "this month" tile shows
    /// the same number the gate enforces.
    /// </summary>
    public static decimal EffectiveSpend(Subscription sub, DateOnly utcToday) =>
        sub.SpendMonthStart >= MonthStart(utcToday) ? sub.ExtractionSpendThisMonthUsd : 0m;

    public async Task<bool> CanSpendAsync(Guid organizationId, decimal plannedUsd, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        if (sub is null) return false;
        var ceiling = sub.Plan == "free"
            ? ceilings.Value.FreeTierMonthlyUsd
            : ceilings.Value.PaidTierMonthlyUsd;
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        return EffectiveSpend(sub, today) + plannedUsd <= ceiling;
    }

    public async Task RecordSpendAsync(Guid organizationId, decimal usd, CancellationToken ct)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var monthStart = MonthStart(DateOnly.FromDateTime(nowUtc));

        // One atomic conditional UPDATE: increment when the row is anchored to the current (or a
        // newer) month, otherwise overwrite with this spend and re-anchor (the lazy reset).
        // ExecuteUpdate (CASE WHEN server-side) instead of read-modify-write so two concurrent
        // workers can't lose an increment — the old tracked-entity save raced exactly that way.
        // The anchor is MONOTONIC (>= / never moves backwards): a writer that stamped its month
        // just before a UTC month flip but commits after another instance already re-anchored
        // the row must INCREMENT the new month's counter, not wipe it by re-anchoring to the
        // old month — a boundary-straddling laggard's cents land in the newer month, which is
        // the safe direction for a spend control (#256 review).
        // ExecuteUpdate bypasses the audit interceptor, which is the pre-existing behavior
        // anyway for this write (background scope has no current user → no audit row);
        // UpdatedAt is set manually for the same reason. A missing subscription row updates
        // nothing — same as before.
        await db.Subscriptions
            .Where(s => s.OrganizationId == organizationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ExtractionSpendThisMonthUsd,
                    x => x.SpendMonthStart >= monthStart ? x.ExtractionSpendThisMonthUsd + usd : usd)
                .SetProperty(x => x.SpendMonthStart,
                    x => x.SpendMonthStart >= monthStart ? x.SpendMonthStart : monthStart)
                .SetProperty(x => x.UpdatedAt, nowUtc), ct);
    }
}
