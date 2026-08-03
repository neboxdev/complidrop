using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Base class for HTTP-level integration tests. Resets the database before each test for
/// isolation and exposes helpers for cookie-authenticated clients and direct DB access.
/// Lives in the "integration" collection so all such tests share one container and run serially.
/// </summary>
[Collection("integration")]
public abstract class IntegrationTestBase(IntegrationTestFixture fixture) : IAsyncLifetime
{
    protected IntegrationTestFixture Fixture { get; } = fixture;

    /// <summary>
    /// Resets the DB before every test. Subclasses that need to seed fixture data should override
    /// and <c>await base.InitializeAsync()</c> first so the reset happens before their seed.
    /// </summary>
    public virtual Task InitializeAsync() => Fixture.ResetAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A client that stores/sends cookies, so auth survives across requests.</summary>
    protected HttpClient CreateClient() =>
        Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    /// <summary>
    /// Constructs a <see cref="SystemDbContext"/> against the test container with the same
    /// <see cref="ISaveChangesInterceptor"/>s <c>Program.cs</c> wires, so seeds/reads via the harness see
    /// the same behavior as production code paths:
    /// <list type="bullet">
    /// <item><see cref="AuditSaveChangesInterceptor"/> — UpdatedAt auto-set, soft-delete translation,
    /// audit-log emission when a user is supplied. Passing <c>null</c> for the user skips audit-log
    /// emission but still applies UpdatedAt — the right semantic for test fixture setup.</item>
    /// <item><see cref="ComplianceCheckDeleteConcurrencyInterceptor"/> — a <c>ComplianceCheck</c> DELETE
    /// that matched nothing is a success rather than a <c>DbUpdateConcurrencyException</c> (#468, ADR 0030
    /// Amendment 4). Wired here for the reason the list exists at all: a helper that is MOSTLY production
    /// is worse than one that plainly is not, because a test written through it silently exercises
    /// different save semantics. Left out, the two scope-checking tests in
    /// <c>ComplianceCheckDeleteConcurrencyTests</c> would pass identically if the interceptor were deleted
    /// from <c>Program.cs</c>, and a future reproduction driven through this helper would see the pre-#468
    /// throw and conclude the bug is still live.</item>
    /// </list>
    /// <para/>
    /// Note: because the audit interceptor is wired, <c>db.Remove(entity)</c> on a soft-deletable
    /// entity here is translated to a soft delete (UPDATE DeletedAt=now). If a test needs a
    /// genuine hard delete, run it directly against the EF Core context without going through
    /// this helper, or use <c>ExecuteDeleteAsync</c>.
    /// </summary>
    protected SystemDbContext CreateSystemDb(ICurrentUser? user = null) =>
        new(new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors(
                new AuditSaveChangesInterceptor(() => user),
                new ComplianceCheckDeleteConcurrencyInterceptor(
                    NullLogger<ComplianceCheckDeleteConcurrencyInterceptor>.Instance))
            .Options);

