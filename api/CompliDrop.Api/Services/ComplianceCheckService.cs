using System.Collections.ObjectModel;
using System.Linq.Expressions;
using CompliDrop.Api.Configuration;
using CompliDrop.Api.Data;
using CompliDrop.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompliDrop.Api.Services;

public interface IComplianceCheckService
{
    /// <summary>
    /// The single-document PURE re-grade on the tenant context: load, compute the verdict the document's
    /// CURRENT canonical inputs imply, save. It changes no inputs — only <see cref="Document.ComplianceStatus"/>
    /// and the <see cref="ComplianceCheck"/> rows.
    /// <para/>
    /// ITS CONCURRENCY GUARD LIVES AT THE CALL SITE, and it has exactly one production call site:
    /// <c>ComplianceEndpoints.RunCheck</c>, which runs it inside
    /// <c>Endpoints/DocumentWriteConcurrency.RunAsync</c> (<c>REPEATABLE READ</c> + bounded re-run,
    /// #461 / ADR 0030 Amendment 3). Bare, this method is a read → compute → write with no lock and no
    /// token, so a field edit committing inside its window leaves the row holding the EDITED inputs
    /// beside THIS verdict — a stored <c>Compliant</c> over a limit somebody just lowered, with passing
    /// check rows citing a value the row no longer holds, and nothing to heal it.
    /// <para/>
    /// So a SECOND caller is not a free addition: it must either take the same guard or be a place where
    /// the window provably cannot matter. Pinned by <c>Adr0030EnforcementTests</c>, which fails when the
    /// production call count moves or when <c>RunCheck</c> stops routing through the guard — no
    /// behavioural test can see either.
    /// </summary>
    Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// The <see cref="SystemDbContext"/> twin of <see cref="EvaluateAsync"/>. Caller-less in production
    /// since #337 folded the worker's grading into <c>PersistSuccess</c> (ADR 0030 § Neutral); retained as
    /// the symmetric system-context entry point and exercised by the sample-grading tests. It carries the
    /// same unguarded read → compute → write window, so a future production caller owes the same
    /// question <see cref="EvaluateAsync"/>'s remarks pose — and note the tenant-context guard does not
    /// transfer as-is: <c>DocumentWriteConcurrency</c> takes an <see cref="AppDbContext"/>.
    /// </summary>
    Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Evaluates the verdict for an ALREADY-TRACKED document and applies it (<see cref="Document.ComplianceStatus"/>
    /// + the <see cref="ComplianceCheck"/> rows) to the SAME <paramref name="context"/> WITHOUT saving — so the
    /// caller commits the canonical inputs and the verdict they imply in ONE transaction. This is the fix for the
    /// torn <c>(inputs, verdict)</c> state (#337 / ADR 0030): a verdict written in a transaction SEPARATE from its
    /// inputs can be left contradicting them under a manual-edit-vs-(re)extraction race. The caller owns the unit
    /// of work and MUST <c>SaveChanges</c>. Loads Vendor → ComplianceTemplate → Rules against the document's
    /// current (possibly just-edited) <see cref="Document.VendorId"/>.
    /// </summary>
    Task ApplyEvaluationAsync(DbContext context, Document doc, CancellationToken ct);

    /// <summary>
    /// As <see cref="ApplyEvaluationAsync(DbContext, Document, CancellationToken)"/>, but grades
    /// <paramref name="gradingBasis"/> instead of <paramref name="doc"/> while still applying the verdict
    /// (and the <see cref="ComplianceCheck"/> rows) onto <paramref name="doc"/> and its
    /// <paramref name="context"/>. Nothing is copied back onto <paramref name="doc"/>, so the caller keeps
    /// writing exactly the columns it wrote before — that restraint is the decision, not an implementation
    /// detail (ADR 0030 Amendment 2).
    /// <para/>
    /// TWO PRECONDITIONS, both ENFORCED (an <see cref="ArgumentException"/>, not a comment) because a
    /// caller that broke either would corrupt data rather than fail:
    /// <list type="bullet">
    /// <item><paramref name="gradingBasis"/> MUST carry the same <see cref="Document.Id"/> as
    /// <paramref name="doc"/>. The new <see cref="ComplianceCheck"/> rows are stamped from the BASIS while
    /// the clear-existing predicate keys on the TRACKED document, so a mismatched pair would insert check
    /// rows against one document while deleting another's.</item>
    /// <item><paramref name="gradingBasis"/> MUST be DETACHED (see
    /// <c>Services/DocumentGradingBasis.AfterPendingCommitAsync</c>, which materializes exactly that). This
    /// method ASSIGNS the basis's <see cref="Document.Vendor"/> navigation from an <c>AsNoTracking</c>
    /// query; hanging an untracked graph off a TRACKED principal is the shape EF turns into spurious
    /// inserts at the next <c>DetectChanges</c>.</item>
    /// </list>
    /// <para/>
    /// For the ONE caller whose grading inputs may have moved under it: <c>ExtractionWorker.PersistSuccess</c>
    /// grades a snapshot taken before an OCR + LLM run that lasts minutes, and EF writes back only what it
    /// MODIFIED — so a verdict input it left unmodified is a request's committed value beside a verdict
    /// computed from the pre-run one (<see href="https://github.com/neboxdev/complidrop/issues/460">#460</see>,
    /// ADR 0030 Amendment 2). It hands in <c>DocumentGradingBasis.AfterPendingCommitAsync</c>'s prediction of
    /// the row its own commit will leave.
    /// <para/>
    /// The REQUEST-path callers deliberately do NOT use this overload, and switching them to it would be a
    /// bug rather than consistency: <c>UpdateDocument</c> grades against a <c>VendorId</c> it has assigned in
    /// memory and not yet committed, so a freshly-read basis would grade the OLD vendor's checklist; and both
    /// partial writers already detect a conflicting concurrent commit through <c>DocumentWriteConcurrency</c>'s
    /// <c>REPEATABLE READ</c> + <c>40001</c> re-run (ADR 0030 Amendment 1), which re-runs the whole callback
    /// against a fresh read rather than patching one basis.
    /// </summary>
    Task ApplyEvaluationAsync(DbContext context, Document doc, Document gradingBasis, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document whose vendor is assigned the given template. The fan-out that
    /// keeps verdicts fresh after a rule/template MUTATION — pure DB work, no LLM cost (#257).
    /// Batched a page at a time so a template shared by a large vendor base no longer turns a single
    /// rule edit into hundreds of serial round-trips on the request thread (#293).
    /// </summary>
    Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document whose vendor is assigned the given template, UNION the given
    /// document ids — the rule-DELETE fan-out (#364). Template membership alone is not a superset of
    /// the population the pre-#364 per-document loop re-graded: a document can hold a check row
    /// against the deleted rule while sitting OUTSIDE that membership, most reachably when its vendor
    /// was soft-deleted and the delete-time re-grade did not land (<see
    /// cref="VendorEndpoints.DeleteVendor"/> fans out post-commit since #422, but that pass is
    /// best-effort — a truncated or failed run leaves the documents behind, and the Vendor
    /// soft-delete query filter then makes <c>d.Vendor</c> read null, hiding them from the
    /// template predicate). Such a document keeps a NON-Expired verdict — a Compliant one graded
    /// against rules that no longer govern it — and the old loop healed it to Pending as a side
    /// effect of iterating the deleted rule's check rows. Passing those ids here keeps the batched
    /// fan-out a strict superset of the behaviour it replaced, so the performance fix cannot silently
    /// drop a verdict correction. Ids outside the caller's tenant (possible only via the #273
    /// cross-org assignment state) are filtered out by the AppDbContext tenant filter, exactly as
    /// <c>EvaluateAsync</c>'s per-id lookup did.
    /// </summary>
    Task ReevaluateForTemplateOrDocumentsAsync(Guid templateId, IReadOnlyList<Guid> documentIds, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document belonging to the given vendor. The fan-out for a checklist
    /// (re)assignment — so portal-first onboarding (upload, then assign a checklist) no longer
    /// leaves documents stuck at "Awaiting review" forever (#257) — and for a vendor DELETE (#422),
    /// where the soft-deleted vendor's documents must drop their now-ungoverned verdict to Pending
    /// and shed their check rows instead of keeping a vacuous Compliant. Works on a soft-deleted
    /// vendor because the membership predicate keys on the <c>VendorId</c> FK, not the filtered
    /// <c>Vendor</c> nav — see <see cref="ReevaluateForVendorsAsync"/>.
    /// </summary>
    Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document belonging to ANY of the given vendors, in one batched pass. The
    /// template-delete path clears the assignment across the whole vendor base and must then re-grade
    /// all of their documents; looping the single-vendor fan-out re-introduced the per-document
    /// round-trip multiplication this batching exists to remove (#293).
    /// </summary>
    Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct);

    /// <summary>
    /// Re-evaluates every document whose vendor is assigned the given SYSTEM template — ACROSS ALL
    /// ORGS, against <see cref="SystemDbContext"/> (no tenant filter). The seed-time counterpart to
    /// the tenant-filtered <see cref="ReevaluateForTemplateAsync"/>: when the startup reconcile
    /// back-fills a rule onto a SHARED system template, the documents graded against it in every org
    /// must be re-graded, or a document persisted <see cref="ComplianceStatus.Compliant"/> under the
    /// OLD rule set silently stays Compliant despite failing the new rule — a false-Compliant verdict
    /// (#400). Vendors can be assigned a system template directly (the #238 sample vendor is), and
    /// the seed is the only path that mutates system-template rules (endpoint rule edits are blocked
    /// on system templates), so nothing else heals this. EXCLUDES sample-demo documents
    /// (<see cref="Document.IsSample"/>): a pre-#400 sample COI was generated + extracted before
    /// <c>liquor_liability_limit</c> existed, so re-grading it here (this fan-out never re-extracts)
    /// would flip a genuinely-<see cref="ComplianceStatus.Compliant"/> demo artifact to
    /// <see cref="ComplianceStatus.NonCompliant"/> on the next deploy and break the ADR 0028
    /// one-click-demo contract — it is left untouched (Compliant) and self-heals on clear + recreate.
    /// Only THIS seed/system fan-out skips samples; the tenant-filtered re-grades
    /// (<see cref="ReevaluateForTemplateAsync"/> / <see cref="ReevaluateForVendorAsync"/>) still touch
    /// them on a user-initiated Check-again / rule edit / reassignment. Same batched, best-effort
    /// machinery as the endpoint fan-out (ADR 0030: each page commits verdict + checks in ONE unit of
    /// work). Returns a <see cref="RegradeResult"/> (targeted / regraded / failed-page counts, sample docs
    /// excluded) so the seed can tell a FULLY-successful fan-out from one that caught-and-skipped a page —
    /// only the former may advance the template's re-grade watermark (#416, ADR 0036 Amendment 2).
    /// </summary>
    Task<RegradeResult> ReevaluateForTemplateForSystemAsync(Guid templateId, CancellationToken ct);
}

/// <summary>
/// Outcome of a batched re-grade fan-out. <see cref="Targeted"/> is how many documents the predicate
/// selected; <see cref="Regraded"/> how many were actually re-evaluated and committed; <see cref="FailedPages"/>
/// how many pages had their <c>SaveChanges</c> caught-and-skipped (the fan-out is best-effort — a failed page is
/// logged, not thrown, so a shared system-rule mutation that already committed can't be un-done by a re-grade
/// hiccup). <see cref="AllSucceeded"/> is the durability signal the seed keys on: only a fan-out that skipped NO
/// page may advance a system template's <c>RegradedThroughRevision</c>, so an interrupted or partially-failed
/// re-grade re-fires on the next boot until every document catches up (#416, ADR 0036 Amendment 2).
/// </summary>
public readonly record struct RegradeResult(int Targeted, int Regraded, int FailedPages)
{
    public bool AllSucceeded => FailedPages == 0;
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
    int reevaluationPageSize = ComplianceCheckService.DefaultReevaluationPageSize,
    // #396 (CLM-1): the corrected additional-insured claim WORDING flag. Optional + defaulting null so
    // the many unit/integration tests that construct this service directly (some passing pageSize
    // positionally) keep compiling unchanged — DI still injects the registered IOptions in production
    // (a registered service wins over the null default), while a direct `new(...)` without options
    // resolves to false = today's copy. It changes ONLY the affirmative-flag (ACORD checkbox
    // fallback) check NOTE string, never the pass/fail verdict. Behind
    // ComplianceClaims:CorrectedAdditionalInsuredWording, default OFF (ADR 0043).
    IOptions<ComplianceClaimsSettings>? complianceClaims = null) : IComplianceCheckService
{
    public const int DefaultReevaluationPageSize = 200;

    private readonly bool _correctedAdditionalInsuredWording =
        complianceClaims?.Value.CorrectedAdditionalInsuredWording ?? false;

    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(db, documentId, ct);

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        EvaluateInternalAsync(sysDb, documentId, ct);

    public Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct) =>
        // Tenant-filtered db: only the caller org's documents are touched. The vendor → template
        // link is the join; a doc with no vendor (or a vendor on another template) is excluded.
        ReevaluateWhereAsync(db, d => d.Vendor != null && d.Vendor.ComplianceTemplateId == templateId, ct);

    public Task ReevaluateForTemplateOrDocumentsAsync(Guid templateId, IReadOnlyList<Guid> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0) return ReevaluateForTemplateAsync(templateId, ct);
        // Array so Npgsql translates the membership test to `= ANY(@ids)` — one parameter — instead
        // of an IN-list that grows a parameter per document (same reason as ReevaluateForVendorsAsync).
        var ids = documentIds.ToArray();
        return ReevaluateWhereAsync(
            db,
            d => (d.Vendor != null && d.Vendor.ComplianceTemplateId == templateId) || ids.Contains(d.Id),
            ct);
    }

