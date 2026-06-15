using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// HTTP tests for the dashboard "Recent activity" feed dedup + label hygiene (#252). One user
/// action must produce exactly one feed row even though the AuditSaveChangesInterceptor and the
/// explicit IAuditLogger both write a row; internal-flag mutations (user.updated) must not leak raw
/// entity-speak. The AuditLog TABLE keeps both rows (export unchanged) — only the FEED dedupes.
/// </summary>
public sealed class RecentActivityFeedTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task SeedAsync(Guid orgId,
        params (string action, string entityType, Guid? entityId, string? correlationId, int secondsAgo)[] rows)
    {
        await using var db = CreateSystemDb();
        var now = DateTime.UtcNow;
        foreach (var r in rows)
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = Guid.NewGuid(),
                Action = r.action,
                EntityType = r.entityType,
                EntityId = r.entityId,
                CorrelationId = r.correlationId,
                CreatedAt = now.AddSeconds(-r.secondsAgo)
            });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement[]> FeedAsync(AuthenticatedClient auth) =>
        (await auth.Client.GetFromJsonAsync<JsonElement>("/api/dashboard/recent-activity"))
            .GetProperty("data").EnumerateArray().ToArray();

    private static int CountFor(JsonElement[] feed, Guid entityId) =>
        feed.Count(a => a.GetProperty("entityId").GetString() == entityId.ToString());

    [Fact]
    public async Task Same_action_twin_in_one_request_collapses_to_one_feed_row()
    {
        // The interceptor + the explicit logger both write "vendor.created" for the same vendor in
        // one request (same CorrelationId). The feed must show exactly one.
        var auth = await RegisterAndLoginAsync();
        var vendorId = Guid.NewGuid();
        var corr = Guid.NewGuid().ToString("N");
        await SeedAsync(auth.OrgId,
            ("vendor.created", "Vendor", vendorId, corr, 5),
            ("vendor.created", "Vendor", vendorId, corr, 5));

        var feed = await FeedAsync(auth);

        CountFor(feed, vendorId).Should().Be(1, "the interceptor + explicit twin must collapse to one feed row");
    }

    [Fact]
    public async Task Two_separate_requests_on_the_same_entity_are_not_collapsed()
    {
        // Two genuine updates to the same vendor (different requests → different CorrelationIds) are
        // distinct events and must BOTH show — the dedup keys on the request, not just the entity.
        var auth = await RegisterAndLoginAsync();
        var vendorId = Guid.NewGuid();
        await SeedAsync(auth.OrgId,
            ("vendor.updated", "Vendor", vendorId, Guid.NewGuid().ToString("N"), 30),
            ("vendor.updated", "Vendor", vendorId, Guid.NewGuid().ToString("N"), 10));

        var feed = await FeedAsync(auth);

        CountFor(feed, vendorId).Should().Be(2, "two updates in two requests are two events");
    }

    [Fact]
    public async Task Refined_twin_keeps_the_specific_action_over_the_generic_update()
    {
        // Verifying a document writes the explicit "document.verified" AND the interceptor's generic
        // "document.updated" (same request, same entity). The feed must keep ONE row — the specific one.
        var auth = await RegisterAndLoginAsync();
        var docId = Guid.NewGuid();
        var corr = Guid.NewGuid().ToString("N");
        await SeedAsync(auth.OrgId,
            ("document.updated", "Document", docId, corr, 5),
            ("document.verified", "Document", docId, corr, 5));

        var feed = await FeedAsync(auth);

        var rows = feed.Where(a => a.GetProperty("entityId").GetString() == docId.ToString()).ToArray();
        rows.Should().ContainSingle();
        rows[0].GetProperty("action").GetString().Should().Be("document.verified",
            "the specific explicit action must win over the generic interceptor update");
    }

    [Fact]
    public async Task Generic_user_update_twin_is_dropped_in_favour_of_the_specific_action()
    {
        // A password change writes the explicit "user.password_changed" AND the interceptor's
        // "user.updated". The feed must show only the specific, labelled action.
        var auth = await RegisterAndLoginAsync();
        var userId = Guid.NewGuid();
        var corr = Guid.NewGuid().ToString("N");
        await SeedAsync(auth.OrgId,
            ("user.updated", "User", userId, corr, 5),
            ("user.password_changed", "User", userId, corr, 5));

        var feed = await FeedAsync(auth);

        var rows = feed.Where(a => a.GetProperty("entityId").GetString() == userId.ToString()).ToArray();
        rows.Should().ContainSingle().Which.GetProperty("action").GetString().Should().Be("user.password_changed");
    }

    [Fact]
    public async Task Lone_internal_user_update_is_excluded_from_the_feed()
    {
        // The welcome-tour HasCompletedOnboarding flip emits a bare "user.updated" with no explicit
        // twin — it must NOT leak the entity-speak "User - Updated" into a Pat-facing feed (#252 AC2).
        var auth = await RegisterAndLoginAsync();
        var userId = Guid.NewGuid();
        await SeedAsync(auth.OrgId, ("user.updated", "User", userId, Guid.NewGuid().ToString("N"), 5));

        var feed = await FeedAsync(auth);

        feed.Should().NotContain(a => a.GetProperty("action").GetString() == "user.updated",
            "internal-flag user.updated mutations must be excluded from the feed");
    }

    [Fact]
    public async Task Creating_a_vendor_over_http_produces_one_feed_row_but_keeps_both_audit_rows()
    {
        // End-to-end: the real interceptor + explicit logger both fire in one request. The feed shows
        // one row; the AuditLog table keeps both, so the audit export is unchanged (#252 AC1 + AC3).
        var auth = await RegisterAndLoginAsync();

        var create = await auth.Client.PostAsJsonAsync("/api/vendors", new
        {
            name = "Acme Tents", contactEmail = (string?)null, contactPhone = (string?)null,
            category = (string?)null, complianceTemplateId = (Guid?)null
        });
        create.EnsureSuccessStatusCode();
        var vendorId = Guid.Parse((await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("id").GetString()!);

        var feed = await FeedAsync(auth);
        CountFor(feed, vendorId).Should().Be(1, "one vendor add must produce exactly one feed row");

        await using var db = CreateSystemDb();
        (await db.AuditLogs.CountAsync(a => a.Action == "vendor.created" && a.EntityId == vendorId))
            .Should().Be(2, "the table keeps both the interceptor and explicit rows — export is unchanged");
    }

    [Fact]
    public async Task Case_mismatched_twin_still_collapses_on_the_entity_key()
    {
        // The portal-link twin (#252 AC1) is the case where the interceptor lowercases the action
        // ("vendorportallink.created") while the explicit logger preserves camelCase
        // ("vendorPortalLink.created"). They still collapse because the dedup keys on
        // (CorrelationId, EntityType, EntityId) — EntityType is PascalCase "VendorPortalLink" for
        // both — NOT on the differently-cased Action. Pins that the key is entity-based.
        var auth = await RegisterAndLoginAsync();
        var linkId = Guid.NewGuid();
        var corr = Guid.NewGuid().ToString("N");
        await SeedAsync(auth.OrgId,
            ("vendorportallink.created", "VendorPortalLink", linkId, corr, 5),
            ("vendorPortalLink.created", "VendorPortalLink", linkId, corr, 5));

        var feed = await FeedAsync(auth);

        CountFor(feed, linkId).Should().Be(1, "case-mismatched interceptor/explicit twins still collapse on the entity key");
    }

    [Fact]
    public async Task Rows_without_a_correlation_id_are_never_collapsed()
    {
        // Background / non-request audit rows have a null CorrelationId; they must each stay distinct
        // (the collapse only folds request-path twins that share a correlation id).
        var auth = await RegisterAndLoginAsync();
        var entityId = Guid.NewGuid();
        await SeedAsync(auth.OrgId,
            ("reminder.sent", "Reminder", entityId, null, 30),
            ("reminder.sent", "Reminder", entityId, null, 10));

        var feed = await FeedAsync(auth);

        CountFor(feed, entityId).Should().Be(2, "null-correlation rows must not be collapsed together");
    }

    [Fact]
    public async Task Feed_is_capped_at_20_and_ordered_newest_first()
    {
        // 25 distinct events: the feed must cap at 20 (the collapse runs over the 60-row buffer BEFORE
        // the Take(20), so distinct events aren't lost to the cap) and be newest-first.
        var auth = await RegisterAndLoginAsync();
        var rows = Enumerable.Range(0, 25)
            .Select(i => ("vendor.created", "Vendor", (Guid?)Guid.NewGuid(), Guid.NewGuid().ToString("N"), i + 1))
            .ToArray();
        await SeedAsync(auth.OrgId, rows);

        var feed = await FeedAsync(auth);

        feed.Length.Should().Be(20, "the feed caps at 20 rows");
        feed.Select(a => a.GetProperty("createdAt").GetDateTime())
            .Should().BeInDescendingOrder("the feed is ordered newest-first");
    }
}
