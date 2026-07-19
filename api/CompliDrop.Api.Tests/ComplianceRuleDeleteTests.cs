using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public async Task Deleting_a_rule_regrades_a_document_that_was_never_evaluated_against_it()
    {
        // #364 discriminator. Pre-#364 the re-eval list was the DELETE … RETURNING snapshot of
        // documents that held a check row for THIS rule, so a document on the same checklist that
        // had never been evaluated carried no check row, never made the list, and kept whatever
        // stale verdict was stored. The batched post-commit fan-out is scoped to TEMPLATE
        // MEMBERSHIP instead (the same population UpsertRule re-grades), so it lands.
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);

        // Only the seeded document is evaluated — so rule A does have a check row and the
        // cleanup path is genuinely exercised.
        (await auth.Client.PostAsync($"/api/compliance/check/{s.DocId}", null)).EnsureSuccessStatusCode();

        Guid neverEvaluated;
        await using (var db = CreateSystemDb())
        {
            var seeded = await db.Documents.SingleAsync(d => d.Id == s.DocId);
            neverEvaluated = Guid.NewGuid();
            db.Documents.Add(new Document
            {
                Id = neverEvaluated, OrganizationId = auth.OrgId, VendorId = seeded.VendorId,
                OriginalFileName = "never.pdf", BlobStorageUrl = "blob://never", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                GeneralLiabilityLimit = 1_000_000m, ExpirationDate = DateTime.UtcNow.AddYears(1),
                // Deliberately stale: with rule A gone the remaining 'required' rule passes, so the
                // correct verdict is Compliant. A missed re-grade leaves this NonCompliant.
                ComplianceStatus = ComplianceStatus.NonCompliant,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            (await db.ComplianceChecks.CountAsync(c => c.DocumentId == neverEvaluated)).Should().Be(0,
                "arrange: this document must hold no check row, or it would ride the old snapshot");
        }

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db2 = CreateSystemDb();
        (await db2.Documents.SingleAsync(d => d.Id == neverEvaluated)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant,
                "the fan-out is scoped to the template, so a never-evaluated document on it is re-graded too");
        (await db2.ComplianceChecks.CountAsync(c => c.DocumentId == neverEvaluated)).Should().Be(1,
            "the re-grade writes the surviving rule's check row");
    }

    [Fact]
    public async Task Deleting_a_rule_regrades_a_document_whose_type_no_rule_governs()
    {
        // #364, the applicable-rules-empty branch: a non-COI document on a COI-only checklist has
        // zero governing rules and must read Pending, never a vacuous Compliant (#257). It also
        // never held a check row for the deleted rule, so pre-#364 it was outside the snapshot and
        // an over-claiming stored verdict survived the delete untouched.
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);
        (await auth.Client.PostAsync($"/api/compliance/check/{s.DocId}", null)).EnsureSuccessStatusCode();

        Guid licenseDoc;
        await using (var db = CreateSystemDb())
        {
            var seeded = await db.Documents.SingleAsync(d => d.Id == s.DocId);
            licenseDoc = Guid.NewGuid();
            db.Documents.Add(new Document
            {
                Id = licenseDoc, OrganizationId = auth.OrgId, VendorId = seeded.VendorId,
                OriginalFileName = "license.pdf", BlobStorageUrl = "blob://license", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "license",
                ExpirationDate = DateTime.UtcNow.AddYears(1),
                ComplianceStatus = ComplianceStatus.Compliant,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db2 = CreateSystemDb();
        (await db2.Documents.SingleAsync(d => d.Id == licenseDoc)).ComplianceStatus
            .Should().Be(ComplianceStatus.Pending,
                "no rule governs a 'license' on this COI-only checklist — the stale Compliant must not survive");
    }

    [Fact]
    public async Task Deleting_a_rule_regrades_a_document_whose_vendor_was_soft_deleted()
    {
        // #364 review counterexample — the case that falsifies "template membership is a superset".
        // DeleteVendor soft-deletes with NO re-grade (VendorEndpoints), so a deleted vendor's
        // documents keep a Compliant verdict AND their check rows while the Vendor soft-delete query
        // filter makes d.Vendor read null — which drops them out of the template-membership
        // predicate. The pre-#364 per-document loop healed them to Pending as a side effect of
        // iterating the deleted rule's check rows; the fan-out therefore takes the check-row holders
        // as a UNION with template membership so the batched path stays a strict superset. Without
        // that union this document stays on a vacuous Compliant that no rule governs (#257).
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId, twoRules: false);

        Guid orphanedDoc;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var doomedVendor = Guid.NewGuid();
            db.Vendors.Add(new Vendor
            {
                Id = doomedVendor, OrganizationId = auth.OrgId, Name = "Soon deleted",
                ComplianceTemplateId = s.TemplateId, CreatedAt = now, UpdatedAt = now
            });
            orphanedDoc = Guid.NewGuid();
            db.Documents.Add(new Document
            {
                Id = orphanedDoc, OrganizationId = auth.OrgId, VendorId = doomedVendor,
                OriginalFileName = "orphan.pdf", BlobStorageUrl = "blob://orphan", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                // Passes the 2M rule, so it was legitimately Compliant while the rule existed.
                GeneralLiabilityLimit = 3_000_000m, ExpirationDate = now.AddYears(1),
                ComplianceStatus = ComplianceStatus.Compliant,
                CreatedAt = now, UpdatedAt = now
            });
            db.ComplianceChecks.Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(), DocumentId = orphanedDoc, ComplianceRuleId = s.RuleAId,
                IsPassed = true, CheckedAt = now
            });
            await db.SaveChangesAsync();
        }

        // Soft-delete the vendor through the real endpoint, so the no-re-grade behaviour under test
        // is production's, not a hand-built row state.
        (await auth.Client.DeleteAsync($"/api/vendors/{(await VendorIdOfAsync(orphanedDoc))}"))
            .EnsureSuccessStatusCode();

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db2 = CreateSystemDb();
        (await db2.Documents.SingleAsync(d => d.Id == orphanedDoc)).ComplianceStatus
            .Should().Be(ComplianceStatus.Pending,
                "its vendor is gone and the rule it was graded against is gone — a Compliant verdict here would be vacuous");
        (await db2.ComplianceChecks.CountAsync(c => c.DocumentId == orphanedDoc)).Should().Be(0,
            "the orphaned check rows must be shed with the re-grade");
    }

    /// <summary>Reads a document's VendorId through the system context (no tenant filter).</summary>
    private async Task<Guid> VendorIdOfAsync(Guid documentId)
    {
        await using var db = CreateSystemDb();
        return (await db.Documents.SingleAsync(d => d.Id == documentId)).VendorId!.Value;
    }

    [Fact]
    public async Task Deleting_a_rule_keeps_the_expired_verdict_on_a_document_whose_vendor_left_the_template()
    {
        // Companion to the soft-deleted-vendor case: a document that holds a check row for this rule
        // while its vendor sits on NO template is re-graded through the same union, and its verdict
        // is date-driven — Expired is the one branch that preserves check rows across a re-grade
        // (ClearExistingChecks:false), and a rule deletion cannot change it. What must also hold is
        // that its orphaned check row is cleaned up by the transaction (the FK is ON DELETE RESTRICT,
        // so a leftover row would 500 the delete outright).
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);

        Guid strayDoc;
        await using (var db = CreateSystemDb())
        {
            var now = DateTime.UtcNow;
            var unassignedVendor = Guid.NewGuid();
            db.Vendors.Add(new Vendor
            {
                Id = unassignedVendor, OrganizationId = auth.OrgId, Name = "Left the checklist",
                ComplianceTemplateId = null, CreatedAt = now, UpdatedAt = now
            });
            strayDoc = Guid.NewGuid();
            db.Documents.Add(new Document
            {
                Id = strayDoc, OrganizationId = auth.OrgId, VendorId = unassignedVendor,
                OriginalFileName = "stray.pdf", BlobStorageUrl = "blob://stray", FileSizeBytes = 1,
                ContentType = "application/pdf", DocumentType = "coi",
                GeneralLiabilityLimit = 1_000_000m, ExpirationDate = now.AddDays(-10),
                ComplianceStatus = ComplianceStatus.Expired,
                CreatedAt = now, UpdatedAt = now
            });
            db.ComplianceChecks.Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(), DocumentId = strayDoc, ComplianceRuleId = s.RuleAId,
                IsPassed = false, CheckedAt = now
            });
            await db.SaveChangesAsync();
        }

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the orphaned check row must not restrict the delete");
        await using var db2 = CreateSystemDb();
        (await db2.ComplianceChecks.AnyAsync(c => c.ComplianceRuleId == s.RuleAId)).Should().BeFalse();
        (await db2.Documents.SingleAsync(d => d.Id == strayDoc)).ComplianceStatus
            .Should().Be(ComplianceStatus.Expired, "an expired verdict is date-driven — deleting a rule cannot change it");
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
    public async Task The_rule_delete_survives_a_failing_re_evaluation_fan_out()
    {
        // #364's central semantic change, pinned. Pre-#364 the re-evaluation ran INSIDE the delete's
        // transaction, so anything it threw rolled the whole delete back and the rule survived. Now
        // the transaction commits first and the fan-out runs after it, so a catastrophic re-grade
        // failure can no longer resurrect a rule the user deleted.
        //
        // The RESPONSE is pinned too: PostCommitRegrade.RunAsync swallows the failure, so the caller
        // sees the 200 its committed delete earned. Without that the user gets a 500 for a rule that
        // IS gone, retries, and is met with a 404 — and the shape is production-reachable (the
        // fan-out's snapshot query and its cancellation both sit outside the per-page catch).
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IComplianceCheckService>();
                services.AddScoped<IComplianceCheckService, ThrowingComplianceCheckService>();
            }));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var email = $"user-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "Password1234",
            fullName = "Test User",
            companyName = "Test Co",
            industry = (string?)null,
            companySize = (string?)null,
            timeZone = "America/New_York",
        });
        reg.EnsureSuccessStatusCode();
        var orgId = (await reg.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("organizationId").GetGuid();
        var s = await SeedAsync(orgId);

        // Plant a check row directly (the throwing double makes POST /check unusable), so the
        // cleanup + FK-restrict path is genuinely exercised before the fan-out blows up.
        await using (var db = CreateSystemDb())
        {
            db.ComplianceChecks.Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(), DocumentId = s.DocId, ComplianceRuleId = s.RuleAId,
                IsPassed = false, CheckedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the delete committed — a failing post-commit re-grade must not report it as a server error");
        await using var verify = CreateSystemDb();
        (await verify.ComplianceRules.AnyAsync(r => r.Id == s.RuleAId)).Should().BeFalse(
            "the delete committed before the fan-out ran — a failing re-grade must not roll it back");
        (await verify.ComplianceChecks.AnyAsync(c => c.ComplianceRuleId == s.RuleAId)).Should().BeFalse(
            "the check cleanup committed in the same transaction as the rule delete");
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
    public async Task Deleting_a_never_evaluated_rule_still_regrades_the_templates_documents()
    {
        // Pins the EMPTY-documentIds branch of ReevaluateForTemplateOrDocumentsAsync by its EFFECT.
        // With no ComplianceCheck rows for the rule, DELETE … RETURNING yields nothing and
        // affectedDocIds is empty — and that branch must delegate to the full template fan-out, NOT
        // short-circuit. Its sibling ReevaluateForVendorsAsync returns Task.CompletedTask on an empty
        // list, so "return early when the id list is empty" is exactly the copy-paste regression that
        // would slip through here: the rule set changed for every document on the checklist whether
        // or not any of them had been graded against the deleted rule yet.
        var auth = await RegisterAndLoginAsync();
        var s = await SeedAsync(auth.OrgId);

        // Never evaluated ⇒ no check rows anywhere for rule A. The document carries a deliberately
        // stale verdict; with rule A gone the surviving 'required' rule passes, so it must read
        // Compliant afterwards.
        await using (var db = CreateSystemDb())
        {
            await db.Documents.Where(d => d.Id == s.DocId)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.ComplianceStatus, ComplianceStatus.NonCompliant));
            (await db.ComplianceChecks.CountAsync(c => c.ComplianceRuleId == s.RuleAId)).Should().Be(0,
                "arrange: the rule must have no check rows, so affectedDocIds comes back empty");
        }

        var resp = await auth.Client.DeleteAsync($"/api/compliance/templates/{s.TemplateId}/rules/{s.RuleAId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateSystemDb();
        (await verify.Documents.SingleAsync(d => d.Id == s.DocId)).ComplianceStatus
            .Should().Be(ComplianceStatus.Compliant,
                "an empty affectedDocIds must still fan out over template membership, not short-circuit");
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
