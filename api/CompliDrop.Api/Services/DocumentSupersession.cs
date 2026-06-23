using System.Linq.Expressions;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// "A later upload that ALSO extends coverage wins" — the supersession rule (#327 / ADR 0033, as amended by
/// the #327 re-review). When a vendor renews a certificate, the NEW upload becomes the current cert for that
/// requirement and the older copy is <b>superseded</b>: historical, not a current expiry liability. The live
/// "Expired" surfaces (dashboard count, expiry-pipeline expired bucket, documents-list <c>?status=Expired</c>)
/// and the reminder windows exclude superseded documents so a renewed COI's old expired copy stops inflating
/// the count and a renewed vendor isn't reminded; the audit export keeps every document but ANNOTATES the
/// superseded ones (it must not hide history).
/// <para/>
/// A document <c>d</c> is superseded by another doc <c>o</c> for the same <c>(VendorId, DocumentType)</c> only
/// when <c>o</c> is BOTH a later upload (<c>o.CreatedAt &gt; d.CreatedAt</c>) AND actually extends coverage
/// (<c>o.ExpirationDate</c> is non-null and <c>&gt;= d.ExpirationDate</c>). The coverage-extension clause is a
/// compliance-safety guard: a "renewal" that is still processing (no expiry yet — <c>NULL &gt;= date</c> is
/// false in SQL), has no expiration, or expires <i>earlier</i> than the cert it would replace does NOT
/// supersede, so it can never make a genuinely-unmet expired liability silently disappear from the dashboard
/// or stop its reminders. A future-dated or equal-expiry renewal (the real-renewal / duplicate-re-upload
/// cases) still supersedes, so the old copy never double-counts. A non-compliant but coverage-extending
/// renewal supersedes the Expired liability but still surfaces under the NonCompliant tally — so nothing is
/// hidden, only re-classified.
/// <para/>
/// Both predicates are a single set-based correlated <c>EXISTS</c> over the SAME Documents queryable the
/// caller reads from (the tenant-/soft-delete-filtered <c>db.Documents</c>), so a deleted or cross-org
/// document never counts as the superseder, and there are no per-document round trips. A document with no
/// vendor (<c>VendorId == null</c>) belongs to no requirement group and is never superseded. The in-memory
/// mirror used by the audit export (<c>ExportService.SupersededIds</c>) is pinned equal to these predicates
/// by a test.
/// </summary>
public static class DocumentSupersession
{
    /// <summary>The document is the latest covering cert (or only one) for its (vendor, type) group — a current liability.</summary>
    public static Expression<Func<Document, bool>> IsCurrent(IQueryable<Document> documents) =>
        d => d.VendorId == null
            || !documents.Any(o => o.VendorId == d.VendorId
                && o.DocumentType == d.DocumentType
                && o.CreatedAt > d.CreatedAt
                && o.ExpirationDate != null
                && o.ExpirationDate >= d.ExpirationDate);

    /// <summary>A newer, coverage-extending document exists for the same (vendor, type) — this one is historical.</summary>
    public static Expression<Func<Document, bool>> IsSuperseded(IQueryable<Document> documents) =>
        d => d.VendorId != null
            && documents.Any(o => o.VendorId == d.VendorId
                && o.DocumentType == d.DocumentType
                && o.CreatedAt > d.CreatedAt
                && o.ExpirationDate != null
                && o.ExpirationDate >= d.ExpirationDate);
}