    public Task<RegradeResult> ReevaluateForTemplateForSystemAsync(Guid templateId, CancellationToken ct) =>
        // System context (no tenant filter): re-grade the template's documents across EVERY org.
        // Same vendor→template predicate as the tenant path above, evaluated against SystemDbContext —
        // the seed-time fan-out used after the startup reconcile back-fills a rule onto a shared system
        // template (#400). The Vendor soft-delete filter still applies (SystemDbContext keeps it), so a
        // deleted vendor's documents are excluded, exactly as on the tenant path.
        //
        // ...but EXCLUDE sample-demo documents (!d.IsSample) on THIS seed/system path ONLY. The
        // one-click sample (ADR 0028, #238) attaches its sample vendor DIRECTLY to the system Caterer
        // template, and existing sample COIs were generated + extracted BEFORE liquor_liability_limit
        // existed — so their persisted ExtractionFields carry no such field. This fan-out only re-runs
        // rule EVALUATION (never re-extraction), so including a pre-#400 sample would flip a genuinely-
        // Compliant demo artifact to NonCompliant on the very next deploy — a NEW user-visible
        // regression, for every org holding a sample (incl. the protected "Garden Hall" demo), that the
        // ticket never asked for and no user action caused. A sample is a labelled, plan-limit-excluded
        // demo artifact, not a compliance decision about a real vendor: leaving it untouched is
        // do-no-harm (it was Compliant and stays Compliant), and it self-heals — SampleCertificateGenerator
        // now emits a liquor-liability line, so clear + recreate regenerates a genuinely-Compliant sample.
        // Scoped to the seed fan-out ONLY: a user-initiated Check-again / rule edit / reassignment (the
        // tenant-filtered ReevaluateForTemplateAsync / ReevaluateForVendorAsync[s]) still re-grades a
        // sample exactly as before.
        ReevaluateWhereAsync(sysDb, d => d.Vendor != null && d.Vendor.ComplianceTemplateId == templateId && !d.IsSample, ct);

