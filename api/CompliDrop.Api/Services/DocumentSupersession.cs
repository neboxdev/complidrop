using System.Linq.Expressions;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// "Latest document per (VendorId, DocumentType) wins" — the supersession rule (#327 / ADR 0033). When a
/// vendor renews a certificate, the NEW upload (later <see cref="Document.CreatedAt"/>) becomes the current
/// cert for that requirement and the older copy is <b>superseded</b>: historical, not a current expiry
/// liability. The live "Expired" surfaces (dashboard count, expiry-pipeline expired bucket, documents-list
/// <c>?status=Expired</c>) and the reminder windows exclude superseded documents so a renewed COI's old
/// expired copy stops inflating the count and a renewed vendor isn't reminded; the audit export keeps every
/// document but ANNOTATES the superseded ones (it must not hide history).
/// <para/>
/// Both predicates are a single set-based correlated <c>EXISTS</c> over the SAME Documents queryable the
/// caller reads from (the tenant-/soft-delete-filtered <c>db.Documents</c>), so a deleted or cross-org
/// document never counts as the superseder, and there are no per-document round trips. "Latest" is by
/// <see cref="Document.CreatedAt"/> — the most recently uploaded cert, matching the vendor coverage rollup
/// (FP-074). A document with no vendor (<c>VendorId == null</c>) belongs to no requirement group and is
/// never superseded.
/// </summary>
public static class DocumentSupersession
{
    /// <summary>The document is the latest (or only) for its (vendor, type) group — a current liability.</summary>
    public static Expression<Func<Document, bool>> IsCurrent(IQueryable<Document> documents) =>
        d => d.VendorId == null
            || !documents.Any(o => o.VendorId == d.VendorId
                && o.DocumentType == d.DocumentType
                && o.CreatedAt > d.CreatedAt);

    /// <summary>A newer document exists for the same (vendor, type) — this one is historical.</summary>
    public static Expression<Func<Document, bool>> IsSuperseded(IQueryable<Document> documents) =>
        d => d.VendorId != null
            && documents.Any(o => o.VendorId == d.VendorId
                && o.DocumentType == d.DocumentType
                && o.CreatedAt > d.CreatedAt);
}
