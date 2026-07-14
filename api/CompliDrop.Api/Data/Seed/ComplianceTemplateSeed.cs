using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Data.Seed;

public static class ComplianceTemplateSeed
{
    /// <summary>
    /// Identity of the synthetic organization that owns all <c>IsSystemTemplate=true</c>
    /// templates. Exposed to the test project (<see cref="InternalsVisibleToAttribute"/>) so the
    /// integration-test harness can target it directly in custom wipe SQL — keeps the magic
    /// constant in one place.
    /// </summary>
    internal static readonly Guid SystemOrgId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Name of the system venue-type checklist the sample-certificate demo (#238) assigns to its
    /// sample vendor. The generated sample COI is built to PASS this checklist's rules under BOTH
    /// gated rule sets (GL ≥ $1M each-occurrence, expiration date present, workers-comp coverage
    /// present, plus a liquor-liability line ≥ $1M that the corrected #400/#416 set requires and
    /// the legacy set simply never grades — see <see cref="Configuration.TemplateCorrectionsSettings"/>),
    /// so the demo lands a fresh org on a real "Compliant" verdict whichever way the flag is set,
    /// and a pre-flip sample stays valid across a flip. Kept here as the single source so the seed
    /// below and the sample-seed endpoint can never drift on the name.
    /// </summary>
    internal const string SampleVendorTemplateName = "Caterer";

    /// <summary>
    /// Number of system templates this seed installs. Exposed to the test project
    /// (<see cref="InternalsVisibleToAttribute"/>) so harness assertions can pin the exact count
    /// without re-declaring a brittle hand-mirror constant — adding a template here forces the
    /// test to be updated (or, if the count is read indirectly, doesn't break it at all).
    /// Set-independent: both gated sets install the SAME five template names (the
    /// TemplateCorrections flag selects rule CONTENT, never the roster — a name that existed in
    /// only one set would strand or duplicate a live template on a flip).
    /// </summary>
    internal static int TemplateCount => CorrectedTemplates.Length;

    /// <param name="useCorrectedTemplates">
    /// Selects WHICH rule set the system templates converge to this boot — the legal gate
    /// (#416, ADR 0036 Amendment 3), wired from <c>TemplateCorrections:Enabled</c> (default false).
    /// False = <see cref="LegacyTemplates"/> (the pre-#416 set, byte-exact what main's insert-only
    /// seeder installed, so a flag-off boot against a main-seeded production database is a byte-level
    /// no-op); true = <see cref="CorrectedTemplates"/> (the gated §4 correction). Required — every
    /// caller names the world it runs in; a defaulted value could silently flip a legally-gated
    /// behavior. See the selection point in the body.
    /// </param>
    /// <param name="reevaluator">
    /// When supplied, documents graded against a system template whose re-grade WATERMARK is behind
    /// (<see cref="ComplianceTemplate.RulesRevision"/> != <see cref="ComplianceTemplate.RegradedThroughRevision"/>)
    /// are re-evaluated ACROSS ALL ORGS after convergence commits (ADR 0036 Amendment 2). Convergence bumps
    /// <c>RulesRevision</c> on ANY rule-set change (add / delete / value / message / sort — see the loop below),
    /// so the watermark falls behind both when THIS boot changes the rules AND when a PRIOR boot committed the
    /// rule change but its re-grade never finished (SIGTERM / startup timeout / a caught-and-skipped page).
    /// Convergence mutates SHARED system-template rules — the only path that does, since endpoint rule edits
    /// are blocked on system templates — so without a durable re-grade a caterer COI persisted <c>Compliant</c>
    /// under the old rule set would silently stay Compliant despite failing the corrected rules (a
    /// false-Compliant verdict; the project's blocker-class failure). The trigger is ANY rule-set change
    /// rather than only a verdict-affecting one deliberately (#416 re-review): it decouples this data-layer
    /// seeder from <see cref="ComplianceCheckService"/>'s evaluator internals — a future evaluator that reads
    /// ErrorMessage / SortOrder / a new column must not silently skip a needed re-grade — and a redundant
    /// re-grade on a pure message / sort edit is negligible at MVP scale. A template's watermark advances ONLY
    /// when its fan-out reports FULL success, so a partially-failed re-grade re-fires next boot. Null (the
    /// default) skips the fan-out — used by structural tests that seed no documents. A DESCRIPTION-only edit is
    /// display-only and bumps no revision (NO re-grade), and a boot whose system templates are all caught up
    /// (<c>RulesRevision == RegradedThroughRevision</c>) does nothing at all (ADR 0036 idempotency invariant #3).
    /// </param>
    public static async Task EnsureAsync(
        SystemDbContext db,
        bool useCorrectedTemplates,
        IComplianceCheckService? reevaluator = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var org = await db.Organizations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == SystemOrgId, ct);
        if (org is null)
        {
            org = new Organization
            {
                Id = SystemOrgId,
                Name = "CompliDrop System",
                Industry = "system",
                CompanySize = "n/a",
                TimeZone = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Organizations.Add(org);
            await db.SaveChangesAsync(ct);
        }

        // Load already-seeded system templates WITH their rules so a re-run can CONVERGE each existing
        // system template to its seed definition (ADR 0036) — add a missing seed rule, update a live rule
        // whose value / message / sort differs, delete a live rule no seed rule matches, and update the
        // description. Convergence is SYSTEM-ONLY: the query filters to IsSystemTemplate, so a tenant's
        // clone (its own edited copy — user data) is never loaded and never touched. The seed is the single
        // source of truth for system templates; endpoint rule edits are blocked on them, so this is the one
        // path that mutates them. (Seeding was historically insert-only and then, in #400, additive-only —
        // it could neither correct a changed value nor remove a stale rule on a live system row; ADR 0036
        // supersedes both.)
        var existingTemplates = await db.ComplianceTemplates.IgnoreQueryFilters()
            .Where(t => t.IsSystemTemplate)
            .Include(t => t.Rules)
            .ToListAsync(ct);
        var byName = existingTemplates.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // Re-grade durability (#416, ADR 0036 Amendment 2): rather than re-grade only the templates THIS
        // boot mutated (a best-effort, this-boot-only trigger that stranded a stale verdict whenever the boot
        // was interrupted after committing the rules but before finishing the re-grade), convergence bumps a
        // system template's RulesRevision whenever its rule set changes AT ALL — any rule added, deleted, or
        // updated (message- and sort-order-only edits included; see the loop). The post-commit re-grade below
        // then fans out over EVERY system template whose RulesRevision has outrun its RegradedThroughRevision,
        // which catches both this boot's changes AND a prior boot's unfinished re-grade. The trigger is any
        // rule-set change, not only a verdict-affecting one, to decouple this seeder from the evaluator's
        // internals (#416 re-review): a future ComplianceCheckService.EvaluateRule that reads ErrorMessage /
        // SortOrder / a new column would otherwise make convergence skip a needed re-grade and leave a stale
        // persisted verdict. A DESCRIPTION-only drift updates the row but bumps NO revision (display-only,
        // verdict-neutral). A brand-new template (inserted whole) starts at revision 0/0: it has no
        // pre-existing documents to re-grade (no vendor could have been assigned a template that did not exist
        // yet), so its watermark is already caught up.

        // ============================== THE LEGAL GATE (#416, ADR 0036 Amendment 3) ==============================
        // Which rule set is the truth this boot converges to. The §4 corrected set (CorrectedTemplates)
        // is gated on the attorney/broker sign-off (docs/rule-engine/G1-COUNSEL-BRIEF.md §0) via
        // TemplateCorrections:Enabled, default OFF:
        //
        //   - OFF (production today): converge to LegacyTemplates — byte-exact the set main's insert-only
        //     seeder installed. Against a main-seeded production database this pass therefore finds NOTHING
        //     to add / update / delete, changes no description, bumps no RulesRevision, and re-grades
        //     nothing — a byte-level no-op, which is the merge-safety property that lets the correction
        //     merge without deploying the legally-gated behavior (pinned by the flag-off no-op test in
        //     ComplianceTemplateSeedTests).
        //   - ON (the deferred rollout): converge to the §4 corrected set and fire the durable watermarked
        //     cross-org re-grade below.
        //
        // The flag is REVERSIBLE in both directions: the same convergence machinery walks the live rows to
        // whichever set is selected (a flip-back deletes the corrected set's extra rules FK-safely, restores
        // the legacy values/messages, and re-grades via the watermark — no bespoke rollback path).
        var templates = useCorrectedTemplates ? CorrectedTemplates : LegacyTemplates;

        // Ids of live system-template rules this pass REMOVES. Their dependent ComplianceCheck rows must be
        // deleted in the SAME unit of work as the rule removals (the FK-safety block before SaveChanges).
        var removedRuleIds = new List<Guid>();

        foreach (var tpl in templates)
        {
            if (byName.TryGetValue(tpl.Name, out var live))
            {
                var ruleSetChanged = false;

                // Description is display-only (verdict-neutral): update it in place if it drifted, but
                // never let it, on its own, trigger a re-grade.
                if (!string.Equals(live.Description, tpl.Description, StringComparison.Ordinal))
                    live.Description = tpl.Description;

                // Add or update from the seed, keyed on the (DocumentType, FieldName, Operator) natural key
                // the rules endpoints also dedupe on (RuleMatches). A natural-key match is an UPDATE (of
                // value / message / sort); no match is an ADD. Changing a rule's operator / field /
                // document type is not an in-place reinterpretation — it surfaces as an add-of-new here plus
                // a delete-of-old below. Any of these is a rule-set change and re-grades the template.
                foreach (var seed in tpl.Rules)
                {
                    var match = live.Rules.FirstOrDefault(r => RuleMatches(r, seed));
                    if (match is null)
                    {
                        db.ComplianceRules.Add(NewRule(live.Id, seed));
                        ruleSetChanged = true; // a new governing rule can flip a verdict
                        continue;
                    }
                    // Converge every non-natural-key field to the seed. ExpectedValue drives the verdict
                    // directly; ErrorMessage / SortOrder are display-only — but ANY change re-grades, because
                    // the trigger is rule-set change, not verdict-affecting change, keeping this seeder
                    // decoupled from ComplianceCheckService.EvaluateRule's internals (#416 re-review).
                    if (!string.Equals(match.ExpectedValue, seed.ExpectedValue, StringComparison.Ordinal))
                    {
                        match.ExpectedValue = seed.ExpectedValue;
                        ruleSetChanged = true;
                    }
                    if (!string.Equals(match.ErrorMessage, seed.ErrorMessage, StringComparison.Ordinal))
                    {
                        match.ErrorMessage = seed.ErrorMessage;
                        ruleSetChanged = true;
                    }
                    if (match.SortOrder != seed.SortOrder)
                    {
                        match.SortOrder = seed.SortOrder;
                        ruleSetChanged = true;
                    }
                }

                // Delete any live rule the corrected seed no longer defines — a stale rule that graded a
                // fact the checklist should not (ADR 0036 §Context). Removing a governing rule is a rule-set
                // change and re-grades the template. Record the id so its dependent ComplianceCheck rows are
                // deleted in the same unit of work (FK-safety block below).
                foreach (var liveRule in live.Rules.ToList())
                {
                    if (!tpl.Rules.Any(seed => RuleMatches(liveRule, seed)))
                    {
                        db.ComplianceRules.Remove(liveRule);
                        removedRuleIds.Add(liveRule.Id);
                        ruleSetChanged = true;
                    }
                }

                // Bump the revision so the post-commit re-grade fires for this template (and re-fires next
                // boot until it fully succeeds). The increment rides the SAME convergence SaveChanges as the
                // rule changes it accounts for, so RulesRevision can never advance without its rule change
                // also committing.
                if (ruleSetChanged)
                    live.RulesRevision++;
                continue;
            }

            // New template: insert it and all of its rules.
            var templateId = Guid.NewGuid();
            db.ComplianceTemplates.Add(new ComplianceTemplate
            {
                Id = templateId,
                OrganizationId = SystemOrgId,
                Name = tpl.Name,
                Description = tpl.Description,
                IsSystemTemplate = true,
                CreatedAt = DateTime.UtcNow
            });
            foreach (var rule in tpl.Rules)
                db.ComplianceRules.Add(NewRule(templateId, rule));
        }

        // FK-safety (#416, mirrors ComplianceEndpoints.DeleteRule / #269): ComplianceCheck → ComplianceRule
        // is ON DELETE RESTRICT, so a rule this pass removes cannot be deleted while any document's check
        // row still references it. Delete those dependent checks in the SAME unit of work as the rule
        // removals — EF orders the dependent-check deletes before the principal-rule deletes, so the single
        // shared SaveChanges below commits atomically and never raises Postgres 23503. Cross-org by design:
        // one system rule is referenced by checks in many orgs (SystemDbContext skips the tenant filter) and
        // all must go with the rule. The post-commit re-grade recreates checks for the SURVIVING rules.
        // Without this, on any existing DB where a document was graded against a dropped rule the shared
        // SaveChanges throws, the whole convergence rolls back, Program.cs swallows it, and the §4 correction
        // silently never applies — re-failing every boot.
        if (removedRuleIds.Count > 0)
        {
            var orphanedChecks = await db.ComplianceChecks
                .Where(c => removedRuleIds.Contains(c.ComplianceRuleId))
                .ToListAsync(ct);
            db.ComplianceChecks.RemoveRange(orphanedChecks);
        }
        await db.SaveChangesAsync(ct);

        // DURABLE re-grade (#416, ADR 0036 Amendment 2). A rule-set change to a SHARED system template must
        // re-grade the documents already graded against it — across EVERY org — or a document persisted
        // Compliant under the OLD rule set stays Compliant despite failing the corrected rules (a
        // false-Compliant verdict; the project's blocker-class failure). Gate the re-grade on the persisted
        // WATERMARK, not on "templates this boot mutated": re-grade every system template whose RulesRevision
        // has outrun its RegradedThroughRevision. That set is exactly the templates changed THIS boot (their
        // revision was just bumped, committed by the SaveChanges above) PLUS any template a PRIOR boot changed
        // but never finished re-grading (SIGTERM / startup timeout / a caught-and-skipped page left
        // RegradedThroughRevision behind) — the durability the old this-boot-only gate lacked. This is normal
        // application re-evaluation (the same fan-out the endpoint path runs on a rule edit, #257), NOT a
        // destructive migration: no schema change, no ad-hoc SQL on the rules. Best-effort and batched,
        // respecting ADR 0030 (each page commits verdict + checks in one unit of work). Guarded on the
        // reevaluator so structural/insert-only callers can opt out. Sample-demo documents are deliberately
        // EXCLUDED from this cross-org re-grade — a sample predating a newly-required field would falsely flip
        // Compliant → NonCompliant on deploy, breaking the ADR 0028 one-click-demo contract (see
        // ReevaluateForTemplateForSystemAsync). Idempotent (invariant #3): a boot whose system templates are
        // all caught up (RulesRevision == RegradedThroughRevision) re-grades nothing.
        if (reevaluator is not null)
        {
            foreach (var tpl in existingTemplates)
            {
                if (tpl.RulesRevision == tpl.RegradedThroughRevision)
                    continue;

                // Capture the revision we are re-grading THROUGH before the fan-out runs, so a concurrent
                // bump (there is none today — one seed per boot) could never advance the watermark past work
                // this fan-out actually covered.
                var targetRevision = tpl.RulesRevision;
                var result = await reevaluator.ReevaluateForTemplateForSystemAsync(tpl.Id, ct);

                if (result.AllSucceeded)
                {
                    // Advance the watermark ONLY on a fully-successful fan-out; a skipped page leaves
                    // RegradedThroughRevision behind so the next boot re-fires. Written via ExecuteUpdate,
                    // NOT the tracked entity: ReevaluateForTemplateForSystemAsync shares this SystemDbContext
                    // and clears its ChangeTracker per page (ReevaluateWhereAsync), so `tpl` is DETACHED by
                    // the time we get here — a tracked-property write would be silently dropped at the next
                    // SaveChanges. ExecuteUpdate issues the UPDATE directly and enlists in the ambient
                    // transaction when there is one (the tests), or commits standalone in production.
                    await db.ComplianceTemplates.IgnoreQueryFilters()
                        .Where(t => t.Id == tpl.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.RegradedThroughRevision, targetRevision), ct);
                }

                logger?.LogInformation(
                    "Seed: converged system template '{Template}' — re-graded {Regraded}/{Targeted} document(s) across orgs ({FailedPages} page(s) failed; watermark {WatermarkState}).",
                    tpl.Name, result.Regraded, result.Targeted, result.FailedPages,
                    result.AllSucceeded ? $"advanced to revision {targetRevision}" : "held back for retry on the next boot");
            }
        }
    }

