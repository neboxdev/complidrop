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

public class CostTrackingService(
    SystemDbContext db,
    IOptions<CostCeilings> ceilings) : ICostTrackingService
{
    public async Task<bool> CanSpendAsync(Guid organizationId, decimal plannedUsd, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        if (sub is null) return false;
        var ceiling = sub.Plan == "free"
            ? ceilings.Value.FreeTierMonthlyUsd
            : ceilings.Value.PaidTierMonthlyUsd;
        return sub.ExtractionSpendThisMonthUsd + plannedUsd <= ceiling;
    }

    public async Task RecordSpendAsync(Guid organizationId, decimal usd, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        if (sub is null) return;
        sub.ExtractionSpendThisMonthUsd += usd;
        sub.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
