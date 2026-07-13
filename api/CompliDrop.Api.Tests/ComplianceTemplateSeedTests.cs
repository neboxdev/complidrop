using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Data.Seed;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Pins the seed's CONVERGENCE contract (#416 / ADR 0036): <see cref="ComplianceTemplateSeed.EnsureAsync"/>
/// converges each already-seeded system template to its seed definition on boot — adding a missing rule,
/// updating a changed value / message / sort, deleting a stale rule (and its dependent ComplianceCheck rows,
/// FK-safe), and correcting the description — and re-grades the affected documents across orgs on ANY
/// rule-set change (rule add / delete / update including a message- or sort-only edit), never for a
/// description-only edit or an already-converged boot. Tenant clones are never touched. Also pins the #400
/// rule content that survives into §4 (Caterer liquor liability, Security general liability) and the
/// discriminating liquor grading.
///
/// The system templates are shared across the integration collection (Respawn ignores the
/// ComplianceTemplates / ComplianceRules tables — see IntegrationTestFixture), so every test that MUTATES
/// them does so only inside a rolled-back transaction, exactly like SystemTemplateDedupTests.
/// </summary>
public sealed class ComplianceTemplateSeedTests(IntegrationTestFixture fixture) : IntegrationTestBase(fixture)
{
    // ---- structural: the seeded system templates carry the #400 rules ----

    [Fact]
    public async Task Seeded_Caterer_template_requires_liquor_liability_at_one_million()
    {
        await using var db = CreateSystemDb();
        var caterer = await db.ComplianceTemplates.IgnoreQueryFilters().Include(t => t.Rules)
            .FirstAsync(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName);

        caterer.Rules.Should().ContainSingle(r =>
            r.DocumentType == "coi" && r.FieldName == "liquor_liability_limit"
            && r.Operator == "min_value" && r.ExpectedValue == "1000000");
    }

    [Fact]
    public async Task Seeded_Security_Service_template_requires_general_liability_at_one_million()
    {
        await using var db = CreateSystemDb();
        var security = await db.ComplianceTemplates.IgnoreQueryFilters().Include(t => t.Rules)
            .FirstAsync(t => t.IsSystemTemplate && t.Name == "Security Service");

        security.Rules.Should().ContainSingle(r =>
            r.DocumentType == "coi" && r.FieldName == "general_liability_limit"
            && r.Operator == "min_value" && r.ExpectedValue == "1000000");
    }

    // ---- the additive idempotent reconcile onto already-seeded system templates ----

    [Fact]
    public async Task EnsureAsync_backfills_missing_rules_onto_seeded_system_templates_without_touching_tenants_or_duplicating()
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var tenantOrgId = Guid.NewGuid();
            var tenantTemplateId = Guid.NewGuid();

            // Recreate the PRE-#400 seeded state inside the rolled-back transaction: the system
            // Caterer / Security Service rows exist but LACK the newly-added rules.
            await ExecAsync(conn, tx, """
                DELETE FROM "ComplianceRules" cr USING "ComplianceTemplates" ct
                WHERE cr."ComplianceTemplateId" = ct."Id" AND ct."IsSystemTemplate" = true
                  AND ((ct."Name" = 'Caterer' AND cr."FieldName" = 'liquor_liability_limit')
                    OR (ct."Name" = 'Security Service' AND cr."FieldName" = 'general_liability_limit'));
                """);

