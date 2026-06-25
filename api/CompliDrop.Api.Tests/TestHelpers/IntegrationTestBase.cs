using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

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
    /// Constructs a <see cref="SystemDbContext"/> against the test container with the
    /// <see cref="AuditSaveChangesInterceptor"/> wired so seeds/reads via the harness see the
    /// same behavior as production code paths (UpdatedAt auto-set, soft-delete translation,
    /// audit-log emission when a user is supplied). Passing <c>null</c> for the user skips
    /// audit-log emission but still applies UpdatedAt — the right semantic for test fixture
    /// setup.
    /// <para/>
    /// Note: because the interceptor is wired, <c>db.Remove(entity)</c> on a soft-deletable
    /// entity here is translated to a soft delete (UPDATE DeletedAt=now). If a test needs a
    /// genuine hard delete, run it directly against the EF Core context without going through
    /// this helper, or use <c>ExecuteDeleteAsync</c>.
    /// </summary>
    protected SystemDbContext CreateSystemDb(ICurrentUser? user = null) =>
        new(new DbContextOptionsBuilder<SystemDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(() => user))
            .Options);

    protected AppDbContext CreateAppDb(ICurrentUser user) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(() => user))
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
