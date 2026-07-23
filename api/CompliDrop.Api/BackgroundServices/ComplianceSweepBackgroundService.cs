using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.BackgroundServices;

/// <summary>
/// Keeps the stored <see cref="ComplianceStatus"/> cache fresh as the calendar advances (#257).
/// <see cref="ComplianceCheckService"/> computes the verdict only at evaluation time, so without
/// this a Compliant COI keeps its green badge forever past its expiration date, the "Expired" list
/// filter (which queries the stored column) finds nothing, and the audit export certifies a stale
/// status. This sweep runs the same date overlay <see cref="ComplianceStatusDeriver"/> applies on
/// read, but PERSISTS it — so DB-level filters and counts stay correct, not just the rendered badge.
///
/// Runs once on startup and then hourly (so a midnight expiry transition is reflected within an
/// hour). All work is two set-based UPDATEs — no per-document load, no LLM cost. Uses
/// <see cref="SystemDbContext"/> (cross-tenant, as a system job must be) and <c>ExecuteUpdateAsync</c>
/// with C#-computed date boundaries, so there is no raw SQL and ADR 0009 does not apply.
/// </summary>
public class ComplianceSweepBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ComplianceSweepBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ComplianceSweepBackgroundService starting.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Compliance sweep failed."); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("ComplianceSweepBackgroundService stopping.");
    }

    /// <summary>
    /// Flips date-driven status transitions. Public so the regression suite can drive one sweep
    /// deterministically against the test database.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = now.Date;
        // Exclusive upper bound so the instant comparison matches ComplianceStatusDeriver's date-only
        // window (a noon-UTC expiry on day 30 is still expiring-soon, not Compliant) — see #294.
        var expiringSoonCutoffExclusive =
            ComplianceStatusDeriver.WindowUpperBoundExclusive(today, ComplianceStatusDeriver.ExpiringSoonWindowDays);

        // Expired wins over every rule verdict (mirrors ComplianceCheckService's top-precedence
        // expiry branch): any non-Expired doc whose date has passed flips to Expired. `< today`
        // (UTC midnight) is already exactly "calendar date before today" — no shift needed.
        var expired = await db.Documents
            .Where(d => d.DeletedAt == null
                && d.ExpirationDate != null
                && d.ExpirationDate < today
                && d.ComplianceStatus != ComplianceStatus.Expired)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ComplianceStatus, ComplianceStatus.Expired)
                .SetProperty(d => d.UpdatedAt, now), ct);

        // Within the expiring-soon window, a Compliant or (no-requirements) Pending doc reads
        // ExpiringSoon — matching ComplianceStatusDeriver. A NonCompliant doc keeps its hard-fail
        // verdict; an already-ExpiringSoon doc is unchanged.
        var expiringSoon = await db.Documents
            .Where(d => d.DeletedAt == null
                && d.ExpirationDate != null
                && d.ExpirationDate >= today
                && d.ExpirationDate < expiringSoonCutoffExclusive
                && (d.ComplianceStatus == ComplianceStatus.Compliant
                    || d.ComplianceStatus == ComplianceStatus.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ComplianceStatus, ComplianceStatus.ExpiringSoon)
                .SetProperty(d => d.UpdatedAt, now), ct);

        // #362 / ADR 0041: the future-effective → Pending demotion is deliberately NOT swept. Unlike the
        // Expired / ExpiringSoon transitions (monotonic-forward — a date that passes never un-passes), a
        // future-effective doc RESOLVES back to its real verdict once today reaches its EffectiveDate.
        // Persisting Pending here would erase the rule verdict the read overlay needs to reveal on that
        // crossing, stranding the doc at a stale Pending. So the demotion stays a read-only overlay
        // (ComplianceStatusDeriver.Effective + the SQL read mirrors); the sweep keeps storing the real
        // date/rule verdict, which those read surfaces then demote while the doc is not yet in force.
        if (expired > 0 || expiringSoon > 0)
            logger.LogInformation(
                "Compliance sweep flipped {Expired} document(s) to Expired and {ExpiringSoon} to ExpiringSoon.",
                expired, expiringSoon);
    }
}
