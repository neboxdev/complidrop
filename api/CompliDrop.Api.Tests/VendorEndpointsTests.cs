using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Endpoints;
using CompliDrop.Api.Entities;
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

    private static async Task<HttpResponseMessage> AddRuleAsync(
        HttpClient client, Guid templateId, string documentType, string fieldName, string op, string? expectedValue = null) =>
        await client.PostAsJsonAsync($"/api/compliance/templates/{templateId}/rules", new
        {
            documentType, fieldName, @operator = op, expectedValue, errorMessage = "required", sortOrder = 1,
        });

    private async Task SeedVendorDocAsync(
        Guid orgId, Guid vendorId, string documentType, ComplianceStatus status, DateTime? expirationDate = null)
    {
        await using var db = CreateSystemDb();
        var now = DateTime.UtcNow;
        db.Documents.Add(new Document
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "doc.pdf",
            BlobStorageUrl = "memory://x",
            BlobStoragePath = $"path/{Guid.NewGuid():N}",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = documentType,
            ComplianceStatus = status,
            ExpirationDate = expirationDate,
            ExtractionStatus = ExtractionStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static JsonElement CoverageFor(JsonElement[] list, Guid vendorId) =>
        list.First(v => v.GetProperty("id").GetGuid() == vendorId).GetProperty("coverage");

    [Fact]
    public async Task ListVendors_rolls_up_per_vendor_coverage_in_one_query()
    {
        // #319 FP-074: the list must answer "who is NOT ok?" — Covered / Action needed /
        // Missing: <types> / no requirements — computed server-side.
        var auth = await RegisterAndLoginAsync();
        var template = await CreateTemplateAsync(auth.Client, "Caterer");
        (await AddRuleAsync(auth.Client, template, "coi", "general_liability_limit", "required")).EnsureSuccessStatusCode();

        var missingV = await CreateVendorAsync(auth.Client, "Missing LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, missingV, template)).EnsureSuccessStatusCode();

        var coveredV = await CreateVendorAsync(auth.Client, "Covered LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, coveredV, template)).EnsureSuccessStatusCode();
        await SeedVendorDocAsync(auth.OrgId, coveredV, "coi", ComplianceStatus.Compliant);

        var actionV = await CreateVendorAsync(auth.Client, "Action LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, actionV, template)).EnsureSuccessStatusCode();
        await SeedVendorDocAsync(auth.OrgId, actionV, "coi", ComplianceStatus.NonCompliant);

        // An ExpiringSoon doc is VALID coverage (renew-soon), not a hard fail — it must read Covered,
        // matching every other surface (#319 FP-074 review): a valid-but-expiring vendor is not red.
        var expiringV = await CreateVendorAsync(auth.Client, "Expiring LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, expiringV, template)).EnsureSuccessStatusCode();
        await SeedVendorDocAsync(auth.OrgId, expiringV, "coi", ComplianceStatus.ExpiringSoon);

        var noReqV = await CreateVendorAsync(auth.Client, "NoReq LLC", null);

        var list = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/vendors"))
            .GetProperty("data").EnumerateArray().ToArray();

        CoverageFor(list, missingV).GetProperty("status").GetString().Should().Be("Missing");
        CoverageFor(list, missingV).GetProperty("missingTypes").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("insurance");
        CoverageFor(list, coveredV).GetProperty("status").GetString().Should().Be("Covered");
        CoverageFor(list, actionV).GetProperty("status").GetString().Should().Be("ActionNeeded");
        CoverageFor(list, expiringV).GetProperty("status").GetString().Should().Be("Covered");
        CoverageFor(list, noReqV).GetProperty("status").GetString().Should().Be("NoRequirements");
    }

    [Fact]
    public async Task Covered_vendor_surfaces_the_nearest_expiration_as_its_covered_through_horizon()
    {
        // #399: "Covered" means current AS OF TODAY, not covered on a future event date. The rollup
        // surfaces the nearest expiration among the covered required docs — the date coverage as a
        // whole lapses — so a venue manager can eyeball it against their event. This is display-only:
        // it must NOT change which statuses count as Covered (pinned by the assertions below).
        var auth = await RegisterAndLoginAsync();
        var template = await CreateTemplateAsync(auth.Client, "Caterer");
        // Two required document types → "covered through" is the MIN expiry across them.
        (await AddRuleAsync(auth.Client, template, "coi", "general_liability_limit", "required")).EnsureSuccessStatusCode();
        (await AddRuleAsync(auth.Client, template, "license", "license_number", "required")).EnsureSuccessStatusCode();

        var today = DateTime.UtcNow.Date;
        var nearExpiry = today.AddDays(90);
        var farExpiry = today.AddDays(200);

        // Dated-covered vendor: both required docs compliant, license expires FIRST (day 90).
        var datedV = await CreateVendorAsync(auth.Client, "Dated LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, datedV, template)).EnsureSuccessStatusCode();
        await SeedVendorDocAsync(auth.OrgId, datedV, "coi", ComplianceStatus.Compliant, farExpiry);
        await SeedVendorDocAsync(auth.OrgId, datedV, "license", ComplianceStatus.Compliant, nearExpiry);

        // Undated-covered vendor: covered, but its docs carry no expiration → no horizon to show.
        var undatedV = await CreateVendorAsync(auth.Client, "Undated LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, undatedV, template)).EnsureSuccessStatusCode();
        await SeedVendorDocAsync(auth.OrgId, undatedV, "coi", ComplianceStatus.Compliant, expirationDate: null);
        await SeedVendorDocAsync(auth.OrgId, undatedV, "license", ComplianceStatus.Compliant, expirationDate: null);

        var list = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/vendors"))
            .GetProperty("data").EnumerateArray().ToArray();

        // Covered membership UNCHANGED: both vendors read Covered exactly as before.
        var dated = CoverageFor(list, datedV);
        dated.GetProperty("status").GetString().Should().Be("Covered");
        // The horizon is the NEAREST expiry (license, day 90) — not the later coi (day 200).
        dated.GetProperty("coveredThrough").GetDateTime().Date.Should().Be(nearExpiry);

        var undated = CoverageFor(list, undatedV);
        undated.GetProperty("status").GetString().Should().Be("Covered");
        undated.GetProperty("coveredThrough").ValueKind.Should().Be(JsonValueKind.Null,
            "a Covered vendor whose docs have no expiration has no 'covered through' date to show");

        // A non-Covered vendor never carries a horizon (it's meaningless there).
        var missingV = await CreateVendorAsync(auth.Client, "Gaps LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, missingV, template)).EnsureSuccessStatusCode();
        var list2 = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/vendors"))
            .GetProperty("data").EnumerateArray().ToArray();
        var missing = CoverageFor(list2, missingV);
        missing.GetProperty("status").GetString().Should().Be("Missing");
        missing.GetProperty("coveredThrough").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Covered_vendor_with_a_mix_of_dated_and_undated_docs_keeps_the_dated_horizon()
    {
        // #399 regression guard: a Covered vendor whose required docs MIX a dated covered doc with an
        // undated covered doc must surface the dated doc's expiry as its "covered through" horizon — the
        // undated doc is SKIPPED, never allowed to reset/null the accumulated horizon. This is the common
        // production shape (a COI that carries an expiry + a business license that doesn't). The all-dated
        // (min taken) and all-undated (null) cases are pinned by the test above, but NEITHER exercises a
        // dated horizon SURVIVING the presence of an undated covered doc — so a fold that nulled the
        // horizon on encountering an undated doc would pass both yet corrupt this case.
        var auth = await RegisterAndLoginAsync();
        var template = await CreateTemplateAsync(auth.Client, "Caterer");
        // coi is added FIRST (processed first → sets the horizon); license SECOND is covered by an
        // UNDATED doc that must not null the horizon already set by the coi.
        (await AddRuleAsync(auth.Client, template, "coi", "general_liability_limit", "required")).EnsureSuccessStatusCode();
        (await AddRuleAsync(auth.Client, template, "license", "license_number", "required")).EnsureSuccessStatusCode();

        var today = DateTime.UtcNow.Date;
        var datedExpiry = today.AddDays(120);

        var mixedV = await CreateVendorAsync(auth.Client, "Mixed LLC", null);
        (await UpdateVendorTemplateAsync(auth.Client, mixedV, template)).EnsureSuccessStatusCode();
        // Dated covered doc (coi, day 120) + undated covered doc (license, no expiration).
        await SeedVendorDocAsync(auth.OrgId, mixedV, "coi", ComplianceStatus.Compliant, datedExpiry);
        await SeedVendorDocAsync(auth.OrgId, mixedV, "license", ComplianceStatus.Compliant, expirationDate: null);

        var list = (await auth.Client.GetFromJsonAsync<JsonElement>("/api/vendors"))
            .GetProperty("data").EnumerateArray().ToArray();

        var mixed = CoverageFor(list, mixedV);
        // Still Covered — both required types are covered…
        mixed.GetProperty("status").GetString().Should().Be("Covered");
        // …and the horizon is the DATED doc's expiry: the undated license was skipped, not allowed to
        // null the accumulated horizon. A regression that reset coveredThrough on the undated doc would
        // yield JSON null here and fail this assertion.
        mixed.GetProperty("coveredThrough").GetDateTime().Date.Should().Be(datedExpiry);
    }

    [Fact]
    public async Task Adding_a_duplicate_requirement_is_rejected_409()
    {
        // #319 FP-081: the same (documentType, fieldName, operator) added twice produces confusing
        // double sentences + double failures. The backend dedupes (the frontend also grays it out).
        var auth = await RegisterAndLoginAsync();
        var template = await CreateTemplateAsync(auth.Client, "Caterer");
        (await AddRuleAsync(auth.Client, template, "coi", "general_liability_limit", "min_value", "1000000"))
            .EnsureSuccessStatusCode();

        var dup = await AddRuleAsync(auth.Client, template, "coi", "general_liability_limit", "min_value", "2000000");
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(dup)).Should().Be("complianceRule.duplicate");

        // A DIFFERENT operator on the same field is a distinct requirement — still allowed.
        (await AddRuleAsync(auth.Client, template, "coi", "general_liability_limit", "required"))
            .EnsureSuccessStatusCode();
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

    // ───────── #340: a dead contact email surfaces on the vendor ─────────

    [Fact]
    public async Task A_suppressed_contact_email_surfaces_on_the_vendor_detail_and_list()
    {
        var auth = await RegisterAndLoginAsync();
        // Vendor email stored as-typed (mixed case); the suppression stores lowercased — the match must be
        // case-insensitive.
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "Ops@Acme.test");
        await using (var db = CreateSystemDb())
        {
            db.EmailSuppressions.Add(new EmailSuppression
            {
                Id = Guid.NewGuid(), OrganizationId = auth.OrgId, Email = "ops@acme.test",
                Reason = EmailSuppressionReason.Complained, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var detail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/vendors/{vendorId}");
        detail.GetProperty("data").GetProperty("contactEmailStatus").GetString().Should().Be("complained");

        var list = await auth.Client.GetFromJsonAsync<JsonElement>("/api/vendors");
        var row = list.GetProperty("data").EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == vendorId);
        row.GetProperty("contactEmailStatus").GetString().Should().Be("complained");
    }

    [Fact]
    public async Task A_deliverable_contact_email_has_a_null_status()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "ops@acme.test");

        var detail = await auth.Client.GetFromJsonAsync<JsonElement>($"/api/vendors/{vendorId}");
        detail.GetProperty("data").GetProperty("contactEmailStatus").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_suppression_in_one_org_does_not_surface_on_another_orgs_vendor_with_the_same_email()
    {
        // The SAME address can be dead for org A but deliverable for org B. The vendor badge read scopes
        // EmailSuppression ONLY via the AppDbContext tenant query filter (the detail query has no explicit
        // OrganizationId), so this pins that org B never sees org A's suppression — a dropped query filter
        // or a stray IgnoreQueryFilters would falsely light B's badge (a cross-tenant leak).
        const string shared = "shared@vendor.test";
        var orgA = await RegisterAndLoginAsync();
        var vendorA = await CreateVendorAsync(orgA.Client, "Acme A", shared);
        await using (var db = CreateSystemDb())
        {
            db.EmailSuppressions.Add(new EmailSuppression
            {
                Id = Guid.NewGuid(), OrganizationId = orgA.OrgId, Email = shared,
                Reason = EmailSuppressionReason.Bounced, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var orgB = await RegisterAndLoginAsync();
        var vendorB = await CreateVendorAsync(orgB.Client, "Acme B", shared);

        // Org A sees its own suppression…
        var detailA = await orgA.Client.GetFromJsonAsync<JsonElement>($"/api/vendors/{vendorA}");
        detailA.GetProperty("data").GetProperty("contactEmailStatus").GetString().Should().Be("bounced");

        // …Org B, with the SAME contact email, sees nothing — neither on detail nor list.
        var detailB = await orgB.Client.GetFromJsonAsync<JsonElement>($"/api/vendors/{vendorB}");
        detailB.GetProperty("data").GetProperty("contactEmailStatus").ValueKind.Should().Be(JsonValueKind.Null);

        var listB = await orgB.Client.GetFromJsonAsync<JsonElement>("/api/vendors");
        var rowB = listB.GetProperty("data").EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == vendorB);
        rowB.GetProperty("contactEmailStatus").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Vendor_contact_email_is_trimmed_on_write_so_it_round_trips_against_the_suppression_key()
    {
        // #340 regression: ContactEmail must be trimmed on write (like Name) so a padded address matches the
        // Trim()'d suppression key. Without it the worker's recipient match misses and a bounced/complained
        // vendor keeps getting reminders (and the badge never shows).
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme", "  ops@acme.test  ");

        await using var db = CreateSystemDb();
        var vendor = await db.Vendors.IgnoreQueryFilters().SingleAsync(v => v.Id == vendorId);
        vendor.ContactEmail.Should().Be("ops@acme.test");
    }

    // ---- #369: contact email format validation -------------------------------------------------
    // The vendors list add-form guarded this (FP-076); the detail edit form did not, and the API
    // only trimmed. A typo saved through the edit path returned 200 OK and then broke every
    // reminder send silently — sends retry in place (ADR 0025) and surface nothing to the operator.
    // The API is the authoritative gate because it is reachable without either form.
    //
    // The accept/reject corpus is NOT inlined here: it is loaded from the SHARED fixture
    // docs/fixtures/contact-email-cases.json, the same file frontend/src/lib/contact-email.test.ts
    // reads. The first review pass found hand-maintained parallel lists were already unequal at
    // introduction AND that the two \s-based regexes genuinely disagreed on real input (.NET's \s
    // includes U+0085 and excludes U+FEFF; JS's is the reverse). One list makes "the two
    // implementations agree" mechanical instead of a comment nobody re-checks.

    private sealed record ContactEmailCases(
        string[] Valid,
        string[] Malformed,
        PaddedCase[] PaddedValid,
        string[] Blank);

    private sealed record PaddedCase(string Raw, string Normalized);

    private static readonly ContactEmailCases Cases = LoadContactEmailCases();

    private static ContactEmailCases LoadContactEmailCases()
    {
        // Copied next to the test assembly by the csproj <None Include ... Link="SharedFixtures\">.
        var path = Path.Combine(AppContext.BaseDirectory, "SharedFixtures", "contact-email-cases.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Shared contact-email corpus not found at {path}. It is the single source both this " +
                "suite and frontend/src/lib/contact-email.test.ts read (#369) — do not inline the " +
                "cases here instead.", path);

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<ContactEmailCases>(File.ReadAllText(path), opts)
               ?? throw new InvalidOperationException($"Could not parse {path}");
    }

    public static TheoryData<string> MalformedEmails()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Malformed) data.Add(c);
        return data;
    }

    public static TheoryData<string> ValidEmails()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Valid) data.Add(c);
        return data;
    }

    public static TheoryData<string, string> PaddedValidEmails()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in Cases.PaddedValid) data.Add(c.Raw, c.Normalized);
        return data;
    }

    public static TheoryData<string> BlankEmails()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Blank) data.Add(c);
        return data;
    }

    /// <summary>Renders invisible code points so a failure message names the character.</summary>
    private static string Show(string s) =>
        string.Concat(s.Select(ch => ch is >= ' ' and <= '~' ? ch.ToString() : $"\\u{(int)ch:X4}"));

    [Fact]
    public void The_shared_corpus_loaded_and_is_non_trivial()
    {
        // Guards every theory below: a silently-missing or emptied fixture would make them all
        // vacuously pass with zero cases.
        Cases.Valid.Length.Should().BeGreaterThan(3);
        Cases.Malformed.Length.Should().BeGreaterThan(10);
        Cases.PaddedValid.Length.Should().BeGreaterThan(3);
        Cases.Blank.Length.Should().BeGreaterThan(3);
    }

    [Theory]
    [MemberData(nameof(MalformedEmails))]
    public void The_predicate_rejects_every_malformed_case_in_the_shared_corpus(string bad)
    {
        // Unit-level mirror of the frontend's it.each over the SAME list — this is the assertion
        // that actually pins cross-language agreement, including the code points the two engines'
        // \s classes disagree about (U+0085, U+FEFF) and the C0 controls Postgres cannot store.
        ContactEmail.IsWellFormed(bad).Should().BeFalse($"{Show(bad)} must be rejected");
    }

    [Theory]
    [MemberData(nameof(ValidEmails))]
    public void The_predicate_accepts_every_valid_case_in_the_shared_corpus(string good)
    {
        // Includes sample-vendor@example.com (#238 seeds it — rejecting it would break the
        // one-click demo) and a non-ASCII address (the predicate must not become ASCII-only).
        ContactEmail.IsWellFormed(good).Should().BeTrue($"{Show(good)} must be accepted");
    }

    [Theory]
    [MemberData(nameof(PaddedValidEmails))]
    public void Normalization_strips_the_same_edges_the_frontend_strips(string raw, string normalized)
    {
        // The BOM/NEL rows are the ones .NET Trim() and JS .trim() disagree on. Both sides strip
        // via the shared explicit character class instead, so these must agree exactly.
        ContactEmail.Normalize(raw).Should().Be(normalized, $"{Show(raw)} normalizes to its bare address");
        ContactEmail.IsWellFormed(raw).Should().BeTrue($"{Show(raw)} is valid once stripped");
    }

    [Theory]
    [MemberData(nameof(BlankEmails))]
    public void Blank_normalizes_to_null_and_stays_valid(string blank)
    {
        // Load-bearing: a vendor with no contact email is a supported state.
        ContactEmail.Normalize(blank).Should().BeNull($"{Show(blank)} is absent, not empty string");
        ContactEmail.IsWellFormed(blank).Should().BeTrue($"{Show(blank)} must not become a 400");
    }

    [Theory]
    [MemberData(nameof(MalformedEmails))]
    public async Task Creating_a_vendor_with_a_malformed_contact_email_is_400_and_stores_nothing(string bad)
    {
        var auth = await RegisterAndLoginAsync();

        var resp = await auth.Client.PostAsJsonAsync("/api/vendors", new
        {
            name = "Acme Catering",
            contactEmail = bad,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });

        // Specifically a 400, not a 500: the NUL case reaches Postgres as SQLSTATE 22021 without
        // the control-character exclusion, exactly like an over-length value without the cap.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{Show(bad)} is not a usable address");
        (await ErrorCode(resp)).Should().Be("validation.contactEmail");

        await using var db = CreateSystemDb();
        (await db.Vendors.AnyAsync(v => v.Name == "Acme Catering"))
            .Should().BeFalse("the rejected create must not land");
    }

    [Theory]
    [MemberData(nameof(MalformedEmails))]
    public async Task Updating_a_vendor_with_a_malformed_contact_email_is_400_and_preserves_the_stored_address(string bad)
    {
        // This is the path #369 actually reports — where a contact email gets corrected, and mistyped.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", "ops@acme.test");

        var resp = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "Acme Catering",
            contactEmail = bad,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{Show(bad)} is not a usable address");
        (await ErrorCode(resp)).Should().Be("validation.contactEmail");

        await using var db = CreateSystemDb();
        (await db.Vendors.SingleAsync(v => v.Id == vendorId)).ContactEmail
            .Should().Be("ops@acme.test", "a rejected update must not clobber the good address");
    }

    [Fact]
    public async Task A_blank_contact_email_stays_acceptable_on_both_write_paths()
    {
        // Load-bearing at the HTTP level too: if the format gate turned blank into a 400, every
        // vendor without a contact email would become unsaveable.
        var auth = await RegisterAndLoginAsync();

        foreach (var blank in new[] { null, "", "   " })
        {
            var created = await auth.Client.PostAsJsonAsync("/api/vendors", new
            {
                name = $"No Contact {blank?.Length ?? -1}",
                contactEmail = blank,
                contactPhone = (string?)null,
                category = (string?)null,
                complianceTemplateId = (Guid?)null,
            });
            created.StatusCode.Should().Be(HttpStatusCode.OK, $"blank '{blank ?? "<null>"}' must be accepted");

            var id = (await Data(created)).GetProperty("id").GetGuid();
            var updated = await auth.Client.PutAsJsonAsync($"/api/vendors/{id}", new
            {
                name = "Still No Contact",
                contactEmail = blank,
                contactPhone = (string?)null,
                category = (string?)null,
                complianceTemplateId = (Guid?)null,
            });
            updated.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var db = CreateSystemDb();
            (await db.Vendors.SingleAsync(v => v.Id == id)).ContactEmail
                .Should().BeNull("blank normalizes to null, not empty string");
        }
    }

    [Fact]
    public async Task A_valid_contact_email_is_accepted_and_trimmed_on_update()
    {
        // The trim is #340's rule: the stored value must round-trip EXACTLY against the
        // per-(org, email) suppression key the Resend webhook writes Trim()'d.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Acme Catering", "ops@acme.test");

        var resp = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "Acme Catering",
            contactEmail = "  New.Ops+coi@sub.acme.co.uk  ",
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Vendors.SingleAsync(v => v.Id == vendorId)).ContactEmail
            .Should().Be("New.Ops+coi@sub.acme.co.uk", "trimmed, with the vendor's display casing preserved");
    }

    [Fact]
    public async Task An_over_length_contact_email_is_400_rather_than_a_500_from_the_varchar_256_column()
    {
        // Vendor.ContactEmail is varchar(256) and Npgsql does NOT truncate, so without the length
        // cap in the validator this write raises 22001 and surfaces as a 500 (a slice of #389's
        // class on this field). The at-limit address must still be accepted — the cap is a boundary,
        // not a margin.
        var auth = await RegisterAndLoginAsync();
        const string domain = "@acme.com";
        var atLimit = new string('a', ContactEmail.MaxLength - domain.Length) + domain;
        atLimit.Length.Should().Be(ContactEmail.MaxLength);

        var ok = await auth.Client.PostAsJsonAsync("/api/vendors", new
        {
            name = "At Limit",
            contactEmail = atLimit,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "an address exactly at the column width is storable");

        var tooLong = await auth.Client.PostAsJsonAsync("/api/vendors", new
        {
            name = "Over Limit",
            contactEmail = "a" + atLimit,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "one char over the column width is a 400, not a 500");
        (await ErrorCode(tooLong)).Should().Be("validation.contactEmail");
    }

    [Fact]
    public void The_seeded_sample_vendor_address_satisfies_the_validator()
    {
        // #238 seeds this address on the sample vendor. Referenced through the constant (made
        // internal for this) rather than a copied literal: a copy would still pass if the seed
        // were changed to something the validator rejects, i.e. it could not detect the
        // regression it is named for.
        ContactEmail.IsWellFormed(SampleEndpoints.SampleVendorEmail).Should().BeTrue();
    }

    // ---- #369: vendors whose STORED address is already malformed --------------------------------
    // These rows exist: they are what the previously-unguarded edit path wrote. The deliberate
    // decision (recorded in .claude/reviewers.md and the PR body) is BLOCK-UNTIL-FIXED — the update
    // gate validates the submitted address whether or not this request changed it, so the operator
    // must correct a field that is genuinely broken before other edits land. Rationale: the address
    // is actively failing (no reminder can reach it), the detail form surfaces the reason inline on
    // load with Save disabled, and the fix is one edit. The alternative (validate only on change)
    // would let a known-dead address persist indefinitely behind unrelated saves.

    [Fact]
    public async Task A_vendor_whose_stored_address_is_already_malformed_must_fix_it_before_other_edits_land()
    {
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Legacy Vendor", "ops@acme.test");

        // Simulate the pre-fix row: write a malformed address straight to the DB, bypassing the
        // new gate (this is exactly what the unguarded edit path used to persist).
        await using (var seed = CreateSystemDb())
        {
            var v = await seed.Vendors.IgnoreQueryFilters().SingleAsync(x => x.Id == vendorId);
            v.ContactEmail = "Jane Smith <jane@acme.com>";
            await seed.SaveChangesAsync();
        }

        // Renaming while echoing the stored bad address is refused — the gate does not care that
        // this request didn't introduce the typo.
        var blocked = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "Legacy Vendor Renamed",
            contactEmail = "Jane Smith <jane@acme.com>",
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(blocked)).Should().Be("validation.contactEmail");

        // Correcting the address in the same save is the intended escape hatch, and it lands.
        var fixedUp = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "Legacy Vendor Renamed",
            contactEmail = "jane@acme.com",
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });
        fixedUp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateSystemDb();
        var after = await db.Vendors.SingleAsync(v => v.Id == vendorId);
        after.ContactEmail.Should().Be("jane@acme.com");
        after.Name.Should().Be("Legacy Vendor Renamed");
    }

    [Fact]
    public async Task Clearing_a_legacy_malformed_address_is_also_accepted()
    {
        // The other escape hatch: an operator who doesn't know the right address can blank the
        // field rather than being stuck. Blank is a supported state, so this must not 400.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await CreateVendorAsync(auth.Client, "Legacy Vendor", "ops@acme.test");

        await using (var seed = CreateSystemDb())
        {
            var v = await seed.Vendors.IgnoreQueryFilters().SingleAsync(x => x.Id == vendorId);
            v.ContactEmail = "jane@acme,com";
            await seed.SaveChangesAsync();
        }

        var cleared = await auth.Client.PutAsJsonAsync($"/api/vendors/{vendorId}", new
        {
            name = "Legacy Vendor",
            contactEmail = (string?)null,
            contactPhone = (string?)null,
            category = (string?)null,
            complianceTemplateId = (Guid?)null,
        });

        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Vendors.SingleAsync(v => v.Id == vendorId)).ContactEmail.Should().BeNull();
    }
}