    // The (DocumentType, FieldName, Operator) natural key — mirrors the dedupe key the compliance
    // rules endpoints enforce (ComplianceEndpoints.UpsertRule). Case-insensitive and null-safe.
    private static bool RuleMatches(ComplianceRule live, RuleSeed seed) =>
        string.Equals(live.DocumentType, seed.DocumentType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(live.FieldName, seed.FieldName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(live.Operator, seed.Operator, StringComparison.OrdinalIgnoreCase);

    private static ComplianceRule NewRule(Guid templateId, RuleSeed rule) => new()
    {
        Id = Guid.NewGuid(),
        ComplianceTemplateId = templateId,
        DocumentType = rule.DocumentType,
        FieldName = rule.FieldName,
        Operator = rule.Operator,
        ExpectedValue = rule.ExpectedValue,
        ErrorMessage = rule.ErrorMessage,
        SortOrder = rule.SortOrder
    };

    private record RuleSeed(
        string DocumentType,
        string? FieldName,
        string Operator,
        string? ExpectedValue,
        string ErrorMessage,
        int SortOrder);

    private record TemplateSeed(string Name, string Description, RuleSeed[] Rules);

    // Event-venue vendor types (#192) — the suggested checklists a venue manager
    // recognises from her vendor packet. Renamed from the old generic trade names
    // by the RenameSystemTemplatesToVenueTypes migration so existing databases
    // pick up the new names without duplicating. Keep the (name → rules) pairing
    // in lockstep with that migration's renames.
    //
    // This is the corrected set from the deep template review (#416,
    // docs/rule-engine/TEMPLATE-REQUIREMENTS-REVIEW.md §4): rules that graded a fact no
    // real certificate/document carries were removed (Security's certification expiry,
    // Photographer's photographer-license expiry and E&O, Transportation's CDL-for-every-driver
    // check), and two floors were raised to their federal/insurance minimum (Photographer GL
    // $500k → $1M, Transportation auto $1M → $1.5M). When TemplateCorrections:Enabled is true,
    // EnsureAsync CONVERGES the live system rows to exactly this definition on boot and re-grades
    // the affected documents (ADR 0036); until the G1 sign-off flips that flag, the LegacyTemplates
    // set below stays active instead (ADR 0036 Amendment 3 — see the selection point in EnsureAsync).
    private static readonly TemplateSeed[] CorrectedTemplates =
    [
        new(SampleVendorTemplateName,
            "Typical insurance for a food & beverage caterer, including bar / alcohol service.",
            [
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000 per occurrence.", 1),
                // Bar / alcohol service is the classic private-event (dram-shop / social-host)
                // exposure that general liability excludes — a bar-service caterer graded "Covered"
                // with no liquor liability is exactly the gap #400 was filed for. A food-only
                // caterer failing this is safe friction: the owner removes it from their own clone.
                new("coi", "liquor_liability_limit", "min_value", "1000000", "Liquor liability of at least $1,000,000 is required for alcohol service. If this caterer doesn't serve alcohol, remove this rule from your checklist.", 2),
                new("coi", "workers_comp_limit", "required", null, "Workers' compensation coverage is required.", 3),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 4)
            ]),
        new("Event Rental Company",
            "Coverage for table, tent, and equipment rental vendors. (Bounce-house / inflatable vendors: Texas law also mandates a $1M amusement-ride policy — ask for that certificate.)",
            [
                // The "names your venue as additional insured" requirement is intentionally
                // NOT seeded: a venue names ITSELF, so the value is per-tenant — the user adds
                // it (with their own venue name) after cloning. A one-size placeholder reads
                // nonsensically. (#192 review.)
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000 per occurrence.", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2)
            ]),
        new("Security Service",
            "DPS guard-company licensing plus general-liability insurance. (Ask in writing that assault & battery is covered at full limits — certificates don't show it.)",
            [
                new("license", "license_number", "required", null, "License number is required.", 1),
                new("license", "expiration_date", "required", null, "License expiration date is required.", 2),
                // Guard / security vendors carry assault-and-battery exposure; a licence alone
                // insures nothing. Require general liability like every other insured category
                // — a Security Service checklist that asks for NO insurance was the #400 gap.
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000 per occurrence.", 3),
                // The old (certification, expiration_date) rule was removed (#416): a guard COMPANY
                // provides a DPS license + a COI, never a personal "certification" document, so the
                // rule was a permanent false "Missing". A COI expiry check replaces it.
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 4)
            ]),
        new("Transportation / Shuttle",
            "Auto liability and a current driver credential for shuttle and transport vendors. (Vehicles seating 16+ including the driver: require $5,000,000 and a CDL with passenger endorsement.)",
            [
                // $1.5M is the federal floor for small for-hire passenger vehicles (49 CFR 387.33T);
                // the old $1M was below it. The old (license, license_type == "CDL") rule was removed
                // (#416): a CDL attaches only at 16+ seats (49 CFR 383.5), so it failed every lawful
                // ≤15-seat shuttle driver — a real license number + expiry are what to verify instead.
                new("coi", "auto_liability_limit", "min_value", "1500000", "Auto liability must be at least $1,500,000 (the federal floor for small for-hire passenger vehicles).", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2),
                new("license", "license_number", "required", null, "Driver license number is required.", 3),
                new("license", "expiration_date", "required", null, "License expiration date is required.", 4)
            ]),
        new("Photographer / Videographer",
            "General liability coverage for photo and video vendors.",
            [
                // GL raised $500k → $1M (the standard venue floor, #416). The old E&O rule and the
                // photographer-license expiry rule were removed: Texas issues no photographer license,
                // and a venue has no insurable interest in a photographer's E&O — both graded facts no
                // real photographer document carries, so they were permanent false "Missing"s.
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000 per occurrence.", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2)
            ])
    ];

