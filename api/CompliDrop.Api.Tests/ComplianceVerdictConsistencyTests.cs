using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Regression tests for #337 (ADR 0030): the persisted compliance verdict must never be left
/// contradicting the persisted canonical inputs under a manual-edit-vs-(re)extraction race. The fix folds
/// the verdict into each input-writer's own transaction (combined unit of work), so each writer commits
/// the whole <c>(inputs, verdict)</c> tuple atomically.
/// </summary>
public sealed class ComplianceVerdictConsistencyTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // Seeds a vendor on a checklist carrying a single "general_liability_limit >= minLimit" rule for COIs.
    private async Task<Guid> SeedVendorWithGlRuleAsync(Guid orgId, string minLimit)
    {
        var now = DateTime.UtcNow;
        var vendorId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.ComplianceTemplates.Add(new ComplianceTemplate { Id = templateId, OrganizationId = orgId, Name = "T", CreatedAt = now });
        db.Vendors.Add(new Vendor
        {
            Id = vendorId, OrganizationId = orgId, Name = "V", ComplianceTemplateId = templateId,
            CreatedAt = now, UpdatedAt = now
        });
        db.ComplianceRules.Add(new ComplianceRule
        {
            Id = Guid.NewGuid(), ComplianceTemplateId = templateId, DocumentType = "coi",
            FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = minLimit, SortOrder = 0
        });
        await db.SaveChangesAsync();
        return vendorId;
    }

    private async Task<Guid> SeedCoiAsync(Guid orgId, Guid vendorId, decimal glLimit, ComplianceStatus stored)
    {
        var now = DateTime.UtcNow;
        var docId = Guid.NewGuid();
        await using var db = CreateSystemDb();
        db.Documents.Add(new Document
        {
            Id = docId,
            OrganizationId = orgId,
            VendorId = vendorId,
            OriginalFileName = "coi.pdf",
            BlobStorageUrl = "blob://d",
            FileSizeBytes = 1,
            ContentType = "application/pdf",
            DocumentType = "coi",
            ExtractionStatus = ExtractionStatus.Completed,
            ComplianceStatus = stored,
            GeneralLiabilityLimit = glLimit,
            ExtractionFields = JsonDocument.Parse($"{{\"general_liability_limit\":\"{glLimit}\"}}"),
            ExpirationDate = now.AddYears(1), // far future: the verdict is purely rule-driven, not date-driven
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return docId;
    }

    private static void SetGl(Document doc, decimal gl)
    {
        doc.GeneralLiabilityLimit = gl;
        doc.ExtractionFields = JsonDocument.Parse($"{{\"general_liability_limit\":\"{gl}\"}}");
    }

    [Fact]
    public async Task Manual_edit_racing_a_reextraction_leaves_a_consistent_not_torn_verdict()
    {
        // Reproduces the audit's interleave deterministically at the persistence layer: a "worker"
        // (re-extraction) computes the verdict from freshly-extracted inputs W while a "user" commits an
        // edit U + its verdict in between, and the worker commits LAST. Before #337 the verdict was written
        // in a transaction SEPARATE from its inputs, so the terminal row could read inputs=U with
        // verdict(W) — a torn pair. With the combined unit of work each writer commits the whole
        // (inputs, verdict) tuple atomically, so whichever lands last wins it WHOLE: the terminal verdict
        // always matches the terminal inputs.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, glLimit: 1_000_000m, stored: ComplianceStatus.NonCompliant);

        using var scope = Fixture.Factory.Services.CreateScope();
        var compliance = scope.ServiceProvider.GetRequiredService<IComplianceCheckService>();

        // Worker: read the doc, set freshly-extracted inputs W (GL 3M → would grade Compliant), apply the
        // verdict IN PLACE — but do NOT save yet (mid-flight, exactly when the audit's race opens).
        await using var workerDb = CreateSystemDb();
        var workerDoc = await workerDb.Documents.FirstAsync(d => d.Id == docId);
        SetGl(workerDoc, 3_000_000m);
        await compliance.ApplyEvaluationAsync(workerDb, workerDoc, default);

        // User edit lands and COMMITS first: inputs U (GL 100k → NonCompliant) + its verdict, atomically.
        await using (var userDb = CreateSystemDb())
        {
            var userDoc = await userDb.Documents.FirstAsync(d => d.Id == docId);
            SetGl(userDoc, 100_000m);
            await compliance.ApplyEvaluationAsync(userDb, userDoc, default);
            await userDb.SaveChangesAsync();
        }

        // Worker commits LAST, overwriting the user (ADR 0017 last-writer-wins is fine — it's CONSISTENT).
        await workerDb.SaveChangesAsync();

        await using var verify = CreateSystemDb();
        var final = await verify.Documents.FirstAsync(d => d.Id == docId);
        // The terminal row is fully the worker's snapshot: GL 3M AND Compliant. Never the torn pair the
        // old separate-transaction verdict allowed (GL 100k with a Compliant verdict, or 3M with a
        // NonCompliant one). The general invariant: the persisted verdict matches what the inputs grade to.
        final.GeneralLiabilityLimit.Should().Be(3_000_000m);
        final.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public async Task A_verdict_flipping_edit_commits_inputs_and_verdict_in_one_audited_transaction()
    {
        // #337 + the #246/#41 AuditLog golden snapshot: the corrected input and the verdict it flips now
        // commit in ONE transaction, so the interceptor emits a SINGLE "document.updated" row whose
        // Before/After spans BOTH the input (GL limit) and the verdict transition
        // (NonCompliant -> Compliant). Pre-#337 this was TWO rows from two transactions, the first of which
        // captured a torn (new inputs, stale verdict) snapshot.
        var auth = await RegisterAndLoginAsync();
        var vendorId = await SeedVendorWithGlRuleAsync(auth.OrgId, minLimit: "2000000");
        var docId = await SeedCoiAsync(auth.OrgId, vendorId, glLimit: 1_000_000m, stored: ComplianceStatus.NonCompliant);

        var resp = await auth.Client.PutAsJsonAsync($"/api/documents/{docId}/fields", new
        {
            fields = new[] { new { fieldName = "general_liability_limit", fieldValue = "3000000" } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = CreateSystemDb();
        // The persisted pair is consistent: GL 3M, verdict Compliant.
        var saved = await verify.Documents.FirstAsync(d => d.Id == docId);
        saved.GeneralLiabilityLimit.Should().Be(3_000_000m);
        saved.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);

        var docUpdates = await verify.AuditLogs
            .Where(a => a.OrganizationId == auth.OrgId && a.EntityType == nameof(Document) && a.EntityId == docId
                && a.Action == "document.updated")
            .Select(a => new { a.BeforeJson, a.AfterJson })
            .ToListAsync();

        docUpdates.Should().HaveCount(1,
            "the edit + the verdict it flips commit in ONE transaction (#337) — one document.updated row, not two");
        var before = JsonDocument.Parse(docUpdates[0].BeforeJson!).RootElement;
        var after = JsonDocument.Parse(docUpdates[0].AfterJson!).RootElement;
        before.GetProperty("ComplianceStatus").GetInt32().Should().Be((int)ComplianceStatus.NonCompliant,
            "the single audit row's Before holds the pre-edit verdict");
        after.GetProperty("ComplianceStatus").GetInt32().Should().Be((int)ComplianceStatus.Compliant,
            "and its After holds the flipped verdict — the transition is captured atomically with the input change");
        after.GetProperty("GeneralLiabilityLimit").GetDecimal().Should().Be(3_000_000m);
    }
}
