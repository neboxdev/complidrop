using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Services;

public interface IComplianceCheckService
{
    Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct);
    Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document whose vendor is assigned the given template. The fan-out that
    /// keeps verdicts fresh after a rule/template MUTATION — pure DB work, no LLM cost (#257).
    /// Batched a page at a time so a template shared by a large vendor base no longer turns a single
    /// rule edit into hundreds of serial round-trips on the request thread (#293).
    /// </summary>
    Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document belonging to the given vendor. The fan-out for a checklist
    /// (re)assignment — so portal-first onboarding (upload, then assign a checklist) no longer
    /// leaves documents stuck at "Awaiting review" forever (#257).
    /// </summary>
    Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document belonging to ANY of the given vendors, in one batched pass. The
    /// template-delete path clears the assignment across the whole vendor base and must then re-grade
    /// all of their documents; looping the single-vendor fan-out re-introduced the per-document
    /// round-trip multiplication this batching exists to remove (#293).
    /// </summary>
    Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct);
}

public class ComplianceCheckService(
    AppDbContext db,
    SystemDbContext sysDb,
    TimeProvider timeProvider,
    ILogger<ComplianceCheckService> logger,
    // Documents are re-graded a page at a time so the template fan-out does O(documents / PageSize)
    // round-trips instead of one per document, and the change tracker stays bounded no matter how
    // large the vendor base on a shared template (#293). Injectable so a test can force multi-page
    // paging without seeding hundreds of rows; the DI container uses the default (an unresolved int
    // parameter with a default value falls back to that default).
    int reevaluationPageSize = ComplianceCheckService.DefaultReevaluationPageSize) : IComplianceCheckService
{
    public const int DefaultReevaluationPageSize = 200;

    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(db, documentId, timeProvider.GetUtcNow().UtcDateTime, ct);

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(sysDb, documentId, timeProvider.GetUtcNow().UtcDateTime, ct);

    public Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct) =>
        // Tenant-filtered db: only the caller org's documents are touched. The vendor → template
        // link is the join; a doc with no vendor (or a vendor on another template) is excluded.
        ReevaluateWhereAsync(d => d.Vendor != null && d.Vendor.ComplianceTemplateId == templateId, ct);

    public Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct) =>
        ReevaluateWhereAsync(d => d.VendorId == vendorId, ct);

    public Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct)
    {
        if (vendorIds.Count == 0) return Task.CompletedTask;
        // Array so Npgsql translates the membership test to `= ANY(@ids)` — one parameter — instead
        // of an IN-list that grows a parameter per vendor.
        var ids = vendorIds.ToArray();
        return ReevaluateWhereAsync(d => d.VendorId != null && ids.Contains(d.VendorId.Value), ct);
    }

    // Best-effort fan-out, batched per page (#293). The triggering mutation (rule edit / checklist
    // assignment / template delete) has already committed before this runs, so a page that fails to
    // persist is logged and skipped — those documents keep their prior verdict until the nightly
    // sweep or a manual "Check again", never a 500 that, on the rule-create path, would duplicate the
    // rule on retry. Cancellation still propagates (a shutdown isn't a per-page failure).
    private async Task ReevaluateWhereAsync(Expression<Func<Document, bool>> predicate, CancellationToken ct)
    {
        // Snapshot the affected ids first — a cheap key-only projection (no ExtractionFields, no
        // joins) — then re-grade them a page at a time.
        var docIds = await db.Documents.Where(predicate).Select(d => d.Id).ToListAsync(ct);
        if (docIds.Count == 0) return;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var page in docIds.Chunk(reevaluationPageSize))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var docs = await db.Documents
                    .Where(d => page.Contains(d.Id))
                    .Include(d => d.Vendor)
                        .ThenInclude(v => v!.ComplianceTemplate)
                            .ThenInclude(t => t!.Rules)
                    // Split query so a document's ExtractionFields JSON isn't re-transmitted once per
                    // rule (a single join multiplies each doc row — and its JSON payload — by the
                    // rule count). OrderBy gives the split its stable key.
                    .OrderBy(d => d.Id)
                    .AsSplitQuery()
                    .ToListAsync(ct);

                await ApplyEvaluationsAsync(docs, nowUtc, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Re-evaluation fan-out failed for a page of {Count} documents", page.Length);
            }
            finally
            {
                // Bound the change tracker to a single page across the whole fan-out, and drop any
                // half-applied changes from a failed page so they can't ride along on the next page's
                // SaveChanges. The triggering mutation already committed on this same context before
                // the fan-out began, so clearing here cannot lose it.
                db.ChangeTracker.Clear();
            }
        }
    }

    // Applies one page of evaluations as a single round-trip group: one bulk load of the page's
    // existing checks, then one SaveChanges carrying every delete + insert + status update — so the
    // page commits atomically. RemoveRange (not ExecuteDelete) keeps the delete on the SAME
    // SaveChanges/transaction as the inserts AND on the audit-interceptor path; because
    // ComplianceCheck has no DeletedAt the interceptor leaves it a hard delete with no audit row,
    // exactly as the prior per-document RemoveRange did.
    private async Task ApplyEvaluationsAsync(IReadOnlyList<Document> docs, DateTime nowUtc, CancellationToken ct)
    {
        if (docs.Count == 0) return;

        var outcomes = new List<(Document Doc, EvaluationOutcome Outcome)>(docs.Count);
        foreach (var doc in docs)
            outcomes.Add((doc, ComputeOutcome(doc, nowUtc)));

        // The id set is drawn from the tenant-filtered Documents query above, so this delete over the
        // (filter-less) ComplianceChecks set cannot reach another org's rows.
        var clearIds = outcomes.Where(o => o.Outcome.ClearExistingChecks).Select(o => o.Doc.Id).ToArray();
        if (clearIds.Length > 0)
        {
            var existing = await db.ComplianceChecks
                .Where(c => clearIds.Contains(c.DocumentId))
                .ToListAsync(ct);
            db.ComplianceChecks.RemoveRange(existing);
        }

        foreach (var (doc, outcome) in outcomes)
        {
            if (outcome.NewChecks.Count > 0)
                db.ComplianceChecks.AddRange(outcome.NewChecks);
            doc.ComplianceStatus = outcome.Status;
            doc.UpdatedAt = nowUtc;
        }

        await db.SaveChangesAsync(ct);
    }

    // nowUtc is injected (via TimeProvider) instead of read from DateTime.UtcNow so the
    // expiration / expiring-soon date boundaries are deterministically testable.
    private static async Task<ComplianceStatus> EvaluateInternalAsync(
        DbContext context,
        Guid documentId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var doc = await context.Set<Document>()
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(t => t!.Rules)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (doc is null) return ComplianceStatus.Pending;

        var outcome = ComputeOutcome(doc, nowUtc);

        if (outcome.ClearExistingChecks)
        {
            // Materialized (ToListAsync) before RemoveRange — handing RemoveRange an IQueryable would
            // execute the delete-driving query on the blocking sync path.
            var existing = await context.Set<ComplianceCheck>()
                .Where(c => c.DocumentId == doc.Id)
                .ToListAsync(ct);
            context.Set<ComplianceCheck>().RemoveRange(existing);
        }
        if (outcome.NewChecks.Count > 0)
            context.Set<ComplianceCheck>().AddRange(outcome.NewChecks);

        doc.ComplianceStatus = outcome.Status;
        doc.UpdatedAt = nowUtc;
        await context.SaveChangesAsync(ct);
        return outcome.Status;
    }

    internal readonly record struct EvaluationOutcome(
        ComplianceStatus Status,
        IReadOnlyList<ComplianceCheck> NewChecks,
        bool ClearExistingChecks);

    // Pure verdict computation for ONE already-loaded document (Vendor → ComplianceTemplate → Rules
    // must be Include-loaded). Extracted so the single-document path (EvaluateInternalAsync) and the
    // batched fan-out (ReevaluateWhereAsync) share one source of truth and cannot drift. No DB I/O:
    // returns the status to store, the check rows to insert, and whether the document's existing
    // check rows should be cleared first.
    internal static EvaluationOutcome ComputeOutcome(Document doc, DateTime nowUtc)
    {
        var today = nowUtc.Date;

        // Expired wins outright — and, preserving prior behavior, does NOT touch existing check rows
        // on this path (only the date crossed; the rule verdicts that produced those checks stand).
        if (doc.ExpirationDate is DateTime exp && exp.Date < today)
            return new EvaluationOutcome(ComplianceStatus.Expired, [], ClearExistingChecks: false);

        var expiringSoon = doc.ExpirationDate is DateTime exp2 && exp2.Date <= today.AddDays(30);

        var template = doc.Vendor?.ComplianceTemplate;

        // Defense-in-depth for the tenant boundary (#273): the system path runs on SystemDbContext
        // (no tenant filter), so a Vendor row whose ComplianceTemplateId was poisoned with another
        // org's template — possible only via data written before the assignment-time guard in
        // VendorEndpoints — would load the FOREIGN template here and write its rule names/expected
        // values into this org's visible ComplianceCheck rows. Treat such a template as absent: the
        // no-governing-rules branch below then clears any previously-leaked check rows, so a poisoned
        // row self-heals on its next evaluation.
        if (template is not null && !template.IsSystemTemplate && template.OrganizationId != doc.OrganizationId)
            template = null;

        if (template is null || template.Rules.Count == 0)
            // "No governing rules" must also mean "no check rows" — without clearing, a doc whose
            // template was unassigned/emptied keeps stale checks from the old rules while showing
            // Pending (#269 review). Preserve a date-driven ExpiringSoon.
            return new EvaluationOutcome(
                expiringSoon ? ComplianceStatus.ExpiringSoon : ComplianceStatus.Pending,
                [],
                ClearExistingChecks: true);

        var applicableRules = template.Rules
            .Where(r => string.IsNullOrEmpty(r.DocumentType) || r.DocumentType == doc.DocumentType)
            .ToList();

        if (applicableRules.Count == 0)
            // The template has rules, but NONE govern this document's type — e.g. a menu PDF uploaded
            // as "Other" against a COI-only checklist. Zero applicable rules must read Pending — never
            // the vacuous Compliant an all-passed-over-zero-rules loop produced pre-#257 — and certify
            // nothing. Preserve a date-driven ExpiringSoon.
            return new EvaluationOutcome(
                expiringSoon ? ComplianceStatus.ExpiringSoon : ComplianceStatus.Pending,
                [],
                ClearExistingChecks: true);

        var newChecks = new List<ComplianceCheck>(applicableRules.Count);
        var allPassed = true;
        foreach (var rule in applicableRules)
        {
            var (passed, actualValue, note) = EvaluateRule(doc, rule);
            newChecks.Add(new ComplianceCheck
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                ComplianceRuleId = rule.Id,
                IsPassed = passed,
                // Both columns are varchar(500) and Npgsql does NOT truncate — an oversize value
                // (a long description_of_operations as the actual, or a note embedding a near-500-char
                // ExpectedValue) threw 22001 at evaluation time: request-path evaluations 500ed and
                // the worker-path swallow left checks silently un-updated (#272 review).
                ActualValue = ClampToColumn(actualValue),
                Notes = ClampToColumn(note),
                CheckedAt = nowUtc
            });
            if (!passed) allPassed = false;
        }

        var status = allPassed
            ? (expiringSoon ? ComplianceStatus.ExpiringSoon : ComplianceStatus.Compliant)
            : ComplianceStatus.NonCompliant;
        return new EvaluationOutcome(status, newChecks, ClearExistingChecks: true);
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
