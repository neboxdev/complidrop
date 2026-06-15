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
        IAuditLogger audit,
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
        await audit.LogAsync("complianceTemplate.created", nameof(ComplianceTemplate), template.Id, after: new { template.Name });
        return Results.Ok(new { data = new { id = template.Id }, error = (object?)null });
    }

    private static async Task<IResult> UpdateTemplate(
        Guid id,
        UpdateTemplateRequest req,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var template = await db.ComplianceTemplates.FirstOrDefaultAsync(t => t.Id == id && !t.IsSystemTemplate, ct);
        if (template is null) return NotFound();
        template.Name = req.Name.Trim();
        template.Description = req.Description;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("complianceTemplate.updated", nameof(ComplianceTemplate), template.Id);
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> DeleteTemplate(
        Guid id,
        AppDbContext db,
        IComplianceCheckService checker,
        IAuditLogger audit,
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

        if (vendorIds.Count > 0)
            await audit.LogAsync(
                "vendor.template_cleared_on_template_delete", nameof(ComplianceTemplate), id,
                after: new { count = vendorIds.Count, vendorIds });
        await audit.LogAsync("complianceTemplate.deleted", nameof(ComplianceTemplate), id);

        // Re-evaluation fan-out (#257): the affected vendors now have no checklist, so their
        // documents must drop from any rule verdict to "no requirements apply" (Pending) and shed
        // the now-orphaned check rows. Runs after commit — the template is gone and the assignments
        // are cleared, so each evaluation takes the no-governing-rules path.
        foreach (var vendorId in vendorIds)
            await checker.ReevaluateForVendorAsync(vendorId, ct);

        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> UpsertRule(
        Guid templateId,
        UpsertRuleRequest req,
        AppDbContext db,
        IComplianceCheckService checker,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var template = await db.ComplianceTemplates.Include(t => t.Rules)
            .FirstOrDefaultAsync(t => t.Id == templateId && !t.IsSystemTemplate, ct);
        if (template is null) return NotFound();

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
        await checker.ReevaluateForTemplateAsync(template.Id, ct);

        await audit.LogAsync("complianceRule.upserted", nameof(ComplianceRule), rule.Id);
        return Results.Ok(new { data = new { id = rule.Id }, error = (object?)null });
    }

    private static async Task<IResult> DeleteRule(
        Guid templateId,
        Guid ruleId,
        AppDbContext db,
        IComplianceCheckService checker,
        IAuditLogger audit,
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
        // any document had been evaluated against the rule). Everything — check cleanup,
        // rule delete, and the re-evaluation of affected documents — runs in ONE
        // transaction: a failure (or client abort) mid-re-eval rolls the whole delete back
        // for a clean retry, instead of leaving documents stuck on verdicts computed
        // against a rule that no longer exists. EvaluateAsync enlists because it uses this
        // same request-scoped AppDbContext. Re-eval on rule EDITS / assignment changes
        // stays #257's scope.
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Single DELETE … RETURNING so the affected-document snapshot and the check
            // cleanup are one atomic statement — a separate SELECT-then-DELETE leaves a
            // window where a concurrent evaluation's check row is deleted without its
            // document making the re-eval list. No timestamptz involved (ADR 0009 n/a).
            var affectedDocIds = (await db.Database
                .SqlQuery<Guid>($"""
                    DELETE FROM "ComplianceChecks" WHERE "ComplianceRuleId" = {ruleId} RETURNING "DocumentId"
                    """)
                .ToListAsync(ct))
                .Distinct()
                .ToList();

            db.ComplianceRules.Remove(rule);
            await db.SaveChangesAsync(ct);

            // EvaluateAsync is tenant-filtered, so a foreign document id (possible only via
            // a cross-org template assignment, see #273) resolves to nothing and is skipped.
            foreach (var docId in affectedDocIds)
                await checker.EvaluateAsync(docId, ct);

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
