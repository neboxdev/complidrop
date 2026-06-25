using System.Net;
using System.Net.Http.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests;

/// <summary>
/// HTTP-level tests for DELETE /api/compliance/templates/{tid}/rules/{rid} (#269).
/// Pre-fix, deleting any rule that had ever been evaluated 500'd forever: the
/// ComplianceCheck → ComplianceRule FK is ON DELETE RESTRICT and the endpoint never
/// removed the dependent check rows. The fix also closed the missing system-template /
/// tenant guard (DeleteRule was the only rule/template mutator without it — and
/// ComplianceRule has no tenant query filter of its own).
/// </summary>
public sealed class ComplianceRuleDeleteTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    private sealed record Seeded(Guid TemplateId, Guid RuleAId, Guid RuleBId, Guid DocId);

    /// <summary>Seeds template (+1 or 2 rules) + vendor + COI document (GL limit 1M, expiry
    /// far out). Rule A (min_value 2M) FAILS against the document; rule B (required) passes —
    /// so deleting rule A flips the verdict, proving re-evaluation genuinely ran.</summary>
    private async Task<Seeded> SeedAsync(Guid orgId, bool isSystem = false, bool twoRules = true)
    {
        var now = DateTime.UtcNow;
        var s = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var vendorId = Guid.NewGuid();

        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate
        {
            Id = s.TemplateId, OrganizationId = orgId, Name = "T", IsSystemTemplate = isSystem, CreatedAt = now
        });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = s.RuleAId, ComplianceTemplateId = s.TemplateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "2000000", SortOrder = 0
        });
        if (twoRules)
            db.ComplianceRules.Add(new ComplianceRule
            {
                Id = s.RuleBId, ComplianceTemplateId = s.TemplateId, DocumentType = "coi",
                FieldName = "general_liability_limit", Operator = "required", SortOrder = 1
            });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = "V",
            ComplianceTemplateId = s.TemplateId, CreatedAt = now, UpdatedAt = now
        });
        db.Documents.Add(new Document
        {
            Id = s.DocId, OrganizationId = orgId, VendorId = vendorId,
            OriginalFileName = "d.pdf", BlobStorageUrl = "blob://d", FileSizeBytes = 1,
            ContentType = "application/pdf", DocumentType = "coi",
            GeneralLiabilityLimit = 1_000_000m, ExpirationDate = now.AddYears(1),
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task Deleting_an_evaluated_rule_succeeds_removes_its_checks_and_reevaluates()
    {
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);

        (await auth.Client.PostAsync($"/api/compliance/check/{s.DocId}", null)).EnsureSuccessStatusCode();
        await using (var db = CreateSystemDb())
        {
            (await db.ComplianceChecks.CountAsync(c => c.DocumentId == s.DocId)).Should().Be(2);
            (await db.Documents.SingleAsync(d => d.Id == s.DocId))
                .ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant);
        }

        // Pre-#269 this returned 500: Postgres restricted the delete on the check rows.
        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db2 = CreateSystemDb();
        (await db2.ComplianceRules.AnyAsync(r => r.Id == s.RuleAId)).Should().BeFalse();
        (await db2.ComplianceChecks.AnyAsync(c => c.ComplianceRuleId == s.RuleAId)).Should().BeFalse();
        var doc = await db2.Documents.SingleAsync(d => d.Id == s.DocId);
        doc.ComplianceStatus.Should().Be(ComplianceStatus.Compliant,
            "the failing rule is gone and the remaining 'required' rule passes — re-evaluation must have run");
        (await db2.ComplianceChecks.CountAsync(c => c.DocumentId == s.DocId)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_a_rule_reevaluates_every_affected_document_not_just_one()
    {
        // Pins the plural in "re-evaluates affected documents": both docs were
        // NonCompliant solely because of rule A, so both must flip to Compliant.
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);
        Guid doc2;
        await using (var db = CreateSystemDb())
        {
            var doc1 = await db.Documents.SingleAsync(d => d.Id == s.DocId);
            doc2 = Guid.NewGuid();
            db.Documents.Add(new Document
            {
                Id = doc2, OrganizationId = auth.OrgId, VendorId = doc1.VendorId,
                OriginalFileName = "d2.pdf", BlobStorageUrl = "blob://d2", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                GeneralLiabilityLimit = 1_500_000m, ExpirationDate = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        (await auth.Client.PostAsync($"/api/compliance/check/{s.DocId}", null)).EnsureSuccessStatusCode();
        (await auth.Client.PostAsync($"/api/compliance/check/{doc2}", null)).EnsureSuccessStatusCode();

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db2 = CreateSystemDb();
        (await db2.Documents.SingleAsync(d => d.Id == s.DocId)).ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
        (await db2.Documents.SingleAsync(d => d.Id == doc2)).ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public async Task Foreign_org_checks_against_the_rule_are_cleaned_without_touching_the_foreign_document()
    {
        // Simulates the #273 state: another org's document carries a check row against
        // OUR rule. Deleting the rule must remove that row (the FK would restrict
        // otherwise) but the tenant-filtered re-eval must leave the foreign document
        // itself untouched.
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);
        Guid foreignDocId;
        await using (var db = CreateSystemDb())
        {
            var foreignOrg = Guid.NewGuid();
            var foreignVendor = Guid.NewGuid();
            foreignDocId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Organizations.Add(new Organization { Id = foreignOrg, Name = "F", CreatedAt = now, UpdatedAt = now });
            db.Vendors.Add(new Vendor { Id = foreignVendor, OrganizationId = foreignOrg, Name = "FV", CreatedAt = now, UpdatedAt = now });
            db.Documents.Add(new Document
            {
                Id = foreignDocId, OrganizationId = foreignOrg, VendorId = foreignVendor,
                OriginalFileName = "f.pdf", BlobStorageUrl = "blob://f", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                ComplianceStatus = ComplianceStatus.NonCompliant,
                CreatedAt = now, UpdatedAt = now
            });
            db.ComplianceChecks.Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(), DocumentId = foreignDocId, ComplianceRuleId = s.RuleAId,
                IsPassed = false, CheckedAt = now
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db2 = CreateSystemDb();
        (await db2.ComplianceChecks.AnyAsync(c => c.ComplianceRuleId == s.RuleAId)).Should().BeFalse();
        var foreign = await db2.Documents.SingleAsync(d => d.Id == foreignDocId);
        foreign.ComplianceStatus.Should().Be(ComplianceStatus.NonCompliant,
            "the tenant-filtered re-eval must not rewrite another org's verdict");
    }

    [Fact]
    public async Task Second_delete_of_the_same_rule_returns_404()
    {
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);
        (await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        second.StatusCode.Should().Be(HttpStatusCode.NotFound, "the second trash-button click must not 500");
    }

    [Fact]
    public async Task Deleting_the_last_rule_resets_affected_documents_to_Pending()
    {
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId, twoRules: false);
        (await auth.Client.PostAsync($"/api/compliance/check/{s.DocId}", null)).EnsureSuccessStatusCode();

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.Documents.SingleAsync(d => d.Id == s.DocId))
            .ComplianceStatus.Should().Be(ComplianceStatus.Pending, "no rules remain to judge the document");
        (await db.ComplianceChecks.CountAsync(c => c.DocumentId == s.DocId)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_rule_that_was_never_evaluated_still_works()
    {
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.AnyAsync(r => r.Id == s.RuleAId)).Should().BeFalse();
    }

    [Fact]
    public async Task System_template_rules_cannot_be_deleted()
    {
        // System templates are SHARED across orgs (tenant filter: IsSystemTemplate ||
        // own org). Every other rule/template mutator excludes them; pre-#269 DeleteRule
        // did not — any user could hard-delete a rule every org relies on.
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId, isSystem: true);

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.AnyAsync(r => r.Id == s.RuleAId)).Should().BeTrue("the shared rule must survive");
    }

    [Fact]
    public async Task Another_orgs_rule_cannot_be_deleted_even_with_both_ids()
    {
        // ComplianceRule has NO tenant query filter — the template-first lookup is the
        // only isolation. Two-org pin: org A holding org B's template+rule GUIDs gets a
        // 404 and B's rule survives.
        var orgB = await RegisterAndLoginAsync();
        var s = await SeedAsync(orgB.OrgId);
        var orgA = await RegisterAndLoginAsync();

        var resp = await orgA.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.AnyAsync(r => r.Id == s.RuleAId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_rule_cannot_be_upserted_onto_another_orgs_template()
    {
        // The upsert twin of the delete pin above (#273 review): UpsertRule's tenant isolation
        // rests entirely on the template-first lookup (tenant-filtered set + !IsSystemTemplate);
        // ComplianceRule itself has no tenant filter, so a regression here would let an org
        // create/edit rules on another org's checklist.
        var orgB = await RegisterAndLoginAsync();
        var s = await SeedAsync(orgB.OrgId);
        var orgA = await RegisterAndLoginAsync();

        var create = await orgA.Client.PostAsJsonAsync($"/api/compliance/templates/{s.TemplateId}/rules", new
        {
            id = (Guid?)null,
            documentType = "coi",
            fieldName = "general_liability_limit",
            @operator = "min_value",
            expectedValue = "1",
            errorMessage = "planted",
            sortOrder = 99,
        });
        create.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Edit attempt with org B's rule id riding the same foreign template id.
        var edit = await orgA.Client.PostAsJsonAsync($"/api/compliance/templates/{s.TemplateId}/rules", new
        {
            id = s.RuleAId,
            documentType = "coi",
            fieldName = "general_liability_limit",
            @operator = "min_value",
            expectedValue = "1",
            errorMessage = "tampered",
            sortOrder = 0,
        });
        edit.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var db = CreateSystemDb();
        (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplateId == s.TemplateId)).Should().Be(2,
            "no rule may be planted on the foreign template");
        (await db.ComplianceRules.SingleAsync(r => r.Id == s.RuleAId)).ExpectedValue.Should().Be("2000000",
            "the foreign rule must not be tampered with");
    }

    [Fact]
    public async Task A_rule_cannot_be_upserted_onto_a_shared_system_template()
    {
        // System templates are visible to every org through the filter's IsSystemTemplate arm,
        // but mutating them would change the SHARED checklist every org sees — the
        // !IsSystemTemplate clause in UpsertRule's lookup is the guard.
        var auth = await RegisterAndLoginAsync();
        // Own a system-flagged template under the caller's org (not SystemOrgId): the !IsSystemTemplate
        // guard keys on the flag, not the owner, so this still exercises the share-protection — and a
        // caller-org row is cascade-cleaned by ResetAsync, whereas a SystemOrgId row would survive the
        // wipe and (since #251's partial unique index) collide with the sibling system-template test.
        var s = await SeedAsync(auth.OrgId, isSystem: true, twoRules: false);

        var resp = await auth.Client.PostAsJsonAsync($"/api/compliance/templates/{s.TemplateId}/rules", new
        {
            id = (Guid?)null,
            documentType = "coi",
            fieldName = "general_liability_limit",
            @operator = "min_value",
            expectedValue = "1",
            errorMessage = "planted on shared template",
            sortOrder = 99,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = CreateSystemDb();
        (await db.ComplianceRules.CountAsync(r => r.ComplianceTemplateId == s.TemplateId)).Should().Be(1,
            "the shared system template's rules must be unchanged");
    }
}
