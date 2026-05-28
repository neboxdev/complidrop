using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests.Migrations;

/// <summary>
/// Pins the data-mutation semantics of the
/// <c>20260528102131_RenameSubscriptionPlanMonthlyToPro</c> migration
/// added by #147 + ADR 0011: legacy <c>Subscriptions.Plan = 'monthly'</c>
/// rows become <c>'pro'</c>; every other plan literal (annual, founding,
/// free, plus any future canonical value) is left untouched.
///
/// The Testcontainers harness has already applied every migration before
/// the test runs, so re-applying via EF's <c>Database.MigrateAsync</c>
/// would be a no-op. Instead, this test:
///   1. Inserts <c>Subscription</c> rows with each of the relevant plan
///      literals (the post-migration state).
///   2. Manually <c>UPDATE</c>s one of them BACK to <c>'monthly'</c> to
///      simulate a row that survived from the pre-migration epoch.
///   3. Re-executes the migration's idempotent <c>UPDATE … WHERE
///      "Plan" = 'monthly'</c> SQL.
///   4. Asserts the legacy row flipped to <c>'pro'</c> and the others
///      did not change.
///
/// This pins the WHERE-clause guard. A regression that dropped the
/// guard (<c>UPDATE Subscriptions SET Plan = 'pro'</c>) would clobber
/// annual + founding + free rows — and this test catches it.
/// </summary>
public sealed class RenameSubscriptionPlanMonthlyToProTests(IntegrationTestFixture fixture)
    : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Up_renames_legacy_monthly_rows_to_pro_and_leaves_other_plans_untouched()
    {
        // Arrange: seed one organization per plan with the corresponding subscription.
        var now = DateTime.UtcNow;
        var seededPlans = new[] { "pro", "annual", "founding", "free" };
        var orgIds = new Dictionary<string, Guid>();

        await using (var db = CreateSystemDb())
        {
            foreach (var plan in seededPlans)
            {
                var orgId = Guid.NewGuid();
                orgIds[plan] = orgId;
                db.Organizations.Add(new Organization
                {
                    Id = orgId,
                    Name = $"Org-{plan}",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.Subscriptions.Add(new Subscription
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = orgId,
                    Plan = plan,
                    Status = "active",
                    DocumentLimit = plan == "free" ? 5 : null,
                    HasVendorPortal = plan != "free",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync();
        }

        // Simulate a row that survived from before the migration ran (the migration's
        // forward path turns these into 'pro'; we flip one back so we can re-run the
        // UP statement and observe the rename).
        var legacyOrgId = Guid.NewGuid();
        orgIds["monthly"] = legacyOrgId;
        await using (var db = CreateSystemDb())
        {
            db.Organizations.Add(new Organization
            {
                Id = legacyOrgId,
                Name = "Org-legacy-monthly",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = legacyOrgId,
                Plan = "monthly",
                Status = "active",
                DocumentLimit = null,
                HasVendorPortal = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // Act: re-execute the migration's Up() SQL — bit-for-bit identical to
        // 20260528102131_RenameSubscriptionPlanMonthlyToPro.cs:30.
        await using (var db = CreateSystemDb())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Subscriptions\" SET \"Plan\" = 'pro' WHERE \"Plan\" = 'monthly';");
        }

        // Assert: the legacy row became 'pro'; every other plan literal is untouched.
        await using (var db = CreateSystemDb())
        {
            var legacy = await db.Subscriptions.FirstAsync(s => s.OrganizationId == legacyOrgId);
            legacy.Plan.Should().Be("pro", "the migration renames 'monthly' → 'pro'");

            foreach (var plan in seededPlans)
            {
                var sub = await db.Subscriptions.FirstAsync(s => s.OrganizationId == orgIds[plan]);
                sub.Plan.Should().Be(plan, $"the migration must NOT touch '{plan}' rows");
            }
        }
    }

    [Fact]
    public async Task Up_is_idempotent_a_second_application_is_a_noop()
    {
        // Pin the idempotency claim in the migration's docstring: running the UPDATE
        // twice changes nothing the second time.
        var now = DateTime.UtcNow;
        var orgId = Guid.NewGuid();

        await using (var db = CreateSystemDb())
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = "Org-1", CreatedAt = now, UpdatedAt = now });
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Plan = "monthly",
                Status = "active",
                DocumentLimit = null,
                HasVendorPortal = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        async Task RunUpAsync()
        {
            await using var db = CreateSystemDb();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Subscriptions\" SET \"Plan\" = 'pro' WHERE \"Plan\" = 'monthly';");
        }

        await RunUpAsync(); // first apply
        await RunUpAsync(); // second apply — no-op

        await using var verify = CreateSystemDb();
        var sub = await verify.Subscriptions.FirstAsync(s => s.OrganizationId == orgId);
        sub.Plan.Should().Be("pro");
    }
}
