using System.Globalization;
using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Services;

public interface IComplianceCheckService
{
    Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct);
    Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct);
}

public class ComplianceCheckService(
    AppDbContext db,
    SystemDbContext sysDb) : IComplianceCheckService
{
    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(db, documentId, ct);

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(sysDb, documentId, ct);

    private static async Task<ComplianceStatus> EvaluateInternalAsync(
        DbContext context,
        Guid documentId,
        CancellationToken ct)
    {
        var doc = await context.Set<Document>()
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(ct => ct!.Rules)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (doc is null) return ComplianceStatus.Pending;

        if (doc.ExpirationDate is DateTime exp && exp.Date < DateTime.UtcNow.Date)
        {
            doc.ComplianceStatus = ComplianceStatus.Expired;
            doc.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
            return doc.ComplianceStatus;
        }

        if (doc.ExpirationDate is DateTime exp2
            && exp2.Date <= DateTime.UtcNow.Date.AddDays(30))
        {
            doc.ComplianceStatus = ComplianceStatus.ExpiringSoon;
        }

        var template = doc.Vendor?.ComplianceTemplate;
        if (template is null || template.Rules.Count == 0)
        {
            if (doc.ComplianceStatus != ComplianceStatus.ExpiringSoon)
                doc.ComplianceStatus = ComplianceStatus.Pending;
            doc.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
            return doc.ComplianceStatus;
        }

        var previous = context.Set<ComplianceCheck>().Where(c => c.DocumentId == doc.Id);
        context.Set<ComplianceCheck>().RemoveRange(previous);

        var applicableRules = template.Rules
            .Where(r => string.IsNullOrEmpty(r.DocumentType) || r.DocumentType == doc.DocumentType)
            .ToList();

        bool allPassed = true;
        foreach (var rule in applicableRules)
        {
            var (passed, actualValue, note) = EvaluateRule(doc, rule);
            context.Set<ComplianceCheck>().Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                ComplianceRuleId = rule.Id,
                IsPassed = passed,
                ActualValue = actualValue,
                Notes = note,
                CheckedAt = DateTime.UtcNow
            });
            if (!passed) allPassed = false;
        }

        doc.ComplianceStatus = allPassed
            ? (doc.ComplianceStatus == ComplianceStatus.ExpiringSoon
                ? ComplianceStatus.ExpiringSoon
                : ComplianceStatus.Compliant)
            : ComplianceStatus.NonCompliant;
        doc.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        return doc.ComplianceStatus;
    }

    // internal (not private) so the pure rule-evaluation logic can be unit-tested directly
    // without a database — see InternalsVisibleTo in CompliDrop.Api.csproj.
    internal static (bool passed, string? actualValue, string? note) EvaluateRule(Document doc, ComplianceRule rule)
    {
        string? actual = LookupValue(doc, rule.FieldName);
        var op = rule.Operator?.ToLowerInvariant() ?? "required";

        switch (op)
        {
            case "required":
                return (!string.IsNullOrWhiteSpace(actual), actual, actual is null ? "Field missing." : null);

            case "equals":
                return (string.Equals(actual?.Trim(), rule.ExpectedValue?.Trim(), StringComparison.OrdinalIgnoreCase),
                    actual,
                    actual is null ? "Field missing." : null);

            case "contains":
                var hasValue = actual is not null && rule.ExpectedValue is not null
                    && actual.Contains(rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
                return (hasValue, actual, hasValue ? null : $"Expected to contain '{rule.ExpectedValue}'.");

            case "min_value":
                if (!decimal.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
                    || !decimal.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var min))
                    return (false, actual, "Unable to parse numeric comparison.");
                return (a >= min, actual, a >= min ? null : $"Value {a} below required minimum {min}.");

            default:
                return (false, actual, $"Unknown operator '{rule.Operator}'.");
        }
    }

    internal static string? LookupValue(Document doc, string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return null;
        if (string.Equals(fieldName, "expiration_date", StringComparison.OrdinalIgnoreCase) && doc.ExpirationDate is { } ed)
            return ed.ToString("yyyy-MM-dd");
        if (string.Equals(fieldName, "effective_date", StringComparison.OrdinalIgnoreCase) && doc.EffectiveDate is { } efd)
            return efd.ToString("yyyy-MM-dd");
        if (string.Equals(fieldName, "general_liability_limit", StringComparison.OrdinalIgnoreCase) && doc.GeneralLiabilityLimit is { } gll)
            return gll.ToString(CultureInfo.InvariantCulture);

        if (doc.ExtractionFields?.RootElement.ValueKind == JsonValueKind.Object
            && doc.ExtractionFields.RootElement.TryGetProperty(fieldName, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => value.GetRawText()
            };
        }
        return null;
    }
}
