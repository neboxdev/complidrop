using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// A <see cref="SaveChangesInterceptor"/> that throws on the FIRST async SaveChanges and lets every
/// subsequent one through. Used to prove the batched re-eval fan-out's per-page best-effort
/// guarantee (#293): when one page's SaveChanges fails, the fan-out logs and skips it (the page's
/// documents keep their prior verdict) and KEEPS GOING — later pages still commit. Wired only onto
/// the AppDbContext the fan-out runs on; seeding/verification use a separate context.
/// </summary>
public sealed class ThrowOnceSaveChangesInterceptor : SaveChangesInterceptor
{
    private int _calls;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _calls) == 1)
            throw new DbUpdateException("Simulated first-page persistence failure.");
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
