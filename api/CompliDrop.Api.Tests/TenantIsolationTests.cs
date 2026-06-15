using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Two-org cross-tenant authorization regression suite (#242). For every id-taking endpoint that
/// relies on the <c>AppDbContext</c> global query filter, an org-A caller passing org-B ids must get
/// 404 / no effect — proving the filter is ACTIVE on that exact path, and (for the filter-LESS child
/// entities ComplianceCheck / ComplianceRule / VendorPortalLink / ReminderLog) that the parent's org
/// is verified first. Two of these pin cross-tenant defects this audit found and fixed: the reminder
/// history leak and the portal-link revoke IDOR.
/// </summary>
public sealed class TenantIsolationTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private sealed record SeededOrg(
        Guid OrgId, Guid VendorId, Guid DocumentId, Guid TemplateId, Guid RuleId,
        Guid LinkId, Guid ReminderId, string LogRecipient);

    /// <summary>
    /// Registers a fresh org and seeds one of every tenant-owned entity (vendor, document, template
    /// + rule, active portal link, reminder log) via the system context. Returns the ids an attacker
    /// in another org would target. The returned org's HTTP client is intentionally discarded — these
    /// tests act as a DIFFERENT org.
    /// </summary>
    private async Task<SeededOrg> SeedOrgAsync(string logRecipient)
    {
        var owner = await RegisterAndLoginAsync();
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var linkId = Guid.NewGuid();

        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = owner.OrgId, Name = "Checklist", CreatedAt = now });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = ruleId, ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "1000000", SortOrder = 0
        });
        db.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = owner.OrgId, Name = "Vendor", ComplianceTemplateId = templateId, CreatedAt = now, UpdatedAt = now });
        db.Documents.Add(new Document
        {
            Id = docId, OrganizationId = owner.OrgId, VendorId = vendorId,
            OriginalFileName = "d.pdf", BlobStorageUrl = "blob://d", FileSizeBytes = 1, ContentType = "application/pdf",
            DocumentType = "coi", CreatedAt = now, UpdatedAt = now
        });
        db.VendorPortalLinks.Add(new VendorPortalLink { Id = linkId, VendorId = vendorId, Token = $"tok-{Guid.NewGuid():N}", IsActive = true, CreatedAt = now, MaxUploads = 20, UploadCount = 0 });
        var reminder = await db.Reminders.FirstAsync(r => r.OrganizationId == owner.OrgId);
        db.ReminderLogs.Add(new ReminderLog
        {
            Id = Guid.NewGuid(), ReminderId = reminder.Id, DocumentId = docId,
            RecipientEmail = logRecipient, SentAt = now, SendDate = DateOnly.FromDateTime(now), Status = ReminderLogStatus.Sent
        });
        await db.SaveChangesAsync();

        return new SeededOrg(owner.OrgId, vendorId, docId, templateId, ruleId, linkId, reminder.Id, logRecipient);
    }

    // ---------------- the two cross-tenant defects this audit fixed ----------------

    [Fact]
    public async Task Reminder_history_returns_only_the_callers_org_logs()
    {
        // BUG #242: GET /api/reminders/history read the GLOBAL most-recent-200 ReminderLog rows
        // (ReminderLog has no query filter), leaking every org's recipient emails + ids.
        var b = await SeedOrgAsync("secret-vendor@orgb.example");
        var a = await RegisterAndLoginAsync();

        // Give org A its own log so we prove the filter INCLUDES own and EXCLUDES others.
        await using (var db = CreateSystemDb())
        {
            var aVendor = new Vendor { Id = Guid.NewGuid(), OrganizationId = a.OrgId, Name = "A vendor", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var aDoc = new Document { Id = Guid.NewGuid(), OrganizationId = a.OrgId, VendorId = aVendor.Id, OriginalFileName = "a.pdf", BlobStorageUrl = "blob://a", FileSizeBytes = 1, ContentType = "application/pdf", DocumentType = "coi", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.AddRange(aVendor, aDoc);
            var aReminder = await db.Reminders.FirstAsync(r => r.OrganizationId == a.OrgId);
            db.ReminderLogs.Add(new ReminderLog { Id = Guid.NewGuid(), ReminderId = aReminder.Id, DocumentId = aDoc.Id, RecipientEmail = "vendor@orga.example", SentAt = DateTime.UtcNow, SendDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = ReminderLogStatus.Sent });
            await db.SaveChangesAsync();
        }

        var resp = await a.Client.GetAsync("/api/reminders/history");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var recipients = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")
            .EnumerateArray().Select(r => r.GetProperty("recipient").GetString()).ToArray();

        recipients.Should().Contain("vendor@orga.example", "org A sees its own reminder logs");
        recipients.Should().NotContain(b.LogRecipient, "org A must NEVER see org B's reminder-log recipients (#242)");
    }

    [Fact]
    public async Task Revoke_portal_link_cannot_deactivate_another_orgs_link()
    {
        // BUG #242: RevokePortalLink queried the filter-less VendorPortalLinks by (vendorId, linkId)
        // without first verifying the vendor belongs to the caller's org.
        var b = await SeedOrgAsync("vendor@orgb.example");
        var a = await RegisterAndLoginAsync();

        var resp = await a.Client.DeleteAsync($"/api/vendors/{b.VendorId}/portal-link/{b.LinkId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var db = CreateSystemDb();
        (await db.VendorPortalLinks.SingleAsync(l => l.Id == b.LinkId)).IsActive
            .Should().BeTrue("org A must not be able to revoke org B's portal link");
    }

    // ---------------- IDOR matrix: filtered-entity fetches ----------------

    [Fact]
    public async Task Get_document_cross_tenant_is_404()
    {
        var b = await SeedOrgAsync("v@b.example");
        var a = await RegisterAndLoginAsync();
        (await a.Client.GetAsync($"/api/documents/{b.DocumentId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_document_file_cross_tenant_is_404()
    {
        var b = await SeedOrgAsync("v@b.example");
        var a = await RegisterAndLoginAsync();
        (await a.Client.GetAsync($"/api/documents/{b.DocumentId}/file")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_vendor_cross_tenant_is_404()
    {
        var b = await SeedOrgAsync("v@b.example");
        var a = await RegisterAndLoginAsync();
        (await a.Client.GetAsync($"/api/vendors/{b.VendorId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_compliance_template_cross_tenant_is_404()
    {
        var b = await SeedOrgAsync("v@b.example");
        var a = await RegisterAndLoginAsync();
        (await a.Client.GetAsync($"/api/compliance/templates/{b.TemplateId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------- IDOR matrix: filter-LESS child entities (the riskiest paths) ----------------

    [Fact]
    public async Task Get_compliance_checks_cross_tenant_is_404()
    {
        // ComplianceCheck has no query filter; the endpoint must gate on the parent Document being
        // visible through the tenant filter first.
        var b = await SeedOrgAsync("v@b.example");
        var a = await RegisterAndLoginAsync();
        (await a.Client.GetAsync($"/api/compliance/checks/{b.DocumentId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_rule_cross_tenant_is_404_and_leaves_the_rule()
    {
        // ComplianceRule has no query filter and the delete runs raw SQL; the parent template must be
        // resolved through the tenant filter first.
        var b = await SeedOrgAsync("v@b.example");
        var a = await RegisterAndLoginAsync();

        var resp = await a.Client.DeleteAsync($"/api/compliance/templates/{b.TemplateId}/rules/{b.RuleId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var db = CreateSystemDb();
        (await db.ComplianceRules.AnyAsync(r => r.Id == b.RuleId)).Should().BeTrue("org A must not delete org B's rule");
    }
}
