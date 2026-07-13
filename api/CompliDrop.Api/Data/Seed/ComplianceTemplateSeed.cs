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
    /// sample vendor. The generated sample COI is built to PASS exactly this checklist's rules
    /// (GL ≥ $1M each-occurrence, expiration date present, workers-comp coverage present, and
    /// liquor liability ≥ $1M — the Caterer checklist now covers bar / alcohol service, #400), so
    /// the demo lands a fresh org on a real "Compliant" verdict. Kept here as the single source so
    /// the seed below and the sample-seed endpoint can never drift on the name.
    /// </summary>
    internal const string SampleVendorTemplateName = "Caterer";

    /// <summary>
    /// Number of system templates this seed installs. Exposed to the test project
    /// (<see cref="InternalsVisibleToAttribute"/>) so harness assertions can pin the exact count
    /// without re-declaring a brittle hand-mirror constant — adding a template here forces the
    /// test to be updated (or, if the count is read indirectly, doesn't break it at all).
    /// </summary>
    internal static int TemplateCount => Templates.Length;

    /// <param name="reevaluator">
    /// When supplied, documents graded against a system template whose rule set this run changes in a
    /// VERDICT-AFFECTING way — a rule added, a rule deleted, or an existing rule's compared value
    /// (<see cref="ComplianceRule.ExpectedValue"/>) / natural key changed — are re-evaluated ACROSS ALL
    /// ORGS after convergence commits (ADR 0036). Convergence mutates SHARED system-template rules — the
    /// only path that does, since endpoint rule edits are blocked on system templates — so without this a
    /// caterer COI persisted <c>Compliant</c> under the old rule set would silently stay Compliant despite
    /// failing the corrected rules (a false-Compliant verdict). Null (the default) skips the fan-out —
    /// used by structural tests that seed no documents. The re-grade runs ONLY for templates that actually
    /// changed a verdict-affecting way this run; a pure error-message / sort-order / description edit
    /// updates the row but is verdict-neutral and triggers NO re-grade, and a boot whose templates already
    /// match the seed does nothing at all.
    /// </param>
    public static async Task EnsureAsync(
        SystemDbContext db,
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

        // System templates whose rule set changed in a VERDICT-AFFECTING way this run (a rule added,
        // deleted, or an existing rule's compared value changed). After convergence commits, the documents
        // graded against each must be re-evaluated across every org (ADR 0036) — see the fan-out below. A
        // pure message / sort-order / description edit is verdict-neutral: the row is updated but the
        // template is NOT listed here. A brand-new template (inserted whole) is NOT listed either: it has
        // no pre-existing documents to re-grade (no vendor could have been assigned a template that did not
        // exist yet).
        var changedTemplateIds = new List<(Guid Id, string Name)>();

        foreach (var tpl in Templates)
        {
            if (byName.TryGetValue(tpl.Name, out var live))
            {
                var verdictAffectingChange = false;

                // Description is display-only (verdict-neutral): update it in place if it drifted, but
                // never let it, on its own, trigger a re-grade.
                if (!string.Equals(live.Description, tpl.Description, StringComparison.Ordinal))
                    live.Description = tpl.Description;

                // Add or update from the seed, keyed on the (DocumentType, FieldName, Operator) natural key
                // the rules endpoints also dedupe on (RuleMatches). A natural-key match is an UPDATE (of
                // value / message / sort); no match is an ADD. Changing a rule's operator / field /
                // document type is not an in-place reinterpretation — it surfaces as an add-of-new here plus
                // a delete-of-old below (both verdict-affecting).
                foreach (var seed in tpl.Rules)
                {
                    var match = live.Rules.FirstOrDefault(r => RuleMatches(r, seed));
                    if (match is null)
                    {
                        db.ComplianceRules.Add(NewRule(live.Id, seed));
                        verdictAffectingChange = true; // a new governing rule can flip a verdict
                        continue;
                    }
                    // ExpectedValue is the ONLY non-natural-key field the evaluator reads (EvaluateRule),
                    // so a change to it is verdict-affecting; ErrorMessage and SortOrder are display-only.
                    if (!string.Equals(match.ExpectedValue, seed.ExpectedValue, StringComparison.Ordinal))
                    {
                        match.ExpectedValue = seed.ExpectedValue;
                        verdictAffectingChange = true;
                    }
                    if (!string.Equals(match.ErrorMessage, seed.ErrorMessage, StringComparison.Ordinal))
                        match.ErrorMessage = seed.ErrorMessage;
                    if (match.SortOrder != seed.SortOrder)
                        match.SortOrder = seed.SortOrder;
                }

                // Delete any live rule the corrected seed no longer defines — a stale rule that graded a
                // fact the checklist should not (ADR 0036 §Context). Removing a governing rule can flip a
                // verdict, so it is verdict-affecting.
                foreach (var liveRule in live.Rules.ToList())
                {
                    if (!tpl.Rules.Any(seed => RuleMatches(liveRule, seed)))
                    {
                        db.ComplianceRules.Remove(liveRule);
                        verdictAffectingChange = true;
                    }
                }

                if (verdictAffectingChange)
                    changedTemplateIds.Add((live.Id, live.Name));
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
        await db.SaveChangesAsync(ct);

        // A verdict-affecting change to a SHARED system template must re-grade the documents already graded
        // against it — across EVERY org — or a document persisted Compliant under the OLD rule set stays
        // Compliant despite failing the corrected rules (a false-Compliant verdict, ADR 0036). This is
        // normal application re-evaluation (the same fan-out the endpoint path runs on a rule edit, #257),
        // NOT a destructive migration: no schema change, no ad-hoc SQL. It runs ONLY for templates that
        // changed a verdict-affecting way this run, so a boot with no drift — or a verdict-neutral
        // message / sort-order / description-only edit — does no extra work. Best-effort and batched,
        // respecting ADR 0030 (each page commits verdict + checks in one unit of work). Guarded on the
        // reevaluator so structural/insert-only callers can opt out. Sample-demo documents are deliberately
        // EXCLUDED from this cross-org re-grade — a sample predating a newly-required field would falsely
        // flip Compliant → NonCompliant on deploy, breaking the ADR 0028 one-click-demo contract (see
        // ReevaluateForTemplateForSystemAsync).
        if (reevaluator is not null && changedTemplateIds.Count > 0)
        {
            foreach (var (id, name) in changedTemplateIds)
            {
                var regraded = await reevaluator.ReevaluateForTemplateForSystemAsync(id, ct);
                logger?.LogInformation(
                    "Seed: converged system template '{Template}' — re-graded {Count} document(s) across orgs.",
                    name, regraded);
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
    // $500k → $1M, Transportation auto $1M → $1.5M). EnsureAsync CONVERGES the live system rows
    // to exactly this definition on boot and re-grades the affected documents (ADR 0036).
    private static readonly TemplateSeed[] Templates =
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
}