    /// <inheritdoc cref="CreateSystemDb"/>
    protected AppDbContext CreateAppDb(ICurrentUser user) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors(
                new AuditSaveChangesInterceptor(() => user),
                new ComplianceCheckDeleteConcurrencyInterceptor(
                    NullLogger<ComplianceCheckDeleteConcurrencyInterceptor>.Instance))
            .Options, user);

    /// <summary>Registers a fresh org + admin user and returns a cookie-authenticated client.</summary>
    protected async Task<AuthenticatedClient> RegisterAndLoginAsync(
        string? email = null, string password = "Password1234")
    {
        var client = CreateClient();
        email ??= $"user-{Guid.NewGuid():N}@example.com";

        var resp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            fullName = "Test User",
            companyName = "Test Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        return new AuthenticatedClient(
            client,
            data.GetProperty("userId").GetGuid(),
            data.GetProperty("organizationId").GetGuid(),
            email);
    }

    /// <summary>
    /// Flips the org's portal entitlement + document cap (#261). Registration seeds every
    /// org as Free (HasVendorPortal=false, DocumentLimit=5), so tests that mint or email
    /// portal links must grant the entitlement first — mirroring what the Stripe webhook
    /// does on checkout (StripeService: portal on, cap removed). <c>on: false</c> mirrors
    /// the cancel path (portal off; pass <paramref name="documentLimit"/> 5 to fully match).
    /// </summary>
    protected async Task SetPortalEntitlementAsync(Guid orgId, bool on, int? documentLimit = null)
    {
        await using var db = CreateSystemDb();
        await db.Subscriptions.Where(s => s.OrganizationId == orgId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.HasVendorPortal, on)
                .SetProperty(x => x.DocumentLimit, documentLimit));
    }

    /// <summary>
    /// Marks the given documents as GRADED (#443 / ADR 0048) by seeding one passing
    /// <see cref="ComplianceCheck"/> row against each — the artifact the engine emits when it
    /// actually measured a requirement, and the fact <c>DocumentGrading.IsGraded</c> reads.
    /// <para/>
    /// A fixture that writes a stored <c>Compliant</c> / <c>ExpiringSoon</c> straight into the
    /// Documents table without this is seeding a state no production path can reach: every
    /// <c>ComputeOutcome</c> branch that certifies nothing returns Pending-or-ExpiringSoon AND clears
    /// the check rows, so an affirmative stored verdict always comes with at least one check. Since
    /// #443 the read overlay demotes an ungraded affirmative verdict to Pending, so such a fixture no
    /// longer exercises what it means to — it exercises the never-graded path instead. Call this for
    /// any seeded doc whose AFFIRMATIVE verdict is the thing under test.
    /// <para/>
    /// Reuses any rule already in the org (so a test's own checklist backs its checks) and otherwise
    /// mints a throwaway template + rule. Two fixture side effects worth knowing before you call it:
    /// <list type="bullet">
    ///   <item><description>The rule pick is ORDERED (<c>SortOrder</c> then <c>Id</c>). Postgres
    ///     guarantees no row order without an <c>ORDER BY</c>, so an unordered
    ///     <c>FirstOrDefaultAsync</c> would make WHICH rule backs the check row non-deterministic —
    ///     invisible while a test only counts check rows, but not once one asserts on the rule behind
    ///     one (the detail page's "What we checked" panel renders it).</description></item>
    ///   <item><description>The no-rule fallback mints a REAL, org-visible <c>ComplianceTemplate</c>
    ///     named <c>Graded-{guid}</c> plus one rule. It is assigned to no vendor, so it stays inert for
    ///     coverage rollups (which key on the VENDOR's <c>ComplianceTemplateId</c>) — but it DOES appear
    ///     on any surface that lists templates or counts an org's requirements. A test asserting on
    ///     those should seed its own checklist first, which this helper then reuses instead.</description></item>
    /// </list>
    /// </summary>
    protected async Task MarkGradedAsync(Guid organizationId, params Guid[] documentIds)
    {
        if (documentIds.Length == 0) return;
        await using var db = CreateSystemDb();
        var ruleId = await db.ComplianceRules
            .Where(r => r.ComplianceTemplate.OrganizationId == organizationId)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync();
        var now = DateTime.UtcNow;
        if (ruleId is null)
        {
            var templateId = Guid.NewGuid();
            ruleId = Guid.NewGuid();
            db.ComplianceTemplates.Add(new ComplianceTemplate
            {
                Id = templateId, OrganizationId = organizationId, Name = $"Graded-{templateId:N}", CreatedAt = now
            });
            db.ComplianceRules.Add(new ComplianceRule
            {
                Id = ruleId.Value, ComplianceTemplateId = templateId, DocumentType = "coi",
                FieldName = "expiration_date", Operator = "required", SortOrder = 0
            });
        }
        foreach (var documentId in documentIds)
            db.ComplianceChecks.Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(), DocumentId = documentId, ComplianceRuleId = ruleId.Value,
                IsPassed = true, CheckedAt = now
            });
        await db.SaveChangesAsync();
    }

    protected sealed record SeededLink(Guid OrgId, Guid VendorId, Guid LinkId, string Token, string OrgName, string VendorName);

    /// <summary>
    /// Seeds an org + subscription + vendor + portal link directly via the system context (no
    /// tenant filter). The subscription defaults to portal-entitled with no document cap — the
    /// realistic state for an org holding a working link, since the #261 plan gate refuses both
    /// link minting (403 at generation) and link use (the portal answers 404 for
    /// <c>HasVendorPortal=false</c>). Fence tests override <paramref name="hasVendorPortal"/> /
    /// <paramref name="documentLimit"/>, or set <paramref name="seedSubscription"/> false to pin
    /// the fail-closed missing-row behavior.
    /// </summary>
    protected async Task<SeededLink> SeedLinkAsync(
        bool isActive = true,
        DateTime? expiresAt = null,
        int maxUploads = 20,
        int uploadCount = 0,
        bool hasVendorPortal = true,
        int? documentLimit = null,
        bool seedSubscription = true)
    {
        var orgId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var token = $"tok-{Guid.NewGuid():N}";
        var orgName = $"SecretOrg-{orgId:N}";
        var vendorName = $"SecretVendor-{vendorId:N}";
        var now = DateTime.UtcNow;

        await using var db = CreateSystemDb();
        db.Organizations.Add(new Organization { Id = orgId, Name = orgName, CreatedAt = now, UpdatedAt = now });
        if (seedSubscription)
        {
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                Plan = hasVendorPortal ? "pro" : "free",
                Status = "active",
                HasVendorPortal = hasVendorPortal,
                DocumentLimit = documentLimit,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = vendorName, CreatedAt = now, UpdatedAt = now });
        db.VendorPortalLinks.Add(new VendorPortalLink
        {
            Id = linkId,
            VendorId = vendorId,
            Token = token,
            IsActive = isActive,
            ExpiresAt = expiresAt,
            MaxUploads = maxUploads,
            UploadCount = uploadCount,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        return new SeededLink(orgId, vendorId, linkId, token, orgName, vendorName);
    }

    /// <summary>
    /// Posts <c>/api/auth/login</c> for an existing user and returns a cookie-authenticated
    /// client. Caller is responsible for having registered <paramref name="email"/> first —
    /// typically via <see cref="RegisterAndLoginAsync"/> in arrangement, then discarding that
    /// client and logging back in with <c>LoginAsync</c> when the test scenario needs an
    /// independent login (e.g. a second session for the same user, or proving the login path
    /// itself works given the user already exists).
    /// </summary>
    protected async Task<AuthenticatedClient> LoginAsync(
        string email, string password = "Password1234")
    {
        var client = CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        return new AuthenticatedClient(
            client,
            data.GetProperty("userId").GetGuid(),
            data.GetProperty("organizationId").GetGuid(),
            email);
    }

    protected sealed record AuthenticatedClient(HttpClient Client, Guid UserId, Guid OrgId, string Email);
}
