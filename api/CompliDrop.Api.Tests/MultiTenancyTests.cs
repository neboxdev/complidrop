using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

[Collection("integration")]
public class MultiTenancyTests : IAsyncLifetime
{
    private Guid _orgA;
    private Guid _orgB;
    private Guid _docA;
    private Guid _docB;
    private Guid _vendorA;
    private Guid _vendorB;

    public async Task InitializeAsync()
    {
        _orgA = Guid.NewGuid();
        _orgB = Guid.NewGuid();
        _docA = Guid.NewGuid();
        _docB = Guid.NewGuid();
        _vendorA = Guid.NewGuid();
        _vendorB = Guid.NewGuid();

        await using var sys = DbContextFactory.CreateSystem();
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

    public async Task DisposeAsync()
    {
        await using var sys = DbContextFactory.CreateSystem();
        await sys.Organizations
            .Where(o => o.Id == _orgA || o.Id == _orgB)
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task OrgA_only_sees_OrgA_documents()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgA };
        await using var db = DbContextFactory.CreateApp(user);

        var docs = await db.Documents.ToListAsync();

        docs.Should().ContainSingle().Which.Id.Should().Be(_docA);
        docs.Should().NotContain(d => d.Id == _docB);
    }

    [Fact]
    public async Task OrgB_only_sees_OrgB_documents()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgB };
        await using var db = DbContextFactory.CreateApp(user);

        var docs = await db.Documents.ToListAsync();

        docs.Should().ContainSingle().Which.Id.Should().Be(_docB);
        docs.Should().NotContain(d => d.Id == _docA);
    }

    [Fact]
    public async Task Anonymous_sees_no_tenant_documents()
    {
        var user = new FakeCurrentUser { OrganizationId = null };
        await using var db = DbContextFactory.CreateApp(user);

        var docs = await db.Documents.Where(d => d.Id == _docA || d.Id == _docB).ToListAsync();

        docs.Should().BeEmpty();
    }

    [Fact]
    public async Task OrgA_only_sees_OrgA_vendors()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgA };
        await using var db = DbContextFactory.CreateApp(user);

        var vendors = await db.Vendors.ToListAsync();

        vendors.Should().ContainSingle().Which.Id.Should().Be(_vendorA);
    }

    [Fact]
    public async Task SystemDbContext_sees_both_orgs()
    {
        await using var sys = DbContextFactory.CreateSystem();

        var docs = await sys.Documents
            .Where(d => d.Id == _docA || d.Id == _docB)
            .ToListAsync();

        docs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Soft_deleted_document_hidden_from_tenant_query()
    {
        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = _orgA };
        await using (var db = DbContextFactory.CreateApp(user))
        {
            var doc = await db.Documents.FirstAsync(d => d.Id == _docA);
            db.Documents.Remove(doc); // interceptor converts to soft delete
            await db.SaveChangesAsync();
        }

        await using var db2 = DbContextFactory.CreateApp(user);
        var visible = await db2.Documents.Where(d => d.Id == _docA).ToListAsync();
        visible.Should().BeEmpty();

        await using var sys = DbContextFactory.CreateSystem();
        var raw = await sys.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == _docA);
        raw.DeletedAt.Should().NotBeNull();
    }
}
