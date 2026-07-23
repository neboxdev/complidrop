using System.Globalization;
using CompliDrop.Api.Auth;
using CompliDrop.Api.Data;
using CompliDrop.Api.DTOs.Compliance;
using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Endpoints;

public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/compliance").RequireAuthorization();

        group.MapGet("/templates", ListTemplates);
        group.MapGet("/templates/{id:guid}", GetTemplate);
        group.MapPost("/templates", CreateTemplate);
        group.MapPut("/templates/{id:guid}", UpdateTemplate);
        group.MapDelete("/templates/{id:guid}", DeleteTemplate);

        group.MapPost("/templates/{templateId:guid}/rules", UpsertRule);
        group.MapDelete("/templates/{templateId:guid}/rules/{ruleId:guid}", DeleteRule);

        group.MapPost("/check/{documentId:guid}", RunCheck);
        group.MapGet("/checks/{documentId:guid}", ListChecks);
        group.MapGet("/status", OrgStatus);
    }

    private static async Task<IResult> ListTemplates(AppDbContext db, CancellationToken ct)
    {
        var templates = await db.ComplianceTemplates
            .Include(t => t.Rules)
            .Include(t => t.Vendors)
            .Select(t => new TemplateSummary(
                t.Id, t.Name, t.Description, t.IsSystemTemplate,
                t.Rules.Count, t.Vendors.Count))
            .ToListAsync(ct);
        return Results.Ok(new { data = templates, error = (object?)null });
    }

    private static async Task<IResult> GetTemplate(Guid id, AppDbContext db, CancellationToken ct)
    {
        var template = await db.ComplianceTemplates
            .Include(t => t.Rules)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) return NotFound();

        var detail = new TemplateDetail(
            template.Id, template.Name, template.Description, template.IsSystemTemplate,
            template.Rules
                .OrderBy(r => r.SortOrder)
                .Select(r => new TemplateRule(r.Id, r.DocumentType, r.FieldName, r.Operator, r.ExpectedValue, r.ErrorMessage, r.SortOrder))
                .ToArray());
        return Results.Ok(new { data = detail, error = (object?)null });
    }

    private static async Task<IResult> CreateTemplate(
        CreateTemplateRequest req,
        AppDbContext db,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (currentUser.OrganizationId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return Error(400, "validation.name", "Template name is required.");

        var template = new ComplianceTemplate
        {
            Id = Guid.NewGuid(),
            OrganizationId = currentUser.OrganizationId.Value,
            Name = req.Name.Trim(),
            Description = req.Description,
            IsSystemTemplate = false,
            CreatedAt = DateTime.UtcNow
        };
        db.ComplianceTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        // Interceptor records "compliancetemplate.created" with a full snapshot — the explicit
        // duplicate was dropped (#318 FP-043); manual IAuditLogger is for non-entity events only.
        return Results.Ok(new { data = new { id = template.Id }, error = (object?)null });
    }

    private static async Task<IResult> UpdateTemplate(
        Guid id,
        UpdateTemplateRequest req,
        AppDbContext db,
        CancellationToken ct)
    {
        var template = await db.ComplianceTemplates.FirstOrDefaultAsync(t => t.Id == id && !t.IsSystemTemplate, ct);
        if (template is null) return NotFound();
        template.Name = req.Name.Trim();
        template.Description = req.Description;
        await db.SaveChangesAsync(ct);
        // Interceptor records "compliancetemplate.updated" — no explicit duplicate (#318 FP-043).
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> DeleteTemplate(
        Guid id,
        AppDbContext db,
        IComplianceCheckService checker,
        IAuditLogger audit,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var template = await db.ComplianceTemplates.FirstOrDefaultAsync(t => t.Id == id && !t.IsSystemTemplate, ct);
        if (template is null) return NotFound();

        // Clear the assignment on vendors still pointing at this template, atomically with the
        // soft delete (#273 review): the soft-deleted template vanishes behind the query filter,
        // but the vendor row would keep the stale FK — GetVendor round-trips it into the edit
        // form, and the assignment guard (VendorEndpoints.TemplateIsAssignable) would then 400
        // every save of that form, even for unrelated field edits. ExecuteUpdate (not tracked
        // entities) for the same reason as DeleteVendor's link deactivation; the in-transaction
        // id snapshot feeds the explicit audit row below because ExecuteUpdate bypasses the
        // audit interceptor (UpdatedAt is bumped manually for the same reason). The UPDATE
        // filters by predicate, not the snapshot, so an assignment committed between the two
        // statements is still cleared; one committed after the UPDATE leaves a dangling FK —
        // recoverable (clearing the field is always assignable) and eval-safe (both evaluation
        // contexts filter out the soft-deleted template). Re-evaluating the affected vendors'
        // documents stays #257's scope (assignment change) — the next evaluation clears their
        // now-orphaned checks.
        List<Guid> vendorIds;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            vendorIds = await db.Vendors
                .Where(v => v.ComplianceTemplateId == id)
                .Select(v => v.Id)
                .ToListAsync(ct);

            await db.Vendors
                .Where(v => v.ComplianceTemplateId == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.ComplianceTemplateId, (Guid?)null)
                    .SetProperty(v => v.UpdatedAt, DateTime.UtcNow), ct);

            db.ComplianceTemplates.Remove(template);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Only the assignment-clearing row is explicit: ExecuteUpdate bypasses the interceptor.
        // The template soft-delete is recorded by the interceptor as "compliancetemplate.deleted" —
        // the explicit duplicate was dropped (#318 FP-043).
        if (vendorIds.Count > 0)
            await audit.LogAsync(
                "vendor.template_cleared_on_template_delete", nameof(ComplianceTemplate), id,
                after: new { count = vendorIds.Count, vendorIds });

        // Re-evaluation fan-out (#257): the affected vendors now have no checklist, so their
        // documents must drop from any rule verdict to "no requirements apply" (Pending) and shed
        // the now-orphaned check rows. Runs after commit — the template is gone and the assignments
        // are cleared, so each evaluation takes the no-governing-rules path. One batched pass over
        // the whole vendor set rather than a per-vendor loop, so deleting a checklist shared by a
        // large vendor base doesn't pin the request thread with hundreds of serial round-trips (#293).
        // Routed through PostCommitRegrade.RunAsync (#364) — see there for the token and
        // failure-swallowing rationale: the template delete and the assignment clearing have already
        // committed, so neither a client disconnect nor a fan-out failure may leave half the vendor
        // base holding verdicts computed against a checklist that no longer exists — or 500 a delete
        // that landed.
        await PostCommitRegrade.RunAsync(
            token => checker.ReevaluateForVendorsAsync(vendorIds, token),
            lifetime, loggerFactory, "template delete");

        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> UpsertRule(
        Guid templateId,
        UpsertRuleRequest req,
        AppDbContext db,
        IComplianceCheckService checker,
        IAuditLogger audit,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var template = await db.ComplianceTemplates.Include(t => t.Rules)
            .FirstOrDefaultAsync(t => t.Id == templateId && !t.IsSystemTemplate, ct);
        if (template is null) return NotFound();

        // A value-operator rule (equals / contains / min_value) is meaningless without an
        // ExpectedValue to compare against, and a null one is actively unsafe: it made the `equals`
        // arm fail OPEN — a document MISSING the field read Compliant — until #374. Reject it at the
        // write boundary (both create and update run through here) so only a well-formed rule can be
        // persisted; ComplianceCheckService.EvaluateRule also fails such a rule closed as a
        // defense-in-depth net for rows written before this guard. `required` legitimately carries no
        // expected value, so it stays exempt. Op set + normalization mirror EvaluateRule's switch.
        var op = req.Operator?.ToLowerInvariant();
        if (op is "equals" or "contains" or "min_value" && string.IsNullOrWhiteSpace(req.ExpectedValue))
            return Error(400, "validation.expected_value_required",
                "This requirement needs a value to check against.");

        // A NON-blank min_value threshold that isn't a number is just as broken as a blank one: it
        // passes the guard above but can never be satisfied — ComplianceCheckService.EvaluateRule's
        // min_value arm parses ExpectedValue and, on a parse failure, fails EVERY document closed with
        // the note "Unable to parse numeric comparison." That's fail-closed (never a false Compliant),
        // but it's still a misconfiguration, so reject it at the write boundary too. Runs only for a
        // non-blank min_value — the blank case already returned above — and mirrors EvaluateRule's
        // parse call byte-for-byte (NumberStyles.Any + CultureInfo.InvariantCulture) so a threshold the
        // eval can parse is never rejected here and vice versa. Distinct code from the blank case (#374).
        if (op == "min_value"
            && !decimal.TryParse(req.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return Error(400, "validation.expected_value_not_numeric",
                "This requirement needs a number to compare against.");

        // Dedupe on (documentType, fieldName, operator) (#319 FP-081): the same requirement added
        // twice produces confusing double sentences and double failures. Excludes the rule being
        // edited (req.Id) so re-saving an existing rule is fine. The frontend also grays out
        // already-added types, but this is the authoritative guard.
        var isDuplicate = template.Rules.Any(r =>
            r.Id != req.Id &&
            string.Equals(r.DocumentType, req.DocumentType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.FieldName, req.FieldName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Operator, req.Operator, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
            return Error(409, "complianceRule.duplicate",
                "That requirement is already on this checklist — edit the existing one instead.");

        ComplianceRule rule;
        if (req.Id is Guid ruleId)
        {
            rule = template.Rules.FirstOrDefault(r => r.Id == ruleId)
                   ?? throw new InvalidOperationException("Rule not found.");
            rule.DocumentType = req.DocumentType;
            rule.FieldName = req.FieldName;
            rule.Operator = req.Operator;
            rule.ExpectedValue = req.ExpectedValue;
            rule.ErrorMessage = req.ErrorMessage;
            rule.SortOrder = req.SortOrder;
        }
        else
        {
            rule = new ComplianceRule
            {
                Id = Guid.NewGuid(),
                ComplianceTemplateId = template.Id,
                DocumentType = req.DocumentType,
                FieldName = req.FieldName,
                Operator = req.Operator,
                ExpectedValue = req.ExpectedValue,
                ErrorMessage = req.ErrorMessage,
                SortOrder = req.SortOrder
            };
            db.ComplianceRules.Add(rule);
        }

        await db.SaveChangesAsync(ct);

        // Re-evaluation fan-out (#257): a rule add/edit changes the verdict for every document whose
        // vendor uses this template. Without this, the rules page saves and the documents keep their
        // stale Compliant/NonCompliant badge until something else happens to re-trigger evaluation.
        // Routed through PostCommitRegrade.RunAsync (#364) — see there for the token and
        // failure-swallowing rationale. It matters MORE here than on the delete path: an edit can
        // TIGHTEN a requirement, so a document left on its pre-edit verdict is a genuine false
        // Compliant, not merely a stale-strict one.
        await PostCommitRegrade.RunAsync(
            token => checker.ReevaluateForTemplateAsync(template.Id, token),
            lifetime, loggerFactory, "rule upsert");

        await audit.LogAsync("complianceRule.upserted", nameof(ComplianceRule), rule.Id);
        return Results.Ok(new { data = new { id = rule.Id }, error = (object?)null });
    }

    private static async Task<IResult> DeleteRule(
        Guid templateId,
        Guid ruleId,
        AppDbContext db,
        IComplianceCheckService checker,
        IAuditLogger audit,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Resolve through the tenant-filtered template set, excluding system templates —
        // mirrors UpsertRule. ComplianceRule itself carries no OrganizationId (and so no
        // tenant filter), so querying it directly would let a caller holding both GUIDs
        // delete another org's rule — or mutate the SHARED system templates every org
        // sees (#269).
        var template = await db.ComplianceTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId && !t.IsSystemTemplate, ct);
        if (template is null) return NotFound();

        var rule = await db.ComplianceRules
            .Where(r => r.Id == ruleId && r.ComplianceTemplateId == templateId)
            .FirstOrDefaultAsync(ct);
        if (rule is null) return NotFound();

        // ComplianceCheck → ComplianceRule is ON DELETE RESTRICT, so the dependent check
        // rows must go with the rule (#269: the rules-page trash button 500'd forever once
        // any document had been evaluated against the rule). Check cleanup and rule delete
        // stay in ONE transaction, so the FK-restrict conflict caught below is a clean
        // all-or-nothing retry. The re-evaluation of affected documents runs AFTER the
        // commit, batched — see the fan-out below (#364).
        List<Guid> affectedDocIds;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Single DELETE … RETURNING so the affected-document snapshot and the check
            // cleanup are one atomic statement — a separate SELECT-then-DELETE leaves a
            // window where a concurrent evaluation's check row is deleted without its
            // document making the re-eval list. No timestamptz involved (ADR 0009 n/a).
            // The snapshot feeds the post-commit fan-out below; see there for why template
            // membership alone would NOT cover every document these rows belong to (#364).
            affectedDocIds = (await db.Database
                .SqlQuery<Guid>($"""
                    DELETE FROM "ComplianceChecks" WHERE "ComplianceRuleId" = {ruleId} RETURNING "DocumentId"
                    """)
                .ToListAsync(ct))
                .Distinct()
                .ToList();

            db.ComplianceRules.Remove(rule);
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent evaluation inserted a fresh check for this rule between the
            // cleanup and the rule delete (FK restrict). The rollback left everything
            // intact — the retry's snapshot will include the new row.
            return Error(409, "compliance.conflict",
                "This requirement was being checked at the same moment. Please try again.");
        }

        // Audit AFTER commit: IAuditLogger writes through SystemDbContext — a different
        // connection that cannot join this transaction (see #269 review). Worst case is a
        // missing audit row on a post-commit crash, the same shape as every other endpoint.
        await audit.LogAsync("complianceRule.deleted", nameof(ComplianceRule), ruleId);

        // Re-evaluation fan-out (#364): batched and AFTER the commit, mirroring UpsertRule — a
        // rule delete is the twin of a rule edit and must re-grade the same population. Pre-#364
        // this was a per-document EvaluateAsync loop INSIDE the transaction above: ~4 serial
        // round-trips per affected document, all on one never-cleared change tracker (so every
        // SaveChanges re-ran DetectChanges and the audit interceptor's full Entries() scan —
        // quadratic), while holding the rule + check locks the whole time. On a checklist shared
        // by a large vendor base that turned one trash-button click into thousands of serial
        // round-trips and timed the request out, so the delete never landed at all.
        //
        // SCOPE = template membership UNION the deleted rule's check-row holders, which is what
        // makes this a STRICT SUPERSET of the loop it replaces. Membership alone is not: the
        // predicate joins through d.Vendor, which carries the Vendor soft-delete query filter, so
        // a document whose vendor was soft-deleted reads Vendor == null and drops out of it. That
        // state is still reachable — DeleteVendor fans out its own post-commit re-grade since
        // #422, but that pass is best-effort (a truncated or failed run is swallowed), so a
        // document can be left on a Compliant verdict graded against rules that no longer govern
        // it (the vacuous-Compliant class #257 exists to prevent). The old loop healed those to
        // Pending as a side effect of iterating the deleted rule's check rows; passing
        // affectedDocIds keeps that heal instead of letting a performance fix quietly drop a
        // verdict correction. Foreign ids (possible only via the #273 cross-org assignment
        // state) are filtered out by the tenant filter, exactly as EvaluateAsync did.
        //
        // Token handling and failure swallowing live in PostCommitRegrade.RunAsync — read its doc
        // comment for why this must not ride the request's ct and must not 500 a committed delete.
        //
        // FAILURE POSTURE: the fan-out is still best-effort per page (a page whose SaveChanges
        // fails is logged and skipped), so a persistence failure leaves the rule deleted with
        // that page's documents on their previous verdict, where the old in-transaction loop
        // rolled the delete back. That is the posture UpsertRule and DeleteTemplate already ship,
        // and it is the SAFE direction here: deleting a requirement can only loosen a checklist,
        // so a verdict left stale is stale-STRICT (a NonCompliant that should now read Compliant),
        // never a false Compliant — with one bounded exception, deleting the LAST applicable rule,
        // where a stale Compliant should have become Pending. The nightly sweep does NOT heal
        // this: ComplianceSweepBackgroundService only does date-transition ExecuteUpdates and
        // never re-runs rule evaluation. The healers are the next user-initiated re-grade of the
        // document — "Check again", another rule edit, or a checklist reassignment.
        await PostCommitRegrade.RunAsync(
            token => checker.ReevaluateForTemplateOrDocumentsAsync(template.Id, affectedDocIds, token),
            lifetime, loggerFactory, "rule delete");

        return Results.Ok(new { data = new { id = ruleId }, error = (object?)null });
    }

    private static async Task<IResult> RunCheck(
        Guid documentId,
        IComplianceCheckService checker,
        CancellationToken ct)
    {
        var status = await checker.EvaluateAsync(documentId, ct);
        return Results.Ok(new { data = new { status = status.ToString() }, error = (object?)null });
    }

    private static async Task<IResult> ListChecks(
        Guid documentId,
        AppDbContext db,
        CancellationToken ct)
    {
        // Tenant guard: ComplianceCheck carries no OrganizationId and therefore
        // has NO global query filter (see AppDbContext.OnModelCreating). Querying
        // it directly by documentId would return ANOTHER org's checks for any
        // GUID an authenticated caller supplies — an IDOR / tenant-isolation
        // leak. Gate on the Document being visible first: db.Documents IS
        // tenant-filtered, so a cross-org or missing id resolves to 404, never a
        // data leak. Mirrors the vendor-ownership guard in DocumentEndpoints. (#193)
        if (!await db.Documents.AnyAsync(d => d.Id == documentId, ct))
            return NotFound();

        var checks = await db.ComplianceChecks
            .Where(c => c.DocumentId == documentId)
            // No .Include(c => c.ComplianceRule): the projection below pulls the rule
            // columns directly, so EF joins ComplianceRule from the Select — an
            // explicit Include would be silently dropped (and log a warning). (#193 review)
            .OrderBy(c => c.CheckedAt)
            .Select(c => new ComplianceCheckDto(
                c.Id, c.ComplianceRuleId,
                c.ComplianceRule.FieldName, c.ComplianceRule.Operator, c.ComplianceRule.ExpectedValue,
                c.ComplianceRule.ErrorMessage,
                c.ActualValue, c.IsPassed, c.Notes, c.CheckedAt))
            .ToListAsync(ct);
        return Results.Ok(new { data = checks, error = (object?)null });
    }

    private static async Task<IResult> OrgStatus(AppDbContext db, CancellationToken ct)
    {
        var counts = await db.Documents
            .GroupBy(d => d.ComplianceStatus)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .ToListAsync(ct);
        return Results.Ok(new { data = counts, error = (object?)null });
    }

    private static IResult Unauthorized() =>
        Results.Json(new { data = (object?)null, error = new { code = "auth.unauthorized", message = "Not authenticated." } }, statusCode: 401);

    private static IResult NotFound() =>
        Results.Json(new { data = (object?)null, error = new { code = "compliance.not_found", message = "Not found." } }, statusCode: 404);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { data = (object?)null, error = new { code, message } }, statusCode: status);
}
