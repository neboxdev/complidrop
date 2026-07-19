using CompliDrop.Api.Entities;
using CompliDrop.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// An <see cref="IComplianceCheckService"/> whose system fan-out simulates an INTERRUPTED / partially-failed
/// re-grade: it records the call but reports <see cref="RegradeResult.FailedPages"/> &gt; 0 WITHOUT touching any
/// document — exactly the observable state <c>ReevaluateWhereAsync</c> leaves when a page's SaveChanges is
/// caught-and-skipped (or a boot is killed mid-fan-out). Lets the durability test (#416 / ADR 0036 Amendment 2)
/// prove that the seed leaves the document STALE and does NOT advance the template's RegradedThroughRevision, so
/// the next boot re-fires the re-grade. Every other method throws — the seed path only ever calls the system
/// fan-out, so an unexpected call is a bug the test should surface.
/// </summary>
public sealed class FailingSystemReevaluator : IComplianceCheckService
{
    public List<Guid> RegradedTemplateIds { get; } = [];

    public Task<RegradeResult> ReevaluateForTemplateForSystemAsync(Guid templateId, CancellationToken ct)
    {
        RegradedTemplateIds.Add(templateId);
        // One document targeted, none regraded, one failed page → AllSucceeded == false, so the seed must
        // leave the watermark behind (no RegradedThroughRevision advance) and the document unchanged.
        return Task.FromResult(new RegradeResult(Targeted: 1, Regraded: 0, FailedPages: 1));
    }

    public Task ApplyEvaluationAsync(DbContext context, Document doc, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ApplyEvaluationAsync.");

    public Task<ComplianceStatus> EvaluateAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls EvaluateAsync.");

    public Task<ComplianceStatus> EvaluateForSystemAsync(Guid documentId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls EvaluateForSystemAsync.");

    public Task ReevaluateForTemplateAsync(Guid templateId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls the tenant-filtered ReevaluateForTemplateAsync.");

    public Task ReevaluateForTemplateOrDocumentsAsync(Guid templateId, IReadOnlyList<Guid> documentIds, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ReevaluateForTemplateOrDocumentsAsync.");

    public Task ReevaluateForVendorAsync(Guid vendorId, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ReevaluateForVendorAsync.");

    public Task ReevaluateForVendorsAsync(IReadOnlyList<Guid> vendorIds, CancellationToken ct) =>
        throw new InvalidOperationException("The seed never calls ReevaluateForVendorsAsync.");
}