            // A tenant-owned template that shares a seed name ("Caterer") with its OWN rule set —
            // the reconcile must never load or touch it (IsSystemTemplate = false).
            await using (var seed = TxContext(conn))
            {
                await seed.Database.UseTransactionAsync(tx);
                seed.Organizations.Add(new Organization
                {
                    Id = tenantOrgId, Name = "Tenant", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                seed.ComplianceTemplates.Add(new ComplianceTemplate
                {
                    Id = tenantTemplateId, OrganizationId = tenantOrgId, Name = "Caterer",
                    IsSystemTemplate = false, CreatedAt = DateTime.UtcNow
                });
                seed.ComplianceRules.Add(new ComplianceRule
                {
                    Id = Guid.NewGuid(), ComplianceTemplateId = tenantTemplateId,
                    DocumentType = "coi", FieldName = "general_liability_limit", Operator = "min_value",
                    ExpectedValue = "500000", SortOrder = 1
                });
                await seed.SaveChangesAsync();
            }

            // Precondition: the deletes landed.
            (await ScalarAsync(conn, tx, CatererLiquorCountSql)).Should().Be(0, "precondition: the system Caterer lacks the liquor rule");
            (await ScalarAsync(conn, tx, SecurityGlCountSql)).Should().Be(0, "precondition: the system Security Service lacks the GL rule");

            // First reconcile: back-fills the two missing rules onto the ALREADY-SEEDED system rows
            // (not a whole-template insert — the templates already exist).
            await using (var run1 = TxContext(conn))
            {
                await run1.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run1);
            }

            (await ScalarAsync(conn, tx, CatererLiquorCountSql)).Should().Be(1, "the reconcile back-fills the missing liquor rule");
            (await ScalarAsync(conn, tx, SecurityGlCountSql)).Should().Be(1, "the reconcile back-fills the missing GL rule");
            (await ScalarStringAsync(conn, tx, CatererLiquorExpectedValueSql))
                .Should().Be("1000000", "the re-added rule carries the seeded $1M threshold");

            // The tenant template that shares the name is UNTOUCHED — still exactly its one own rule,
            // and it never gained the system Caterer's liquor rule.
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"ComplianceTemplateId\" = '{tenantTemplateId}'"))
                .Should().Be(1, "a tenant-owned template is never reconciled");
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"ComplianceTemplateId\" = '{tenantTemplateId}' AND \"FieldName\" = 'liquor_liability_limit'"))
                .Should().Be(0, "the tenant template did not gain the system Caterer's liquor rule");

            // Second reconcile: idempotent — the rules are present, so it adds NOTHING (no duplicates).
            await using (var run2 = TxContext(conn))
            {
                await run2.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run2);
            }

            (await ScalarAsync(conn, tx, CatererLiquorCountSql)).Should().Be(1, "a repeat reconcile adds no duplicate liquor rule");
            (await ScalarAsync(conn, tx, SecurityGlCountSql)).Should().Be(1, "a repeat reconcile adds no duplicate GL rule");
        }
        finally
        {
            await tx.RollbackAsync(); // restore the shared system seed for the rest of the collection
        }
    }

    // ---- the back-fill re-grades documents across orgs — but leaves sample-demo docs Compliant ----

    [Fact]
    public async Task EnsureAsync_backfill_regrades_normal_docs_across_orgs_but_leaves_sample_demo_docs_compliant()
    {
        // Two properties of the #400 seed fan-out, pinned in ONE run (both discriminating):
        //
        //  (Finding 2 — cross-org) The reconcile back-fills rules onto SHARED system templates, so the
        //  documents graded against them must be re-graded ACROSS EVERY ORG (SystemDbContext, no tenant
        //  filter). A caterer COI persisted Compliant under the PRE-#400 rule set must not silently stay
        //  Compliant while carrying no liquor coverage — a wrong persisted verdict failing OPEN (the
        //  product IS the verdict; reviewers.md blocker). Endpoint rule edits are blocked on system
        //  templates, so the seed is the only mutator and must fan out. This seeds a normal COI in TWO
        //  DIFFERENT orgs and asserts BOTH flip — the org-B flip is what actually exercises the cross-org
        //  (no-tenant-filter) guarantee, not merely SystemDbContext's design. (Swap the fan-out to the
        //  tenant-filtered AppDbContext — scoped to org A — and org B's document is NOT re-graded, so the
        //  second-org assertion fails.)
        //
        //  (Finding 1 — samples are do-no-harm) The one-click sample demo (ADR 0028, #238) attaches its
        //  sample vendor DIRECTLY to the system Caterer template, and a pre-#400 sample COI was generated
        //  + extracted before liquor_liability_limit existed, so its persisted ExtractionFields carry no
        //  such field. This fan-out only re-runs rule evaluation (never re-extraction), so re-grading a
        //  sample would flip a genuinely-Compliant demo artifact to NonCompliant on the next deploy for
        //  every org holding a sample. The seed fan-out therefore EXCLUDES IsSample docs — they stay
        //  Compliant (and self-heal on clear + recreate, since the generator now emits a liquor line).
        //  (Remove the !d.IsSample exclusion and the sample flips too, so the sample assertion fails.)
        //
        // Built in a rolled-back transaction (system templates are shared across the collection).
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
            var orgA = Guid.NewGuid();
            var orgB = Guid.NewGuid();
            var vendorNormalA = Guid.NewGuid();
            var vendorSampleA = Guid.NewGuid();
            var vendorNormalB = Guid.NewGuid();
            var normalDocA = Guid.NewGuid();
            var sampleDocA = Guid.NewGuid();
            var normalDocB = Guid.NewGuid();

            // Recreate the PRE-#400 seeded state: the system Caterer row exists but LACKS the liquor rule.
            await ExecAsync(conn, tx, """
                DELETE FROM "ComplianceRules" cr USING "ComplianceTemplates" ct
                WHERE cr."ComplianceTemplateId" = ct."Id" AND ct."IsSystemTemplate" = true
                  AND ct."Name" = 'Caterer' AND cr."FieldName" = 'liquor_liability_limit';
                """);
            (await ScalarAsync(conn, tx, CatererLiquorCountSql))
                .Should().Be(0, "precondition: the system Caterer lacks the liquor rule");

            await using (var seed = TxContext(conn))
            {
                await seed.Database.UseTransactionAsync(tx);
                var catererId = (await seed.ComplianceTemplates.IgnoreQueryFilters()
                    .FirstAsync(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName)).Id;

                seed.Organizations.Add(new Organization { Id = orgA, Name = "Tenant A", CreatedAt = now, UpdatedAt = now });
                seed.Organizations.Add(new Organization { Id = orgB, Name = "Tenant B", CreatedAt = now, UpdatedAt = now });

                // Three vendors, all assigned the SYSTEM Caterer template DIRECTLY (as the #238 sample
                // vendor is): a normal + a sample vendor in org A, and a normal vendor in a DIFFERENT org B.
                seed.Vendors.Add(new Vendor { Id = vendorNormalA, OrganizationId = orgA, Name = "Caterer A", ComplianceTemplateId = catererId, CreatedAt = now, UpdatedAt = now });
                seed.Vendors.Add(new Vendor { Id = vendorSampleA, OrganizationId = orgA, Name = "Sample Caterer A", ComplianceTemplateId = catererId, IsSample = true, CreatedAt = now, UpdatedAt = now });
                seed.Vendors.Add(new Vendor { Id = vendorNormalB, OrganizationId = orgB, Name = "Caterer B", ComplianceTemplateId = catererId, CreatedAt = now, UpdatedAt = now });

                // Every doc: GL ≥ $1M, future expiration, workers-comp present, NO liquor coverage — so
                // genuinely Compliant under the pre-#400 rule set. Persist that verdict, as prod would.
                seed.Documents.Add(NoLiquorCatererDoc(normalDocA, orgA, vendorNormalA, now, isSample: false));
                seed.Documents.Add(NoLiquorCatererDoc(sampleDocA, orgA, vendorSampleA, now, isSample: true));
                seed.Documents.Add(NoLiquorCatererDoc(normalDocB, orgB, vendorNormalB, now, isSample: false));
                await seed.SaveChangesAsync();
            }

            foreach (var (id, label) in new[] { (normalDocA, "normal A"), (sampleDocA, "sample A"), (normalDocB, "normal B") })
                (await ReadStatusAsync(conn, tx, id))
                    .Should().Be(ComplianceStatus.Compliant, $"precondition: {label} graded Compliant under the pre-#400 rule set");

            // Run the seed WITH the real re-eval fan-out (simulating the deploy). Both contexts share the
            // transaction so the fan-out sees the just-back-filled rule and the seeded documents. The
            // AppDbContext is enlisted too and scoped to org A: it is UNUSED on the (system) production
            // path, but it is exactly what the Finding-2 discriminating swap (sysDb → db) would run
            // against — and then org B's document falls outside its tenant filter and is left un-regraded.
            var reevalUser = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgA };
            await using var appDbForEval = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options, reevalUser);
            await appDbForEval.Database.UseTransactionAsync(tx);
            await using var sysDbForEval = TxContext(conn);
            await sysDbForEval.Database.UseTransactionAsync(tx);
            var reevaluator = new ComplianceCheckService(
                appDbForEval, sysDbForEval, new FixedTimeProvider(now), NullLogger<ComplianceCheckService>.Instance);

            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, reevaluator);
            }

            (await ScalarAsync(conn, tx, CatererLiquorCountSql))
                .Should().Be(1, "the reconcile back-filled the liquor rule");

            // Finding 2: BOTH normal COIs — in TWO different orgs — are re-graded to NonCompliant. The
            // org-B flip is the cross-org (no-tenant-filter) guarantee; swap the fan-out to the tenant
            // AppDbContext (scoped to org A) and org B stays Compliant, failing the second assertion.
            (await ReadStatusAsync(conn, tx, normalDocA))
                .Should().Be(ComplianceStatus.NonCompliant,
                    "the org-A caterer COI carries no liquor coverage and must fail the back-filled rule");
            (await ReadStatusAsync(conn, tx, normalDocB))
                .Should().Be(ComplianceStatus.NonCompliant,
                    "the SECOND org's caterer COI must be re-graded too — the seed fan-out is cross-org (no tenant filter)");

            // Finding 1: the sample-demo COI is left Compliant. A pre-#400 sample predates the liquor
            // field and re-grading it (no re-extraction) would break the ADR 0028 one-click-demo contract.
            // Remove the !d.IsSample exclusion and this flips to NonCompliant, failing this assertion.
            (await ReadStatusAsync(conn, tx, sampleDocA))
                .Should().Be(ComplianceStatus.Compliant,
                    "the sample-demo COI must be EXCLUDED from the seed fan-out (ADR 0028) — untouched and still Compliant");
        }
        finally
        {
            await tx.RollbackAsync(); // restore the shared system seed for the rest of the collection
        }
    }

    // A caterer COI that passes every PRE-#400 Caterer rule (GL ≥ $1M, future expiration, workers-comp
    // present) but carries NO liquor coverage — so it is Compliant before the liquor rule is back-filled
    // and NonCompliant after. Persisted Compliant, as production would have graded it before #400.
    private static Document NoLiquorCatererDoc(Guid id, Guid orgId, Guid vendorId, DateTime now, bool isSample) => new()
    {
        Id = id, OrganizationId = orgId, VendorId = vendorId,
        DocumentType = "coi", OriginalFileName = "coi.pdf",
        GeneralLiabilityLimit = 2_000_000m, ExpirationDate = now.AddYears(1),
        ExtractionFields = JsonSerializer.SerializeToDocument(
            new Dictionary<string, object> { ["workers_comp_limit"] = "1000000" }),
        ComplianceStatus = ComplianceStatus.Compliant,
        IsSample = isSample,
        CreatedAt = now, UpdatedAt = now,
    };

    // ---- discriminating behaviour: the seeded Caterer liquor rule actually grades ----

    [Theory]
    [InlineData(null, ComplianceStatus.NonCompliant)]      // liquor liability absent
    [InlineData("500000", ComplianceStatus.NonCompliant)]  // liquor liability below the $1M minimum
    [InlineData("1000000", ComplianceStatus.Compliant)]    // liquor liability exactly at the minimum
    [InlineData("2000000", ComplianceStatus.Compliant)]    // liquor liability above the minimum
    public async Task Seeded_Caterer_template_grades_liquor_liability_discriminatingly(
        string? liquorValue, ComplianceStatus expected)
    {
        // Every OTHER Caterer rule is made to PASS (GL 2M ≥ 1M, a future expiration, workers-comp
        // present), so liquor liability is the SOLE differentiator. If the liquor rule were absent
        // from the seed, all four cases would grade Compliant and the two NonCompliant rows here
        // would fail — this is the test that proves the seeded rule is present AND governs.
        var orgId = Guid.NewGuid();
        var vendorId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var setup = CreateSystemDb())
        {
            var catererId = (await setup.ComplianceTemplates
                .FirstAsync(t => t.IsSystemTemplate && t.Name == ComplianceTemplateSeed.SampleVendorTemplateName)).Id;

            setup.Organizations.Add(new Organization { Id = orgId, Name = $"Org-{orgId:N}", CreatedAt = now, UpdatedAt = now });
            setup.Vendors.Add(new Vendor
            {
                Id = vendorId, OrganizationId = orgId, Name = "Caterer Co",
                ComplianceTemplateId = catererId, CreatedAt = now, UpdatedAt = now,
            });

            var fields = new Dictionary<string, object> { ["workers_comp_limit"] = "1000000" };
            if (liquorValue is not null) fields["liquor_liability_limit"] = liquorValue;

            setup.Documents.Add(new Document
            {
                Id = docId, OrganizationId = orgId, VendorId = vendorId,
                DocumentType = "coi", OriginalFileName = "coi.pdf",
                GeneralLiabilityLimit = 2_000_000m, ExpirationDate = now.AddYears(1),
                ExtractionFields = JsonSerializer.SerializeToDocument(fields),
                CreatedAt = now, UpdatedAt = now,
            });
            await setup.SaveChangesAsync();
        }

        var user = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
        await using var appDb = CreateAppDb(user);
        await using var sysDb = CreateSystemDb();
        var status = await new ComplianceCheckService(
                appDb, sysDb, new FixedTimeProvider(now), NullLogger<ComplianceCheckService>.Instance)
            .EvaluateForSystemAsync(docId, default);

        status.Should().Be(expected);
    }

    // ---- convergence: EnsureAsync updates / deletes / re-grades to the corrected §4 seed (#416, ADR 0036) ----

    [Fact]
    public async Task EnsureAsync_updates_a_changed_ExpectedValue_and_regrades_that_template()
    {
        // Convergence UPDATE arm: a live system rule whose compared value drifted from the seed is
        // corrected in place, and because ExpectedValue is the one non-natural-key field the evaluator
        // reads, that change is VERDICT-AFFECTING → the template is re-graded across orgs. Drift the two
        // #416 value corrections back to their pre-correction numbers, converge, and assert both.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var photographerId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Photographer / Videographer"));
            var transportId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Transportation / Shuttle"));

            // Pre-correction values: Photographer GL $1M → $500k, Transport auto $1.5M → $1M.
            await ExecAsync(conn, tx, SetRuleValueSql("Photographer / Videographer", "coi", "general_liability_limit", "min_value", "500000"));
            await ExecAsync(conn, tx, SetRuleValueSql("Transportation / Shuttle", "coi", "auto_liability_limit", "min_value", "1000000"));

            var spy = new RecordingComplianceCheckService();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, spy);
            }

            (await ScalarStringAsync(conn, tx, RuleValueSql("Photographer / Videographer", "general_liability_limit")))
                .Should().Be("1000000", "convergence raises the Photographer GL floor to the corrected $1M");
            (await ScalarStringAsync(conn, tx, RuleValueSql("Transportation / Shuttle", "auto_liability_limit")))
                .Should().Be("1500000", "convergence raises the Transport auto floor to the corrected $1.5M");

            spy.RegradedTemplateIds.Should().Contain(photographerId)
                .And.Contain(transportId, "a changed ExpectedValue is verdict-affecting and must re-grade the template");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task EnsureAsync_deletes_live_rules_the_corrected_seed_dropped_and_regrades_those_templates()
    {
        // Convergence DELETE arm: a live rule matching NO seed rule is removed, and removing a governing
        // rule is verdict-affecting → re-grade. Re-inject the three rules #416 removed (the review §2
        // "graded a fact no real document carries" set) onto their system templates, converge, and assert
        // each is deleted and its template re-graded.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var securityId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Security Service"));
            var transportId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Transportation / Shuttle"));
            var photographerId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Photographer / Videographer"));

            // The removed rules: Security certification expiry, Transport CDL-for-every-driver, Photographer E&O.
            await ExecAsync(conn, tx, InsertRuleSql("Security Service", "certification", "expiration_date", "required", null, "Certification expiration date is required.", 90));
            await ExecAsync(conn, tx, InsertRuleSql("Transportation / Shuttle", "license", "license_type", "equals", "CDL", "Driver must hold a CDL.", 91));
            await ExecAsync(conn, tx, InsertRuleSql("Photographer / Videographer", "coi", "professional_liability_limit", "min_value", "1000000", "Professional liability (E&O) must be at least $1,000,000.", 92));

            (await ScalarAsync(conn, tx, RuleCountSql("Security Service", "certification", "expiration_date", "required"))).Should().Be(1, "precondition: the stale certification rule is present");
            (await ScalarAsync(conn, tx, RuleCountSql("Transportation / Shuttle", "license", "license_type", "equals"))).Should().Be(1, "precondition: the stale CDL rule is present");
            (await ScalarAsync(conn, tx, RuleCountSql("Photographer / Videographer", "coi", "professional_liability_limit", "min_value"))).Should().Be(1, "precondition: the stale E&O rule is present");

            var spy = new RecordingComplianceCheckService();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, spy);
            }

            (await ScalarAsync(conn, tx, RuleCountSql("Security Service", "certification", "expiration_date", "required"))).Should().Be(0, "convergence deletes the stale certification rule");
            (await ScalarAsync(conn, tx, RuleCountSql("Transportation / Shuttle", "license", "license_type", "equals"))).Should().Be(0, "convergence deletes the stale CDL rule");
            (await ScalarAsync(conn, tx, RuleCountSql("Photographer / Videographer", "coi", "professional_liability_limit", "min_value"))).Should().Be(0, "convergence deletes the stale E&O rule");

            spy.RegradedTemplateIds.Should()
                .Contain(securityId).And.Contain(transportId).And.Contain(photographerId,
                    "deleting a governing rule is verdict-affecting and must re-grade the template");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task EnsureAsync_deletes_a_rule_with_graded_check_rows_then_recreates_surviving_checks_and_regrades()
    {
        // Finding 1 (#416 re-review, BLOCKER): the convergence DELETE arm removes a ComplianceRule, but
        // ComplianceCheck → ComplianceRule is ON DELETE RESTRICT. On any DB where a document was graded
        // against a rule #416 drops, a real ComplianceCheck row references it — so the shared SaveChanges
        // would raise Postgres 23503 and roll the WHOLE convergence back (Program.cs swallows it; the §4
        // correction then silently never applies and re-fails every boot). This reproduces that state: it
        // re-injects the dropped Photographer E&O rule, attaches a real vendor + COI, GRADES it (so a
        // genuine check row references the E&O rule), then converges — and asserts EnsureAsync SUCCEEDS,
        // the E&O rule AND its check rows are gone, the surviving rules' checks are recreated, and the doc's
        // persisted verdict re-grades NonCompliant → Compliant. Reverting the FK-safety check-delete makes
        // EnsureAsync throw DbUpdateException (23503) here — the discrimination this test exists to make.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var now = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
            var orgId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var docId = Guid.NewGuid();

            var photographerId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Photographer / Videographer"));

            // Re-inject the E&O rule #416 removed, recreating a pre-correction production row.
            await ExecAsync(conn, tx, InsertRuleSql("Photographer / Videographer", "coi", "professional_liability_limit", "min_value", "1000000", "Professional liability (E&O) must be at least $1,000,000.", 92));
            var eoRuleId = await ScalarGuidAsync(conn, tx,
                "SELECT cr.\"Id\" FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
                "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Photographer / Videographer' " +
                "AND cr.\"FieldName\" = 'professional_liability_limit' AND cr.\"Operator\" = 'min_value'");

            // A real vendor on the SYSTEM Photographer template + a COI that PASSES the surviving rules
            // (GL $2M ≥ $1M, a future expiration) but carries NO E&O value — so it grades NonCompliant
            // while the E&O rule is present, and Compliant once it is dropped.
            await using (var seed = TxContext(conn))
            {
                await seed.Database.UseTransactionAsync(tx);
                seed.Organizations.Add(new Organization { Id = orgId, Name = "Photo Org", CreatedAt = now, UpdatedAt = now });
                seed.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "Shutter Co", ComplianceTemplateId = photographerId, CreatedAt = now, UpdatedAt = now });
                seed.Documents.Add(new Document
                {
                    Id = docId, OrganizationId = orgId, VendorId = vendorId,
                    DocumentType = "coi", OriginalFileName = "coi.pdf",
                    GeneralLiabilityLimit = 2_000_000m, ExpirationDate = now.AddYears(1),
                    ExtractionFields = JsonSerializer.SerializeToDocument(new Dictionary<string, object>()),
                    ComplianceStatus = ComplianceStatus.Pending,
                    CreatedAt = now, UpdatedAt = now,
                });
                await seed.SaveChangesAsync();
            }

            // GRADE the doc against the template (E&O rule present) → creates a real ComplianceCheck per
            // applicable rule, INCLUDING one referencing the E&O rule. Verdict: NonCompliant (E&O missing).
            var gradeUser = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
            await using (var appDbForGrade = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options, gradeUser))
            await using (var sysDbForGrade = TxContext(conn))
            {
                await appDbForGrade.Database.UseTransactionAsync(tx);
                await sysDbForGrade.Database.UseTransactionAsync(tx);
                await new ComplianceCheckService(appDbForGrade, sysDbForGrade, new FixedTimeProvider(now), NullLogger<ComplianceCheckService>.Instance)
                    .EvaluateForSystemAsync(docId, default);
            }

            // Pre-state: three checks (GL, expiration, E&O), graded NonCompliant, one check on the E&O rule.
            (await ReadStatusAsync(conn, tx, docId)).Should().Be(ComplianceStatus.NonCompliant, "precondition: a missing E&O value fails the re-injected rule");
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceChecks\" WHERE \"DocumentId\" = '{docId}'")).Should().Be(3, "precondition: a check exists per applicable rule (GL, expiration, E&O)");
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceChecks\" WHERE \"ComplianceRuleId\" = '{eoRuleId}'")).Should().Be(1, "precondition: a real check row references the to-be-dropped E&O rule — this is what makes the FK bite");

            // Converge WITH the real cross-org re-grade fan-out (production shape). The system SaveChanges
            // must NOT 23503: the FK-safety block deletes the E&O check in the same unit of work as the rule.
            var reevalUser = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
            await using var appDbForEval = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options, reevalUser);
            await appDbForEval.Database.UseTransactionAsync(tx);
            await using var sysDbForEval = TxContext(conn);
            await sysDbForEval.Database.UseTransactionAsync(tx);
            var reevaluator = new ComplianceCheckService(appDbForEval, sysDbForEval, new FixedTimeProvider(now), NullLogger<ComplianceCheckService>.Instance);

            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, reevaluator); // must NOT throw 23503
            }

            // The E&O rule and its check rows are gone (the rule delete itself proves the checks went with
            // it — ON DELETE RESTRICT would have blocked it otherwise).
            (await ScalarAsync(conn, tx, RuleCountSql("Photographer / Videographer", "coi", "professional_liability_limit", "min_value"))).Should().Be(0, "convergence deletes the stale E&O rule");
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceChecks\" WHERE \"ComplianceRuleId\" = '{eoRuleId}'")).Should().Be(0, "the E&O rule's dependent check rows are deleted in the same unit of work");

            // The surviving rules' checks are recreated by the post-commit re-grade, and the doc re-grades
            // Compliant now that the unmeetable E&O rule is gone.
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceChecks\" WHERE \"DocumentId\" = '{docId}'")).Should().Be(2, "the re-grade recreates checks for the two surviving rules (GL, expiration)");
            (await ReadStatusAsync(conn, tx, docId)).Should().Be(ComplianceStatus.Compliant, "with the unmeetable E&O rule dropped, the COI re-grades Compliant");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task EnsureAsync_adds_a_missing_seed_rule_and_regrades_that_template()
    {
        // Convergence ADD arm, isolated: delete one §4 rule from a system template, converge, and assert it
        // is re-added and the template re-graded. (The liquor back-fill test above exercises ADD end-to-end
        // with real documents; this pins the discrimination via the spy on a single template.)
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var securityId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Security Service"));

            // Drop Security's COI expiration rule (added by #416) to recreate a pre-correction gap.
            await ExecAsync(conn, tx, DeleteRuleSql("Security Service", "coi", "expiration_date", "required"));
            (await ScalarAsync(conn, tx, RuleCountSql("Security Service", "coi", "expiration_date", "required"))).Should().Be(0, "precondition: the COI expiration rule is missing");

            var spy = new RecordingComplianceCheckService();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, spy);
            }

            (await ScalarAsync(conn, tx, RuleCountSql("Security Service", "coi", "expiration_date", "required"))).Should().Be(1, "convergence back-fills the missing COI expiration rule");
            spy.RegradedTemplateIds.Should().Contain(securityId, "adding a governing rule is verdict-affecting and must re-grade the template");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task EnsureAsync_updates_a_drifted_description_without_regrading()
    {
        // A description is display-only. Convergence corrects it, but on its own it is VERDICT-NEUTRAL and
        // must trigger NO re-grade — the discrimination the re-grade trigger exists to make.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var photographerId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Photographer / Videographer"));
            const string seededDesc = "General liability coverage for photo and video vendors.";

            await ExecAsync(conn, tx,
                "UPDATE \"ComplianceTemplates\" SET \"Description\" = 'DRIFTED DESCRIPTION' " +
                "WHERE \"IsSystemTemplate\" = true AND \"Name\" = 'Photographer / Videographer';");

            var spy = new RecordingComplianceCheckService();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, spy);
            }

            (await ScalarStringAsync(conn, tx,
                "SELECT \"Description\" FROM \"ComplianceTemplates\" WHERE \"IsSystemTemplate\" = true AND \"Name\" = 'Photographer / Videographer'"))
                .Should().Be(seededDesc, "convergence restores the seeded description");
            spy.RegradedTemplateIds.Should().NotContain(photographerId, "a description-only change is verdict-neutral — no re-grade");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task EnsureAsync_updates_and_regrades_on_a_message_or_sort_only_change()
    {
        // Re-grade trigger is ANY rule-set change (#416 re-review, ADR 0036 invariant #2): an ErrorMessage /
        // SortOrder edit on a rule whose natural key AND ExpectedValue are unchanged is written back to the
        // seed value (convergence) AND now re-grades the template — the seeder no longer reaches into the
        // evaluator to decide the edit is verdict-neutral, so a future evaluator that reads those fields can
        // never leave a stale persisted verdict. Prove both halves: the row is corrected AND the template IS
        // re-graded. (The idempotency guarantee that an ALREADY-CONVERGED boot re-grades nothing is pinned
        // separately by EnsureAsync_is_a_no_op_with_no_regrade_when_system_templates_already_match_the_seed.)
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var eventRentalId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Event Rental Company"));

            await ExecAsync(conn, tx,
                "UPDATE \"ComplianceRules\" cr SET \"ErrorMessage\" = 'DRIFTED MSG', \"SortOrder\" = 99 " +
                "FROM \"ComplianceTemplates\" ct WHERE cr.\"ComplianceTemplateId\" = ct.\"Id\" " +
                "AND ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Event Rental Company' " +
                "AND cr.\"FieldName\" = 'general_liability_limit' AND cr.\"Operator\" = 'min_value';");

            var spy = new RecordingComplianceCheckService();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, spy);
            }

            (await ScalarStringAsync(conn, tx,
                "SELECT cr.\"ErrorMessage\" FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
                "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Event Rental Company' AND cr.\"FieldName\" = 'general_liability_limit'"))
                .Should().Be("General liability must be at least $1,000,000 per occurrence.", "convergence restores the seeded error message");
            (await ScalarAsync(conn, tx,
                "SELECT cr.\"SortOrder\"::bigint FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
                "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Event Rental Company' AND cr.\"FieldName\" = 'general_liability_limit'"))
                .Should().Be(1, "convergence restores the seeded sort order");

            spy.RegradedTemplateIds.Should().Contain(eventRentalId,
                "a message/sort-only change is a rule-set change and now re-grades the template (#416, ADR 0036)");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task EnsureAsync_is_a_no_op_with_no_regrade_when_system_templates_already_match_the_seed()
    {
        // Idempotency invariant (ADR 0036 #3): a boot whose system templates already match the seed makes
        // no rule changes and triggers no re-grade. The fixture booted the templates at the current seed, so
        // a fresh EnsureAsync must touch nothing.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var before = await ScalarAsync(conn, tx, AllSystemRuleCountSql);

            var spy = new RecordingComplianceCheckService();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, spy);
            }

            spy.RegradedTemplateIds.Should().BeEmpty("an already-converged boot re-grades nothing");
            (await ScalarAsync(conn, tx, AllSystemRuleCountSql)).Should().Be(before, "an already-converged boot adds/deletes no rules");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Interrupted_regrade_leaves_watermark_behind_and_the_next_boot_heals_the_stale_verdict()
    {
        // Finding 1 (#416 re-review, BLOCKER) — the re-grade must be DURABLE. Convergence commits the
        // corrected rules and re-grades the affected documents in SEPARATE steps; if the boot is interrupted
        // (SIGTERM / startup timeout) or a re-grade page is caught-and-skipped, the rules persist but a
        // document keeps its STALE verdict. The OLD this-boot-only gate (re-grade only templates THIS boot
        // mutated) never healed it: the next boot's convergence is idempotent (rules already match → nothing
        // "changed" → re-grade skipped), so a caterer COI persisted Compliant with no liquor coverage stayed
        // Compliant forever (ADR 0036 invariant #2 violated — the product's blocker-class failure). The fix is
        // a persisted revision WATERMARK: RulesRevision bumps on the rule-set change, RegradedThroughRevision
        // advances ONLY on a fully-successful re-grade, and every boot re-grades any template whose watermark
        // is behind.
        //
        // Both halves run in ONE rolled-back transaction:
        //   (a) converge with a rule change that SHOULD flip a doc, but the re-grade FAILS its page
        //       (FailingSystemReevaluator → FailedPages>0): the doc stays STALE and RegradedThroughRevision
        //       does NOT advance (still behind RulesRevision).
        //   (b) a SECOND EnsureAsync with a WORKING reevaluator and NO new rule mutation (rules already match
        //       the seed): it STILL re-grades because the watermark is behind, the doc heals, and the watermark
        //       catches up.
        // Reverting the gate to the this-boot-only "changedTemplateIds" set makes (b) skip the re-grade and
        // the doc stay Compliant — the exact bug this watermark closes.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var now = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
            var orgId = Guid.NewGuid();
            var vendorId = Guid.NewGuid();
            var docId = Guid.NewGuid();

            var catererId = await ScalarGuidAsync(conn, tx, SystemTemplateIdSql("Caterer"));

            // Recreate the PRE-#400 seeded state: the system Caterer row exists but LACKS the liquor rule.
            // The watermark starts caught up (0/0) — the fixture booted the templates at the current seed.
            await ExecAsync(conn, tx, """
                DELETE FROM "ComplianceRules" cr USING "ComplianceTemplates" ct
                WHERE cr."ComplianceTemplateId" = ct."Id" AND ct."IsSystemTemplate" = true
                  AND ct."Name" = 'Caterer' AND cr."FieldName" = 'liquor_liability_limit';
                """);
            (await RulesRevisionAsync(conn, tx, catererId)).Should().Be(0, "precondition: the Caterer watermark starts caught up");
            (await RegradedThroughRevisionAsync(conn, tx, catererId)).Should().Be(0, "precondition: the Caterer watermark starts caught up");

            // A normal caterer COI: GL ≥ $1M, future expiration, workers-comp present, NO liquor coverage —
            // genuinely Compliant under the pre-#400 rule set. Persist that verdict, as prod would have.
            await using (var seed = TxContext(conn))
            {
                await seed.Database.UseTransactionAsync(tx);
                seed.Organizations.Add(new Organization { Id = orgId, Name = "Caterer Org", CreatedAt = now, UpdatedAt = now });
                seed.Vendors.Add(new Vendor { Id = vendorId, OrganizationId = orgId, Name = "No-Liquor Caterer", ComplianceTemplateId = catererId, CreatedAt = now, UpdatedAt = now });
                seed.Documents.Add(NoLiquorCatererDoc(docId, orgId, vendorId, now, isSample: false));
                await seed.SaveChangesAsync();
            }
            (await ReadStatusAsync(conn, tx, docId)).Should().Be(ComplianceStatus.Compliant, "precondition: Compliant under the pre-#400 rule set");

            // ---- (a) converge, but the re-grade FAILS its page ----
            var failing = new FailingSystemReevaluator();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, failing);
            }

            // Convergence committed: the liquor rule is back and the revision bumped 0 → 1...
            (await ScalarAsync(conn, tx, CatererLiquorCountSql)).Should().Be(1, "convergence back-filled the liquor rule and committed it");
            (await RulesRevisionAsync(conn, tx, catererId)).Should().Be(1, "the rule-set change bumped RulesRevision");
            failing.RegradedTemplateIds.Should().Contain(catererId, "the watermark-behind template was handed to the re-grade");
            // ...but the re-grade FAILED, so the watermark is HELD BACK and the document is STILL STALE.
            (await RegradedThroughRevisionAsync(conn, tx, catererId)).Should().Be(0, "a failed re-grade must NOT advance the watermark");
            (await ReadStatusAsync(conn, tx, docId)).Should().Be(ComplianceStatus.Compliant, "the failed re-grade left the caterer COI its stale Compliant verdict");

            // ---- (b) a SECOND boot with a WORKING reevaluator and NO new rule mutation ----
            var reevalUser = new FakeCurrentUser { UserId = Guid.NewGuid(), OrganizationId = orgId };
            await using var appDbForEval = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options, reevalUser);
            await appDbForEval.Database.UseTransactionAsync(tx);
            await using var sysDbForEval = TxContext(conn);
            await sysDbForEval.Database.UseTransactionAsync(tx);
            var reevaluator = new ComplianceCheckService(
                appDbForEval, sysDbForEval, new FixedTimeProvider(now), NullLogger<ComplianceCheckService>.Instance);

            await using (var run2 = TxContext(conn))
            {
                await run2.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run2, reevaluator);
            }

            // No rule changed this boot (idempotent convergence), yet the doc HEALS — because the watermark was
            // behind (RulesRevision 1 > RegradedThroughRevision 0), the durable gate re-fired the re-grade.
            (await RulesRevisionAsync(conn, tx, catererId)).Should().Be(1, "no new rule change — RulesRevision stays 1");
            (await ReadStatusAsync(conn, tx, docId)).Should().Be(ComplianceStatus.NonCompliant,
                "the watermark-driven re-grade heals the stale verdict on the next boot even with no new rule mutation");
            (await RegradedThroughRevisionAsync(conn, tx, catererId)).Should().Be(1, "a fully-successful re-grade advances the watermark to catch up");
        }
        finally
        {
            await tx.RollbackAsync(); // restore the shared system seed for the rest of the collection
        }
    }

    [Fact]
    public async Task EnsureAsync_never_touches_a_tenant_template_that_shares_a_system_name()
    {
        // Hard invariant (ADR 0036 #1): convergence is SYSTEM-ONLY. A tenant clone that shares a system
        // template's NAME and carries the OLD (pre-correction) rules — a venue's own user data — is never
        // loaded and never touched, even as the system row of the same name converges around it.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var tenantOrgId = Guid.NewGuid();
            var tenantTemplateId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await using (var seed = TxContext(conn))
            {
                await seed.Database.UseTransactionAsync(tx);
                seed.Organizations.Add(new Organization { Id = tenantOrgId, Name = "Tenant", CreatedAt = now, UpdatedAt = now });
                seed.ComplianceTemplates.Add(new ComplianceTemplate
                {
                    Id = tenantTemplateId, OrganizationId = tenantOrgId, Name = "Photographer / Videographer",
                    Description = "My own tweaked checklist", IsSystemTemplate = false, CreatedAt = now,
                });
                // The OLD Photographer rules the system template no longer carries (GL $500k, E&O, license expiry).
                seed.ComplianceRules.Add(new ComplianceRule { Id = Guid.NewGuid(), ComplianceTemplateId = tenantTemplateId, DocumentType = "coi", FieldName = "general_liability_limit", Operator = "min_value", ExpectedValue = "500000", SortOrder = 1 });
                seed.ComplianceRules.Add(new ComplianceRule { Id = Guid.NewGuid(), ComplianceTemplateId = tenantTemplateId, DocumentType = "coi", FieldName = "professional_liability_limit", Operator = "min_value", ExpectedValue = "1000000", SortOrder = 2 });
                seed.ComplianceRules.Add(new ComplianceRule { Id = Guid.NewGuid(), ComplianceTemplateId = tenantTemplateId, DocumentType = "license", FieldName = "expiration_date", Operator = "required", ExpectedValue = null, SortOrder = 3 });
                await seed.SaveChangesAsync();
            }

            var spy = new RecordingComplianceCheckService();
            await using (var run = TxContext(conn))
            {
                await run.Database.UseTransactionAsync(tx);
                await ComplianceTemplateSeed.EnsureAsync(run, spy);
            }

            // The tenant rules are byte-for-byte what the venue authored — untouched by convergence.
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"ComplianceTemplateId\" = '{tenantTemplateId}'"))
                .Should().Be(3, "the tenant template keeps exactly its own three rules");
            (await ScalarStringAsync(conn, tx, $"SELECT \"ExpectedValue\" FROM \"ComplianceRules\" WHERE \"ComplianceTemplateId\" = '{tenantTemplateId}' AND \"FieldName\" = 'general_liability_limit'"))
                .Should().Be("500000", "the tenant's GL floor is user data — convergence must not raise it to $1M");
            (await ScalarAsync(conn, tx, $"SELECT count(*) FROM \"ComplianceRules\" WHERE \"ComplianceTemplateId\" = '{tenantTemplateId}' AND \"FieldName\" = 'professional_liability_limit'"))
                .Should().Be(1, "the tenant's E&O rule is user data — convergence must not delete it");
            (await ScalarStringAsync(conn, tx, $"SELECT \"Description\" FROM \"ComplianceTemplates\" WHERE \"Id\" = '{tenantTemplateId}'"))
                .Should().Be("My own tweaked checklist", "the tenant description is user data — convergence must not overwrite it");
            spy.RegradedTemplateIds.Should().NotContain(tenantTemplateId, "a tenant template is never in the system re-grade fan-out");
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Seeded_system_templates_match_the_corrected_section_4_rule_set_exactly()
    {
        // The end state: each of the five system templates carries EXACTLY the review §4 rule set — natural
        // keys AND compared values, no extra, none missing. This is the single assertion that locks the
        // corrected set (#416); a stray add/remove/value-typo in the seed fails here.
        var expected = new Dictionary<string, (string DocumentType, string? FieldName, string Operator, string? ExpectedValue)[]>
        {
            ["Caterer"] =
            [
                ("coi", "general_liability_limit", "min_value", "1000000"),
                ("coi", "liquor_liability_limit", "min_value", "1000000"),
                ("coi", "workers_comp_limit", "required", null),
                ("coi", "expiration_date", "required", null),
            ],
            ["Event Rental Company"] =
            [
                ("coi", "general_liability_limit", "min_value", "1000000"),
                ("coi", "expiration_date", "required", null),
            ],
            ["Security Service"] =
            [
                ("license", "license_number", "required", null),
                ("license", "expiration_date", "required", null),
                ("coi", "general_liability_limit", "min_value", "1000000"),
                ("coi", "expiration_date", "required", null),
            ],
            ["Transportation / Shuttle"] =
            [
                ("coi", "auto_liability_limit", "min_value", "1500000"),
                ("coi", "expiration_date", "required", null),
                ("license", "license_number", "required", null),
                ("license", "expiration_date", "required", null),
            ],
            ["Photographer / Videographer"] =
            [
                ("coi", "general_liability_limit", "min_value", "1000000"),
                ("coi", "expiration_date", "required", null),
            ],
        };

        await using var db = CreateSystemDb();
        foreach (var (name, expectedRules) in expected)
        {
            var tpl = await db.ComplianceTemplates.IgnoreQueryFilters().Include(t => t.Rules)
                .FirstAsync(t => t.IsSystemTemplate && t.Name == name);

            tpl.Rules.Select(r => (r.DocumentType, r.FieldName, r.Operator, r.ExpectedValue))
                .Should().BeEquivalentTo(expectedRules,
                    $"the system '{name}' template must carry exactly the §4 rule set");
        }
    }

    // ---- helpers ----

    private static SystemDbContext TxContext(NpgsqlConnection conn) =>
        new(new DbContextOptionsBuilder<SystemDbContext>().UseNpgsql(conn).Options);

    private static string SystemTemplateIdSql(string name) =>
        $"SELECT \"Id\" FROM \"ComplianceTemplates\" WHERE \"IsSystemTemplate\" = true AND \"Name\" = '{name}'";

    // A system template's stored ExpectedValue for one field (used by the value-convergence assertions).
    private static string RuleValueSql(string templateName, string fieldName) =>
        "SELECT cr.\"ExpectedValue\" FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        $"WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = '{templateName}' AND cr.\"FieldName\" = '{fieldName}'";

    private static string SetRuleValueSql(string templateName, string documentType, string fieldName, string op, string value) =>
        $"UPDATE \"ComplianceRules\" cr SET \"ExpectedValue\" = '{value}' FROM \"ComplianceTemplates\" ct " +
        "WHERE cr.\"ComplianceTemplateId\" = ct.\"Id\" AND ct.\"IsSystemTemplate\" = true " +
        $"AND ct.\"Name\" = '{templateName}' AND cr.\"DocumentType\" = '{documentType}' " +
        $"AND cr.\"FieldName\" = '{fieldName}' AND cr.\"Operator\" = '{op}';";

    private static string RuleCountSql(string templateName, string documentType, string fieldName, string op) =>
        "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        $"WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = '{templateName}' " +
        $"AND cr.\"DocumentType\" = '{documentType}' AND cr.\"FieldName\" = '{fieldName}' AND cr.\"Operator\" = '{op}'";

    private static string DeleteRuleSql(string templateName, string documentType, string fieldName, string op) =>
        "DELETE FROM \"ComplianceRules\" cr USING \"ComplianceTemplates\" ct " +
        "WHERE cr.\"ComplianceTemplateId\" = ct.\"Id\" AND ct.\"IsSystemTemplate\" = true " +
        $"AND ct.\"Name\" = '{templateName}' AND cr.\"DocumentType\" = '{documentType}' " +
        $"AND cr.\"FieldName\" = '{fieldName}' AND cr.\"Operator\" = '{op}';";

    private static string InsertRuleSql(string templateName, string documentType, string fieldName, string op, string? value, string message, int sortOrder)
    {
        var valueLiteral = value is null ? "NULL" : $"'{value}'";
        return "INSERT INTO \"ComplianceRules\" (\"Id\", \"ComplianceTemplateId\", \"DocumentType\", \"FieldName\", \"Operator\", \"ExpectedValue\", \"ErrorMessage\", \"SortOrder\") " +
            $"SELECT gen_random_uuid(), ct.\"Id\", '{documentType}', '{fieldName}', '{op}', {valueLiteral}, '{message.Replace("'", "''")}', {sortOrder} " +
            $"FROM \"ComplianceTemplates\" ct WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = '{templateName}';";
    }

    private const string AllSystemRuleCountSql =
        "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        "WHERE ct.\"IsSystemTemplate\" = true";

    private static async Task<Guid> ScalarGuidAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    // The re-grade durability watermark columns (#416, ADR 0036 Amendment 2), read through the same
    // transaction so they reflect the transaction's current state.
    private static Task<long> RulesRevisionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid templateId) =>
        ScalarAsync(conn, tx, $"SELECT \"RulesRevision\"::bigint FROM \"ComplianceTemplates\" WHERE \"Id\" = '{templateId}'");

    private static Task<long> RegradedThroughRevisionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid templateId) =>
        ScalarAsync(conn, tx, $"SELECT \"RegradedThroughRevision\"::bigint FROM \"ComplianceTemplates\" WHERE \"Id\" = '{templateId}'");

    // Reads a document's persisted ComplianceStatus through a fresh, no-tracking context enlisted in
    // the same transaction — so EF converts the enum column regardless of its storage type and the
    // read reflects the transaction's current committed-to-tx state (never a cached tracked entity).
    private static async Task<ComplianceStatus> ReadStatusAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid docId)
    {
        await using var read = TxContext(conn);
        await read.Database.UseTransactionAsync(tx);
        var doc = await read.Documents.AsNoTracking().FirstAsync(d => d.Id == docId);
        return doc.ComplianceStatus;
    }

    private const string CatererLiquorCountSql =
        "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Caterer' " +
        "AND cr.\"DocumentType\" = 'coi' AND cr.\"FieldName\" = 'liquor_liability_limit' AND cr.\"Operator\" = 'min_value'";

    private const string SecurityGlCountSql =
        "SELECT count(*) FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Security Service' " +
        "AND cr.\"DocumentType\" = 'coi' AND cr.\"FieldName\" = 'general_liability_limit' AND cr.\"Operator\" = 'min_value'";

    private const string CatererLiquorExpectedValueSql =
        "SELECT cr.\"ExpectedValue\" FROM \"ComplianceRules\" cr JOIN \"ComplianceTemplates\" ct ON ct.\"Id\" = cr.\"ComplianceTemplateId\" " +
        "WHERE ct.\"IsSystemTemplate\" = true AND ct.\"Name\" = 'Caterer' AND cr.\"FieldName\" = 'liquor_liability_limit'";

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync()) as string;
    }
}
