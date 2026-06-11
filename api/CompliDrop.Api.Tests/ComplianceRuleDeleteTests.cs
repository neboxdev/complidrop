using System.Net;
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
}
