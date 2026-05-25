using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Proves the integration-test harness itself works: the host boots against the container,
/// migrations are applied, auth flows end-to-end with cookies, and Respawn resets state
/// (including automatic reset between tests).
/// </summary>
public sealed class HarnessSmokeTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // Tightened from "> 0" to exact equality so a partial reseed regression fails loud. Reading
    // ComplianceTemplateSeed.TemplateCount directly (exposed internal via InternalsVisibleTo) keeps
    // the count in one place — adding a sixth template doesn't break this test, but a seeder
    // regression that fails to insert all templates does.
    private static int ExpectedSystemTemplateCount => ComplianceTemplateSeed.TemplateCount;

    [Fact]
    public async Task Health_live_returns_ok()
    {
        var resp = await CreateClient().GetAsync("/health/live");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ready_confirms_migrated_database_is_reachable()
    {
        var resp = await CreateClient().GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_sets_auth_cookies_and_me_returns_the_user()
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.GetAsync("/api/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("userId").GetGuid().Should().Be(auth.UserId);
    }

    /// <summary>
    /// Pins the cookie contract that everything else in the harness — and the frontend —
    /// implicitly depends on: registering issues both <c>cd_session</c> and <c>cd_refresh</c>
    /// as <c>HttpOnly</c> cookies. A regression that flipped HttpOnly off would expose tokens
    /// to client-side script, and a regression that dropped one cookie would silently break
    /// every cookie-authenticated test downstream.
    /// </summary>
    [Fact]
    public async Task Register_response_sets_session_and_refresh_cookies_as_HttpOnly()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"cookie-test-{Guid.NewGuid():N}@example.com",
            password = "Password1234",
            fullName = "Cookie Test",
            companyName = "Cookie Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        resp.EnsureSuccessStatusCode();

        AssertAuthCookiesPresent(resp);
    }

    /// <summary>Mirror of the register assertion for the login path.</summary>
    [Fact]
    public async Task Login_response_sets_session_and_refresh_cookies_as_HttpOnly()
    {
        var email = $"login-cookie-test-{Guid.NewGuid():N}@example.com";
        await RegisterAndLoginAsync(email: email); // arrange: user exists

        // Use a fresh client so the response Set-Cookies under test aren't conflated with the
        // ones from register (the cookie container would otherwise hold both sets).
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Password1234" });
        resp.EnsureSuccessStatusCode();

        AssertAuthCookiesPresent(resp);
    }

    /// <summary>
    /// Smoke test for the <see cref="IntegrationTestBase.LoginAsync"/> helper added in #13.
    /// Register a user, then exercise the login path independently and confirm the returned
    /// client carries credentials that <c>/api/auth/me</c> recognises.
    /// </summary>
    [Fact]
    public async Task LoginAsync_helper_returns_cookie_authed_client_for_existing_user()
    {
        var email = $"login-helper-{Guid.NewGuid():N}@example.com";
        var registered = await RegisterAndLoginAsync(email: email);

        var auth = await LoginAsync(email);

        auth.UserId.Should().Be(registered.UserId);
        auth.OrgId.Should().Be(registered.OrgId);
        auth.Email.Should().Be(email);

        var resp = await auth.Client.GetAsync("/api/auth/me");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("userId").GetGuid().Should().Be(registered.UserId);
    }

    /// <summary>
    /// Sister of <see cref="Reset_cascade_wipes_tenant_templates_while_system_seed_persists"/>.
    /// This one pins the User cleanup angle specifically; the cascade test below covers the
    /// FK-cascade tables (Organizations/ComplianceTemplates/ComplianceRules) that the optimised
    /// reset strategy treats specially. Kept separate because they assert different invariants.
    /// </summary>
    [Fact]
    public async Task Reset_clears_tenant_data_but_keeps_system_templates()
    {
        await RegisterAndLoginAsync(email: "persisted@example.com");
        await using (var db = CreateSystemDb())
        {
            (await db.Users.CountAsync(u => u.Email == "persisted@example.com")).Should().Be(1);
        }

        await Fixture.ResetAsync();

        await using (var db = CreateSystemDb())
        {
            (await db.Users.CountAsync()).Should().Be(0);
            (await db.ComplianceTemplates.CountAsync(t => t.IsSystemTemplate))
                .Should().Be(ExpectedSystemTemplateCount);
        }
    }

    /// <summary>
    /// Pins the cascade-based wipe strategy used by <c>ResetAsync</c>. Because
    /// <c>Organizations</c>, <c>ComplianceTemplates</c>, and <c>ComplianceRules</c> are in
    /// Respawn's <c>TablesToIgnore</c>, the only thing wiping tenant rows in those tables is the
    /// targeted <c>DELETE FROM "Organizations" WHERE "Id" &lt;&gt; sysOrgId</c> + FK cascade. We
    /// seed every cascade arm the optimization implicitly depends on:
    /// <list type="bullet">
    /// <item><c>tenant Org → tenant ComplianceTemplate → tenant ComplianceRule</c> (CASCADE x2)</item>
    /// <item><c>tenant Vendor → system ComplianceTemplate</c> (Vendor's FK to template is
    /// SetNull, so it's safe across the cascade as long as the Vendor itself is wiped by Respawn
    /// first — pins the ordering invariant).</item>
    /// <item><c>tenant Document → tenant ComplianceCheck → tenant ComplianceRule</c>
    /// (ComplianceCheck → ComplianceRule is RESTRICT; the cascade only stays safe because the
    /// ComplianceChecks are wiped by Respawn before the Org-DELETE fires).</item>
    /// </list>
    /// Stable system-template rows across resets (Id + Name + CreatedAt) confirm rows weren't
    /// re-inserted by a hypothetical re-run of the seed. Looping the reset three times catches
    /// a regression that only kicks in on later resets (e.g. lazy-init cache invalidation).
    /// </summary>
    [Fact]
    public async Task Reset_cascade_wipes_tenant_templates_while_system_seed_persists()
    {
        // Snapshot the system rows before any tenant data lands. Use a projection rich enough
        // that a hypothetical "rows updated rather than re-inserted" regression also fails.
        // OrderBy(Id) — Name has no unique index, so name-ordering could theoretically be
        // ambiguous if a future seed duplicated a name.
        SystemTemplateSnapshot[] systemBefore = await SnapshotSystemTemplatesAsync();
        systemBefore.Should().HaveCount(ExpectedSystemTemplateCount);

        // Pick the first system template's Id — the Vendor row below will reference it via
        // Vendor.ComplianceTemplateId (FK = SetNull) — and a system rule Id for the
        // RESTRICT-FK ComplianceCheck arm.
        var systemTemplateId = systemBefore[0].Id;
        Guid systemRuleId;
        await using (var db = CreateSystemDb())
        {
            systemRuleId = await db.ComplianceRules
                .Where(r => r.ComplianceTemplate.IsSystemTemplate)
                .Select(r => r.Id)
                .FirstAsync();
        }
        var seedSuffix = Guid.NewGuid().ToString("N")[..8];
        var tenantOrgId = Guid.NewGuid();
        var tenantTemplateId = Guid.NewGuid();
        var tenantRuleId = Guid.NewGuid();
        var tenantVendorId = Guid.NewGuid();
        var tenantDocId = Guid.NewGuid();
        var tenantCheckOnTenantRuleId = Guid.NewGuid();
        var tenantCheckOnSystemRuleId = Guid.NewGuid();

        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            db.Organizations.Add(new() { Id = tenantOrgId, Name = $"tenant-org-{seedSuffix}", CreatedAt = now, UpdatedAt = now });
            db.ComplianceTemplates.Add(new()
            {
                Id = tenantTemplateId,
                OrganizationId = tenantOrgId,
                Name = $"tenant-template-{seedSuffix}",
                IsSystemTemplate = false,
                CreatedAt = now
            });
            db.ComplianceRules.Add(new()
            {
                Id = tenantRuleId,
                ComplianceTemplateId = tenantTemplateId,
                DocumentType = "coi",
                Operator = "required",
                ErrorMessage = "x",
                SortOrder = 1
            });
            // Vendor points at a SYSTEM template via SetNull FK — exercises the order-dependent
            // safety (Vendor must be wiped by Respawn before the system template would matter).
            db.Vendors.Add(new()
            {
                Id = tenantVendorId,
                OrganizationId = tenantOrgId,
                Name = $"tenant-vendor-{seedSuffix}",
                ComplianceTemplateId = systemTemplateId,
                CreatedAt = now,
                UpdatedAt = now
            });
            // Document + two ComplianceChecks — one referencing the tenant rule, one referencing
            // a SYSTEM rule. ComplianceCheck → ComplianceRule is RESTRICT, so the cascade only
            // stays safe because Respawn wipes the checks before the Org-DELETE fires.
            db.Documents.Add(new()
            {
                Id = tenantDocId,
                OrganizationId = tenantOrgId,
                VendorId = tenantVendorId,
                OriginalFileName = "x.pdf",
                BlobStorageUrl = "blob://x",
                FileSizeBytes = 1,
                ContentType = "application/pdf",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ComplianceChecks.Add(new()
            {
                Id = tenantCheckOnTenantRuleId,
                DocumentId = tenantDocId,
                ComplianceRuleId = tenantRuleId,
                IsPassed = true,
                CheckedAt = now
            });
            db.ComplianceChecks.Add(new()
            {
                Id = tenantCheckOnSystemRuleId,
                DocumentId = tenantDocId,
                ComplianceRuleId = systemRuleId,
                IsPassed = true,
                CheckedAt = now
            });
            await db.SaveChangesAsync();
        }

        // Pre-reset sanity: tenant rows exist alongside the system rows.
        await using (var db = CreateSystemDb())
        {
            (await db.Organizations.CountAsync()).Should().Be(2, "system org + tenant org");
            (await db.ComplianceTemplates.CountAsync(t => !t.IsSystemTemplate)).Should().Be(1);
            (await db.ComplianceRules.CountAsync(r => r.Id == tenantRuleId)).Should().Be(1);
            (await db.Vendors.CountAsync(v => v.Id == tenantVendorId)).Should().Be(1);
            (await db.Documents.CountAsync(d => d.Id == tenantDocId)).Should().Be(1);
            (await db.ComplianceChecks.CountAsync(c => c.DocumentId == tenantDocId)).Should().Be(2);
        }

        // Loop the reset to catch hypothetical regressions that only kick in on later resets
        // (lazy-init invalidation, cached state in Respawn, etc.). The reset must not throw —
        // including the RESTRICT-FK case (ComplianceCheck → system ComplianceRule). The system
        // snapshot must remain byte-identical to the initial capture every iteration.
        for (var i = 1; i <= 3; i++)
        {
            await Fixture.ResetAsync();

            await using var db = CreateSystemDb();

            // Only the system org survives the targeted DELETE.
            (await db.Organizations.CountAsync()).Should().Be(1, $"iteration {i}");

            // Cascade-delete wiped the tenant template (parent org gone) — even though Respawn
            // ignored the ComplianceTemplates table.
            (await db.ComplianceTemplates.CountAsync(t => !t.IsSystemTemplate)).Should().Be(0, $"iteration {i}");
            // Cascade through ComplianceTemplate → ComplianceRule too.
            (await db.ComplianceRules.CountAsync(r => r.Id == tenantRuleId)).Should().Be(0, $"iteration {i}");
            // Respawn wiped the tenant Vendor + Document + ComplianceChecks.
            (await db.Vendors.CountAsync(v => v.Id == tenantVendorId)).Should().Be(0, $"iteration {i}");
            (await db.Documents.CountAsync(d => d.Id == tenantDocId)).Should().Be(0, $"iteration {i}");
            (await db.ComplianceChecks.CountAsync()).Should().Be(0, $"iteration {i}");

            // The SYSTEM template referenced by the tenant Vendor (SetNull) survives.
            (await db.ComplianceTemplates.CountAsync(t => t.Id == systemTemplateId)).Should().Be(1, $"iteration {i}");
            // The SYSTEM rule referenced by a tenant check (RESTRICT) survives.
            (await db.ComplianceRules.CountAsync(r => r.Id == systemRuleId)).Should().Be(1, $"iteration {i}");

            // Rows weren't re-inserted: Id + Name + CreatedAt are byte-identical to the pre-test
            // snapshot. A hypothetical reseed-as-update regression would shift CreatedAt; the
            // pre-optimization reseed-after-wipe would have shifted Id.
            var systemAfter = await SnapshotSystemTemplatesAsync(db);
            systemAfter.Should().BeEquivalentTo(systemBefore,
                $"system template rows must not be re-inserted across resets (iteration {i})");

            // The system rules count also stays at the seeded value — locks in the cascade isn't
            // accidentally widened to system rows.
            (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplate.IsSystemTemplate))
                .Should().BeGreaterThan(0, $"iteration {i}");
        }
    }

    /// <summary>Snapshot the system template rows in a stable order, projecting fields that
    /// would differ if a reseed (insert-or-update) were silently running on reset.</summary>
    private async Task<SystemTemplateSnapshot[]> SnapshotSystemTemplatesAsync(
        Data.SystemDbContext? db = null)
    {
        if (db is not null) return await Query(db);
        await using var owned = CreateSystemDb();
        return await Query(owned);

        static async Task<SystemTemplateSnapshot[]> Query(Data.SystemDbContext db) =>
            await db.ComplianceTemplates
                .Where(t => t.IsSystemTemplate)
                .OrderBy(t => t.Id) // Id is the canonical stable key — Name has no unique index.
                .Select(t => new SystemTemplateSnapshot(t.Id, t.Name, t.CreatedAt))
                .ToArrayAsync();
    }

    private sealed record SystemTemplateSnapshot(Guid Id, string Name, DateTime CreatedAt);

    /// <summary>
    /// Inspects the response's <c>Set-Cookie</c> headers (independent of the cookie container)
    /// so the assertions don't lean on the same code path that stores them.
    /// <list type="bullet">
    /// <item>Both <c>cd_session</c> and <c>cd_refresh</c> must be present (exactly once).</item>
    /// <item>Both must carry <c>HttpOnly</c> so the tokens aren't readable by client-side script.</item>
    /// <item>The refresh cookie must be <c>Path</c>-scoped to <c>/api/auth</c> — it's long-lived
    /// and shouldn't be sent on every request, only to the refresh endpoint.</item>
    /// </list>
    /// Each cookie is parsed by splitting on <c>;</c> and trimming, so the attribute check looks
    /// at attribute segments rather than substring-matching the whole header (a JWT value could
    /// in principle contain a literal substring; the segment-based check can't false-positive).
    /// </summary>
    private static void AssertAuthCookiesPresent(HttpResponseMessage resp)
    {
        resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
            .Should().BeTrue("auth endpoints must return Set-Cookie headers");
        var cookies = setCookies!.ToList();

        var sessionMatches = cookies
            .Where(c => c.StartsWith($"{CookieAuthSetup.SessionCookie}=", StringComparison.Ordinal))
            .ToList();
        sessionMatches.Should().ContainSingle("session cookie 'cd_session' must be issued exactly once");
        var sessionAttrs = ParseAttributes(sessionMatches[0]);
        sessionAttrs.Should().Contain("httponly",
            "the session token must not be readable by client-side script");

        var refreshMatches = cookies
            .Where(c => c.StartsWith($"{CookieAuthSetup.RefreshCookie}=", StringComparison.Ordinal))
            .ToList();
        refreshMatches.Should().ContainSingle("refresh cookie 'cd_refresh' must be issued exactly once");
        var refreshAttrs = ParseAttributes(refreshMatches[0]);
        refreshAttrs.Should().Contain("httponly",
            "the refresh token must not be readable by client-side script");
        refreshAttrs.Should().Contain("path=/api/auth",
            "the long-lived refresh cookie must be scoped to /api/auth, not the whole site");
    }

    /// <summary>
    /// Parses a Set-Cookie header into a list of lowercase attribute strings (excluding the
    /// name=value pair itself), so callers can assert membership by attribute name or
    /// name=value without false-positive matches inside the cookie value.
    /// </summary>
    private static List<string> ParseAttributes(string setCookieHeader) =>
        setCookieHeader
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1) // first segment is name=value
            .Select(s => s.ToLowerInvariant())
            .ToList();

    // The two tests below share a fixed email and each assert a clean database at the START of
    // the test. They both pass only if IntegrationTestBase.InitializeAsync resets between tests —
    // i.e. they make the per-test auto-reset (the harness's core promise that every downstream
    // ticket depends on) load-bearing, and would fail if it regressed. Without the auto-reset,
    // whichever runs second would see the first test's user (count != 0, and a duplicate-email 409).
    [Fact]
    public async Task Auto_reset_gives_each_test_a_clean_database_1() => await AssertCleanStartThenRegister();

    [Fact]
    public async Task Auto_reset_gives_each_test_a_clean_database_2() => await AssertCleanStartThenRegister();

    private async Task AssertCleanStartThenRegister()
    {
        await using (var db = CreateSystemDb())
        {
            (await db.Users.CountAsync())
                .Should().Be(0, "the per-test reset must wipe data created by other tests in the collection");
        }

        await RegisterAndLoginAsync(email: "iso@example.com");
    }
}
