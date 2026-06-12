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
    SystemDbContext sysDb,
    TimeProvider timeProvider) : IComplianceCheckService
{
    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(db, documentId, timeProvider.GetUtcNow().UtcDateTime, ct);

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(sysDb, documentId, timeProvider.GetUtcNow().UtcDateTime, ct);

    // nowUtc is injected (via TimeProvider) instead of read from DateTime.UtcNow so the
    // expiration / expiring-soon date boundaries are deterministically testable.
    private static async Task<ComplianceStatus> EvaluateInternalAsync(
        DbContext context,
        Guid documentId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var today = nowUtc.Date;

        var doc = await context.Set<Document>()
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(ct => ct!.Rules)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (doc is null) return ComplianceStatus.Pending;

        if (doc.ExpirationDate is DateTime exp && exp.Date < today)
        {
            doc.ComplianceStatus = ComplianceStatus.Expired;
            doc.UpdatedAt = nowUtc;
            await context.SaveChangesAsync(ct);
            return doc.ComplianceStatus;
        }

        if (doc.ExpirationDate is DateTime exp2
            && exp2.Date <= today.AddDays(30))
        {
            doc.ComplianceStatus = ComplianceStatus.ExpiringSoon;
        }

        var template = doc.Vendor?.ComplianceTemplate;

        // Defense-in-depth for the tenant boundary (#273): the system path runs on
        // SystemDbContext (no tenant filter), so a Vendor row whose ComplianceTemplateId was
        // poisoned with another org's template — possible only via data written before the
        // assignment-time guard in VendorEndpoints — would load the FOREIGN template here and
        // write its rule names/expected values into this org's visible ComplianceCheck rows.
        // Treat such a template as absent: the no-governing-rules branch below then clears any
        // previously-leaked check rows, so a poisoned row self-heals on its next evaluation.
        if (template is not null && !template.IsSystemTemplate && template.OrganizationId != doc.OrganizationId)
            template = null;

        if (template is null || template.Rules.Count == 0)
        {
            // "No governing rules" must also mean "no check rows" — without this, a doc
            // whose template was unassigned/emptied keeps stale checks from the old rules
            // while showing Pending (#269 review). Materialized async: handing RemoveRange
            // an IQueryable executes the query on the blocking sync path.
            var stale = await context.Set<ComplianceCheck>()
                .Where(c => c.DocumentId == doc.Id)
                .ToListAsync(ct);
            context.Set<ComplianceCheck>().RemoveRange(stale);

            if (doc.ComplianceStatus != ComplianceStatus.ExpiringSoon)
                doc.ComplianceStatus = ComplianceStatus.Pending;
            doc.UpdatedAt = nowUtc;
            await context.SaveChangesAsync(ct);
            return doc.ComplianceStatus;
        }

        var previous = await context.Set<ComplianceCheck>()
            .Where(c => c.DocumentId == doc.Id)
            .ToListAsync(ct);
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
                // Both columns are varchar(500) and Npgsql does NOT truncate — an oversize
                // value (a long description_of_operations as the actual, or a note embedding
                // a near-500-char ExpectedValue) threw 22001 at evaluation time: request-path
                // evaluations 500ed and the worker-path swallow left checks silently
                // un-updated (#272 review).
                ActualValue = ClampToColumn(actualValue),
                Notes = ClampToColumn(note),
                CheckedAt = nowUtc
            });
            if (!passed) allPassed = false;
        }

        doc.ComplianceStatus = allPassed
            ? (doc.ComplianceStatus == ComplianceStatus.ExpiringSoon
                ? ComplianceStatus.ExpiringSoon
                : ComplianceStatus.Compliant)
            : ComplianceStatus.NonCompliant;
        doc.UpdatedAt = nowUtc;
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
                // ACORD checkbox door (#272): when `additional_insured` arrives as a bare
                // affirmative flag ("Y", "X", "true" — the per-coverage ADDL INSD column
                // reading, common in pre-v2-prompt extractions), the certificate SAYS the
                // provision exists but names no party, so a contains-venue-name check would
                // flag honest certificates. Look for the expected name where certificates
                // customarily put it instead: the certificate-holder box and the
                // description-of-operations text. A missing or negative flag never falls
                // back — the holder box almost always names the venue, so falling back on
                // absence would pass certificates with no additional-insured provision at
                // all (the #257 vacuous-Compliant class).
                if (string.Equals(rule.FieldName, "additional_insured", StringComparison.OrdinalIgnoreCase)
                    && IsAffirmativeFlag(actual))
                {
                    var holder = LookupValue(doc, "certificate_holder");
                    var operations = LookupValue(doc, "description_of_operations");
                    var fallbackHit = rule.ExpectedValue is not null
                        && (holder?.Contains(rule.ExpectedValue, StringComparison.OrdinalIgnoreCase) == true
                            || operations?.Contains(rule.ExpectedValue, StringComparison.OrdinalIgnoreCase) == true);
                    return (fallbackHit, actual, fallbackHit
                        ? "The additional-insured box is checked; matched the name in the certificate holder / description of operations."
                        : $"The additional-insured box is checked, but '{rule.ExpectedValue}' was not found in the certificate holder or description of operations.");
                }
                var hasValue = actual is not null && rule.ExpectedValue is not null
                    && actual.Contains(rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
                return (hasValue, actual, hasValue ? null : $"Expected to contain '{rule.ExpectedValue}'.");

            case "min_value":
                // Distinguish "the document doesn't show this coverage" from "we couldn't
                // read the number" — the missing case previously surfaced as the jargon
                // note "Unable to parse numeric comparison" (#272).
                if (string.IsNullOrWhiteSpace(actual))
                    return (false, actual, "Field missing.");
                if (!decimal.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
                    || !decimal.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var min))
                    return (false, actual, "Unable to parse numeric comparison.");
                return (a >= min, actual, a >= min ? null : $"Value {a} below required minimum {min}.");

            default:
                return (false, actual, $"Unknown operator '{rule.Operator}'.");
        }
    }

    // Matches ComplianceCheck.ActualValue / .Notes HasMaxLength(500) in ModelConfiguration.
    private const int CheckColumnMaxLength = 500;

    internal static string? ClampToColumn(string? value)
    {
        if (value is not { Length: > CheckColumnMaxLength }) return value;
        // Back off one code unit when the cut would split a surrogate pair (an emoji in
        // vendor-typed text straddling index 499/500): a lone high surrogate is an invalid
        // string that Npgsql's strict UTF-8 encoder rejects at SaveChangesAsync — the very
        // write-path failure this clamp exists to remove.
        var cut = char.IsHighSurrogate(value[CheckColumnMaxLength - 1])
            ? CheckColumnMaxLength - 1
            : CheckColumnMaxLength;
        return value[..cut];
    }

    // The checkbox readings a model may emit for `additional_insured` when the certificate
    // marks the provision without naming a party (ACORD 25's per-coverage Y/N column, a
    // bare ✓, or a literal boolean serialized to text). Deliberately NOT including "yes
    // ..." prefixes of longer strings — only an exact (trimmed) flag triggers the
    // certificate-holder fallback; any actual party-name text takes the normal contains path.
    private static readonly string[] AffirmativeFlags = ["y", "yes", "true", "x", "✓", "checked"];

    internal static bool IsAffirmativeFlag(string? value) =>
        value is not null && AffirmativeFlags.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

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