    // The LEGACY (pre-#416) set — BYTE-EXACT the template definitions main's insert-only seeder
    // installed (verified against `git show origin/main:api/CompliDrop.Api/Data/Seed/
    // ComplianceTemplateSeed.cs`). Active while TemplateCorrections:Enabled is false (the default),
    // so a merged-but-unflipped deploy converges to EXACTLY what is already in the production
    // database and touches nothing (ADR 0036 Amendment 3 — the merge-safety property).
    //
    // DO NOT EDIT these definitions. Any difference from main's set — a message, a sort order, a
    // description character — would make the flag-off boot "correct" live production rows and
    // re-grade customer verdicts BEFORE the legal sign-off, the exact thing the gate exists to
    // prevent. The set is retired wholesale (with the flag) after the G1 sign-off + flip have been
    // stable; it is never maintained.
    private static readonly TemplateSeed[] LegacyTemplates =
    [
        new(SampleVendorTemplateName,
            "Typical insurance for a food & beverage caterer.",
            [
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000.", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2),
                new("coi", "workers_comp_limit", "required", null, "Workers comp coverage is required.", 3)
            ]),
        new("Event Rental Company",
            "Coverage for table, tent, and equipment rental vendors.",
            [
                // The "names your venue as additional insured" requirement is intentionally
                // NOT seeded: a venue names ITSELF, so the value is per-tenant — the user adds
                // it (with their own venue name) after cloning. A one-size placeholder reads
                // nonsensically. (#192 review.)
                new("coi", "general_liability_limit", "min_value", "1000000", "General liability must be at least $1,000,000.", 1),
                new("coi", "expiration_date", "required", null, "Expiration date is required.", 2)
            ]),
        new("Security Service",
            "Licensing for event security and guard services.",
            [
                new("license", "license_number", "required", null, "License number is required.", 1),
                new("license", "expiration_date", "required", null, "License expiration date is required.", 2),
                new("certification", "expiration_date", "required", null, "Certification expiration date is required.", 3)
            ]),
        new("Transportation / Shuttle",
            "Auto coverage and a CDL for shuttle and transport vendors.",
            [
                new("coi", "auto_liability_limit", "min_value", "1000000", "Auto liability must be at least $1,000,000.", 1),
                new("license", "license_type", "equals", "CDL", "Driver must hold a CDL.", 2),
                new("license", "expiration_date", "required", null, "License expiration date is required.", 3)
            ]),
        new("Photographer / Videographer",
            "General + professional (E&O) coverage for photo and video vendors.",
            [
                new("coi", "general_liability_limit", "min_value", "500000", "General liability must be at least $500,000.", 1),
                new("coi", "professional_liability_limit", "min_value", "1000000", "Professional liability (E&O) must be at least $1,000,000.", 2),
                new("license", "expiration_date", "required", null, "Professional license expiration date is required.", 3)
            ])
    ];
}
