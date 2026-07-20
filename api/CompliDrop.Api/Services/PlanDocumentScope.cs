using System.Linq.Expressions;
using CompliDrop.Api.Entities;

namespace CompliDrop.Api.Services;

/// <summary>
/// "Which documents consume a paid plan slot" — the single definition of the plan-limit population
/// (#367). A document counts when it belongs to the org, is not soft-deleted, and is not the
/// one-click sample-demo artifact (#238 / ADR 0028 Amendment 1): the sample is a throwaway that
/// must never occupy a slot the customer paid for.
/// <para/>
/// This predicate is shared by every surface that enforces or REPORTS the limit, because those
/// surfaces disagreeing is exactly the defect #367 fixed. Before the extraction the expression was
/// hand-copied into three call sites and only one of them carried the sample exclusion, so a capped
/// org's dashboard upload succeeded while the same org's vendor-portal upload was refused a document
/// early, and the Settings tile reported a number neither gate enforced. Callers:
/// <list type="bullet">
///   <item><description><c>DocumentEndpoints.UploadDocument</c> — the dashboard ingress fence.</description></item>
///   <item><description><c>VendorPortalEndpoints.UploadViaPortal</c> — the vendor-facing mirror of that fence (#261).</description></item>
///   <item><description><c>BillingEndpoints.GetSubscription</c> — the Settings "used / limit" tile.</description></item>
/// </list>
/// <c>PlanLimitConsistencyTests</c> pins all three to the same number for one seeded org state, so a
/// future edit to one call site can't silently re-open the drift. Follows the shared-predicate
/// pattern established by <see cref="DocumentSupersession"/>.
/// <para/>
/// The soft-delete clause is deliberately explicit even though <c>SystemDbContext</c> also applies a
/// global soft-delete query filter: this expression must stay correct on its own terms for any
/// caller, including one that reaches for <c>IgnoreQueryFilters()</c>.
/// </summary>
public static class PlanDocumentScope
{
    /// <summary>Documents in <paramref name="organizationId"/> that count against the plan's DocumentLimit.</summary>
    public static Expression<Func<Document, bool>> CountsTowardLimit(Guid organizationId) =>
        d => d.OrganizationId == organizationId && d.DeletedAt == null && !d.IsSample;
}
