using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Verifies <c>AppDbContext.CurrentOrgId</c>-driven query filters: an authenticated user only
/// ever sees rows for their org, anonymous queries see nothing, and <see cref="SystemDbContext"/>
/// skips the filter. Now inherits <see cref="IntegrationTestBase"/> so it shares the same
/// Respawn-based reset strategy as the rest of the integration suite (previously self-managed
/// cleanup by deleting both orgs in <c>DisposeAsync</c>, which left orphan AuditLog rows behind
/// — the Respawn wipe in <c>InitializeAsync</c> now handles everything).
/// </summary>
public class MultiTenancyTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private Guid _orgA;
    private Guid _orgB;
    private Guid _docA;
    private Guid _docB;
    private Guid _vendorA;
    private Guid _vendorB;

    public override async Task InitializeAsync()
    {
        // Run the base reset first so any prior test's data is gone before we seed.
        await base.InitializeAsync();

        _orgA = Guid.NewGuid();
        _orgB = Guid.NewGuid();
        _docA = Guid.NewGuid();
        _docB = Guid.NewGuid();
        _vendorA = Guid.NewGuid();
        _vendorB = Guid.NewGuid();

        // No ICurrentUser — this is fixture-setup, not user-driven work, so we skip audit-log
        // emission while still benefiting from the interceptor's UpdatedAt auto-set semantics.
        await using var sys = CreateSystemDb();
        var now = DateTime.UtcNow;

        sys.Organizations.AddRange(
            new Organization { Id = _orgA, Name = $"OrgA-{_orgA:N}", CreatedAt = now, UpdatedAt = now },
            new Organization { Id = _orgB, Name = $"OrgB-{_orgB:N}", CreatedAt = now, UpdatedAt = now });

        sys.Vendors.AddRange(
            new Vendor { Id = _vendorA, OrganizationId = _orgA, Name = "VendorA", CreatedAt = now, UpdatedAt = now },
            new Vendor { Id = _vendorB, OrganizationId = _orgB, Name = "VendorB", CreatedAt = now, UpdatedAt = now });

        sys.Documents.AddRange(
            new Document
            {
                Id = _docA,
                OrganizationId = _orgA,
                VendorId = _vendorA,
                OriginalFileName = "A.pdf",
                BlobStorageUrl = "blob://A",
                FileSizeBytes = 100,
                ContentType = "application/pdf",
                CreatedAt = now,
                UpdatedAt = now
            },
            new Document
            {
                Id = _docB,
                OrganizationId = _orgB,
                VendorId = _vendorB,
                OriginalFileName = "B.pdf",
                BlobStorageUrl = "blob://B",
                FileSizeBytes = 100,
                ContentType = "application/pdf",
                CreatedAt = now,
                UpdatedAt = now
            });

        await sys.SaveChangesAsync();
    }

    [Fact]
    public async Task OrgA_only_sees_OrgA_documents()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgA };
        await using var db = CreateAppDb(user);

        var docs = await db.Documents.ToListAsync();

        docs.Should().ContainSingle().Which.Id.Should().Be(_docA);
        docs.Should().NotContain(d => d.Id == _docB);
    }

    [Fact]
    public async Task OrgB_only_sees_OrgB_documents()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgB };
        await using var db = CreateAppDb(user);

        var docs = await db.Documents.ToListAsync();

        docs.Should().ContainSingle().Which.Id.Should().Be(_docB);
        docs.Should().NotContain(d => d.Id == _docA);
    }

    [Fact]
    public async Task Anonymous_sees_no_tenant_documents()
    {
        var user = new FakeCurrentUser { OrganizationId = null };
        await using var db = CreateAppDb(user);

        var docs = await db.Documents.Where(d => d.Id == _docA || d.Id == _docB).ToListAsync();

        docs.Should().BeEmpty();
    }

    [Fact]
    public async Task OrgA_only_sees_OrgA_vendors()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgA };
        await using var db = CreateAppDb(user);

        var vendors = await db.Vendors.ToListAsync();

        vendors.Should().ContainSingle().Which.Id.Should().Be(_vendorA);
    }

    [Fact]
    public async Task SystemDbContext_sees_both_orgs()
    {
        await using var sys = CreateSystemDb();

        var docs = await sys.Documents
            .Where(d => d.Id == _docA || d.Id == _docB)
            .ToListAsync();

        docs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Soft_deleted_document_hidden_from_tenant_query()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgA };
        await using (var db = CreateAppDb(user))
        {
            var doc = await db.Documents.FirstAsync(d => d.Id == _docA);
            db.Documents.Remove(doc); // interceptor converts to soft delete
            await db.SaveChangesAsync();
        }

        await using var db2 = CreateAppDb(user);
        var visible = await db2.Documents.Where(d => d.Id == _docA).ToListAsync();
        visible.Should().BeEmpty();

        await using var sys = CreateSystemDb();
        var raw = await sys.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == _docA);
        raw.DeletedAt.Should().NotBeNull();
    }
}
