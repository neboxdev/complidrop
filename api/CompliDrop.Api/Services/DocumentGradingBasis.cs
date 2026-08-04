using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Services;

/// <summary>
/// Answers ONE question for a writer that has been holding a tracked <see cref="Document"/> across a
/// long-running side effect: <b>what will the row actually hold once my <c>SaveChanges</c> lands?</b>
/// The returned instance is DETACHED — a grading basis, never something to save.
/// <para/>
/// It exists for <c>ExtractionWorker.PersistSuccess</c> (<see
/// href="https://github.com/neboxdev/complidrop/issues/460">#460</see>, ADR 0030 Amendment 2).
/// <c>ProcessDocumentAsync</c> loads the document BEFORE OCR + the LLM call and holds that snapshot for
/// the minutes the read takes, then grades from it. EF Core emits only the properties the writer
/// MODIFIED, so every canonical verdict input the worker leaves unmodified keeps whatever a request
/// committed during the window — beside a verdict computed from the pre-run value. That is not one
/// column but a mechanism, which is why this helper is derived from the CHANGE TRACKER rather than from
/// a list of columns: <c>VendorId</c> (never assigned), <c>DocumentType</c> (assigned, but a blank/absent
/// model answer falls back to the stored type and a canonical answer can equal it), and any typed column
/// whose field the model omitted are all instances of the same rule, and so is the next one.
/// <para/>
/// The composition IS the prediction, exactly and by construction:
/// <list type="bullet">
/// <item>a property this writer MODIFIED will be in the UPDATE, so the basis takes the writer's value;</item>
/// <item>a property it did not will be absent from the UPDATE, so the basis takes the row's CURRENT
/// committed value — including a value a request wrote after this writer's snapshot.</item>
/// </list>
/// Nothing here is assigned back onto the tracked entity, and that restraint is the point. Re-reading an
/// input and ASSIGNING it would mark the property modified, so the worker would start WRITING a column it
/// does not write today and silently clobber a request landing between the re-read and the commit — a
/// LOST UPDATE traded for the stale-basis one (ADR 0030 Amendment 1, and reviewers.md names it as a
/// finding if it ever appears in a diff). Contrast <c>ExtractionWorker.SetTrust</c>, which DOES force its
/// column: that value is the worker's OWN conclusion, which ADR 0052 §2 says it owns, not a verdict input
/// a request owns.
/// <para/>
/// SECOND CONSUMER, same rule (<see href="https://github.com/neboxdev/complidrop/issues/467">#467</see>,
/// ADR 0052 Amendment 1): <c>PersistSuccess</c> also asks <c>DocumentFieldReadability</c> of this basis,
/// because "does this document carry a canonical value nothing can parse?" is a question about a ROW and
/// the pre-run snapshot is not the row. So it inherits every property above — the read is read-only with
/// respect to the tracked entity, it must stay inside the caller's degrade guard, and the answer it feeds
/// (<c>ExtractionTrust</c>) is still FORCED into the UPDATE because the CONCLUSION is the worker's even
/// though the SUBJECT is the row. The basis is therefore not "the grading basis" narrowly but the row
/// this commit will leave, which every conclusion this writer draws about the document must be about.
/// <para/>
/// RESIDUAL, deliberately not closed here: this narrows the stale window from the whole extraction run to
/// the gap between this read and the caller's commit. It does not remove it. Closing that would need the
/// commit itself to DETECT a conflicting write, and every detecting shape available (an entity-level
/// <c>xmin</c> token, <c>REPEATABLE READ</c>) throws out of a <c>SaveChanges</c> whose failure costs a
/// re-paid Document AI + LLM run — refuted in ADR 0030 Option A and Amendment 1.
/// </summary>
internal static class DocumentGradingBasis
{
    /// <summary>
    /// Reads <paramref name="tracked"/>'s CURRENT committed row and overlays the properties
    /// <paramref name="context"/> will actually write for it, yielding a detached <see cref="Document"/>
    /// equal to the row this writer's pending commit will leave behind — for every property the WRITER
    /// itself sets.
    /// <para/>
    /// The claim is bounded rather than absolute, in two ways, and neither reaches a verdict input or a
    /// canonical field value:
    /// <list type="bullet">
    /// <item><c>AuditSaveChangesInterceptor</c> re-stamps <c>UpdatedAt</c> from <c>SavingChanges</c>, i.e.
    /// strictly AFTER this read has returned, so <c>basis.UpdatedAt</c> holds the caller's value and the
    /// row commits the interceptor's later one. Deliberately not chased: ADR 0030 Amendment 2's "the basis
    /// is READ-ONLY with respect to <c>doc</c>" means this helper predicts what the writer writes, it does
    /// not model the pipeline.</item>
    /// <item>Anything the caller assigns AFTER calling this — which for <c>PersistSuccess</c> is its own
    /// two conclusions, <c>ExtractionStatus</c>/<c>ExtractionTrust</c> (they are DECIDED from this basis,
    /// so they cannot precede it), the forced <c>ComplianceStatus</c>, and since #464 the withdrawn
    /// <c>IsManuallyVerified</c> (which is NOT decided from the basis — it is a fact about the EVENT, that
    /// a new reading replaced the values, so it merely sits with its two neighbours). The basis holds the
    /// row's values for all four. Immaterial by inspection: <c>ComplianceCheckService</c> reads none of
    /// them, and <c>DocumentFieldReadability</c> reads only the canonical fields.</item>
    /// </list>
    /// <para/>
    /// Returns <c>null</c> only when the row is GENUINELY GONE — a hard delete. A SOFT delete does NOT
    /// produce a null basis: <c>EntityEntry.GetDatabaseValuesAsync</c>
    /// issues an <c>AsNoTracking().IgnoreQueryFilters()</c> key lookup, so the row a mid-run
    /// <c>DELETE /api/documents/{id}</c> soft-deletes is still read and still graded as the row this commit
    /// will leave. Since every API delete path goes through the audit interceptor's soft delete, nothing in
    /// production is expected to reach the null case — it is DEFENSIVE, and callers fall back to grading the
    /// tracked entity (the pre-#460 behaviour) because a document that is going away is not a document to
    /// invent a basis for. Pinned directly, both branches, by
    /// <c>ExtractionWorkerStaleBasisTests.The_grading_basis_*</c>; the mid-run-delete INTEGRATION test pins
    /// something else (that this read never becomes a new way for the persist to throw).
    /// <para/>
    /// One extra key-lookup round trip per call. It sits on a path that has just paid for OCR + an LLM
    /// call, so it is not a cost worth optimising, and the caller MUST keep it inside whatever guard makes
    /// a grading failure degrade to <see cref="ComplianceStatus.Pending"/> — this read must never become a
    /// new way for the persist to throw.
    /// </summary>
    public static async Task<Document?> AfterPendingCommitAsync(
        DbContext context, Document tracked, CancellationToken ct)
    {
        // Entry() runs DetectChanges, so IsModified below reflects every assignment the caller has made —
        // including the ones that assigned a property its snapshot value and are therefore NOT modified.
        // That distinction is the whole mechanism; reading the flags before the caller finishes mutating
        // would silently under-report what the UPDATE carries.
        var entry = context.Entry(tracked);
        var row = await entry.GetDatabaseValuesAsync(ct);
        if (row is null) return null;

        foreach (var property in entry.Properties)
            if (property.IsModified)
                row[property.Metadata] = property.CurrentValue;

        // ToObject() materializes a DETACHED clone — no identity-map entry, nothing for SaveChanges to
        // pick up. Navigations are not copied; the grading path loads the vendor chain from this
        // instance's own (possibly freshly-read) VendorId.
        return (Document)row.ToObject();
    }
}