    public Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct) =>
        // Delegates to the plural so there is a single vendor-membership predicate to maintain.
        ReevaluateForVendorsAsync([vendorId], ct);

    public Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct)
    {
        if (vendorIds.Count == 0) return Task.CompletedTask;
        // Array so Npgsql translates the membership test to `= ANY(@ids)` — one parameter — instead
        // of an IN-list that grows a parameter per vendor.
        var ids = vendorIds.ToArray();
        // The predicate keys on the VendorId FK, NOT the d.Vendor nav: the nav carries the Vendor
        // soft-delete query filter, and the vendor-DELETE fan-out (#422) exists precisely to
        // re-grade a just-soft-deleted vendor's documents — a nav-joined predicate would silently
        // select nothing there. Each selected document then loads Vendor == null (the page query's
        // Include honors the filter) and takes the no-governing-rules branch: Pending, checks shed.
        return ReevaluateWhereAsync(db, d => d.VendorId != null && ids.Contains(d.VendorId.Value), ct);
    }

    // Best-effort fan-out, batched per page (#293). The triggering mutation (rule edit / checklist
    // assignment / template delete / seed convergence) has already committed before this runs, so a page
    // that fails to persist is logged and SKIPPED rather than thrown — never a 500 that, on the rule-create
    // path, would duplicate the rule on retry. Those documents keep their prior verdict until something
    // re-fires the re-grade. Cancellation still propagates (a shutdown isn't a per-page failure).
    //
    // How a skipped page is HEALED depends on the caller — and it is NOT the nightly sweep:
    // ComplianceSweepBackgroundService only does date-transition ExecuteUpdates (Compliant→Expired etc.);
    // it never re-runs rule EVALUATION, so it cannot heal a stale rule-verdict. Instead:
    //   * Tenant-path callers (rule edit / reassignment / Check-again) recover on the next user-initiated
    //     re-grade of the same document.
    //   * The SEED/system caller (ReevaluateForTemplateForSystemAsync) recovers DURABLY: this method reports
    //     FailedPages via the returned RegradeResult, the seed holds that template's re-grade watermark back
    //     when any page failed, and the next boot re-fires the re-grade until every page lands (#416, ADR
    //     0036 Amendment 2). That watermark — not the sweep — is what stops a stale verdict surviving an
    //     interrupted boot.
    //
    // CONCURRENCY — #470 / ADR 0030 Amendment 5. This fan-out keeps READ COMMITTED and it is NOT put under
    // DocumentWriteConcurrency's REPEATABLE READ (Amendment 3 Option K, refuted): a page is up to PageSize
    // documents committed as ONE unit, so a single conflicting edit anywhere in it would abandon and re-run
    // the WHOLE page (up to MaxAttempts) and then skip it — forfeiting hundreds of unrelated re-grades where
    // the window degrades exactly one. These callers also run POST-COMMIT on a background token, with no
    // user to answer 409 to.
    //
    // So the window is closed from the OTHER side: the page commits exactly as before, and VerifyPageAsync
    // then re-reads it and re-grades ONLY the documents whose verdict the page's own commit left
    // contradicting their inputs. Detection is by RE-GRADING, never by comparing a list of columns — a
    // fresh outcome that differs from the one this page applied IS the signal, so it covers every verdict
    // input including one added tomorrow (the same mechanism-not-enumeration rule as
    // Services/DocumentGradingBasis; ADR 0030 Amendment 2 Option E refutes the enumeration). Bounded at
    // MaxVerificationPasses over a SHRINKING set, so a document nobody is editing costs one extra read and
    // zero writes, and a document somebody keeps editing cannot spin the fan-out.
    //
    // Granularity note: a page commits as a unit (one SaveChanges), so one document that fails to persist
    // forfeits the re-grade of its WHOLE page (≤ PageSize), not just itself — coarser than the old
    // per-document loop. Accepted trade-off of batching the writes, and bounded: the one known write-path
    // failure (oversize check text → 22001) is clamped at the source (#272), so a realistic page rarely
    // fails. Parameterized on the DbContext so the SAME batched fan-out serves both the tenant-filtered path
    // (AppDbContext — the global query filter scopes it to the caller org) and the cross-org seed path
    // (SystemDbContext — no tenant filter, #400). Returns a RegradeResult (targeted / actually-regraded /
    // failed-page counts) so the seed can distinguish a fully-successful fan-out from a partial one.
    private async Task<RegradeResult> ReevaluateWhereAsync(DbContext context, Expression<Func<Document, bool>> predicate, CancellationToken ct)
    {
        // Snapshot the affected ids first — a cheap key-only projection (no ExtractionFields, no
        // joins) — then re-grade them a page at a time.
        var docIds = await context.Set<Document>().Where(predicate).Select(d => d.Id).ToListAsync(ct);
        if (docIds.Count == 0) return new RegradeResult(0, 0, 0);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var regraded = 0;
        var failedPages = 0;
        foreach (var page in docIds.Chunk(reevaluationPageSize))
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyDictionary<Guid, EvaluationOutcome>? applied = null;
            try
            {
                var docs = await LoadPageAsync(context, page, ct);
                applied = await ApplyEvaluationsAsync(context, docs, nowUtc, ct);
                regraded += docs.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedPages++;
                logger.LogError(ex, "Re-evaluation fan-out failed for a page of {Count} documents", page.Length);
            }
            finally
            {
                // Bound the change tracker to a single page across the whole fan-out, and drop any
                // half-applied changes from a failed page so they can't ride along on the next page's
                // SaveChanges. The triggering mutation already committed on this same context before
                // the fan-out began, so clearing here cannot lose it.
                context.ChangeTracker.Clear();
            }

            // A page that never committed has nothing to verify — and it must not be counted twice.
            if (applied is null) continue;

            try
            {
                await VerifyPageAsync(context, applied, nowUtc, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Counted as a FAILED page even though the page's own SaveChanges committed, because that
                // is what the counter is FOR: RegradeResult.AllSucceeded gates the seed's re-grade
                // watermark (#416, ADR 0036 Amendment 2), and a page whose verdicts are committed but
                // UNVERIFIED is exactly a page the next boot should re-fire. `regraded` deliberately still
                // counts these documents — they were re-graded; what failed is the confirmation.
                failedPages++;
                logger.LogError(ex,
                    "Re-evaluation verification pass failed for a page of {Count} documents; their verdicts "
                    + "are committed but unconfirmed against a fresh read", page.Length);
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }
        return new RegradeResult(docIds.Count, regraded, failedPages);
    }

    /// <summary>
    /// The ONE page-load shape, shared by the initial grade and every verification pass (#470). They must
    /// read the same graph or the verification would grade a different document than the page did and
    /// "the verdict moved" would stop meaning "the inputs moved" — the same reason
    /// <see cref="WithChecklist"/> exists for the single-document path.
    /// </summary>
    private static Task<List<Document>> LoadPageAsync(DbContext context, Guid[] ids, CancellationToken ct) =>
        context.Set<Document>()
            .Where(d => ids.Contains(d.Id))
            .Include(d => d.Vendor)
                .ThenInclude(v => v!.ComplianceTemplate)
                    .ThenInclude(t => t!.Rules)
            // Split query so a document's ExtractionFields JSON isn't re-transmitted once per
            // rule (a single join multiplies each doc row — and its JSON payload — by the
            // rule count). OrderBy gives the split its stable key.
            .OrderBy(d => d.Id)
            .AsSplitQuery()
            .ToListAsync(ct);

    /// <summary>
    /// How many times a committed page is re-read and re-graded to CONFIRM the verdicts it wrote (#470,
    /// ADR 0030 Amendment 5). Two, over a set that shrinks to the documents that actually moved, so a
    /// document must lose THREE consecutive races — the page's own write plus both corrections — before
    /// the fan-out leaves it. Same reasoning as <see cref="DocumentConcurrency.MaxAttempts"/> (3 total
    /// chances at a correct verdict) and deliberately not that constant: this bounds a best-effort
    /// background CONFIRMATION over a whole page, not a request's retry of one document, and the two must
    /// be free to move apart.
    /// </summary>
    internal const int MaxVerificationPasses = 2;

    /// <summary>
    /// Re-reads the page this fan-out has just committed and re-grades ONLY the documents whose verdict no
    /// longer follows from their inputs — the #470 half of ADR 0030's invariant on the batched fan-out.
    /// <para/>
    /// A page is loaded, graded and saved under <c>READ COMMITTED</c>, so a <c>PUT /documents/{id}/fields</c>
    /// (or an <c>ExtractionWorker</c> persist) committing inside that span leaves its document holding the
    /// EDITED inputs beside the verdict this page graded from the pre-edit ones, with
    /// <see cref="ComplianceCheck"/> rows citing values the row no longer holds — an affirmative verdict
    /// standing over inputs somebody just lowered, which nothing heals (the nightly sweep does date
    /// transitions only).
    /// <para/>
    /// The signal is the RE-GRADE ITSELF: recompute each document's outcome from a fresh read and compare it
    /// to the outcome the page applied. Equal ⇒ nothing that decides this verdict moved, whoever wrote in the
    /// window, and NOTHING is written. Different ⇒ the inputs moved, and the fresh outcome replaces the stale
    /// one. That is a mechanism rather than a column list, so it covers `ExtractionFields`, the typed
    /// columns, <see cref="Document.VendorId"/>, <see cref="Document.DocumentType"/> and any verdict input
    /// added later, by construction (ADR 0030 Amendment 2 Option E refutes the enumeration).
    /// <para/>
    /// <paramref name="nowUtc"/> is the fan-out's OWN clock reading, reused deliberately: grading the
    /// verification against a later `now` would turn an expiry boundary crossed mid-fan-out into a "mover"
    /// on documents nobody touched, so a difference here means an INPUT changed and nothing else.
    /// <para/>
    /// Comparing against what the page APPLIED — rather than against the row's stored status and check rows —
    /// costs no extra query and cannot MISS a stale verdict: the only writer that can have replaced this
    /// page's verdict since it committed is one of ADR 0030's combined-unit-of-work writers, which commits
    /// its own <c>(inputs, verdict)</c> pair, so a row that disagrees with the page's outcome while agreeing
    /// with its own inputs simply re-grades to the same values it already holds. The cost is that harmless
    /// idempotent rewrite; the benefit is that the common case reads once and writes nothing.
    /// </summary>
    private async Task VerifyPageAsync(
        DbContext context,
        IReadOnlyDictionary<Guid, EvaluationOutcome> applied,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var expected = applied;
        for (var pass = 1; pass <= MaxVerificationPasses && expected.Count > 0; pass++)
        {
            ct.ThrowIfCancellationRequested();

            // MUST come before the re-read. EF resolves a query against already-tracked instances and does
            // NOT refresh their values, so without this the "fresh" documents would be the very entities
            // this fan-out just wrote and every pass would confirm itself.
            context.ChangeTracker.Clear();

            var fresh = await LoadPageAsync(context, [.. expected.Keys], ct);

            var movers = new List<(Document Doc, EvaluationOutcome Outcome)>();
            foreach (var doc in fresh)
            {
                // A document that vanished from the re-read was soft-deleted (or moved out of the tenant
                // filter) after the page committed — it is simply absent from `fresh`, and re-grading a row
                // that is going away is not this pass's business.
                var outcome = ComputeOutcome(doc, nowUtc, _correctedAdditionalInsuredWording);
                if (!OutcomeMatches(outcome, expected[doc.Id])) movers.Add((doc, outcome));
            }

            if (movers.Count == 0) return;

            logger.LogInformation(
                "Re-evaluation verification pass {Pass} found {Count} document(s) whose inputs changed while "
                + "the page was being graded; re-grading those and leaving the rest untouched",
                pass, movers.Count);

            // Only the movers are written, so one concurrent edit costs its OWN document a second re-grade
            // and costs the other documents in the page nothing (the anti-forfeit property Option K lacks).
            // The clear-and-replace of their check rows composes with
            // ComplianceCheckDeleteConcurrencyInterceptor exactly as every other re-grade does (#468).
            expected = await ApplyOutcomesAsync(context, movers, nowUtc, ct);
        }

        // The bound is spent and something is still moving. LEAVE the last verdict this fan-out computed:
        // the alternative — degrading it to Pending "for safety" — is ADR 0030 Amendment 3 Option I,
        // refuted for exactly this caller shape, since a pure re-grade owns no inputs and would be
        // replacing a possibly-correct verdict with a non-committal one through the very write that keeps
        // losing. Whoever keeps winning is a combined-unit-of-work writer committing its own consistent
        // pair, so the next thing that grades this document heals it.
        if (expected.Count > 0)
            logger.LogWarning(
                "Gave up confirming {Count} document(s) after {Passes} verification pass(es): their inputs "
                + "changed inside every pass, so the last correction is committed but unconfirmed. They keep "
                + "that verdict rather than being degraded, and the next evaluation of them heals it",
                expected.Count, MaxVerificationPasses);
    }

    /// <summary>
    /// Whether two evaluations of the same document say the SAME thing — the whole assertion a re-grade
    /// makes, not just its headline. The status alone would miss a document whose aggregate verdict is
    /// unchanged while its check rows moved (a different rule now failing, an <c>ActualValue</c> the row no
    /// longer holds), which is half of what #470 describes.
    /// <para/>
    /// Keyed on <see cref="ComplianceCheck.ComplianceRuleId"/> rather than positional, because
    /// <c>template.Rules</c> carries no ORDER BY and two loads may materialize it differently — a
    /// positional compare would call every document a mover on a reordered read. <see cref="ComplianceCheck.Id"/>
    /// (freshly minted per evaluation) and <see cref="ComplianceCheck.CheckedAt"/> (this fan-out's clock) are
    /// deliberately excluded: neither is an assertion about the document.
    /// <para/>
    /// <see cref="EvaluationOutcome.ClearExistingChecks"/> is compared too, and it also GATES the row
    /// comparison — an <c>Expired</c> outcome deliberately leaves the existing check rows alone
    /// (<see cref="ComputeOutcome"/>), so its empty <c>NewChecks</c> asserts nothing about them.
    /// </summary>
    private static bool OutcomeMatches(in EvaluationOutcome fresh, in EvaluationOutcome applied)
    {
        if (fresh.Status != applied.Status || fresh.ClearExistingChecks != applied.ClearExistingChecks)
            return false;
        if (!fresh.ClearExistingChecks) return true;
        if (fresh.NewChecks.Count != applied.NewChecks.Count) return false;

        var appliedByRule = applied.NewChecks.ToDictionary(c => c.ComplianceRuleId);
        foreach (var check in fresh.NewChecks)
        {
            if (!appliedByRule.TryGetValue(check.ComplianceRuleId, out var before)) return false;
            if (before.IsPassed != check.IsPassed
                || !string.Equals(before.ActualValue, check.ActualValue, StringComparison.Ordinal)
                || !string.Equals(before.Notes, check.Notes, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    // Grades one page and applies it. The COMPUTE half is split from the write half because the
    // verification pass (#470) has already computed its outcomes — that is how it decided who moved — and
    // grading each mover a second time to write it would be both wasteful and a second chance to disagree.
    private Task<IReadOnlyDictionary<Guid, EvaluationOutcome>> ApplyEvaluationsAsync(
        DbContext context, IReadOnlyList<Document> docs, DateTime nowUtc, CancellationToken ct)
    {
        var outcomes = new List<(Document Doc, EvaluationOutcome Outcome)>(docs.Count);
        foreach (var doc in docs)
            outcomes.Add((doc, ComputeOutcome(doc, nowUtc, _correctedAdditionalInsuredWording)));
        return ApplyOutcomesAsync(context, outcomes, nowUtc, ct);
    }

    // Applies one page of evaluations as a single round-trip group: one bulk load of the page's
    // existing checks, then one SaveChanges carrying every delete + insert + status update — so the
    // page commits atomically. RemoveRange (not ExecuteDelete) keeps the delete on the SAME
    // SaveChanges/transaction as the inserts AND on the audit-interceptor path; because
    // ComplianceCheck has no DeletedAt the interceptor leaves it a hard delete with no audit row,
    // exactly as the prior per-document RemoveRange did. Same shape, same reason and same
    // zero-row-delete exposure as ApplyEvaluationCoreAsync's clear above — see it, and
    // ComplianceCheckDeleteConcurrencyInterceptor, for why that exposure is answered rather than
    // designed out (#468). This fan-out runs READ COMMITTED and catches per page, so before that
    // interceptor a competing re-grade forfeited the WHOLE page's re-grade, not just one document's.
    //
    // Returns the outcomes it committed, keyed by document: that map is what VerifyPageAsync compares the
    // next fresh grade against, so "what this fan-out last asserted about this document" is carried by the
    // code that asserted it rather than re-derived from the row afterwards.
    private static async Task<IReadOnlyDictionary<Guid, EvaluationOutcome>> ApplyOutcomesAsync(
        DbContext context,
        IReadOnlyList<(Document Doc, EvaluationOutcome Outcome)> outcomes,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (outcomes.Count == 0) return ReadOnlyDictionary<Guid, EvaluationOutcome>.Empty;

        // The id set is drawn from the Documents query above — tenant-filtered on AppDbContext, or
        // cross-org BY DESIGN on SystemDbContext (#400) — so this delete over the ComplianceChecks
        // set is scoped to exactly those documents' check rows, never a broader sweep.
        var clearIds = outcomes.Where(o => o.Outcome.ClearExistingChecks).Select(o => o.Doc.Id).ToArray();
        if (clearIds.Length > 0)
        {
            var existing = await context.Set<ComplianceCheck>()
                .Where(c => clearIds.Contains(c.DocumentId))
                .ToListAsync(ct);
            context.Set<ComplianceCheck>().RemoveRange(existing);
        }

        foreach (var (doc, outcome) in outcomes)
        {
            if (outcome.NewChecks.Count > 0)
                context.Set<ComplianceCheck>().AddRange(outcome.NewChecks);
            doc.ComplianceStatus = outcome.Status;
            doc.UpdatedAt = nowUtc;
        }

        await context.SaveChangesAsync(ct);
        return outcomes.ToDictionary(o => o.Doc.Id, o => o.Outcome);
    }

    public Task ApplyEvaluationAsync(DbContext context, Document doc, CancellationToken ct) =>
        // No separate basis: the tracked entity IS what this caller is committing, so it grades itself.
        ApplyEvaluationCoreAsync(context, doc, gradingBasis: null, ct);

    public Task ApplyEvaluationAsync(DbContext context, Document doc, Document gradingBasis, CancellationToken ct) =>
        ApplyEvaluationCoreAsync(context, doc, gradingBasis, ct);

    /// <summary>
    /// The vendor graph <see cref="ComputeOutcome"/> reads — <c>Vendor → ComplianceTemplate → Rules</c> —
    /// spelled ONCE so the two branches of <see cref="ApplyEvaluationCoreAsync"/> cannot drift (#460 review
    /// round 2, S5). Their tracking/loading difference is deliberate and stays per-branch; the loaded GRAPH
    /// is not, and a chain that lost <c>.ThenInclude(t =&gt; t!.Rules)</c> on one of them would silently read
    /// no-governing-rules and store <see cref="ComplianceStatus.Pending"/> there while the other still graded.
    /// </summary>
    private static IQueryable<Vendor> WithChecklist(IQueryable<Vendor> vendors) =>
        vendors.Include(v => v.ComplianceTemplate).ThenInclude(t => t!.Rules);

    private async Task ApplyEvaluationCoreAsync(DbContext context, Document doc, Document? gradingBasis, CancellationToken ct)
    {
        // WHAT gets graded vs WHERE the verdict lands are two different documents when a basis is supplied
        // (#460 / ADR 0030 Amendment 2). The basis is read-only with respect to `doc`: the tracked entity
        // still receives the status and the check rows, and no basis value is ever copied onto it — that
        // restraint is what keeps the caller writing exactly the columns it wrote before.
        if (gradingBasis is not null)
        {
            // ComputeOutcome stamps every new ComplianceCheck.DocumentId from the BASIS while the
            // clear-existing predicate below keys on the TRACKED doc.Id. Today's one caller derives the
            // basis from the same tracked entity by primary key so the two always agree — but the coupling
            // is load-bearing on a PUBLIC interface member in the compliance-verdict core, so enforce it
            // rather than leave a future caller free to insert check rows against one document while
            // deleting another's (#460 review, S1).
            if (gradingBasis.Id != doc.Id)
                throw new ArgumentException(
                    "The grading basis must be the same document as the entity receiving the verdict.",
                    nameof(gradingBasis));

            // The basis's Vendor navigation is ASSIGNED below from an AsNoTracking query. That is safe only
            // while the basis is DETACHED (what DocumentGradingBasis.AfterPendingCommitAsync produces):
            // grafting an untracked graph onto a TRACKED principal is exactly the shape EF turns into
            // spurious inserts at the next DetectChanges (#460 review, S6).
            if (context.Entry(gradingBasis).State != EntityState.Detached)
                throw new ArgumentException(
                    "The grading basis must be a DETACHED document — see DocumentGradingBasis.AfterPendingCommitAsync.",
                    nameof(gradingBasis));
        }

        var basis = gradingBasis ?? doc;

        // Load Vendor → ComplianceTemplate → Rules for the verdict computation, against the basis's CURRENT
        // VendorId — for the tracked path that is the doc's possibly-just-edited, uncommitted value, fixed
        // up on this same context. A SINGLE query (no AsSplitQuery): the root is ONE Vendor (not a set of
        // Documents) and the only collection in the chain is template.Rules, so there is no cartesian
        // payload multiplication — the batched fan-out splits because its root IS a set of documents whose
        // ExtractionFields JSON would be re-shipped per rule, which does not apply here. The nav query
        // honors the Vendor soft-delete filter, so a deleted vendor reads as no-template (Pending) exactly
        // as the prior Include did.
        if (gradingBasis is null)
        {
            var vendorRef = context.Entry(doc).Reference(d => d.Vendor);
            if (doc.VendorId is not null)
                await WithChecklist(vendorRef.Query()).LoadAsync(ct);
            else
            {
                // No vendor assigned: force the in-memory navigation to match the FK so ComputeOutcome reads
                // no-template (Pending) even if a caller ever hands us a tracked doc with a stale Vendor loaded.
                doc.Vendor = null;
                vendorRef.IsLoaded = true;
            }
        }
        else
        {
            // The basis is DETACHED, so there is no navigation to load through — query the vendor chain by
            // the basis's own FK and hang it off the basis. AsNoTracking so a vendor row this context may
            // already be tracking is neither overwritten nor pulled into the caller's unit of work; the
            // same global filters (Vendor soft-delete, and the tenant filter on AppDbContext) apply to
            // `Set<Vendor>()` as to the navigation query above, so a deleted vendor still reads
            // no-template (Pending).
            basis.Vendor = basis.VendorId is null
                ? null
                : await WithChecklist(context.Set<Vendor>().AsNoTracking())
                    .FirstOrDefaultAsync(v => v.Id == basis.VendorId.Value, ct);
        }

        // nowUtc comes from TimeProvider (not DateTime.UtcNow) so the expiration / expiring-soon date
        // boundaries in ComputeOutcome are deterministically testable.
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var outcome = ComputeOutcome(basis, nowUtc, _correctedAdditionalInsuredWording);

        if (outcome.ClearExistingChecks)
        {
            // Materialized (ToListAsync) before RemoveRange — handing RemoveRange an IQueryable would
            // execute the delete-driving query on the blocking sync path.
            //
            // RemoveRange, not ExecuteDeleteAsync, and that is the DECISION (#468). The staged deletes ride
            // in the CALLER's own SaveChanges, which is what keeps the clear, the new check rows and the
            // verdict in ONE transaction on every caller (#337 / ADR 0030) — including the two that own no
            // explicit transaction, ExtractionWorker.PersistSuccess and EvaluateForSystemAsync, where a
            // set-based ExecuteDeleteAsync would issue its own statement and COMMIT the clear separately.
            // The cost is that EF emits a per-row DELETE keyed on the primary key and demands one row each,
            // so a competing re-grade committing between this read and the caller's SaveChanges leaves them
            // matching nothing. That is answered where it belongs — ComplianceCheckDeleteConcurrencyInterceptor
            // makes a check-row DELETE row-count-tolerant — rather than by moving the delete out of the unit
            // of work. Do not "simplify" this to a set-based clear.
            var existing = await context.Set<ComplianceCheck>()
                .Where(c => c.DocumentId == doc.Id)
                .ToListAsync(ct);
            context.Set<ComplianceCheck>().RemoveRange(existing);
        }
        if (outcome.NewChecks.Count > 0)
            context.Set<ComplianceCheck>().AddRange(outcome.NewChecks);

        doc.ComplianceStatus = outcome.Status;
        doc.UpdatedAt = nowUtc;
        // No SaveChanges — the caller commits the inputs and this verdict in ONE transaction (#337).
    }

    // Loads the document, applies the verdict in place, and SAVES — the read-then-write convenience behind
    // the two pure RE-GRADE entry points, which do not themselves change the canonical inputs: EvaluateAsync
    // (the "Check again" button, its ONE production caller) and EvaluateForSystemAsync (its system-context
    // twin, with no production caller). Nothing else reaches this method, and the near misses are worth
    // naming because they used to be listed here as callers and are not: the vendor/type assign
    // (DocumentEndpoints.UpdateDocument) calls ApplyEvaluationAsync and folds the verdict into its own
    // SaveChanges, and the template/vendor fan-outs go through the BATCHED ReevaluateWhereAsync. The
    // input-CHANGING paths (manual field edit in DocumentEndpoints.UpdateFields, extraction persist in
    // ExtractionWorker.PersistSuccess) likewise call ApplyEvaluationAsync directly, so inputs and verdict
    // commit atomically and can never be left torn (#337).
    //
    // There is deliberately NO transaction, lock or token in here (#461 / ADR 0030 Amendment 3). The
    // window between the read and the save is real — a field edit landing inside it leaves the row's
    // edited inputs beside this verdict — but closing it belongs to the CALLER, because the two callers
    // want different answers. EvaluateAsync's one caller (ComplianceEndpoints.RunCheck) wraps this in
    // DocumentWriteConcurrency's REPEATABLE READ + bounded re-run, which needs an AppDbContext and an
    // IResult to answer with; EvaluateForSystemAsync has no production caller. Taking the transaction in
    // here would also mean Services/ owning transaction scope, which nothing else in this layer does,
    // and would put the guard on a method the BATCHED fan-out deliberately does not use.
    private async Task<ComplianceStatus> EvaluateInternalAsync(DbContext context, Guid documentId, CancellationToken ct)
    {
        var doc = await context.Set<Document>().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return ComplianceStatus.Pending;
        await ApplyEvaluationAsync(context, doc, ct);
        await context.SaveChangesAsync(ct);
        return doc.ComplianceStatus;
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
    // correctedAdditionalInsuredWording (#396 / CLM-1, default false) is passed straight through to
    // EvaluateRule; it only ever changes the affirmative-flag check's NOTE string, never a verdict —
    // so the EvaluationOutcome's Status and pass/fail are identical regardless of its value.
    internal static EvaluationOutcome ComputeOutcome(Document doc, DateTime nowUtc, bool correctedAdditionalInsuredWording = false)
    {
        var today = nowUtc.Date;

        // Expired wins outright — and, preserving prior behavior, does NOT touch existing check rows
        // on this path (only the date crossed; the rule verdicts that produced those checks stand).
        if (doc.ExpirationDate is DateTime exp && exp.Date < today)
            return new EvaluationOutcome(ComplianceStatus.Expired, [], ClearExistingChecks: false);

        // Same 30-day window as ComplianceStatusDeriver / the SQL read sites — reference the shared
        // constant so the number lives in one place (#294 review).
        var expiringSoon = doc.ExpirationDate is DateTime exp2
            && exp2.Date <= today.AddDays(ComplianceStatusDeriver.ExpiringSoonWindowDays);

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

        // The blank-DocumentType arm is a WILDCARD ("applies to every document type") and exists ONLY to
        // tolerate rows written before #373 / ADR 0045. Nothing can write that state any more:
        // ComplianceEndpoints.UpsertRule now rejects a blank/unrecognized documentType with
        // `400 validation.document_type`, so for any rule created or edited from here on, blank is
        // impossible rather than meaningful. KEEP the arm — deleting it would silently change grading for
        // any pre-existing blank-type rule (a live-data behavior change, not a code fix), and a legacy
        // blank-type rule must be re-typed before it can be saved again anyway.
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
            var (passed, actualValue, note) = EvaluateRule(doc, rule, correctedAdditionalInsuredWording);
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

        // Future-effective demotion (#362 / ADR 0041) is DELIBERATELY NOT applied here. The persisted
        // status keeps the REAL rule verdict; the "not yet in force → Pending" demotion is a READ-ONLY
        // overlay (ComplianceStatusDeriver.Effective + every SQL read mirror). Storing Pending would
        // strand the doc at a stale Pending after it becomes effective — nothing re-runs rule evaluation
        // on an EffectiveDate crossing, so the stored verdict must retain enough to SELF-HEAL on read the
        // instant today reaches EffectiveDate, exactly as the Expired/ExpiringSoon overlay does. So a
        // future-effective all-passing COI is stored Compliant here and READS Pending everywhere.
        var status = allPassed
            ? (expiringSoon ? ComplianceStatus.ExpiringSoon : ComplianceStatus.Compliant)
            : ComplianceStatus.NonCompliant;
        return new EvaluationOutcome(status, newChecks, ClearExistingChecks: true);
    }

    // internal (not private) so the pure rule-evaluation logic can be unit-tested directly
    // without a database — see InternalsVisibleTo in CompliDrop.Api.csproj.
    /// <summary>
    /// The note stored on a check that failed because we could not READ the value, as opposed to the
    /// document not carrying one. Distinct from "Field missing." on purpose — the two say opposite
    /// things about the certificate, and only one of them is the user's cue to correct a value (#383).
    /// </summary>
    internal const string UnreadableValueNote =
        "We couldn't read this value, so we can't confirm this requirement. Check the document and correct it.";

    // #396 (CLM-1, ADR 0043): the honest "a certificate only INDICATES additional-insured status; it
    // does not GRANT coverage — request the endorsement" reminder appended to the affirmative-flag
    // (ACORD checkbox fallback) check note WHEN the ComplianceClaims:CorrectedAdditionalInsuredWording
    // flag is ON. Flag OFF keeps the pre-#396 note ("The additional-insured box is checked…")
    // byte-for-byte. The reminder never changes a verdict — copy only (TRR §3).
    internal const string AdditionalInsuredEndorsementReminder =
        "A certificate only indicates this — request the endorsement (e.g. CG 20 26) to confirm coverage.";
    internal const string AdditionalInsuredIndicatedHitNote =
        "The certificate indicates additional insured and shows this name in the certificate holder / description of operations. "
        + AdditionalInsuredEndorsementReminder;

    // correctedAdditionalInsuredWording (#396 / CLM-1, default false) selects ONLY the affirmative-flag
    // branch's NOTE wording (legacy "box is checked" vs the honest "certificate indicates… request the
    // endorsement"). It is threaded, never read from config here, so the pure logic stays unit-testable
    // both ways. It NEVER affects the returned `passed` bool — the verdict is identical for either value.
    internal static (bool passed, string? actualValue, string? note) EvaluateRule(
        Document doc, ComplianceRule rule, bool correctedAdditionalInsuredWording = false)
    {
        // FAIL-CLOSED GUARD (#383, ADR 0040). A canonical field whose raw value is non-blank but
        // UNPARSEABLE ("12/31/2026 (per endorsement)", "continuous until cancelled") clears its typed
        // column to null. Every date check downstream reads that column, so the document can never
        // enter Expired/ExpiringSoon — while the `required` rule USED to pass anyway, satisfied by the
        // non-empty raw string. That combination rendered the affirmative "Insurance has not expired"
        // next to a green check on a certificate that expired years ago, and the reminder windows (also
        // keyed on the null column) never fired: one root cause failing in three places, all toward
        // false-Compliant. An unreadable value certifies NOTHING, whatever the operator: fail the rule,
        // show the user the raw text so they can fix it, and say WHY in the note. The document is also
        // pushed to ExtractionStatus.ManualRequired by both writers, so it surfaces even under a
        // checklist that carries no rule on the field.
        if (DocumentFieldReadability.TryGetUnreadableValue(doc, rule.FieldName, out var unreadable))
            return (false, unreadable, UnreadableValueNote);

        string? actual = LookupValue(doc, rule.FieldName);
        var op = rule.Operator?.ToLowerInvariant() ?? "required";

        switch (op)
        {
            case "required":
                return (!string.IsNullOrWhiteSpace(actual), actual, actual is null ? "Field missing." : null);

            case "equals":
                // Fail CLOSED on a misconfigured rule (null/blank ExpectedValue): without this guard
                // `string.Equals(null, null)` is TRUE, so a document MISSING the field read Compliant
                // while one that HAD the field failed — the wrong-direction (fail-open) verdict #374
                // fixes, unique among the operators. Mirrors the sibling value-operators, which also
                // fail closed on a null/blank expected (`contains` short-circuits at the top of its arm;
                // `min_value`'s parse of a null/blank expected fails). UpsertRule now rejects such a rule at
                // write time; this arm is the safety net for any row persisted before that guard. A
                // WELL-FORMED equals rule is unchanged below — same case-insensitive Trim comparison,
                // and the "Field missing." note still applies when the expected value is real but the
                // field is absent. The misconfig case deliberately does NOT reuse "Field missing.":
                // the field being absent is not why this failed — the rule itself is broken.
                if (string.IsNullOrWhiteSpace(rule.ExpectedValue))
                    return (false, actual, "Rule is misconfigured: no expected value.");
                return (string.Equals(actual?.Trim(), rule.ExpectedValue.Trim(), StringComparison.OrdinalIgnoreCase),
                    actual,
                    actual is null ? "Field missing." : null);

            case "contains":
                // Fail CLOSED on a misconfigured rule (null/blank/empty ExpectedValue) BEFORE either
                // contains path runs: `"Acme".Contains("")` is TRUE in .NET, so an EMPTY expected would
                // grade any document that HAS the field as passing — a vacuous false-Compliant (the empty
                // string is non-null, so it slipped past the plain-path `is not null` guard below; #374
                // re-review). Placed at the TOP so it ALSO covers the additional_insured affirmative-flag
                // fallback, where `holder.Contains("")` is likewise TRUE. Mirrors the `equals` guard
                // exactly (same note). UpsertRule rejects such a rule at write time; this arm is the
                // safety net for any row persisted before that guard. A WELL-FORMED contains rule (a real,
                // non-blank expected) — plain substring OR the affirmative-flag fallback — is unchanged
                // below, since the guard is a no-op for a non-blank expected.
                if (string.IsNullOrWhiteSpace(rule.ExpectedValue))
                    return (false, actual, "Rule is misconfigured: no expected value.");

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
                    // #396 (CLM-1): fallbackHit — the pass/fail — is computed ABOVE and is identical
                    // regardless of the flag; only the NOTE wording is staged. Flag OFF keeps the
                    // pre-#396 "box is checked" note byte-for-byte; flag ON states the honest
                    // "certificate INDICATES… request the endorsement" framing (ADR 0043, TRR §3).
                    var note = correctedAdditionalInsuredWording
                        ? (fallbackHit
                            ? AdditionalInsuredIndicatedHitNote
                            : $"The certificate indicates additional insured, but '{rule.ExpectedValue}' was not found in the certificate holder or description of operations. {AdditionalInsuredEndorsementReminder}")
                        : (fallbackHit
                            ? "The additional-insured box is checked; matched the name in the certificate holder / description of operations."
                            : $"The additional-insured box is checked, but '{rule.ExpectedValue}' was not found in the certificate holder or description of operations.");
                    return (fallbackHit, actual, note);
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
                // Both sides go through the shared money parse so a currency-symbol amount reads the
                // same everywhere (#383): "$1,000,000" is how a model reads a COI and how an owner
                // types a minimum, and NumberStyles.Any + InvariantCulture alone rejects it (the
                // invariant currency symbol is ¤, not $) — which silently failed the comparison on a
                // certificate that genuinely met the floor.
                if (!CanonicalDocumentFields.TryParseAmount(actual, out var a)
                    || !CanonicalDocumentFields.TryParseAmount(rule.ExpectedValue, out var min))
                    return (false, actual, "Unable to parse numeric comparison.");
                return (a >= min, actual, a >= min ? null : $"Value {a} below required minimum {min}.");

            default:
                return (false, actual, $"Unknown operator '{rule.Operator}'.");
        }
    }

    /// <summary>
    /// Width of <c>ComplianceCheck.ActualValue</c> / <c>.Notes</c>. <c>ModelConfiguration</c>
    /// CONSUMES this constant (#372) instead of re-declaring 500, so the column and the clamp that
    /// feeds it cannot drift: the check rows commit in the same <c>SaveChanges</c> as the verdict
    /// and its inputs (ADR 0030), and Npgsql does not truncate — a drifted width would fail that
    /// whole unit of work with Postgres 22001, not just lose a note.
    /// </summary>
    internal const int CheckColumnMaxLength = 500;

    // Delegates to the shared surrogate-safe truncation (#372) so the codebase carries ONE
    // implementation, not a copy per bounded column. Behavior is unchanged — pinned by the
    // ClampToColumn boundary/surrogate tests in ComplianceRuleEvaluationTests.
    internal static string? ClampToColumn(string? value) =>
        ColumnClamp.To(value, CheckColumnMaxLength);

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

        if (CanonicalDocumentFields.IsCanonical(fieldName))
        {
            // The typed column is the authority for these three — it is what the date windows, the
            // dashboard counts and the reminder queries all read.
            if (DocumentFieldReadability.TypedColumnValue(doc, fieldName) is { } typed) return typed;

            // Column null. The raw JSON is still consulted, but ONLY when it would parse (#383): a
            // legacy row whose JSON holds a readable value keeps resolving, while a value that failed
            // to parse — the case that nulled the column in the first place — resolves to null instead
            // of fail-open-satisfying a `required` rule with text nothing else in the system can read.
            var raw = DocumentFieldReadability.RawFieldValue(doc, fieldName);
            return CanonicalDocumentFields.IsUnreadable(fieldName, raw) ? null : raw;
        }

        return DocumentFieldReadability.RawFieldValue(doc, fieldName);
    }
}
