using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Integration tests for the authenticated vendor management endpoints
/// (<see cref="CompliDrop.Api.Endpoints.VendorEndpoints"/>), focused on the #190
/// "email the upload link to the vendor" path. The send goes through the in-memory
/// <see cref="FakeEmailService"/> (the real Resend service self-disables with no API key),
/// so each test resets the fake AFTER arrangement to drop the registration verification
/// email and isolate the portal-invite send.
/// </summary>
public sealed class VendorEndpointsTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private FakeEmailService Email =>
        (FakeEmailService)Fixture.Factory.Services.GetRequiredService<IEmailService>();

    private static async Task<JsonElement> Data(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private static async Task<string?> ErrorCode(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString();

    private static async Task<Guid> CreateVendorAsync(HttpClient client, string name, string? contactEmail)
    {
        var resp = await client.PostAsJsonAsync("/api/vendors", new
        {
            name,
            contactEmail,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });
        resp.EnsureSuccessStatusCode();
        return (await Data(resp)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid LinkId, string Token, string Url)> GenerateLinkAsync(HttpClient client, Guid vendorId)
    {
        var resp = await client.PostAsync($"/api/vendors/{vendorId}/portal-link", null);
        resp.EnsureSuccessStatusCode();
        var data = await Data(resp);
        return (data.GetProperty("id").GetGuid(), data.GetProperty("token").GetString()!, data.GetProperty("url").GetString()!);
    }

    [Fact]
    public async Task Updating_a_vendor_with_a_blank_name_is_400_and_preserves_the_name()
    {
        // #264 / FP-074: CreateVendor always rejected blank names; the update path forgot the
        // guard, letting a whitespace name through — an invisible, unclickable row in the
        // vendors list (the name is the row's link).
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", "ops@acme.test");

        foreach (var blank in new[] { "", "   ", null })
        {
            var resp = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
            {
                name = blank,
                contactEmail = "ops@acme.test",
                contactPhone = (string?)null,
                category = (string?)null,
                complianceTemplateId = (Guid?)null,
            });

            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"name '{blank ?? "<null>"}' must be rejected");
            (await ErrorCode(resp)).Should().Be("validation.name");
        }

        await using var db = CreateSystemDb();
        (await db.Vendors.SingleAsync(v => v.Id == vendorId)).Name.Should().Be("Acme Catering");
    }

    [Fact]
    public async Task Updating_a_vendor_with_a_valid_name_trims_and_persists_it()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", "ops@acme.test");

        var resp = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "  Acme Catering LLC  ",
            contactEmail = "ops@acme.test",
            contactPhone = (string?)null,
            category = "Catering",
            complianceTemplateId = (Guid?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        var v = await db.Vendors.SingleAsync(v => v.Id == vendorId);
        v.Name.Should().Be("Acme Catering LLC");
        v.Category.Should().Be("Catering");
    }

    private static async Task<Guid> CreateTemplateAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/compliance/templates", new
        {
            name,
            description = (string?)null,
        });
        resp.EnsureSuccessStatusCode();
        return (await Data(resp)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UpdateVendorTemplateAsync(HttpClient client, Guid vendorId, Guid? templateId) =>
        client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "Acme Catering",
            contactEmail = (string?)null,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = templateId,
        });

    [Fact]
    public async Task Assigning_a_cross_org_template_is_rejected_and_never_lands()
    {
        // #273: the template id used to bind with only the DB FK as a guard, so another org's
        // template id was accepted — and the SystemDbContext evaluation path (no tenant filter)
        // would then run the foreign org's rules against this org's documents. The evaluation
        // half (defense-in-depth for rows poisoned before this guard) is pinned in
        // ComplianceCheckServiceTests.Foreign_org_template_is_ignored_by_the_system_evaluation_path.
        var orgB = await RegisterAndLoginAsync();
        var foreignTemplateId = await CreateTemplateAsync(orgB.Client, "Org B secret checklist");
        // A real rule on the foreign template — its content is exactly what #273 stops leaking.
        var rule = await orgB.Client.PostAsJsonAsync($"/api/compliance/templates/{foreignTemplateId}/rules", new
        {
            id = (Guid?)null,
            documentType = "coi",
            fieldName = "general_liability_limit",
            @operator = "min_value",
            expectedValue = "5000000",
            errorMessage = "Org B's secret threshold",
            sortOrder = 1,
        });
        rule.EnsureSuccessStatusCode();

        var orgA = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(orgA.Client, "Acme Catering", null);

        // Update path rejected; assignment never lands.
        var update = await UpdateVendorTemplateAsync(orgA.Client, vendorId, foreignTemplateId);
        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(update)).Should().Be("complianceTemplate.not_found");

        // Create path rejected; no vendor row materializes.
        var create = await orgA.Client.PostAsJsonAsync("/api/vendors", new
        {
            name = "Mole LLC",
            contactEmail = (string?)null,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = foreignTemplateId,
        });
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(create)).Should().Be("complianceTemplate.not_found");

        await using var db = CreateSystemDb();
        (await db.Vendors.SingleAsync(v => v.Id == vendorId)).ComplianceTemplateId.Should().BeNull();
        (await db.Vendors.AnyAsync(v => v.Name == "Mole LLC")).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_template_clears_the_assignment_on_its_vendors()
    {
        // #273 review: DeleteTemplate used to soft-delete the template while vendors kept the
        // stale FK — GetVendor round-trips it into the edit form, and the new assignment guard
        // would then 400 every save of that form, even for unrelated field edits.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", null);
        var templateId = await CreateTemplateAsync(auth.Client, "House rules");
        (await UpdateVendorTemplateAsync(auth.Client, vendorId, templateId)).EnsureSuccessStatusCode();

        (await auth.Client.DeleteAsync($"/api/compliance/templates/{templateId}")).EnsureSuccessStatusCode();

        await using (var db = CreateSystemDb())
        {
            (await db.Vendors.SingleAsync(v => v.Id == vendorId)).ComplianceTemplateId.Should().BeNull();
            // ExecuteUpdate bypasses the audit interceptor — the explicit audit row is the only
            // trace of the cleared assignments, and audit-ready export is the product promise.
            var cleared = await db.AuditLogs.SingleAsync(a =>
                a.Action == "vendor.template_cleared_on_template_delete" && a.EntityId == templateId);
            cleared.AfterJson.Should().Contain(vendorId.ToString());
        }

        // The B1 regression scenario: an edit-form save for that vendor (now round-tripping a
        // null template) succeeds instead of 400ing.
        var save = await UpdateVendorTemplateAsync(auth.Client, vendorId, null);
        save.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Remediation_migration_clears_stale_and_foreign_template_refs_but_keeps_valid_ones()
    {
        // Executes the EXACT statement the ClearForeignOrDeletedVendorTemplateRefs migration
        // ships (same pin pattern as the SendDate backfill test). Legacy shapes are seeded
        // directly via SystemDb — they predate the assignment-time guard and the
        // delete-clears-vendors behavior, so the API can no longer produce them.
        var orgA = await RegisterAndLoginAsync();
        var orgB = await RegisterAndLoginAsync();

        var foreignTemplateId = await CreateTemplateAsync(orgB.Client, "Org B checklist");
        var ownTemplateId = await CreateTemplateAsync(orgA.Client, "Keep me");
        var deletedTemplateId = await CreateTemplateAsync(orgA.Client, "Was deleted");

        var poisonedVendorId = await CreateVendorAsync(orgA.Client, "Poisoned LLC", null);
        var staleVendorId = await CreateVendorAsync(orgA.Client, "Stale LLC", null);
        var healthyVendorId = await CreateVendorAsync(orgA.Client, "Healthy LLC", null);
        (await UpdateVendorTemplateAsync(orgA.Client, healthyVendorId, ownTemplateId)).EnsureSuccessStatusCode();

        // The IsSystemTemplate arm: system templates belong to the synthetic system org, so
        // without that arm the migration would null EVERY system-template assignment in prod.
        var templates = await orgA.Client.GetAsync("/api/compliance/templates");
        templates.EnsureSuccessStatusCode();
        var systemTemplateId = (await Data(templates)).EnumerateArray()
            .First(t => t.GetProperty("isSystemTemplate").GetBoolean())
            .GetProperty("id").GetGuid();
        var systemVendorId = await CreateVendorAsync(orgA.Client, "System-assigned LLC", null);
        (await UpdateVendorTemplateAsync(orgA.Client, systemVendorId, systemTemplateId)).EnsureSuccessStatusCode();

        await using (var db = CreateSystemDb())
        {
            // Cross-org assignment written while the FK was the only guard.
            await db.Vendors.Where(v => v.Id == poisonedVendorId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ComplianceTemplateId, foreignTemplateId));
            // Assign-then-delete legacy shape: stale FK onto a soft-deleted template.
            await db.Vendors.Where(v => v.Id == staleVendorId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ComplianceTemplateId, deletedTemplateId));
            await db.ComplianceTemplates.Where(t => t.Id == deletedTemplateId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedAt, DateTime.UtcNow));

            // Leaked-check shape the lazy self-heal can NEVER reach: an EXPIRED document whose
            // checks were written by a pre-guard cross-org evaluation (the Expired early-return
            // exits before the no-governing-rules branch). An own-org check row on the same
            // org's live template must survive the purge.
            var now = DateTime.UtcNow;
            var foreignRuleId = Guid.NewGuid();
            var ownRuleId = Guid.NewGuid();
            db.ComplianceRules.Add(new CompliDrop.Api.Entities.ComplianceRule
            {
                Id = foreignRuleId, ComplianceTemplateId = foreignTemplateId, DocumentType = "coi",
                FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "5000000", SortOrder = 0
            });
            db.ComplianceRules.Add(new CompliDrop.Api.Entities.ComplianceRule
            {
                Id = ownRuleId, ComplianceTemplateId = ownTemplateId, DocumentType = "coi",
                FieldName = "general_liability_limit", Operator = "required", SortOrder = 0
            });
            var expiredDoc = new CompliDrop.Api.Entities.Document
            {
                Id = Guid.NewGuid(), OrganizationId = orgA.OrgId, VendorId = poisonedVendorId,
                OriginalFileName = "expired.pdf", BlobStorageUrl = "blob://x", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                ExpirationDate = now.AddDays(-30), CreatedAt = now, UpdatedAt = now
            };
            db.Documents.Add(expiredDoc);
            await db.SaveChangesAsync();

            var leakedCheckId = Guid.NewGuid();
            var ownCheckId = Guid.NewGuid();
            db.ComplianceChecks.Add(new CompliDrop.Api.Entities.ComplianceCheck
            {
                Id = leakedCheckId, DocumentId = expiredDoc.Id, ComplianceRuleId = foreignRuleId,
                IsPassed = false, Notes = "leaked", CheckedAt = now
            });
            db.ComplianceChecks.Add(new CompliDrop.Api.Entities.ComplianceCheck
            {
                Id = ownCheckId, DocumentId = expiredDoc.Id, ComplianceRuleId = ownRuleId,
                IsPassed = true, CheckedAt = now
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(
                CompliDrop.Api.Migrations.ClearForeignOrDeletedVendorTemplateRefs.UpSql);
            await db.Database.ExecuteSqlRawAsync(
                CompliDrop.Api.Migrations.ClearForeignOrDeletedVendorTemplateRefs.PurgeLeakedChecksSql);

            (await db.Vendors.SingleAsync(v => v.Id == poisonedVendorId)).ComplianceTemplateId.Should().BeNull(
                "the cross-org reference must be remediated");
            (await db.Vendors.SingleAsync(v => v.Id == staleVendorId)).ComplianceTemplateId.Should().BeNull(
                "the soft-deleted-template reference must be remediated");
            (await db.Vendors.SingleAsync(v => v.Id == healthyVendorId)).ComplianceTemplateId.Should().Be(ownTemplateId,
                "a live own-org assignment must survive the remediation");
            (await db.Vendors.SingleAsync(v => v.Id == systemVendorId)).ComplianceTemplateId.Should().Be(systemTemplateId,
                "a system-template assignment must survive the remediation");
            (await db.ComplianceChecks.AnyAsync(c => c.Id == leakedCheckId)).Should().BeFalse(
                "the cross-org leaked check row on the expired doc must be purged");
            (await db.ComplianceChecks.AnyAsync(c => c.Id == ownCheckId)).Should().BeTrue(
                "an own-org check row must survive the purge");
        }
    }

    [Fact]
    public async Task Assigning_a_system_template_or_own_template_is_accepted()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", null);

        // System template: visible to every org through the tenant filter's IsSystemTemplate arm.
        var templates = await auth.Client.GetAsync("/api/compliance/templates");
        templates.EnsureSuccessStatusCode();
        var systemTemplateId = (await Data(templates)).EnumerateArray()
            .First(t => t.GetProperty("isSystemTemplate").GetBoolean())
            .GetProperty("id").GetGuid();

        var assignSystem = await UpdateVendorTemplateAsync(auth.Client, vendorId, systemTemplateId);
        assignSystem.StatusCode.Should().Be(HttpStatusCode.OK);

        // Own template.
        var ownTemplateId = await CreateTemplateAsync(auth.Client, "House rules");
        var assignOwn = await UpdateVendorTemplateAsync(auth.Client, vendorId, ownTemplateId);
        assignOwn.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        (await db.Vendors.SingleAsync(v => v.Id == vendorId)).ComplianceTemplateId.Should().Be(ownTemplateId);

        // Clearing the assignment (null) stays allowed.
        (await UpdateVendorTemplateAsync(auth.Client, vendorId, null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Nonexistent_and_soft_deleted_template_ids_get_the_same_response_as_cross_org()
    {
        // The rejection must not disclose whether the id exists in another org — random,
        // soft-deleted, and cross-org ids all yield the identical envelope (#273).
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", null);

        var random = await UpdateVendorTemplateAsync(auth.Client, vendorId, Guid.NewGuid());
        random.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(random)).Should().Be("complianceTemplate.not_found");

        var deletedTemplateId = await CreateTemplateAsync(auth.Client, "Short-lived");
        (await auth.Client.DeleteAsync($"/api/compliance/templates/{deletedTemplateId}")).EnsureSuccessStatusCode();

        var deleted = await UpdateVendorTemplateAsync(auth.Client, vendorId, deletedTemplateId);
        deleted.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(deleted)).Should().Be("complianceTemplate.not_found");
    }

    [Fact]
    public async Task Deleting_a_vendor_deactivates_its_portal_links()
    {
        // #269: the soft-deleted vendor vanishes behind the query filter, so a still-active
        // emailed link used to NRE-500 on every click. Deletion must kill the links with it.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Doomed LLC", "x@doomed.test");
        await SetPortalEntitlementAsync(auth.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, token, _) = await GenerateLinkAsync(auth.Client, vendorId);

        var del = await auth.Client.DeleteAsync($"/api/vendors/{vendorId}");
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = CreateSystemDb())
            (await db.VendorPortalLinks.SingleAsync(l => l.Id == linkId)).IsActive.Should().BeFalse();

        // The emailed link now dies cleanly at the IsActive gate — friendly 404, not a 500.
        var portal = await CreateClient().GetAsync($"/api/portal/{token}");
        portal.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(portal)).Should().Be("vendor.portal_token_invalid");

        // ExecuteUpdate bypasses the audit interceptor, so the explicit audit row is the
        // ONLY trace of the link mutation — and audit-ready export is the product promise.
        await using var db2 = CreateSystemDb();
        var deactivation = await db2.AuditLogs.SingleAsync(a =>
            a.Action == "vendorPortalLink.deactivated_on_vendor_delete" && a.EntityId == vendorId);
        deactivation.AfterJson.Should().Contain(linkId.ToString(), "the affected link ids belong in the audit payload");
        (await db2.AuditLogs.AnyAsync(a => a.Action == "vendor.deleted" && a.EntityId == vendorId)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_vendor_with_no_links_emits_no_link_deactivation_audit_row()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Linkless LLC", null);

        (await auth.Client.DeleteAsync($"/api/vendors/{vendorId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        (await db.AuditLogs.AnyAsync(a => a.Action == "vendorPortalLink.deactivated_on_vendor_delete" && a.EntityId == vendorId))
            .Should().BeFalse("no links were deactivated");
    }

    [Fact]
    public async Task Second_delete_of_the_same_vendor_returns_404()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Twice LLC", null);
        (await auth.Client.DeleteAsync($"/api/vendors/{vendorId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await auth.Client.DeleteAsync($"/api/vendors/{vendorId}");

        second.StatusCode.Should().Be(HttpStatusCode.NotFound, "the soft-deleted vendor is hidden by the query filter");
    }

    [Fact]
    public async Task Email_portal_link_sends_to_contact_email_and_returns_recipient()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", "ops@acme.test");
        await SetPortalEntitlementAsync(auth.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, _, linkUrl) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset(); // drop the registration verification email so we assert only the portal send

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(resp)).GetProperty("sentTo").GetString().Should().Be("ops@acme.test");

        Email.Sends.Should().ContainSingle();
        var sent = Email.Sends.Single();
        sent.ToEmail.Should().Be("ops@acme.test");
        sent.HtmlBody.Should().Contain(linkUrl, "the actual portal upload link must be in the email body");
        // Org name personalises the subject; RegisterAndLoginAsync registers companyName "Test Co".
        sent.Subject.Should().Contain("Test Co");
    }

    [Fact]
    public async Task Email_portal_link_without_contact_email_is_400_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "No Contact LLC", contactEmail: null);
        await SetPortalEntitlementAsync(auth.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("vendor.no_contact_email");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_with_unknown_link_is_404_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        Email.Reset();

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{Guid.NewGuid()}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendorPortalLink.not_found");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_when_delivery_returns_null_is_502()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        await SetPortalEntitlementAsync(auth.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();
        Email.NextSendReturnsNull = true; // simulate Resend non-2xx → SendAsync returns null

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ErrorCode(resp)).Should().Be("email.send_failed");
    }

    [Fact]
    public async Task Email_portal_link_when_send_throws_timeout_is_502_not_500()
    {
        // The "resend" HttpClient has a 30s timeout → a hung send throws TaskCanceledException
        // (an OperationCanceledException whose token is the client's internal timeout, NOT the
        // request ct). The endpoint must catch it and return the friendly 502, not let it escape
        // as an unhandled 500. Gating the catch on `!ct.IsCancellationRequested` (rather than
        // `ex is not OperationCanceledException`) is what makes this pass.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        await SetPortalEntitlementAsync(auth.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();
        Email.NextSendThrows = new TaskCanceledException("simulated 30s Resend timeout");

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ErrorCode(resp)).Should().Be("email.send_failed");
    }

    [Fact]
    public async Task Email_portal_link_when_email_not_configured_is_503_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        await SetPortalEntitlementAsync(auth.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        Email.Reset();
        Email.IsEnabled = false; // Resend not configured (no API key / from-email)

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorCode(resp)).Should().Be("email.not_configured");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_for_revoked_link_is_400_and_sends_nothing()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");
        await SetPortalEntitlementAsync(auth.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, _, _) = await GenerateLinkAsync(auth.Client, vendorId);
        (await auth.Client.DeleteAsync($"/api/vendors/{vendorId}/portal-link/{linkId}")).EnsureSuccessStatusCode();
        Email.Reset();

        var resp = await auth.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(resp)).Should().Be("vendorPortalLink.inactive");
        Email.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Email_portal_link_is_tenant_scoped()
    {
        // Org A owns the vendor + link; Org B must not be able to email it.
        var orgA = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(orgA.Client, "Acme", "ops@acme.test");
        await SetPortalEntitlementAsync(orgA.OrgId, on: true); // #261: minting needs the portal entitlement
        var (linkId, _, _) = await GenerateLinkAsync(orgA.Client, vendorId);

        var orgB = await RegisterAndLoginAsync();
        Email.Reset();

        var resp = await orgB.Client.PostAsync($"/api/vendors/{vendorId}/portal-link/{linkId}/email", null);

        // Org B can't see Org A's vendor through the tenant filter → 404, nothing sent.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(resp)).Should().Be("vendor.not_found");
        Email.Sends.Should().BeEmpty();
    }
}
