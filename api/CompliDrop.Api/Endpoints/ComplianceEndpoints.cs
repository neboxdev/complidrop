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
        IAuditLogger audit,
        CancellationToken ct)
    {
        var template = await db.ComplianceTemplates.FirstOrDefaultAsync(t => t.Id == id && !t.IsSystemTemplate, ct);
        if (template is null) return NotFound();
        db.ComplianceTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("complianceTemplate.deleted", nameof(ComplianceTemplate), id);
        return Results.Ok(new { data = new { id }, error = (object?)null });
    }

    private static async Task<IResult> UpsertRule(
        Guid templateId,
        UpsertRuleRequest req,
        AppDbContext db,
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
        await audit.LogAsync("complianceRule.upserted", nameof(ComplianceRule), rule.Id);
        return Results.Ok(new { data = new { id = rule.Id }, error = (object?)null });
    }

    private static async Task<IResult> DeleteRule(
        Guid templateId,
        Guid ruleId,
        AppDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var rule = await db.ComplianceRules
            .Where(r => r.Id == ruleId && r.ComplianceTemplateId == templateId)
            .FirstOrDefaultAsync(ct);
        if (rule is null) return NotFound();
        db.ComplianceRules.Remove(rule);
        await db.SaveChangesAsync(ct);
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
            .Include(c => c.ComplianceRule)
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
