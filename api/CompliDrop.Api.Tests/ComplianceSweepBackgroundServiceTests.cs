using CompliDrop.Api.BackgroundServices;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Regression tests for the nightly sweep that keeps the stored ComplianceStatus fresh as the
/// calendar advances (#257). Drives <see cref="ComplianceSweepBackgroundService.SweepAsync"/>
/// against a fixed clock and asserts the date-driven transitions persist — matching
/// <see cref="Services.ComplianceStatusDeriver"/>.
/// </summary>
public sealed class ComplianceSweepBackgroundServiceTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private DateTime Anchor => FixedNow.UtcDateTime;

    private ComplianceSweepBackgroundService BuildSweep() =>
        new(
            Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(FixedNow),
            NullLogger<ComplianceSweepBackgroundService>.Instance);

    private async Task<Guid> SeedAsync(ComplianceStatus status, DateTime? expiration, bool deleted = false, DateTime? effective = null)
    {
        var orgId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            OriginalFileName = "d.pdf",
            BlobStorageUrl = "blob://d",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ComplianceStatus = status,
            ExpirationDate = expiration,
            EffectiveDate = effective,
            DeletedAt = deleted ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return docId;
    }

    private async Task<ComplianceStatus> StatusAsync(Guid id)
    {
        await using var db = CreateSystemDb();
        return (await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == id)).ComplianceStatus;
    }

    [Fact]
    public async Task Sweep_flips_a_compliant_doc_past_its_expiration_to_Expired()
    {
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.AddDays(-1));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public async Task Sweep_flips_a_compliant_doc_within_the_window_to_ExpiringSoon()
    {
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.AddDays(10));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public async Task Sweep_flips_an_expired_non_compliant_doc_to_Expired()
    {
        // Expired wins over the rule verdict (mirrors the service's top-precedence expiry branch).
        var id = await SeedAsync(ComplianceStatus.NonCompliant, Anchor.AddDays(-5));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.Expired);
    }

    [Fact]
    public async Task Sweep_leaves_a_non_compliant_doc_within_the_window_NonCompliant()
    {
        // A failing doc that's merely expiring-soon keeps its hard-fail verdict.
        var id = await SeedAsync(ComplianceStatus.NonCompliant, Anchor.AddDays(10));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task Sweep_flips_a_pending_doc_within_the_window_to_ExpiringSoon()
    {
        // Matches the deriver: a no-requirements Pending doc surfaces date urgency.
        var id = await SeedAsync(ComplianceStatus.Pending, Anchor.AddDays(5));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public async Task Sweep_leaves_a_far_future_compliant_doc_untouched()
    {
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.AddDays(200));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public async Task Sweep_flips_a_time_bearing_expiry_on_the_30_day_boundary_to_ExpiringSoon()
    {
        // #294: Anchor is noon, so AddDays(30) is a NON-midnight expiry exactly on the 30-day
        // boundary. The deriver (date-only) reads ExpiringSoon; before the exclusive-bound fix the
        // sweep's `exp <= today+30 (midnight)` left it Compliant (noon > midnight) — the two-answers
        // split this fix removes.
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.AddDays(30));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.ExpiringSoon,
            "a time-bearing expiry on the boundary day is within the window, matching the deriver");
    }

    [Fact]
    public async Task Sweep_flips_a_midnight_expiry_on_the_30_day_boundary_to_ExpiringSoon()
    {
        // The canonical case — extraction stores dates at UTC midnight — is in-window under BOTH the
        // old and new bound. Pins that the exclusive-bound fix didn't regress the already-correct
        // midnight boundary (Anchor.Date strips the noon component so this is exactly today+30 00:00).
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.Date.AddDays(30));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.ExpiringSoon);
    }

    [Fact]
    public async Task Sweep_leaves_a_doc_expiring_exactly_today_as_ExpiringSoon_not_Expired()
    {
        // The Expired LOWER edge at the exact flip instant: a doc expiring at today's UTC midnight is
        // NOT yet expired (the sweep's `ExpirationDate < today` is strict), so it reads ExpiringSoon —
        // matching ComplianceStatusDeriver.Effective (`expiry < todayDate`). Pins the worker SQL
        // boundary so an off-by-one to `<=` (flipping a doc expiring TODAY to Expired) fails here; the
        // past-date Expired case stays covered by Sweep_flips_a_compliant_doc_past_its_expiration_to_Expired.
        // (#244 time/TZ audit — lower-edge guard for the worker SQL path.)
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.Date);

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.ExpiringSoon,
            "a doc expiring at today's midnight is not yet Expired — the boundary is strict (< today)");
    }

    [Fact]
    public async Task Sweep_leaves_a_time_bearing_expiry_just_past_the_window_Compliant()
    {
        // The exclusive bound must not over-reach: a noon expiry on day 31 is beyond the 30-day
        // window (the deriver keeps it Compliant), so the sweep must not flip it.
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.AddDays(31));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.Compliant,
            "day 31 is outside the 30-day window — the exclusive bound is today+31, not today+32");
    }

    [Fact]
    public async Task Sweep_does_not_persist_the_future_effective_demotion_leaving_the_real_verdict_intact()
    {
        // #362 / ADR 0041: the future-effective → Pending demotion is a READ-only overlay, deliberately
        // NOT swept. A far-future-expiry Compliant doc that isn't in force yet (effective next month) must
        // keep its STORED Compliant verdict through the sweep — storing Pending would erase the rule
        // verdict the read overlay needs to reveal the day the doc becomes effective (the self-heal). The
        // read surfaces demote it to Pending while it's future-effective; the stored column stays Compliant.
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.AddDays(300), effective: Anchor.AddDays(30));

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.Compliant,
            "the sweep must not persist the future-effective demotion — the stored verdict self-heals on read");
    }

    [Fact]
    public async Task Sweep_skips_soft_deleted_documents()
    {
        // A soft-deleted doc must never be resurrected by the sweep — its status stays frozen.
        var id = await SeedAsync(ComplianceStatus.Compliant, Anchor.AddDays(-1), deleted: true);

        await BuildSweep().SweepAsync(CancellationToken.None);

        (await StatusAsync(id)).Should().Be(ComplianceStatus.Compliant,
            "the sweep's DeletedAt == null guard must exclude soft-deleted documents");
    }
}
